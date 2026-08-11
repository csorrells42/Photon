using HermesDeveloperServices;

namespace HermesDotNetDebugger;

/// <summary>
/// Typed, bounded operations for one negotiated DAP session. There is intentionally no generic
/// request method and reconnect or adapter restart is never automatic.
/// </summary>
public sealed class HermesDotNetDebugSession : IAsyncDisposable
{
    private readonly DapSession _session;
    private readonly IOwnedDapMessageTransport _transport;
    private readonly string _workspaceRoot;
    private readonly Action<HermesDotNetDebugSession> _onDisposed;
    private readonly TimeSpan _operationTimeout;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;

    internal HermesDotNetDebugSession(
        DapSession session,
        IOwnedDapMessageTransport transport,
        string workspaceRoot,
        Action<HermesDotNetDebugSession> onDisposed,
        TimeSpan operationTimeout)
    {
        _session = session;
        _transport = transport;
        _workspaceRoot = workspaceRoot;
        _onDisposed = onDisposed;
        _operationTimeout = operationTimeout;
    }

    public event Action<DapEvent>? EventReceived
    {
        add => _session.EventReceived += value;
        remove => _session.EventReceived -= value;
    }

    public DapSessionState State => _session.State;

    public DapStartMode? StartMode => _session.StartMode;

    public DapAdapterCapabilities AdapterCapabilities => _session.AdapterCapabilities;

    internal int? AdapterProcessId => _transport.ProcessId;

    internal bool AdapterHasExited => _transport.HasExited;

    internal string BoundedAdapterStandardError => _transport.StandardError;

    internal long DroppedAdapterStandardErrorCharacters => _transport.DroppedStandardErrorCharacters;

    public Task<IReadOnlyList<DapBreakpoint>> SetBreakpointsAsync(
        string sourcePath,
        IReadOnlyList<DapSourceBreakpoint> breakpoints,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var source = DebugPathPolicy.ValidateSource(_workspaceRoot, sourcePath);
        DebugPathPolicy.ValidateBreakpoints(breakpoints);
        return RunAsync(
            token => _session.SetBreakpointsAsync(
                new DapSource(Path.GetFileName(source), source),
                breakpoints,
                token),
            "setBreakpoints",
            cancellationToken);
    }

    public Task ConfigurationDoneAsync(CancellationToken cancellationToken = default) =>
        RunAsync(_session.ConfigurationDoneAsync, "configurationDone", cancellationToken);

    public Task<IReadOnlyList<DapThread>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
        RunAsync(_session.GetThreadsAsync, "threads", cancellationToken);

    public Task<IReadOnlyList<DapStackFrame>> GetStackTraceAsync(
        int threadId,
        int? startFrame = null,
        int? levels = null,
        CancellationToken cancellationToken = default)
    {
        if (threadId <= 0 || startFrame is < 0 || levels is < 0 or > DebugPathPolicy.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(threadId));
        }

        return RunAsync(
            token => _session.GetStackTraceAsync(threadId, startFrame, levels, token),
            "stackTrace",
            cancellationToken);
    }

    public Task<IReadOnlyList<DapScope>> GetScopesAsync(
        int frameId,
        CancellationToken cancellationToken = default)
    {
        if (frameId < 0) throw new ArgumentOutOfRangeException(nameof(frameId));
        return RunAsync(token => _session.GetScopesAsync(frameId, token), "scopes", cancellationToken);
    }

    public Task<IReadOnlyList<DapVariable>> GetVariablesAsync(
        int variablesReference,
        int? start = null,
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        if (variablesReference <= 0) throw new ArgumentOutOfRangeException(nameof(variablesReference));
        DebugPathPolicy.ValidatePage(start, count);
        return RunAsync(
            token => _session.GetVariablesAsync(variablesReference, start, count, token),
            "variables",
            cancellationToken);
    }

    public Task<DapEvaluateResult> EvaluateAsync(
        string expression,
        int? frameId = null,
        string context = "watch",
        CancellationToken cancellationToken = default)
    {
        DebugPathPolicy.ValidateExpression(expression);
        if (frameId is < 0) throw new ArgumentOutOfRangeException(nameof(frameId));
        if (context is not ("watch" or "hover" or "repl"))
        {
            throw new ArgumentException("The evaluation context is unsupported.", nameof(context));
        }

        return RunAsync(
            token => _session.EvaluateAsync(expression, frameId, context, token),
            "evaluate",
            cancellationToken);
    }

    public Task ContinueAsync(int threadId, CancellationToken cancellationToken = default)
    {
        if (threadId <= 0) throw new ArgumentOutOfRangeException(nameof(threadId));
        return RunAsync(token => _session.ContinueAsync(threadId, token), "continue", cancellationToken);
    }

    public Task StepOverAsync(int threadId, CancellationToken cancellationToken = default) =>
        StepAsync(DapStepKind.Over, threadId, cancellationToken);

    public Task StepIntoAsync(int threadId, CancellationToken cancellationToken = default) =>
        StepAsync(DapStepKind.Into, threadId, cancellationToken);

    public Task StepOutAsync(int threadId, CancellationToken cancellationToken = default) =>
        StepAsync(DapStepKind.Out, threadId, cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            await RunAsync(_session.DisconnectAsync, "disconnect", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposeTask = DisposeCoreAsync();
            }
            disposeTask = _disposeTask;
        }
        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            try { await _session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is DapProtocolException or DapSessionException or IOException) { }
        }
        finally
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _onDisposed(this);
        }
    }

    private Task StepAsync(DapStepKind kind, int threadId, CancellationToken cancellationToken)
    {
        if (threadId <= 0) throw new ArgumentOutOfRangeException(nameof(threadId));
        return RunAsync(token => _session.StepAsync(kind, threadId, token), kind.ToString(), cancellationToken);
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            operationName,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        try
        {
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new DotNetDebuggerOperationException($"The fixed DAP operation '{operationName}' timed out.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
