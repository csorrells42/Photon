using System.Collections.Concurrent;
using System.Text.Json;
using HermesDeveloperServices;

namespace HermesRoslynLanguageServer;

/// <summary>
/// Owns the bounded provider-side LSP surface. It exposes LSP values only and never Roslyn
/// workspace, compilation, syntax-tree, semantic-model, MEF, or assembly-catalog objects.
/// </summary>
public sealed class RoslynLanguageSession : IAsyncDisposable
{
    public const int MaximumDocumentCharacters = 4 * 1024 * 1024;
    public const int MaximumOpenDocuments = 256;
    public const int MaximumDiagnostics = 2_000;
    public const int MaximumResultCharacters = 1024 * 1024;
    private const int MaximumPendingRequests = 128;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IRoslynServerTransport _transport;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<LspIncomingResponse>> _pending = new();
    private readonly Dictionary<string, DocumentState> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _diagnosticPulls = new(StringComparer.Ordinal);
    private readonly object _documentGate = new();
    private readonly object _diagnosticGate = new();
    private readonly object _solutionGate = new();
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _pumpTask;
    private TaskCompletionSource<bool>? _solutionInitialization;
    private string? _solutionPath;
    private string? _workspaceRoot;
    private LspSessionState _state = LspSessionState.Created;
    private int _nextId;
    private bool _disposed;

