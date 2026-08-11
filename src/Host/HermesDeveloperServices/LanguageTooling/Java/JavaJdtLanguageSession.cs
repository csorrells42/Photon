using System.Text.Json;

namespace HermesDeveloperServices.LanguageTooling.Java;

public sealed record JavaJdtNegotiatedCapabilities(
    bool DocumentSynchronization,
    bool Diagnostics,
    bool Completion,
    bool Hover,
    bool Definition,
    bool References,
    bool Rename)
{
    public bool SupportsRequiredSurface =>
        DocumentSynchronization && Diagnostics && Completion && Hover && Definition && References && Rename;
}

public sealed record JavaJdtLanguageResult(JsonElement Value);

/// <summary>
/// Typed, bounded Java view over the provider-neutral LSP session. It accepts only file URIs,
/// the Java language id, monotonic document versions, and a fixed language-operation surface.
/// </summary>
public sealed class JavaJdtLanguageSession : IAsyncDisposable
{
    public const int MaximumResultCharacters = 2 * 1024 * 1024;

    private readonly LspSession _inner;
    private readonly Dictionary<string, int> _documentVersions = new(StringComparer.Ordinal);
    private readonly object _documentGate = new();
    private bool _disposed;

    internal JavaJdtLanguageSession(ILspMessageTransport transport)
    {
        _inner = new LspSession(transport ?? throw new ArgumentNullException(nameof(transport)));
        _inner.DiagnosticsPublished += HandleDiagnostics;
    }

    public event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;

    public LspSessionState State => _inner.State;

    public JavaJdtNegotiatedCapabilities? Capabilities { get; private set; }

    public async Task<JavaJdtNegotiatedCapabilities> InitializeAsync(
        string workspaceRootUri,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var initialized = await _inner.InitializeAsync(workspaceRootUri, JsonSerializer.SerializeToElement(new
        {
            workspace = new { workspaceFolders = false, configuration = false },
            textDocument = new
            {
                synchronization = new { dynamicRegistration = false, willSave = false, didSave = true },
                completion = new { dynamicRegistration = false },
                hover = new { dynamicRegistration = false },
                definition = new { dynamicRegistration = false },
                references = new { dynamicRegistration = false },
                rename = new { dynamicRegistration = false, prepareSupport = false },
                publishDiagnostics = new { relatedInformation = false, versionSupport = true },
            },
        }), cancellationToken).ConfigureAwait(false);

        var capabilities = ParseCapabilities(initialized.Capabilities);
        if (!capabilities.SupportsRequiredSurface)
            throw new LspSessionException("The Eclipse JDT server did not negotiate the required bounded Java surface.");
        Capabilities = capabilities;
        return capabilities;
    }

    public async Task OpenDocumentAsync(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(uri, version, text);
        lock (_documentGate)
        {
            if (_documentVersions.ContainsKey(uri)) throw new InvalidOperationException("The Java document is already open.");
            _documentVersions.Add(uri, version);
        }
        try
        {
            await _inner.OpenDocumentAsync(uri, "java", version, text, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_documentGate) _documentVersions.Remove(uri);
            throw;
        }
    }

    public async Task ChangeDocumentAsync(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(uri, version, text);
        int previous;
        lock (_documentGate)
        {
            if (!_documentVersions.TryGetValue(uri, out previous)) throw new InvalidOperationException("The Java document is not open.");
            if (version <= previous) throw new JavaJdtStaleDocumentException(uri, previous, version);
            _documentVersions[uri] = version;
        }
        try
        {
            await _inner.ChangeDocumentAsync(uri, version, text, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_documentGate)
            {
                if (_documentVersions.GetValueOrDefault(uri) == version) _documentVersions[uri] = previous;
            }
            throw;
        }
    }

    public async Task CloseDocumentAsync(string uri, CancellationToken cancellationToken = default)
    {
        RequireDocumentVersion(uri, expectedVersion: null);
        await _inner.CloseDocumentAsync(uri, cancellationToken).ConfigureAwait(false);
        lock (_documentGate) _documentVersions.Remove(uri);
    }

    public Task<JavaJdtLanguageResult?> CompletionAsync(string uri, int documentVersion, LspPosition position, CancellationToken cancellationToken = default) =>
        PositionRequestAsync(_inner.CompletionAsync, uri, documentVersion, position, cancellationToken);

    public Task<JavaJdtLanguageResult?> HoverAsync(string uri, int documentVersion, LspPosition position, CancellationToken cancellationToken = default) =>
        PositionRequestAsync(_inner.HoverAsync, uri, documentVersion, position, cancellationToken);

