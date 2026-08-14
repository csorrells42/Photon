using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HermesDesktop;

internal sealed class ChromiumBookmarksBridge
{
    internal const int ProtocolVersion = 1;
    internal const int MaximumBookmarks = 1_024;
    private const int MaximumNodes = 4_096;
    private const int MaximumDepth = 32;
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private readonly Action<object> _postMessage;
    private readonly string _bookmarkPath;

    internal ChromiumBookmarksBridge(Action<object> postMessage, string? bookmarkPath = null)
    {
        _postMessage = postMessage;
        _bookmarkPath = bookmarkPath ?? DefaultBookmarkPath();
    }

    internal async Task ImportAsync(int version, string? requestId, CancellationToken cancellationToken)
    {
        var safeRequestId = BoundedToken(requestId, 128);
        if (version != ProtocolVersion || safeRequestId.Length == 0)
        {
            PostFailure(safeRequestId, "invalid-request", "Chrome bookmarks could not be imported because the request was invalid.");
            return;
        }

        try
        {
            EnsureOrdinaryLocalFile(_bookmarkPath);
            await using var stream = new FileStream(
                _bookmarkPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
                throw new InvalidDataException("Chrome bookmark data is outside the supported size boundary.");

            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            var snapshot = ParseSnapshot(bytes, MaximumBookmarks);
            _postMessage(new
            {
                type = "browser.bookmarks.snapshot",
                version = ProtocolVersion,
                requestId = safeRequestId,
                status = "available",
                source = "google-chrome",
                sourceRevision = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                bookmarks = snapshot.Bookmarks.Select(bookmark => new
                {
                    title = bookmark.Title,
                    url = bookmark.Url,
                    folder = bookmark.Folder,
                }),
                discoveredCount = snapshot.DiscoveredCount,
                rejectedCount = snapshot.RejectedCount,
                truncated = snapshot.Truncated,
                message = "Chrome bookmarks are ready to merge.",
            });
        }
        catch (OperationCanceledException)
        {
            PostFailure(safeRequestId, "cancelled", "Chrome bookmark import was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            DesktopLog.Write($"Chrome bookmark import unavailable: {exception.GetType().Name}.");
            PostFailure(safeRequestId, "chrome-bookmarks-unavailable", "Chrome bookmarks are unavailable for import.");
        }
    }

    internal static ChromiumBookmarkSnapshot ParseSnapshot(ReadOnlyMemory<byte> bytes, int maximumBookmarks = MaximumBookmarks)
    {
        if (maximumBookmarks <= 0 || maximumBookmarks > MaximumBookmarks)
            throw new ArgumentOutOfRangeException(nameof(maximumBookmarks));

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumDepth + 4,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("roots", out var roots)
            || roots.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Chrome bookmark roots are missing.");

        var state = new ParseState(maximumBookmarks);
        ParseRoot(roots, "bookmark_bar", "Bookmarks bar", state);
        ParseRoot(roots, "other", "Other bookmarks", state);
        ParseRoot(roots, "synced", "Mobile bookmarks", state);
        return new ChromiumBookmarkSnapshot(state.Bookmarks, state.DiscoveredCount, state.RejectedCount, state.Truncated);
    }

    private static void ParseRoot(JsonElement roots, string propertyName, string label, ParseState state)
    {
        if (!roots.TryGetProperty(propertyName, out var root) || root.ValueKind != JsonValueKind.Object) return;
        ParseNode(root, label, 0, state);
    }

    private static void ParseNode(JsonElement node, string folder, int depth, ParseState state)
    {
        state.NodeCount++;
        if (state.NodeCount > MaximumNodes || depth > MaximumDepth)
            throw new InvalidDataException("Chrome bookmark data exceeds the supported structure boundary.");
        if (!node.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            state.RejectedCount++;
            return;
        }

        var type = typeElement.GetString();
        if (string.Equals(type, "url", StringComparison.Ordinal))
        {
            state.DiscoveredCount++;
            if (!node.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String
                || !BrowserSurfaceBridge.TryNormalizeAddress(urlElement.GetString(), out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !state.SeenUrls.Add(uri.AbsoluteUri))
            {
                state.RejectedCount++;
                return;
            }
            if (state.Bookmarks.Count >= state.MaximumBookmarks)
            {
                state.Truncated = true;
                return;
            }

            var title = node.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? BoundedText(nameElement.GetString(), 256)
                : string.Empty;
            if (title.Length == 0) title = uri.IdnHost;
            state.Bookmarks.Add(new ChromiumBookmark(title, uri.AbsoluteUri, BoundedText(folder, 512)));
            return;
        }

        if (!string.Equals(type, "folder", StringComparison.Ordinal)
            || !node.TryGetProperty("children", out var children)
            || children.ValueKind != JsonValueKind.Array)
        {
            state.RejectedCount++;
            return;
        }

        var folderName = node.TryGetProperty("name", out var folderElement) && folderElement.ValueKind == JsonValueKind.String
            ? BoundedText(folderElement.GetString(), 128)
            : string.Empty;
        var childFolder = folderName.Length == 0 || string.Equals(folderName, folder, StringComparison.Ordinal)
            ? folder
            : BoundedText($"{folder} / {folderName}", 512);
        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind == JsonValueKind.Object) ParseNode(child, childFolder, depth + 1, state);
            else state.RejectedCount++;
        }
    }

    private static void EnsureOrdinaryLocalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Chrome bookmarks are unavailable.");
        var file = new FileInfo(path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Chrome bookmark data cannot be read through a reparse point.");
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Chrome bookmark data cannot be read through a reparse point.");
            if (string.Equals(directory.FullName, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase))
                return;
        }
        throw new InvalidDataException("Chrome bookmark data is outside the current user's local application data.");
    }

    private void PostFailure(string requestId, string reason, string message) => _postMessage(new
    {
        type = "browser.bookmarks.snapshot",
        version = ProtocolVersion,
        requestId,
        status = reason == "cancelled" ? "cancelled" : "unavailable",
        source = "google-chrome",
        sourceRevision = string.Empty,
        bookmarks = Array.Empty<object>(),
        discoveredCount = 0,
        rejectedCount = 0,
        truncated = false,
        reason,
        message,
    });

    private static string DefaultBookmarkPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Google", "Chrome", "User Data", "Default", "Bookmarks");

    private static string BoundedToken(string? value, int maximum)
    {
        var bounded = BoundedText(value, maximum);
        return bounded.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? bounded : string.Empty;
    }

    private static string BoundedText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Normalize(NormalizationForm.FormKC).Trim();
        if (text.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
            return string.Empty;
        return text.Length <= maximum ? text : text[..maximum];
    }

    private sealed class ParseState(int maximumBookmarks)
    {
        internal int MaximumBookmarks { get; } = maximumBookmarks;
        internal List<ChromiumBookmark> Bookmarks { get; } = [];
        internal HashSet<string> SeenUrls { get; } = new(StringComparer.Ordinal);
        internal int NodeCount { get; set; }
        internal int DiscoveredCount { get; set; }
        internal int RejectedCount { get; set; }
        internal bool Truncated { get; set; }
    }
}

internal sealed record ChromiumBookmark(string Title, string Url, string Folder);
internal sealed record ChromiumBookmarkSnapshot(
    IReadOnlyList<ChromiumBookmark> Bookmarks,
    int DiscoveredCount,
    int RejectedCount,
    bool Truncated);
