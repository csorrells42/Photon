using System.Text.Json;
using HermesDeveloperServices;

namespace HermesDotNetDebugger;

/// <summary>
/// Debug-only toolchain provider for one installer-provisioned NetCoreDbg release. Starting the
/// provider validates availability; a child process is created only after a caller explicitly
/// authorizes a typed launch or attach request.
/// </summary>
public sealed class HermesDotNetDebuggerProvider : IToolchainProvider
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions DapJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ToolchainProviderDescriptor ProviderDescriptor = new(
        ContractVersion: 1,
        ProviderId: "hermes-dotnet-dap",
        DisplayName: "Hermes .NET Debugger",
        ProviderVersion: "1.0.0",
        LanguageIds: ["csharp", "fsharp", "vb"],
        ProjectKinds: [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"],
        Build: new(false, false, false, Array.Empty<string>()),
        Lsp: new(false, false, false, false, false, false, false),
        Dap: new(true, true, true, true, true, "1.71.0"),
        ExecutionKinds: [ToolchainExecutionKind.LocalSidecarProcess]);

    private readonly NetCoreDbgInstallation _installation;
    private readonly IDotNetDebugAuthorizationPolicy _authorization;
    private readonly INetCoreDbgTransportFactory _transportFactory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private ToolchainAvailability _availability = new(
        ToolchainAvailabilityState.Unknown,
        "not-checked",
        "The .NET debugger has not been checked.",
        DateTimeOffset.UtcNow);
    private ToolchainLifecycleState _lifecycleState = ToolchainLifecycleState.Created;
    private string? _workspaceRoot;
    private HermesDotNetDebugSession? _activeSession;
    private int _disposed;

    public HermesDotNetDebuggerProvider(
        string applicationInstallRoot,
        IDotNetDebugAuthorizationPolicy authorization)
        : this(
            new NetCoreDbgInstallation(applicationInstallRoot),
            authorization,
            new NetCoreDbgProcessTransportFactory())
    {
    }

    internal HermesDotNetDebuggerProvider(
        NetCoreDbgInstallation installation,
        IDotNetDebugAuthorizationPolicy authorization,
        INetCoreDbgTransportFactory transportFactory)
    {
        _installation = installation ?? throw new ArgumentNullException(nameof(installation));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }

    public ToolchainProviderDescriptor Descriptor => ProviderDescriptor;

    public ToolchainLifecycleState LifecycleState
    {
        get { lock (_stateGate) return _lifecycleState; }
    }

    public ToolchainAvailability Availability
    {
        get { lock (_stateGate) return _availability; }
    }

    internal bool HasActiveSession
    {
        get { lock (_stateGate) return _activeSession is not null; }
    }

    public async ValueTask<ToolchainExecutableDiscoveryResult> DiscoverExecutablesAsync(
        ToolchainDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        if (context.ExecutionKind != ToolchainExecutionKind.LocalSidecarProcess)
        {
            var unsupported = SetAvailability(
                ToolchainAvailabilityState.Unavailable,
                "execution-kind-unsupported",
                "The .NET debugger supports only the local owned sidecar.");
            return new ToolchainExecutableDiscoveryResult(
                unsupported,
                [new ResolvedToolchainExecutable("netcoredbg", null, NetCoreDbgProvisioning.Version, unsupported)]);
        }

        var status = await _installation.CheckAsync(cancellationToken).ConfigureAwait(false);
        var availability = SetAvailability(
            status.IsAvailable ? ToolchainAvailabilityState.Available : ToolchainAvailabilityState.Unavailable,
            status.Code,
            status.SafeMessage);
        return new ToolchainExecutableDiscoveryResult(
            availability,
            [new ResolvedToolchainExecutable(
                "netcoredbg",
                status.IsAvailable ? status.ExecutablePath : null,
                NetCoreDbgProvisioning.Version,
                availability)]);
    }

    public async ValueTask StartAsync(ToolchainStartContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        if (context.ExecutionKind != ToolchainExecutionKind.LocalSidecarProcess)
        {
            throw new InvalidOperationException("The .NET debugger supports only a local sidecar process.");
        }

        var workspaceRoot = DebugPathPolicy.ValidateWorkspaceRoot(context.WorkspaceRoot);
        DebugPathPolicy.ValidateIdentityMappings(context.PathMappings);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (LifecycleState is ToolchainLifecycleState.Ready)
            {
                if (!string.Equals(_workspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Stop the .NET debugger provider before changing workspaces.");
                }

                return;
            }

            SetLifecycle(ToolchainLifecycleState.Starting);
            var status = await _installation.CheckAsync(cancellationToken).ConfigureAwait(false);
            SetAvailability(
                status.IsAvailable ? ToolchainAvailabilityState.Available : ToolchainAvailabilityState.Unavailable,
                status.Code,
                status.SafeMessage);
            if (!status.IsAvailable)
            {
                SetLifecycle(ToolchainLifecycleState.Faulted);
                throw new DotNetDebuggerUnavailableException(status.SafeMessage);
            }

            _workspaceRoot = workspaceRoot;
            SetLifecycle(ToolchainLifecycleState.Ready);
        }
        catch
        {
            if (LifecycleState == ToolchainLifecycleState.Starting)
            {
                SetLifecycle(ToolchainLifecycleState.Faulted);
            }

            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<HermesDotNetDebugSession> LaunchAsync(
        DotNetDebugLaunchRequest request,
        CancellationToken cancellationToken = default) =>
        CreateSessionAsync(DapStartMode.Launch, request, null, cancellationToken);

    public Task<HermesDotNetDebugSession> AttachAsync(
        DotNetDebugAttachRequest request,
        CancellationToken cancellationToken = default) =>
        CreateSessionAsync(DapStartMode.Attach, null, request, cancellationToken);

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetLifecycle(ToolchainLifecycleState.Stopping);
            HermesDotNetDebugSession? session;
            lock (_stateGate)
            {
                session = _activeSession;
                _activeSession = null;
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _workspaceRoot = null;
            SetLifecycle(ToolchainLifecycleState.Stopped);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            HermesDotNetDebugSession? session;
            lock (_stateGate)
            {
                session = _activeSession;
                _activeSession = null;
                _lifecycleState = ToolchainLifecycleState.Stopping;
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            lock (_stateGate)
            {
                _workspaceRoot = null;
                _lifecycleState = ToolchainLifecycleState.Stopped;
            }
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async Task<HermesDotNetDebugSession> CreateSessionAsync(
        DapStartMode mode,
        DotNetDebugLaunchRequest? launchRequest,
        DotNetDebugAttachRequest? attachRequest,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var workspaceRoot = RequireReadyWorkspace();
        DotNetDebugLaunchRequest? normalizedLaunch = null;
        if (mode == DapStartMode.Launch)
        {
            normalizedLaunch = DebugPathPolicy.ValidateLaunch(workspaceRoot, launchRequest!);
            if (!await _authorization.AuthorizeLaunchAsync(
                    workspaceRoot,
                    normalizedLaunch,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new DotNetDebuggerAuthorizationException("The .NET debug launch was not authorized.");
            }
        }
        else
        {
            DebugPathPolicy.ValidateAttach(attachRequest!);
            if (!await _authorization.AuthorizeAttachAsync(
                    workspaceRoot,
                    attachRequest!,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new DotNetDebuggerAuthorizationException("The .NET debug attach was not authorized.");
            }
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _ = RequireReadyWorkspace();
            lock (_stateGate)
            {
                if (_activeSession is not null)
                {
                    throw new InvalidOperationException("Only one .NET debug session may be active.");
                }
            }

            using var deadline = CreateDeadline(cancellationToken);
            IOwnedDapMessageTransport? transport = null;
            HermesDotNetDebugSession? session = null;
            try
            {
                transport = await _transportFactory.StartAsync(_installation, deadline.Token).ConfigureAwait(false);
                var dapSession = new DapSession(transport);
                session = new HermesDotNetDebugSession(
                    dapSession,
                    transport,
                    workspaceRoot,
                    OnSessionDisposed,
                    OperationTimeout);
                await dapSession.InitializeAsync(cancellationToken: deadline.Token).ConfigureAwait(false);

                JsonElement arguments = mode == DapStartMode.Launch
                    ? JsonSerializer.SerializeToElement(new LaunchArguments(
                        Name: "Hermes .NET launch",
                        Type: "coreclr",
                        Request: "launch",
                        Program: normalizedLaunch!.ProgramPath,
                        Cwd: normalizedLaunch.WorkingDirectory!,
                        Args: normalizedLaunch.Arguments ?? Array.Empty<string>(),
                        StopAtEntry: normalizedLaunch.StopAtEntry,
                        JustMyCode: true,
                        Console: "internalConsole"), DapJsonOptions)
                    : JsonSerializer.SerializeToElement(new AttachArguments(
                        Name: "Hermes .NET attach",
                        Type: "coreclr",
                        Request: "attach",
                        ProcessId: attachRequest!.ProcessId,
                        JustMyCode: true), DapJsonOptions);
                await dapSession.StartAsync(mode, arguments, deadline.Token).ConfigureAwait(false);

                lock (_stateGate)
                {
                    _activeSession = session;
                }

                StartTerminalSessionRetirement(session);

                return session;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                else if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
                throw new DotNetDebuggerOperationException("The .NET debug adapter negotiation timed out.");
            }
            catch
            {
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                else if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private string RequireReadyWorkspace()
    {
        lock (_stateGate)
        {
            if (_lifecycleState != ToolchainLifecycleState.Ready || _workspaceRoot is null)
            {
                throw new InvalidOperationException("Start the .NET debugger provider for a workspace first.");
            }

            return _workspaceRoot;
        }
    }

    private void OnSessionDisposed(HermesDotNetDebugSession session)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
            }
        }
    }

    private void StartTerminalSessionRetirement(HermesDotNetDebugSession session) =>
        _ = RetireTerminalSessionAsync(session);

    private async Task RetireTerminalSessionAsync(HermesDotNetDebugSession session)
    {
        try
        {
            while (true)
            {
                lock (_stateGate)
                {
                    if (!ReferenceEquals(_activeSession, session)) return;
                }

                if (session.State is DapSessionState.Exited or DapSessionState.Faulted
                    || session.AdapterHasExited)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }

            // This continuation never runs on the DAP pump. Disposing here can therefore
            // cancel and await the pump without self-deadlocking, then releases the provider's
            // exact active-session slot through OnSessionDisposed.
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DapProtocolException or DapSessionException or IOException)
        {
            // Disposal is already fail-closed. Stop/Dispose can still retire the exact session.
        }
    }

    private ToolchainAvailability SetAvailability(
        ToolchainAvailabilityState state,
        string code,
        string safeMessage)
    {
        lock (_stateGate)
        {
            _availability = new ToolchainAvailability(state, code, safeMessage, DateTimeOffset.UtcNow);
            return _availability;
        }
    }

    private void SetLifecycle(ToolchainLifecycleState state)
    {
        lock (_stateGate) _lifecycleState = state;
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        return deadline;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record LaunchArguments(
        string Name,
        string Type,
        string Request,
        string Program,
        string Cwd,
        IReadOnlyList<string> Args,
        bool StopAtEntry,
        bool JustMyCode,
        string Console);

    private sealed record AttachArguments(
        string Name,
        string Type,
        string Request,
        int ProcessId,
        bool JustMyCode);
}

internal static class DebugPathPolicy
{
    internal const int MaximumArguments = 128;
    internal const int MaximumArgumentCharacters = 4096;
    internal const int MaximumTotalArgumentCharacters = 32 * 1024;
    internal const int MaximumExpressionCharacters = 16 * 1024;
    internal const int MaximumBreakpoints = 2048;
    internal const int MaximumPageSize = 1000;

    public static string ValidateWorkspaceRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The workspace root must be absolute.", nameof(path));
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The workspace root does not exist.");
        }

        RejectReparseChain(fullPath, fullPath);
        return fullPath;
    }

    public static void ValidateIdentityMappings(IReadOnlyList<WorkspacePathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        foreach (var mapping in mappings)
        {
            if (!Path.IsPathFullyQualified(mapping.HostPath)
                || !Path.IsPathFullyQualified(mapping.AdapterPath)
                || !string.Equals(
                    Path.GetFullPath(mapping.HostPath),
                    Path.GetFullPath(mapping.AdapterPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The local .NET debugger accepts only identity path mappings.", nameof(mappings));
            }
        }
    }

    public static DotNetDebugLaunchRequest ValidateLaunch(string workspaceRoot, DotNetDebugLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var program = ValidateContainedFile(workspaceRoot, request.ProgramPath, "program");
        var extension = Path.GetExtension(program);
        if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The debug program must be a .dll or .exe.", nameof(request));
        }

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Path.GetDirectoryName(program)!
            : ValidateContainedDirectory(workspaceRoot, request.WorkingDirectory);
        ValidateArguments(request.Arguments);
        return request with
        {
            ProgramPath = program,
            WorkingDirectory = workingDirectory,
            Arguments = request.Arguments?.ToArray() ?? Array.Empty<string>(),
        };
    }

    public static void ValidateAttach(DotNetDebugAttachRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The attach process ID must be positive.");
        }
    }

    public static string ValidateSource(string workspaceRoot, string sourcePath) =>
        ValidateContainedFile(workspaceRoot, sourcePath, "source");

    public static void ValidateBreakpoints(IReadOnlyList<DapSourceBreakpoint> breakpoints)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        if (breakpoints.Count > MaximumBreakpoints)
        {
            throw new ArgumentOutOfRangeException(nameof(breakpoints));
        }

        foreach (var breakpoint in breakpoints)
        {
            if (breakpoint.Line <= 0 || breakpoint.Column is <= 0)
            {
                throw new ArgumentException("Breakpoint lines and columns must be one-based.", nameof(breakpoints));
            }

            ValidateBoundedOptionalText(breakpoint.Condition, MaximumExpressionCharacters, nameof(breakpoints));
            ValidateBoundedOptionalText(breakpoint.HitCondition, MaximumExpressionCharacters, nameof(breakpoints));
            ValidateBoundedOptionalText(breakpoint.LogMessage, MaximumExpressionCharacters, nameof(breakpoints));
        }
    }

    public static void ValidateExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        if (expression.Length > MaximumExpressionCharacters || expression.Contains('\0'))
        {
            throw new ArgumentException("The evaluation expression is invalid.", nameof(expression));
        }
    }

    public static void ValidatePage(int? start, int? count)
    {
        if (start is < 0 || count is < 0 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
    }

    private static string ValidateContainedFile(string root, string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"The debug {description} path must be absolute.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        EnsureContained(root, fullPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The debug {description} does not exist.", fullPath);
        }

        RejectReparseChain(root, fullPath);
        return fullPath;
    }

    private static string ValidateContainedDirectory(string root, string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The debug working directory must be absolute.", nameof(path));
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureContained(root, fullPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The debug working directory does not exist.");
        }

        RejectReparseChain(root, fullPath);
        return fullPath;
    }

    private static void EnsureContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relative))
        {
            throw new UnauthorizedAccessException("The debug path is outside the authorized workspace.");
        }
    }

    private static void ValidateArguments(IReadOnlyList<string>? arguments)
    {
        if (arguments is null) return;
        if (arguments.Count > MaximumArguments)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments));
        }

        var total = 0;
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Length > MaximumArgumentCharacters || argument.Contains('\0'))
            {
                throw new ArgumentException("A debug program argument is invalid.", nameof(arguments));
            }

            total = checked(total + argument.Length);
            if (total > MaximumTotalArgumentCharacters)
            {
                throw new ArgumentException("The debug program arguments exceed the bounded limit.", nameof(arguments));
            }
        }
    }

    private static void ValidateBoundedOptionalText(string? text, int maximum, string parameterName)
    {
        if (text is not null && (text.Length > maximum || text.Contains('\0')))
        {
            throw new ArgumentException("A breakpoint value is invalid.", parameterName);
        }
    }

    private static void RejectReparseChain(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        RejectReparsePoint(current);
        if (relative == ".")
        {
            return;
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Reparse points are not accepted in debug paths.");
        }
    }
}
