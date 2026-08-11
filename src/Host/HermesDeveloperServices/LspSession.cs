using System.Collections.Concurrent;
using System.Text.Json;

namespace HermesDeveloperServices;

/// <summary>
/// Coordinates one provider-neutral LSP client session over an injected, already-authorized
/// transport. It advertises no dynamic registration and does not own executable discovery,
/// process launch, listeners, or workspace authorization.
/// </summary>
public sealed class LspSession : IAsyncDisposable
{
    public const int MaximumDocumentCharacters = 4 * 1024 * 1024;
    public const int MaximumOpenDocuments = 256;
    public const int MaximumDiagnostics = 2_000;
    private const int MaximumPendingRequests = 128;

    private readonly ILspMessageTransport _transport;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<LspIncomingResponse>> _pending = new();
    private readonly HashSet<string> _openDocuments = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();
    private readonly object _documentGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextId;
    private LspSessionState _state = LspSessionState.Created;
    private Task? _pumpTask;
    private bool _disposed;

    public LspSession(ILspMessageTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;

    public event Action<LspIncomingNotification>? NotificationReceived;

    public LspSessionState State
    {
        get { lock (_stateGate) return _state; }
    }

    public LspInitializeResult? InitializeResult { get; private set; }

    public async Task<LspInitializeResult> InitializeAsync(
        string workspaceRootUri,
        JsonElement? clientCapabilities = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireState(LspSessionState.Created);
        ValidateFileUri(workspaceRootUri);
        SetState(LspSessionState.Initializing);
        _pumpTask = PumpAsync(_lifetime.Token);

        try
        {
            var capabilities = clientCapabilities ?? JsonSerializer.SerializeToElement(new
            {
                workspace = new { workspaceFolders = false, configuration = false },
                textDocument = new
                {
                    synchronization = new { dynamicRegistration = false, willSave = false, didSave = true },
                    completion = new { dynamicRegistration = false },
                    hover = new { dynamicRegistration = false },
                    definition = new { dynamicRegistration = false },
                    references = new { dynamicRegistration = false },
                    rename = new { dynamicRegistration = false },
                    publishDiagnostics = new { relatedInformation = false },
                },
            });
            var response = await RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                clientInfo = new { name = "Hermes Workbench", version = "1" },
                rootUri = workspaceRootUri,
                capabilities,
            }, cancellationToken).ConfigureAwait(false);
            InitializeResult = DeserializeResult<LspInitializeResult>(response)
                ?? throw new LspProtocolException("The LSP initialize response has no result.");
            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            SetState(LspSessionState.Ready);
            return InitializeResult;
        }
        catch
        {
            SetState(LspSessionState.Faulted);
            throw;
        }
    }

    public async Task OpenDocumentAsync(
        string uri,
        string languageId,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        ValidateDocument(uri, languageId, version, text);
        lock (_documentGate)
        {
            if (_openDocuments.Contains(uri)) throw new InvalidOperationException("The LSP document is already open.");
            if (_openDocuments.Count >= MaximumOpenDocuments) throw new InvalidOperationException("The LSP open-document limit was reached.");
            _openDocuments.Add(uri);
        }

        try
        {
            await SendNotificationAsync(
                "textDocument/didOpen",
                new { textDocument = new LspTextDocumentItem(uri, languageId, version, text) },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_documentGate) _openDocuments.Remove(uri);
            throw;
        }
    }

    public Task ChangeDocumentAsync(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        ValidateOpenDocument(uri);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        if (text.Length > MaximumDocumentCharacters) throw new ArgumentOutOfRangeException(nameof(text));
        return SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new LspVersionedTextDocumentIdentifier(uri, version),
                contentChanges = new[] { new { text } },
            },
            cancellationToken);
    }

    public async Task CloseDocumentAsync(string uri, CancellationToken cancellationToken = default)
    {
        RequireReady();
        ValidateOpenDocument(uri);
        await SendNotificationAsync(
            "textDocument/didClose",
            new { textDocument = new LspTextDocumentIdentifier(uri) },
            cancellationToken).ConfigureAwait(false);
        lock (_documentGate) _openDocuments.Remove(uri);
    }

    public Task<JsonElement?> CompletionAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default) =>
        DocumentRequestAsync("textDocument/completion", uri, position, cancellationToken);

    public Task<JsonElement?> HoverAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default) =>
        DocumentRequestAsync("textDocument/hover", uri, position, cancellationToken);

    public Task<JsonElement?> DefinitionAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken = default) =>
        DocumentRequestAsync("textDocument/definition", uri, position, cancellationToken);

    public Task<JsonElement?> ReferencesAsync(
        string uri,
        LspPosition position,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        ValidateOpenDocument(uri);
        ValidatePosition(position);
        return RequestResultAsync(
            "textDocument/references",
            new
            {
                textDocument = new LspTextDocumentIdentifier(uri),
                position,
                context = new { includeDeclaration },
            },
            cancellationToken);
    }

    public Task<JsonElement?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        ValidateOpenDocument(uri);
        ValidatePosition(position);
        if (string.IsNullOrWhiteSpace(newName) || newName.Length > 512)
        {
            throw new ArgumentException("The LSP rename target is invalid.", nameof(newName));
        }
        return RequestResultAsync(
            "textDocument/rename",
            new { textDocument = new LspTextDocumentIdentifier(uri), position, newName },
            cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State is LspSessionState.Exited) return;
        RequireState(LspSessionState.Ready, LspSessionState.Faulted);
        SetState(LspSessionState.ShuttingDown);
        try
        {
            if (_pumpTask is not null && !_pumpTask.IsCompleted)
            {
                await RequestAsync("shutdown", parameters: null, cancellationToken).ConfigureAwait(false);
            }
            await SendNotificationAsync("exit", parameters: null, cancellationToken).ConfigureAwait(false);
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
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ShutdownAsync(shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or LspProtocolException or LspSessionException)
        {
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        FailPending(new ObjectDisposedException(nameof(LspSession)));
        lock (_documentGate) _openDocuments.Clear();
        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private Task<JsonElement?> DocumentRequestAsync(
        string method,
        string uri,
        LspPosition position,
        CancellationToken cancellationToken)
    {
        RequireReady();
        ValidateOpenDocument(uri);
        ValidatePosition(position);
        return RequestResultAsync(
            method,
            new LspTextDocumentPositionParams(new LspTextDocumentIdentifier(uri), position),
            cancellationToken);
    }

    private async Task<JsonElement?> RequestResultAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var response = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return response.Result;
    }

    private async Task<LspIncomingResponse> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(method) || method.Length > 256) throw new ArgumentException("The LSP method is invalid.", nameof(method));
        if (_pending.Count >= MaximumPendingRequests) throw new LspSessionException("The LSP pending-request limit was reached.");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<LspIncomingResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new LspSessionException("The LSP request identifier collided.");
        var request = new LspRequest(id, method, Serialize(parameters));

        try
        {
            await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response.Error is not null)
            {
                throw new LspSessionException($"The language server rejected '{method}' ({response.Error.Code}).");
            }
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_pending.TryRemove(id, out _))
            {
                try
                {
                    await _transport.SendAsync(
                        new LspNotification("$/cancelRequest", Serialize(new { id })),
                        _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
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

    private Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(method) || method.Length > 256) throw new ArgumentException("The LSP method is invalid.", nameof(method));
        return _transport.SendAsync(new LspNotification(method, Serialize(parameters)), cancellationToken).AsTask();
    }

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
                        await _transport.SendAsync(
                            new LspClientResponse(
                                request.Id,
                                Error: new LspError(-32601, "The Workbench LSP client does not support this server request.")),
                            cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            if (!_disposed && State is not LspSessionState.Exited) SetState(LspSessionState.Exited);
            FailPending(new LspSessionException("The language server exited."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed) SetState(LspSessionState.Faulted);
            FailPending(new LspSessionException($"The language-server transport failed ({exception.GetType().Name})."));
        }
    }

    private void HandleNotification(LspIncomingNotification notification)
    {
        if (notification.Method == "textDocument/publishDiagnostics" && notification.Params is { } parameters)
        {
            try
            {
                var published = parameters.Deserialize<LspPublishDiagnosticsParams>(LspMessageCodec.SerializerOptions);
                if (published is not null && IsFileUri(published.Uri))
                {
                    DiagnosticsPublished?.Invoke(published with
                    {
                        Diagnostics = published.Diagnostics.Take(MaximumDiagnostics).Select(diagnostic => diagnostic with
                        {
                            Source = Limit(diagnostic.Source, 128),
                            Message = Limit(diagnostic.Message, 16_384) ?? string.Empty,
                        }).ToArray(),
                    });
                }
            }
            catch (JsonException)
            {
            }
        }
        NotificationReceived?.Invoke(notification);
    }

    private static T? DeserializeResult<T>(LspIncomingResponse response) =>
        response.Result is { } result && result.ValueKind is not JsonValueKind.Null
            ? result.Deserialize<T>(LspMessageCodec.SerializerOptions)
            : default;

    private static JsonElement? Serialize(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, LspMessageCodec.SerializerOptions);

    private static void ValidateDocument(string uri, string languageId, int version, string text)
    {
        ValidateFileUri(uri);
        if (string.IsNullOrWhiteSpace(languageId) || languageId.Length > 64) throw new ArgumentException("The LSP language identifier is invalid.", nameof(languageId));
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        if (text.Length > MaximumDocumentCharacters) throw new ArgumentOutOfRangeException(nameof(text));
    }

    private void ValidateOpenDocument(string uri)
    {
        ValidateFileUri(uri);
        lock (_documentGate)
        {
            if (!_openDocuments.Contains(uri)) throw new InvalidOperationException("The LSP document is not open.");
        }
    }

    private static void ValidatePosition(LspPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Line < 0 || position.Character < 0) throw new ArgumentOutOfRangeException(nameof(position));
    }

    private static void ValidateFileUri(string uri)
    {
        if (!IsFileUri(uri)) throw new ArgumentException("The LSP URI must be an absolute file URI.", nameof(uri));
    }

    private static bool IsFileUri(string? uri) =>
        System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile;

    private void RequireReady()
    {
        ThrowIfDisposed();
        RequireState(LspSessionState.Ready);
    }

    private void RequireState(params LspSessionState[] expected)
    {
        var state = State;
        if (!expected.Contains(state)) throw new InvalidOperationException($"The LSP session is {state}, not {string.Join(" or ", expected)}.");
    }

    private void SetState(LspSessionState state)
    {
        lock (_stateGate) _state = state;
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(exception);
        }
    }

    private static string? Limit(string? value, int maximum) =>
        value is null ? null : value[..Math.Min(value.Length, maximum)];

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
