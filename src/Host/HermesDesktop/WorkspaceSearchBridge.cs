using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace HermesDesktop;

internal sealed class WorkspaceSearchBridge : IAsyncDisposable
{
    internal const int ProtocolVersion = 1;
    internal const int MaximumResults = 200;
    internal const int MaximumResultsPerFile = 25;
    internal const int MaximumPreviewCharacters = 1_024;
    internal const int MaximumQueryCharacters = 512;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    private readonly WorkspaceSearchPathPolicy _pathPolicy;
    private readonly SerenaWorkspaceSearchClient _serena;
    private readonly Action<object> _postMessage;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private bool _disposed;

    internal WorkspaceSearchBridge(
        string workspaceRoot,
        Action<object> postMessage,
        SerenaWorkspaceSearchClient? serena = null)
    {
        _pathPolicy = new WorkspaceSearchPathPolicy(workspaceRoot);
        _postMessage = postMessage;
        _serena = serena ?? new SerenaWorkspaceSearchClient();
    }

    internal async Task SearchLiteralAsync(
        int version,
        string? requestId,
        string? query,
        int maximumResults,
        int maximumResultsPerFile,
        int maximumPreviewCharacters)
    {
        if (!TryValidateSearchEnvelope(version, requestId, query, maximumResults, maximumResultsPerFile, maximumPreviewCharacters,
                out var id, out var normalizedQuery, out var limits)) return;
        if (!TryBegin(id, out var operation)) return;
        try
        {
            operation.CancelAfter(OperationTimeout);
            var output = await Task.Run(
                () => SearchLiteralCoreAsync(id, normalizedQuery, limits, operation.Token),
                operation.Token).ConfigureAwait(false);
            PostResult("literal", id, output.Results, output.Truncated);
        }
        catch (OperationCanceledException)
        {
            PostError(id, "cancelled", "Workspace search was cancelled.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PostError(id, "search_failed", "Workspace search could not safely read the workspace.", true);
        }
        finally { Complete(id, operation); }
    }

    internal async Task SearchSemanticAsync(
        int version,
        string? requestId,
        string? intent,
        int maximumResults,
        int maximumResultsPerFile,
        int maximumPreviewCharacters)
    {
        if (!TryValidateSearchEnvelope(version, requestId, intent, maximumResults, maximumResultsPerFile, maximumPreviewCharacters,
                out var id, out var normalizedIntent, out var limits)) return;
        if (!TryBegin(id, out var operation)) return;
        try
        {
            operation.CancelAfter(OperationTimeout);
            var results = await _serena.SearchAsync(
                normalizedIntent,
                limits.MaximumResults,
                limits.MaximumResultsPerFile,
                limits.MaximumPreviewCharacters,
                _pathPolicy,
                operation.Token).ConfigureAwait(false);
            PostResult("semantic", id, results, results.Count >= limits.MaximumResults);
        }
        catch (OperationCanceledException)
        {
            PostError(id, "cancelled", "Serena search was cancelled.", true);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException)
        {
            PostError(id, "serena_unavailable", "The trusted Serena search provider is unavailable.", true);
        }
        finally { Complete(id, operation); }
    }

    internal void Cancel(int version, string? requestId, string? targetRequestId)
    {
        if (!TryValidateRequestId(requestId, out var id))
        {
            PostError(string.Empty, "invalid_request_id", "The workspace-search request identifier is invalid.", false);
            return;
        }
        if (version != ProtocolVersion || !TryValidateRequestId(targetRequestId, out var target))
        {
            PostError(id, "invalid_cancel", "The workspace-search cancellation request is invalid.", false);
            return;
        }
        CancellationTokenSource? operation;
        lock (_gate) _active.TryGetValue(target, out operation);
        operation?.Cancel();
        _postMessage(new
        {
            type = "workspaceSearch.cancel.result",
            version = ProtocolVersion,
            requestId = id,
            targetRequestId = target,
            accepted = operation is not null,
        });
    }

    private async Task<(IReadOnlyList<WorkspaceSearchNativeResult> Results, bool Truncated)> SearchLiteralCoreAsync(
        string requestId,
        string query,
        SearchLimits limits,
        CancellationToken cancellationToken)
    {
        var enumeration = _pathPolicy.EnumerateRegularFiles(cancellationToken);
        PostProgress(requestId, 0, enumeration.Files.Count);
        var results = new List<WorkspaceSearchNativeResult>();
        var truncated = enumeration.Truncated;
        long readBytes = 0;
        var completedFiles = 0;

        foreach (var file in enumeration.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readBytes + file.Length > WorkspaceSearchPathPolicy.MaximumReadBytes)
            {
                truncated = true;
                break;
            }
            readBytes += file.Length;
            var content = await _pathPolicy.ReadUtf8TextAsync(file, cancellationToken).ConfigureAwait(false);
            completedFiles++;
            if (completedFiles % 32 == 0 || completedFiles == enumeration.Files.Count)
            {
                PostProgress(requestId, completedFiles, enumeration.Files.Count);
            }
            if (content is null) continue;

            var perFile = 0;
            using var reader = new StringReader(content);
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                var indexes = MatchIndexes(line, query, maximum: 32);
                if (indexes.Count == 0) continue;
                if (perFile >= limits.MaximumResultsPerFile)
                {
                    truncated = true;
                    break;
                }
                if (results.Count >= limits.MaximumResults)
                {
                    truncated = true;
                    break;
                }
                var preview = CreatePreview(line, query.Length, indexes, limits.MaximumPreviewCharacters);
                results.Add(new WorkspaceSearchNativeResult(
                    file.RelativePath,
                    lineNumber,
                    indexes[0] + 1,
                    preview.Text,
                    preview.Ranges));
                perFile++;
            }
            if (results.Count >= limits.MaximumResults) break;
        }
        return (results, truncated);
    }

    private static IReadOnlyList<int> MatchIndexes(string line, string query, int maximum)
    {
        var indexes = new List<int>();
        var offset = 0;
        while (offset <= line.Length - query.Length && indexes.Count < maximum)
        {
            var index = line.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;
            indexes.Add(index);
            offset = index + Math.Max(1, query.Length);
        }
        return indexes;
    }

    private static (string Text, IReadOnlyList<WorkspaceSearchMatchRange> Ranges) CreatePreview(
        string line,
        int queryLength,
        IReadOnlyList<int> indexes,
        int maximumCharacters)
    {
        var first = indexes[0];
        var start = line.Length <= maximumCharacters ? 0 : Math.Max(0, first - maximumCharacters / 3);
        if (start + maximumCharacters > line.Length) start = Math.Max(0, line.Length - maximumCharacters);
        var length = Math.Min(maximumCharacters, line.Length - start);
        var text = WorkspaceSearchPathPolicy.SanitizePreview(line.Substring(start, length), maximumCharacters);
        var ranges = indexes
            .Where(index => index >= start && index < start + text.Length)
            .Select(index => new WorkspaceSearchMatchRange(index - start, Math.Min(text.Length, index - start + queryLength)))
            .Where(range => range.Start < range.End)
            .ToArray();
        return (text, ranges);
    }

    private bool TryValidateSearchEnvelope(
        int version,
        string? requestId,
        string? query,
        int maximumResults,
        int maximumResultsPerFile,
        int maximumPreviewCharacters,
        out string id,
        out string normalizedQuery,
        out SearchLimits limits)
    {
        id = requestId?.Trim() ?? string.Empty;
        normalizedQuery = NormalizeQuery(query);
        limits = new SearchLimits(maximumResults, maximumResultsPerFile, maximumPreviewCharacters);
        if (version != ProtocolVersion)
        {
            PostError(id, "unsupported_version", $"Workspace-search protocol {version} is not supported.", false);
            return false;
        }
        if (!TryValidateRequestId(id, out id))
        {
            PostError(string.Empty, "invalid_request_id", "The workspace-search request identifier is invalid.", false);
            return false;
        }
        if (normalizedQuery.Length == 0 || normalizedQuery.Length > MaximumQueryCharacters
            || maximumResults is < 1 or > MaximumResults
            || maximumResultsPerFile is < 1 or > MaximumResultsPerFile
            || maximumPreviewCharacters is < 1 or > MaximumPreviewCharacters)
        {
            PostError(id, "invalid_request", "The workspace-search request is outside its safe bounds.", false);
            return false;
        }
        return true;
    }

    private bool TryBegin(string requestId, out CancellationTokenSource operation)
    {
        operation = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed)
            {
                operation.Dispose();
                PostError(requestId, "unavailable", "Workspace search is unavailable.", true);
                return false;
            }
            if (_active.ContainsKey(requestId))
            {
                operation.Dispose();
                PostError(requestId, "duplicate_request", "That workspace-search request is already active.", false);
                return false;
            }
            _active.Add(requestId, operation);
            return true;
        }
    }

    private void Complete(string requestId, CancellationTokenSource operation)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(requestId, out var current) && ReferenceEquals(current, operation)) _active.Remove(requestId);
        }
        operation.Dispose();
    }

    private void PostProgress(string requestId, int completedFiles, int totalFiles) => _postMessage(new
    {
        type = "workspaceSearch.progress",
        version = ProtocolVersion,
        requestId,
        completedFiles = Math.Clamp(completedFiles, 0, WorkspaceSearchPathPolicy.MaximumEnumeratedFiles),
        totalFiles = Math.Clamp(totalFiles, 0, WorkspaceSearchPathPolicy.MaximumEnumeratedFiles),
    });

    private void PostResult(string kind, string requestId, IReadOnlyList<WorkspaceSearchNativeResult> results, bool truncated) => _postMessage(new
    {
        type = $"workspaceSearch.{kind}.result",
        version = ProtocolVersion,
        protocolVersion = ProtocolVersion,
        requestId,
        results = results.Take(MaximumResults).Select(result => new
        {
            path = result.Path,
            pathKind = "regular-file",
            line = result.Line,
            column = result.Column,
            preview = result.Preview,
            matches = result.Matches.Take(32).Select(range => new { start = range.Start, end = range.End }).ToArray(),
        }).ToArray(),
        truncated,
    });

    private void PostError(string requestId, string code, string message, bool retryable) => _postMessage(new
    {
        type = "workspaceSearch.error",
        version = ProtocolVersion,
        requestId,
        code,
        message,
        retryable,
    });

    private static string NormalizeQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(Math.Min(value.Length, MaximumQueryCharacters + 1));
        foreach (var character in value.Trim())
        {
            if (builder.Length > MaximumQueryCharacters) break;
            builder.Append(char.IsControl(character) || character is >= '\u200b' and <= '\u200f'
                || character is >= '\u202a' and <= '\u202e' || character is >= '\u2060' and <= '\u2069'
                || character == '\ufeff' ? ' ' : character);
        }
        return builder.ToString().Trim();
    }

    private static bool TryValidateRequestId(string? requestId, out string id)
    {
        id = requestId?.Trim() ?? string.Empty;
        return id.Length is > 0 and <= 128
            && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':');
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource[] active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active.Values.ToArray();
            _active.Clear();
        }
        foreach (var operation in active) operation.Cancel();
        foreach (var operation in active) operation.Dispose();
        await _serena.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record SearchLimits(int MaximumResults, int MaximumResultsPerFile, int MaximumPreviewCharacters);
}
