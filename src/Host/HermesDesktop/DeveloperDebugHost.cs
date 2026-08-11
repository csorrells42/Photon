using HermesDeveloperServices;
using HermesDotNetDebugger;
using System.IO;

namespace HermesDesktop;

internal interface IDeveloperDebugHost : IAsyncDisposable
{
    event Action<DapEvent>? EventReceived;

    DapSessionState? State { get; }

    DapAdapterCapabilities? Capabilities { get; }

    Task LaunchAsync(DotNetDebugLaunchRequest request, CancellationToken cancellationToken);

    Task AttachAsync(DotNetDebugAttachRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<DapBreakpoint>> SetBreakpointsAsync(string sourcePath, IReadOnlyList<DapSourceBreakpoint> breakpoints, CancellationToken cancellationToken);

    Task ConfigurationDoneAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DapThread>> GetThreadsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DapStackFrame>> GetStackTraceAsync(int threadId, int? startFrame, int? levels, CancellationToken cancellationToken);

    Task<IReadOnlyList<DapScope>> GetScopesAsync(int frameId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DapVariable>> GetVariablesAsync(int variablesReference, int? start, int? count, CancellationToken cancellationToken);

    Task<DapEvaluateResult> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken);

    Task ContinueAsync(int threadId, CancellationToken cancellationToken);

    Task StepOverAsync(int threadId, CancellationToken cancellationToken);

    Task StepIntoAsync(int threadId, CancellationToken cancellationToken);

    Task StepOutAsync(int threadId, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

internal sealed class DeveloperDebugAuthorizationPolicy : IDotNetDebugAuthorizationPolicy
{
    private readonly object _gate = new();
    private LaunchAuthorization? _launch;
    private int? _attachProcessId;

    internal void AuthorizeNextLaunch(DotNetDebugLaunchRequest request)
    {
        lock (_gate) _launch = LaunchAuthorization.Create(request);
    }

    internal void AuthorizeNextAttach(DotNetDebugAttachRequest request)
    {
        lock (_gate) _attachProcessId = request.ProcessId;
    }

    internal void Discard()
    {
        lock (_gate)
        {
            _launch = null;
            _attachProcessId = null;
        }
    }

    public ValueTask<bool> AuthorizeLaunchAsync(string workspaceRoot, DotNetDebugLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var expected = _launch;
            _launch = null;
            return ValueTask.FromResult(expected is not null && expected.Equals(LaunchAuthorization.Create(request)));
        }
    }

    public ValueTask<bool> AuthorizeAttachAsync(string workspaceRoot, DotNetDebugAttachRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var expected = _attachProcessId;
            _attachProcessId = null;
            return ValueTask.FromResult(expected == request.ProcessId);
        }
    }

    private sealed record LaunchAuthorization(string Program, string WorkingDirectory, bool StopAtEntry, string Arguments)
    {
        internal static LaunchAuthorization Create(DotNetDebugLaunchRequest request) => new(
            Path.GetFullPath(request.ProgramPath),
            Path.GetFullPath(request.WorkingDirectory ?? Path.GetDirectoryName(request.ProgramPath)!),
            request.StopAtEntry,
            string.Join('\0', request.Arguments ?? Array.Empty<string>()));
    }
}

internal sealed class DeveloperDotNetDebugHost(
    string workspaceRoot,
    HermesDotNetDebuggerProvider provider,
    DeveloperDebugAuthorizationPolicy authorization) : IDeveloperDebugHost
{
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private HermesDotNetDebugSession? _session;
    private bool _disposed;

    public event Action<DapEvent>? EventReceived;

    public DapSessionState? State => _session?.State;

    public DapAdapterCapabilities? Capabilities => _session?.AdapterCapabilities;

    public async Task LaunchAsync(DotNetDebugLaunchRequest request, CancellationToken cancellationToken)
    {
        await StartSessionAsync(async token =>
        {
            authorization.AuthorizeNextLaunch(request);
            try { return await provider.LaunchAsync(request, token).ConfigureAwait(false); }
            finally { authorization.Discard(); }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task AttachAsync(DotNetDebugAttachRequest request, CancellationToken cancellationToken)
    {
        await StartSessionAsync(async token =>
        {
            authorization.AuthorizeNextAttach(request);
            try { return await provider.AttachAsync(request, token).ConfigureAwait(false); }
            finally { authorization.Discard(); }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DapBreakpoint>> SetBreakpointsAsync(string sourcePath, IReadOnlyList<DapSourceBreakpoint> breakpoints, CancellationToken cancellationToken) =>
        RequireSession().SetBreakpointsAsync(sourcePath, breakpoints, cancellationToken);

    public Task ConfigurationDoneAsync(CancellationToken cancellationToken) => RequireSession().ConfigurationDoneAsync(cancellationToken);

    public Task<IReadOnlyList<DapThread>> GetThreadsAsync(CancellationToken cancellationToken) => RequireSession().GetThreadsAsync(cancellationToken);

    public Task<IReadOnlyList<DapStackFrame>> GetStackTraceAsync(int threadId, int? startFrame, int? levels, CancellationToken cancellationToken) =>
        RequireSession().GetStackTraceAsync(threadId, startFrame, levels, cancellationToken);

    public Task<IReadOnlyList<DapScope>> GetScopesAsync(int frameId, CancellationToken cancellationToken) => RequireSession().GetScopesAsync(frameId, cancellationToken);

    public Task<IReadOnlyList<DapVariable>> GetVariablesAsync(int variablesReference, int? start, int? count, CancellationToken cancellationToken) =>
        RequireSession().GetVariablesAsync(variablesReference, start, count, cancellationToken);

    public Task<DapEvaluateResult> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken) =>
        RequireSession().EvaluateAsync(expression, frameId, context, cancellationToken);

    public Task ContinueAsync(int threadId, CancellationToken cancellationToken) => RequireSession().ContinueAsync(threadId, cancellationToken);

    public Task StepOverAsync(int threadId, CancellationToken cancellationToken) => RequireSession().StepOverAsync(threadId, cancellationToken);

    public Task StepIntoAsync(int threadId, CancellationToken cancellationToken) => RequireSession().StepIntoAsync(threadId, cancellationToken);

    public Task StepOutAsync(int threadId, CancellationToken cancellationToken) => RequireSession().StepOutAsync(threadId, cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _session;
            _session = null;
            if (session is null) return;
            session.EventReceived -= ForwardEvent;
            await session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _sessionGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            _session = null;
            if (session is not null)
            {
                session.EventReceived -= ForwardEvent;
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionGate.Release();
            _sessionGate.Dispose();
        }
    }

    private async Task StartSessionAsync(Func<CancellationToken, Task<HermesDotNetDebugSession>> start, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null) throw new InvalidOperationException("A .NET debug session is already active.");
            await provider.StartAsync(new ToolchainStartContext(
                _workspaceRoot,
                [new WorkspacePathMapping(_workspaceRoot, _workspaceRoot)],
                ToolchainExecutionKind.LocalSidecarProcess), cancellationToken).ConfigureAwait(false);
            var session = await start(cancellationToken).ConfigureAwait(false);
            session.EventReceived += ForwardEvent;
            _session = session;
        }
        finally { _sessionGate.Release(); }
    }

    private HermesDotNetDebugSession RequireSession() => _session
        ?? throw new InvalidOperationException("No .NET debug session is active.");

    private void ForwardEvent(DapEvent value)
    {
        EventReceived?.Invoke(value);
        if (value.Event is "terminated" or "exited") _ = Task.Run(RetireTerminalSessionAsync);
    }

    private async Task RetireTerminalSessionAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            _session = null;
            if (session is null) return;
            session.EventReceived -= ForwardEvent;
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally { _sessionGate.Release(); }
    }
}