    public Task<JavaJdtLanguageResult?> DefinitionAsync(string uri, int documentVersion, LspPosition position, CancellationToken cancellationToken = default) =>
        PositionRequestAsync(_inner.DefinitionAsync, uri, documentVersion, position, cancellationToken);

    public async Task<JavaJdtLanguageResult?> ReferencesAsync(
        string uri,
        int documentVersion,
        LspPosition position,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default)
    {
        RequireDocumentVersion(uri, documentVersion);
        return Bound(await _inner.ReferencesAsync(uri, position, includeDeclaration, cancellationToken).ConfigureAwait(false));
    }

    public async Task<JavaJdtLanguageResult?> RenameAsync(
        string uri,
        int documentVersion,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken = default)
    {
        RequireDocumentVersion(uri, documentVersion);
        if (!IsJavaIdentifier(newName)) throw new ArgumentException("The Java rename target is invalid.", nameof(newName));
        return Bound(await _inner.RenameAsync(uri, position, newName, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.DiagnosticsPublished -= HandleDiagnostics;
        await _inner.DisposeAsync().ConfigureAwait(false);
        lock (_documentGate) _documentVersions.Clear();
    }

    private async Task<JavaJdtLanguageResult?> PositionRequestAsync(
        Func<string, LspPosition, CancellationToken, Task<JsonElement?>> operation,
        string uri,
        int documentVersion,
        LspPosition position,
        CancellationToken cancellationToken)
    {
        RequireDocumentVersion(uri, documentVersion);
        return Bound(await operation(uri, position, cancellationToken).ConfigureAwait(false));
    }

    private void HandleDiagnostics(LspPublishDiagnosticsParams published)
    {
        lock (_documentGate)
        {
            if (!_documentVersions.TryGetValue(published.Uri, out var current)
                || (published.Version is not null && published.Version.Value != current)) return;
        }
        DiagnosticsPublished?.Invoke(published with
        {
            Diagnostics = published.Diagnostics.Take(LspSession.MaximumDiagnostics).ToArray(),
        });
    }

    private static JavaJdtNegotiatedCapabilities ParseCapabilities(JsonElement capabilities) => new(
        Supported(capabilities, "textDocumentSync"),
        Diagnostics: true,
        Supported(capabilities, "completionProvider"),
        Supported(capabilities, "hoverProvider"),
        Supported(capabilities, "definitionProvider"),
        Supported(capabilities, "referencesProvider"),
        Supported(capabilities, "renameProvider"));

    private static bool Supported(JsonElement capabilities, string property) =>
        capabilities.ValueKind == JsonValueKind.Object
        && capabilities.TryGetProperty(property, out var value)
        && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.False and not JsonValueKind.Undefined;

    private static JavaJdtLanguageResult? Bound(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (value.Value.GetRawText().Length > MaximumResultCharacters)
            throw new LspProtocolException("The Eclipse JDT language result exceeds the configured limit.");
        return new JavaJdtLanguageResult(value.Value.Clone());
    }

    private void RequireDocumentVersion(string uri, int? expectedVersion)
    {
        ThrowIfDisposed();
        ValidateFileUri(uri);
        lock (_documentGate)
        {
            if (!_documentVersions.TryGetValue(uri, out var current)) throw new InvalidOperationException("The Java document is not open.");
            if (expectedVersion is not null && current != expectedVersion.Value)
                throw new JavaJdtStaleDocumentException(uri, current, expectedVersion.Value);
        }
    }

    private static void ValidateDocument(string uri, int version, string text)
    {
        ValidateFileUri(uri);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > LspSession.MaximumDocumentCharacters || text.Contains('\0'))
            throw new ArgumentOutOfRangeException(nameof(text));
    }

    private static void ValidateFileUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            throw new ArgumentException("The Java document URI must be an absolute file URI.", nameof(uri));
    }

    private static bool IsJavaIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || !IsJavaIdentifierStart(value[0])) return false;
        return value.Skip(1).All(character => IsJavaIdentifierStart(character) || char.IsAsciiDigit(character));
    }

    private static bool IsJavaIdentifierStart(char character) =>
        char.IsAsciiLetter(character) || character is '_' or '$';

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class JavaJdtStaleDocumentException : Exception
{
    internal JavaJdtStaleDocumentException(string uri, int currentVersion, int requestedVersion)
        : base($"The Java document request is stale (current {currentVersion}, requested {requestedVersion}).")
    {
        Uri = uri;
        CurrentVersion = currentVersion;
        RequestedVersion = requestedVersion;
    }

    public string Uri { get; }
    public int CurrentVersion { get; }
    public int RequestedVersion { get; }
}
