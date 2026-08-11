using System.Collections.Concurrent;
using System.Text.Json;

namespace HermesDeveloperServices;

public enum DapStepKind
{
    Over,
    Into,
    Out,
}

/// <summary>
/// Coordinates the client side of one DAP session over an injected transport. It performs protocol
/// state validation only; process selection, adapter acquisition, and execution authorization remain
/// host responsibilities outside this library.
/// </summary>
public sealed class DapSession : IAsyncDisposable
{
    private readonly IDapMessageTransport _transport;
    private readonly object _stateGate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponse>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _initializedEvent =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextSequence;
    private DapSessionState _state = DapSessionState.Created;
    private Task? _pumpTask;
    private bool _disposed;

    public DapSession(IDapMessageTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public event Action<DapEvent>? EventReceived;

    public DapSessionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public DapAdapterCapabilities AdapterCapabilities { get; private set; } = new();

    public DapStartMode? StartMode { get; private set; }

    public async Task<DapAdapterCapabilities> InitializeAsync(
        DapClientCapabilities? clientCapabilities = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireState(DapSessionState.Created);
        SetState(DapSessionState.Initializing);
        _pumpTask = PumpAsync(_lifetime.Token);

        try
        {
            var response = await RequestAsync(
                "initialize",
                clientCapabilities ?? new DapClientCapabilities(),
                cancellationToken).ConfigureAwait(false);
            AdapterCapabilities = DeserializeBody<DapAdapterCapabilities>(response) ?? new();
            SetState(DapSessionState.Initialized);
            return AdapterCapabilities;
        }
        catch
        {
            SetState(DapSessionState.Faulted);
            throw;
        }
    }

    public async Task StartAsync(
        DapStartMode mode,
        JsonElement? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireState(DapSessionState.Initialized);
        StartMode = mode;
        var command = mode == DapStartMode.Launch ? "launch" : "attach";
        await RequestAsync(command, arguments, cancellationToken).ConfigureAwait(false);
        await _initializedEvent.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetState(DapSessionState.Configuring);
    }

    public async Task<IReadOnlyList<DapBreakpoint>> SetBreakpointsAsync(
        DapSource source,
        IReadOnlyList<DapSourceBreakpoint> breakpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(breakpoints);
        RequireState(DapSessionState.Configuring, DapSessionState.Stopped);

        var response = await RequestAsync(
            "setBreakpoints",
            new SetBreakpointsArguments(source, breakpoints, SourceModified: false),
            cancellationToken).ConfigureAwait(false);
        return DeserializeBody<BreakpointsBody>(response)?.Breakpoints ?? Array.Empty<DapBreakpoint>();
    }

    public async Task ConfigurationDoneAsync(CancellationToken cancellationToken = default)
    {
        RequireState(DapSessionState.Configuring);
        SetState(DapSessionState.Running);
        try
        {
            if (AdapterCapabilities.SupportsConfigurationDoneRequest)
            {
                await RequestAsync("configurationDone", new { }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (State is DapSessionState.Running)
            {
                SetState(DapSessionState.Configuring);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<DapThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        RequireState(DapSessionState.Running, DapSessionState.Stopped);
        var response = await RequestAsync("threads", arguments: null, cancellationToken).ConfigureAwait(false);
        return DeserializeBody<ThreadsBody>(response)?.Threads ?? Array.Empty<DapThread>();
    }

    public async Task<IReadOnlyList<DapStackFrame>> GetStackTraceAsync(
        int threadId,
        int? startFrame = null,
        int? levels = null,
        CancellationToken cancellationToken = default)
    {
        RequireState(DapSessionState.Stopped);
        var response = await RequestAsync(
            "stackTrace",
            new StackTraceArguments(threadId, startFrame, levels),
            cancellationToken).ConfigureAwait(false);
        return DeserializeBody<StackFramesBody>(response)?.StackFrames ?? Array.Empty<DapStackFrame>();
    }

    public async Task<IReadOnlyList<DapScope>> GetScopesAsync(
        int frameId,
        CancellationToken cancellationToken = default)
    {
        RequireState(DapSessionState.Stopped);
        var response = await RequestAsync(
            "scopes",
            new ScopesArguments(frameId),
            cancellationToken).ConfigureAwait(false);
        return DeserializeBody<ScopesBody>(response)?.Scopes ?? Array.Empty<DapScope>();
    }

    public async Task<IReadOnlyList<DapVariable>> GetVariablesAsync(
        int variablesReference,
        int? start = null,
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        RequireState(DapSessionState.Stopped);
        var response = await RequestAsync(
            "variables",
            new VariablesArguments(variablesReference, start, count),
            cancellationToken).ConfigureAwait(false);
        return DeserializeBody<VariablesBody>(response)?.Variables ?? Array.Empty<DapVariable>();
    }

    public async Task<DapEvaluateResult> EvaluateAsync(
        string expression,
        int? frameId = null,
        string context = "watch",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        RequireState(DapSessionState.Stopped);
        var response = await RequestAsync(
            "evaluate",
            new EvaluateArguments(expression, frameId, context),
            cancellationToken).ConfigureAwait(false);
        return DeserializeBody<DapEvaluateResult>(response)
            ?? throw new DapProtocolException("The DAP evaluate response has no body.");
    }

    public async Task ContinueAsync(int threadId, CancellationToken cancellationToken = default)
    {
        RequireState(DapSessionState.Stopped);
        SetState(DapSessionState.Running);
        try
        {
            await RequestAsync(
                "continue",
                new ThreadControlArguments(threadId, SingleThread: false),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (State is DapSessionState.Running)
            {
                SetState(DapSessionState.Stopped);
            }

            throw;
        }
    }

    public async Task StepAsync(
        DapStepKind kind,
        int threadId,
        CancellationToken cancellationToken = default)
    {
        RequireState(DapSessionState.Stopped);
        var command = kind switch
        {
            DapStepKind.Over => "next",
            DapStepKind.Into => "stepIn",
            DapStepKind.Out => "stepOut",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        SetState(DapSessionState.Running);
        try
        {
            await RequestAsync(
                command,
                new ThreadControlArguments(threadId, SingleThread: false),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (State is DapSessionState.Running)
            {
                SetState(DapSessionState.Stopped);
            }

            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var state = State;
        if (state is DapSessionState.Disconnected)
        {
            return;
        }

        if (state is DapSessionState.Created or DapSessionState.Exited or DapSessionState.Faulted)
        {
            SetState(DapSessionState.Disconnected);
            _lifetime.Cancel();
            return;
        }

        SetState(DapSessionState.Disconnecting);
        try
        {
            await RequestAsync(
                "disconnect",
                new DisconnectArguments(TerminateDebuggee: false, Restart: false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SetState(DapSessionState.Disconnected);
            _lifetime.Cancel();
            if (_pumpTask is not null)
            {
                try
                {
                    await _pumpTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        FailPending(new DapSessionException("The DAP session was disposed."));
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task<DapResponse> RequestAsync<TArguments>(
        string command,
        TArguments? arguments,
        CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _nextSequence);
        var completion = new TaskCompletionSource<DapResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(sequence, completion))
        {
            throw new DapSessionException("A DAP request sequence collision occurred.");
        }

        var element = arguments is null
            ? (JsonElement?)null
            : arguments is JsonElement supplied
                ? supplied.Clone()
                : JsonSerializer.SerializeToElement(arguments, DapMessageCodec.SerializerOptions);

        try
        {
            await _transport.SendAsync(new DapRequest(sequence, command, element), cancellationToken)
                .ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                throw new DapSessionException($"The DAP adapter rejected the '{command}' request.");
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (AdapterCapabilities.SupportsCancelRequest)
            {
                await TrySendCancelAsync(sequence).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _pending.TryRemove(sequence, out _);
        }
    }

    private Task<DapResponse> RequestAsync(
        string command,
        object? arguments,
        CancellationToken cancellationToken) =>
        RequestAsync<object>(command, arguments, cancellationToken);

    private async Task TrySendCancelAsync(int requestSequence)
    {
        try
        {
            var cancelSequence = Interlocked.Increment(ref _nextSequence);
            var arguments = JsonSerializer.SerializeToElement(
                new CancelArguments(requestSequence),
                DapMessageCodec.SerializerOptions);
            await _transport.SendAsync(
                new DapRequest(cancelSequence, "cancel", arguments),
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // Cancellation is best effort and must not replace the caller's cancellation result.
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _transport.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (message)
                {
                    case DapIncomingResponse incomingResponse:
                        if (_pending.TryGetValue(incomingResponse.Response.RequestSeq, out var completion))
                        {
                            completion.TrySetResult(incomingResponse.Response);
                        }
                        break;

                    case DapIncomingEvent incomingEvent:
                        HandleEvent(incomingEvent.Event);
                        break;

                    default:
                        throw new DapProtocolException("The DAP transport produced an unsupported message.");
                }
            }

            if (!cancellationToken.IsCancellationRequested && State is not DapSessionState.Disconnected)
            {
                SetState(DapSessionState.Exited);
                FailPending(new DapSessionException("The DAP adapter exited."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetState(DapSessionState.Faulted);
            FailPending(new DapSessionException("The DAP adapter transport failed."));
            if (exception is DapProtocolException)
            {
                throw;
            }
        }
    }

    private void HandleEvent(DapEvent dapEvent)
    {
        switch (dapEvent.Event)
        {
            case "initialized":
                _initializedEvent.TrySetResult(true);
                break;

            case "stopped":
                _ = DeserializeEventBody<DapStoppedEvent>(dapEvent);
                SetState(DapSessionState.Stopped);
                break;

            case "continued":
                _ = DeserializeEventBody<DapContinuedEvent>(dapEvent);
                SetState(DapSessionState.Running);
                break;

            case "exited":
            case "terminated":
                SetState(DapSessionState.Exited);
                break;
        }

        try
        {
            EventReceived?.Invoke(dapEvent);
        }
        catch
        {
            // Host event observers cannot corrupt the protocol pump.
        }
    }

    private static T? DeserializeBody<T>(DapResponse response)
    {
        if (response.Body is not { } body || body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        try
        {
            return body.Deserialize<T>(DapMessageCodec.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new DapProtocolException("A DAP response body is malformed.", exception);
        }
    }

    private static T? DeserializeEventBody<T>(DapEvent dapEvent)
    {
        if (dapEvent.Body is not { } body || body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        try
        {
            return body.Deserialize<T>(DapMessageCodec.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new DapProtocolException("A DAP event body is malformed.", exception);
        }
    }

    private void RequireState(params DapSessionState[] allowed)
    {
        var state = State;
        if (!allowed.Contains(state))
        {
            throw new InvalidOperationException($"The DAP operation is not valid while the session is {state}.");
        }
    }

    private void SetState(DapSessionState state)
    {
        lock (_stateGate)
        {
            _state = state;
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending)
        {
            pair.Value.TrySetException(exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record SetBreakpointsArguments(
        DapSource Source,
        IReadOnlyList<DapSourceBreakpoint> Breakpoints,
        bool SourceModified);

    private sealed record BreakpointsBody(IReadOnlyList<DapBreakpoint> Breakpoints);

    private sealed record ThreadsBody(IReadOnlyList<DapThread> Threads);

    private sealed record StackTraceArguments(int ThreadId, int? StartFrame, int? Levels);

    private sealed record StackFramesBody(IReadOnlyList<DapStackFrame> StackFrames, int? TotalFrames);

    private sealed record ScopesArguments(int FrameId);

    private sealed record ScopesBody(IReadOnlyList<DapScope> Scopes);

    private sealed record VariablesArguments(int VariablesReference, int? Start, int? Count);

    private sealed record VariablesBody(IReadOnlyList<DapVariable> Variables);

    private sealed record EvaluateArguments(string Expression, int? FrameId, string Context);

    private sealed record ThreadControlArguments(int ThreadId, bool SingleThread);

    private sealed record DisconnectArguments(bool TerminateDebuggee, bool Restart);

    private sealed record CancelArguments(int RequestId);
}