    internal RoslynLanguageSession(IRoslynServerTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;

    public LspSessionState State
    {
        get { lock (_stateGate) return _state; }
    }

    public RoslynNegotiatedCapabilities? Capabilities { get; private set; }

    public string BoundedStandardError => _transport.StandardError;

    public long DroppedStandardErrorCharacters => _transport.DroppedStandardErrorCharacters;

    public async Task<RoslynNegotiatedCapabilities> InitializeAsync(
        string workspaceRootUri,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireState(LspSessionState.Created);
        ValidateFileUri(workspaceRootUri);
        SetState(LspSessionState.Initializing);
        _pumpTask = PumpAsync(_lifetime.Token);

        try
        {
            var response = await RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                clientInfo = new { name = "Hermes Workbench", version = "1" },
                rootUri = workspaceRootUri,
                capabilities = new
                {
                    workspace = new { workspaceFolders = true, configuration = false },
                    textDocument = new
                    {
                        synchronization = new { dynamicRegistration = false, willSave = false, didSave = true },
                        completion = new { dynamicRegistration = false },
                        hover = new { dynamicRegistration = false },
                        definition = new { dynamicRegistration = false },
                        references = new { dynamicRegistration = false },
                        rename = new { dynamicRegistration = false, prepareSupport = false },
                        codeAction = new { dynamicRegistration = false, resolveSupport = new { properties = Array.Empty<string>() } },
                        diagnostic = new { dynamicRegistration = false, relatedDocumentSupport = false },
                        publishDiagnostics = new { relatedInformation = false, versionSupport = true },
                    },
                },
                workspaceFolders = new[] { new { uri = workspaceRootUri, name = "Hermes workspace" } },
            }, cancellationToken).ConfigureAwait(false);

            var capabilities = ParseCapabilities(response);
            if (!capabilities.SupportsRequiredSurface)
                throw new LspSessionException("The Roslyn server did not negotiate the required bounded language surface.");
            Capabilities = capabilities;
            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            _workspaceRoot = Path.GetFullPath(new Uri(workspaceRootUri).LocalPath);
            SetState(LspSessionState.Ready);
            return capabilities;
        }
        catch
        {
            SetState(LspSessionState.Faulted);
            throw;
        }
    }

    public async Task OpenSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        RequireReady();
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = ValidateSolutionPath(solutionPath);
        Task initialization;
        var send = false;
        lock (_solutionGate)
        {
            if (_solutionPath is null)
            {
                _solutionPath = resolved;
                _solutionInitialization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                send = true;
            }
            else if (!_solutionPath.Equals(resolved, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Roslyn session already owns a different solution.");
            }

            initialization = _solutionInitialization!.Task;
        }

        if (send)
        {
            try
            {
                await SendNotificationAsync("solution/open", new
                {
                    solution = new Uri(resolved).AbsoluteUri,
                }, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_solutionGate)
                {
                    if (_solutionPath?.Equals(resolved, StringComparison.OrdinalIgnoreCase) == true
                        && _solutionInitialization?.Task == initialization)
                        _solutionInitialization.TrySetException(exception);
                }
                try { await initialization.ConfigureAwait(false); }
                catch { }
                throw;
            }
        }

        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task OpenDocumentAsync(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        ValidateDocumentUri(uri);
        ValidateVersion(version);
        ValidateText(text);
        lock (_documentGate)
        {
            if (_documents.ContainsKey(uri)) throw new InvalidOperationException("The Roslyn document is already open.");
            if (_documents.Count >= MaximumOpenDocuments) throw new InvalidOperationException("The Roslyn open-document limit was reached.");
            _documents.Add(uri, Snapshot(version, text));
        }

        try
        {
            await SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new LspTextDocumentItem(uri, "csharp", version, text),
            }, cancellationToken).ConfigureAwait(false);
            QueueDiagnosticsPull(uri, version);
        }
        catch
        {
            lock (_documentGate) _documents.Remove(uri);
            throw;
        }
    }

    public async Task ChangeDocumentAsync(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        ValidateDocumentUri(uri);
        ValidateVersion(version);
        ValidateText(text);
        DocumentState previous;
        var current = Snapshot(version, text);
        lock (_documentGate)
        {
            if (!_documents.TryGetValue(uri, out previous)) throw new InvalidOperationException("The Roslyn document is not open.");
            if (version <= previous.Version) throw new RoslynStaleDocumentException(uri, previous.Version, version);
            _documents[uri] = current;
        }

        try
        {
            await SendNotificationAsync("textDocument/didChange", new
            {
                textDocument = new LspVersionedTextDocumentIdentifier(uri, version),
                contentChanges = new[]
                {
                    new
                    {
                        range = new LspRange(new LspPosition(0, 0), previous.End),
                        rangeLength = previous.Length,
                        text,
                    },
                },
            }, cancellationToken).ConfigureAwait(false);
            QueueDiagnosticsPull(uri, version);
        }
        catch
        {
            lock (_documentGate)
            {
                if (_documents.TryGetValue(uri, out var retained) && retained.Version == version)
                    _documents[uri] = previous;
            }
            throw;
        }
    }

    public async Task CloseDocumentAsync(string uri, CancellationToken cancellationToken = default)
    {
        RequireReady();
        ValidateOpenDocument(uri, expectedVersion: null);
        await SendNotificationAsync("textDocument/didClose", new
        {
            textDocument = new LspTextDocumentIdentifier(uri),
        }, cancellationToken).ConfigureAwait(false);
        lock (_documentGate) _documents.Remove(uri);
        CancelDiagnosticsPull(uri);
    }

    public Task<RoslynLanguageResult?> CompletionAsync(
        string uri,
        int documentVersion,
        LspPosition position,
        CancellationToken cancellationToken = default)
    {
        RequireDocumentVersion(uri, documentVersion);
        ValidatePosition(position);
        return RequestResultAsync("textDocument/completion", new
        {
            textDocument = new LspTextDocumentIdentifier(uri),
            position,
            context = new { triggerKind = 1 },
        }, cancellationToken);
    }

    public Task<RoslynLanguageResult?> HoverAsync(string uri, int documentVersion, LspPosition position, CancellationToken cancellationToken = default) =>
        DocumentPositionRequestAsync("textDocument/hover", uri, documentVersion, position, cancellationToken);

    public Task<RoslynLanguageResult?> DefinitionAsync(string uri, int documentVersion, LspPosition position, CancellationToken cancellationToken = default) =>
        DocumentPositionRequestAsync("textDocument/definition", uri, documentVersion, position, cancellationToken);

    public Task<RoslynLanguageResult?> ReferencesAsync(
        string uri,
        int documentVersion,
        LspPosition position,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default)
    {
        RequireDocumentVersion(uri, documentVersion);
        ValidatePosition(position);
        return RequestResultAsync("textDocument/references", new
        {
            textDocument = new LspTextDocumentIdentifier(uri),
            position,
            context = new { includeDeclaration },
        }, cancellationToken);
    }

    public Task<RoslynLanguageResult?> RenameAsync(
        string uri,
        int documentVersion,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken = default)
    {
        RequireDocumentVersion(uri, documentVersion);
        ValidatePosition(position);
        if (string.IsNullOrWhiteSpace(newName) || newName.Length > 512 || newName.Contains('\0'))
            throw new ArgumentException("The Roslyn rename target is invalid.", nameof(newName));
        return RequestResultAsync("textDocument/rename", new
        {
            textDocument = new LspTextDocumentIdentifier(uri), position, newName,
        }, cancellationToken);
    }

    public Task<RoslynLanguageResult?> CodeActionsAsync(
        string uri,
        int documentVersion,
        LspRange range,
        IReadOnlyList<LspDiagnostic>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        RequireDocumentVersion(uri, documentVersion);
        ValidateRange(range);
        var boundedDiagnostics = (diagnostics ?? Array.Empty<LspDiagnostic>()).Take(MaximumDiagnostics).ToArray();
        return RequestResultAsync("textDocument/codeAction", new
        {
            textDocument = new LspTextDocumentIdentifier(uri),
            range,
            context = new { diagnostics = boundedDiagnostics },
        }, cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State is LspSessionState.Exited) return;
        RequireState(LspSessionState.Ready, LspSessionState.Faulted);
        SetState(LspSessionState.ShuttingDown);
        try
        {
            if (!_transport.HasExited) await RequestAsync("shutdown", null, cancellationToken).ConfigureAwait(false);
            if (!_transport.HasExited) await SendNotificationAsync("exit", null, cancellationToken).ConfigureAwait(false);
            SetState(LspSessionState.Exited);
        }
        catch
        {
            SetState(LspSessionState.Faulted);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            if (State is LspSessionState.Ready)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ShutdownAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or LspProtocolException or LspSessionException)
        {
        }

        _disposed = true;
        _lifetime.Cancel();
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        FailPending(new ObjectDisposedException(nameof(RoslynLanguageSession)));
        lock (_solutionGate)
        {
            _solutionInitialization?.TrySetException(new ObjectDisposedException(nameof(RoslynLanguageSession)));
            _solutionInitialization = null;
            _solutionPath = null;
        }
        lock (_documentGate) _documents.Clear();
        lock (_diagnosticGate)
        {
            foreach (var cancellation in _diagnosticPulls.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
            _diagnosticPulls.Clear();
        }
        _lifetime.Dispose();
    }

    private Task<RoslynLanguageResult?> DocumentPositionRequestAsync(
        string method,
        string uri,
        int documentVersion,
        LspPosition position,
        CancellationToken cancellationToken)
    {
        RequireDocumentVersion(uri, documentVersion);
        ValidatePosition(position);
        return RequestResultAsync(method, new LspTextDocumentPositionParams(
            new LspTextDocumentIdentifier(uri), position), cancellationToken);
    }

    private async Task<RoslynLanguageResult?> RequestResultAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        if (response is null || response.Value.ValueKind is JsonValueKind.Null) return null;
        var raw = response.Value.GetRawText();
        if (raw.Length > MaximumResultCharacters) throw new LspProtocolException("The Roslyn language result exceeds the configured limit.");
        return new RoslynLanguageResult(response.Value.Clone());
    }

    private async Task PullDiagnosticsAsync(string uri, int version, CancellationToken cancellationToken)
    {
        var response = await RequestAsync("textDocument/diagnostic", new
        {
            textDocument = new LspTextDocumentIdentifier(uri),
        }, cancellationToken).ConfigureAwait(false);
        if (response is not { ValueKind: JsonValueKind.Object } report
            || !report.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new LspProtocolException("The Roslyn diagnostic report is malformed.");
        if (kind.GetString() == "unchanged") return;
        if (kind.GetString() != "full"
            || !report.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            throw new LspProtocolException("The Roslyn diagnostic report is malformed.");
        var diagnostics = items.Deserialize<LspDiagnostic[]>(JsonOptions)
            ?? throw new LspProtocolException("The Roslyn diagnostic report is empty.");
        PublishDiagnostics(new LspPublishDiagnosticsParams(uri, diagnostics, version));
    }

    private void QueueDiagnosticsPull(string uri, int version)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        lock (_diagnosticGate)
        {
            if (_diagnosticPulls.Remove(uri, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }
            _diagnosticPulls.Add(uri, cancellation);
        }
        _ = PullDiagnosticsSafelyAsync(uri, version, cancellation);
    }

    private async Task PullDiagnosticsSafelyAsync(string uri, int version, CancellationTokenSource cancellation)
    {
        try
        {
            await PullDiagnosticsAsync(uri, version, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or LspProtocolException
                                          or LspSessionException or InvalidOperationException
                                          or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_diagnosticGate)
            {
                if (_diagnosticPulls.TryGetValue(uri, out var retained) && ReferenceEquals(retained, cancellation))
                    _diagnosticPulls.Remove(uri);
            }
            cancellation.Dispose();
        }
    }

    private void CancelDiagnosticsPull(string uri)
    {
        lock (_diagnosticGate)
        {
            if (!_diagnosticPulls.Remove(uri, out var cancellation)) return;
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private async Task<JsonElement?> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_pending.Count >= MaximumPendingRequests) throw new LspSessionException("The Roslyn pending-request limit was reached.");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<LspIncomingResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new LspSessionException("The Roslyn request identifier collided.");
        try
        {
            await _transport.SendAsync(new LspRequest(id, method, Serialize(parameters)), cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response.Error is not null) throw new LspSessionException($"The Roslyn server rejected '{method}' ({response.Error.Code}).");
            return response.Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_pending.TryRemove(id, out _))
            {
                try
                {
                    await _transport.SendAsync(new LspNotification("$/cancelRequest", Serialize(new { id })), _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or LspSessionException)
                {
                }
            }
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private ValueTask SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        _transport.SendAsync(new LspNotification(method, Serialize(parameters)), cancellationToken);

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _transport.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (message)
                {
                    case LspIncomingResponse response when _pending.TryRemove(response.Id, out var completion):
                        completion.TrySetResult(response);
                        break;
                    case LspIncomingNotification notification:
                        HandleNotification(notification);
                        break;
                    case LspIncomingServerRequest request:
                        await _transport.SendAsync(new LspClientResponse(
                            request.Id,
                            Error: new LspError(-32601, "Hermes does not expose arbitrary Roslyn server requests.")), cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            if (!_disposed && State is not LspSessionState.Exited) SetState(LspSessionState.Exited);
            var exception = new LspSessionException("The Roslyn language server exited.");
            FailPending(exception);
            FailSolutionInitialization(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed) SetState(LspSessionState.Faulted);
            var failure = new LspSessionException($"The Roslyn transport failed ({exception.GetType().Name}).");
            FailPending(failure);
            FailSolutionInitialization(failure);
        }
    }

    private void HandleNotification(LspIncomingNotification notification)
    {
        if (notification.Method == "workspace/projectInitializationComplete")
        {
            lock (_solutionGate) _solutionInitialization?.TrySetResult(true);
            return;
        }

        if (notification.Method != "textDocument/publishDiagnostics" || notification.Params is not { } parameters) return;
        try
        {
            var published = parameters.Deserialize<LspPublishDiagnosticsParams>(JsonOptions);
            if (published is null || !IsCurrentDiagnosticVersion(published.Uri, published.Version)) return;
            PublishDiagnostics(published);
        }
        catch (JsonException)
        {
        }
    }

    private void PublishDiagnostics(LspPublishDiagnosticsParams published) =>
        DiagnosticsPublished?.Invoke(published with
        {
            Diagnostics = published.Diagnostics.Take(MaximumDiagnostics).Select(diagnostic => diagnostic with
            {
                Source = Limit(diagnostic.Source, 128),
                Message = Limit(diagnostic.Message, 16_384) ?? string.Empty,
            }).ToArray(),
        });

    private RoslynNegotiatedCapabilities ParseCapabilities(JsonElement? initializeResult)
    {
        if (initializeResult is not { ValueKind: JsonValueKind.Object } result
            || !result.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind is not JsonValueKind.Object)
            throw new LspProtocolException("The Roslyn initialize result has no capabilities object.");
        return new RoslynNegotiatedCapabilities(
            Supported(capabilities, "textDocumentSync"),
            Diagnostics: true,
            Supported(capabilities, "completionProvider"),
            Supported(capabilities, "hoverProvider"),
            Supported(capabilities, "definitionProvider"),
            Supported(capabilities, "referencesProvider"),
            Supported(capabilities, "renameProvider"),
            Supported(capabilities, "codeActionProvider"));
    }

    private static bool Supported(JsonElement capabilities, string property) =>
        capabilities.TryGetProperty(property, out var value)
        && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.False and not JsonValueKind.Undefined;

    private void RequireDocumentVersion(string uri, int version)
    {
        RequireReady();
        ValidateVersion(version);
        ValidateOpenDocument(uri, version);
    }

    private void ValidateOpenDocument(string uri, int? expectedVersion)
    {
        ValidateDocumentUri(uri);
        lock (_documentGate)
        {
            if (!_documents.TryGetValue(uri, out var current)) throw new InvalidOperationException("The Roslyn document is not open.");
            if (expectedVersion is not null && expectedVersion.Value != current.Version)
                throw new RoslynStaleDocumentException(uri, current.Version, expectedVersion.Value);
        }
    }

    private bool IsCurrentDiagnosticVersion(string uri, int? version)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return false;
        lock (_documentGate)
        {
            return _documents.TryGetValue(uri, out var current) && (version is null || version.Value == current.Version);
        }
    }

    private static DocumentState Snapshot(int version, string text)
    {
        var lastLineBreak = text.LastIndexOf('\n');
        var line = 0;
        foreach (var character in text)
            if (character == '\n') line++;
        return new DocumentState(
            version,
            new LspPosition(line, lastLineBreak < 0 ? text.Length : text.Length - lastLineBreak - 1),
            text.Length);
    }

    private static JsonElement? Serialize(object? value) => value is null ? null : JsonSerializer.SerializeToElement(value, JsonOptions);

    private static void ValidateFileUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 32_767
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            throw new ArgumentException("The Roslyn document URI must be an absolute file URI.", nameof(uri));
    }

    private void ValidateDocumentUri(string uri)
    {
        ValidateFileUri(uri);
        var path = Path.GetFullPath(new Uri(uri).LocalPath);
        ValidateWorkspacePath(path, "The Roslyn document is outside the active workspace.");
    }

    private string ValidateSolutionPath(string solutionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        if (!Path.IsPathFullyQualified(solutionPath) || solutionPath.Length > 32_767 || solutionPath.Contains('\0'))
            throw new ArgumentException("The Roslyn solution path must be a bounded absolute path.", nameof(solutionPath));
        var resolved = Path.GetFullPath(solutionPath);
        var extension = Path.GetExtension(resolved);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Roslyn workspace must be a .sln, .slnx, or .csproj file.", nameof(solutionPath));
        ValidateWorkspacePath(resolved, "The Roslyn workspace is outside the active workspace.");
        var file = new FileInfo(resolved);
        if (!file.Exists)
            throw new FileNotFoundException("The Roslyn workspace is unavailable.", resolved);
        return resolved;
    }

    private void ValidateWorkspacePath(string resolved, string outsideMessage)
    {
        var root = _workspaceRoot ?? throw new InvalidOperationException("The Roslyn workspace is unavailable.");
        var relative = Path.GetRelativePath(root, resolved);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new UnauthorizedAccessException(outsideMessage);

        var current = root;
        foreach (var component in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The Roslyn workspace path traverses a reparse point.");
        }
    }

    private static void ValidateVersion(int version)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
    }

    private static void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumDocumentCharacters) throw new ArgumentOutOfRangeException(nameof(text));
    }

    private static void ValidatePosition(LspPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Line < 0 || position.Character < 0) throw new ArgumentOutOfRangeException(nameof(position));
    }

    private static void ValidateRange(LspRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        ValidatePosition(range.Start);
        ValidatePosition(range.End);
    }

    private void RequireReady()
    {
        ThrowIfDisposed();
        RequireState(LspSessionState.Ready);
    }

    private void RequireState(params LspSessionState[] expected)
    {
        var state = State;
        if (!expected.Contains(state)) throw new InvalidOperationException($"The Roslyn session is {state}, not {string.Join(" or ", expected)}.");
    }

    private void SetState(LspSessionState state)
    {
        lock (_stateGate) _state = state;
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(exception);
    }

    private void FailSolutionInitialization(Exception exception)
    {
        lock (_solutionGate) _solutionInitialization?.TrySetException(exception);
    }

    private static string? Limit(string? value, int maximum) => value is null ? null : value[..Math.Min(value.Length, maximum)];

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct DocumentState(int Version, LspPosition End, int Length);
}

public sealed class RoslynStaleDocumentException : Exception
{
    internal RoslynStaleDocumentException(string uri, int currentVersion, int requestedVersion)
        : base($"The Roslyn document request is stale (current {currentVersion}, requested {requestedVersion}).")
    {
        Uri = uri;
        CurrentVersion = currentVersion;
        RequestedVersion = requestedVersion;
    }

    public string Uri { get; }

    public int CurrentVersion { get; }

    public int RequestedVersion { get; }
}
