using System.Security.Cryptography;
using HermesDeveloperServices;

namespace HermesRoslynLanguageServer;

/// <summary>
/// Internal C# LSP adapter for the one Hermes developer-services host. It discovers no PATH
/// entries and starts only the installer-provisioned, hash-pinned executable in stdio mode.
/// </summary>
public sealed class RoslynLanguageServerProvider : IToolchainProvider
{
    public const string ProviderId = "roslyn-lsp";
    public const int MaximumStandardErrorCharacters = 32 * 1024;

    private readonly RoslynLanguageServerConfiguration _configuration;
    private readonly IRoslynServerTransportFactory _transportFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private RoslynLanguageSession? _session;
    private string? _workspaceRoot;
    private bool _disposed;

    public RoslynLanguageServerProvider(RoslynLanguageServerConfiguration configuration)
        : this(configuration, new OwnedRoslynServerTransportFactory())
    {
    }

    internal RoslynLanguageServerProvider(
        RoslynLanguageServerConfiguration configuration,
        IRoslynServerTransportFactory transportFactory)
    {
        _configuration = RoslynExecutablePolicy.ValidateConfiguration(configuration);
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        Descriptor = new ToolchainProviderDescriptor(
            DeveloperServicesProtocol.ToolchainProviderVersion,
            ProviderId,
            "Roslyn C# Language Server",
            _configuration.PackageVersion,
            new[] { "csharp" },
            new[] { "sln", "slnx", "csproj" },
            new ToolchainBuildCapabilities(false, false, true, Array.Empty<string>()),
            new ToolchainLspCapabilities(true, true, true, true, true, true, true, "3.17"),
            new ToolchainDapCapabilities(false, false, false, false, false),
            new[] { ToolchainExecutionKind.LocalSidecarProcess });
        Availability = UnknownAvailability();
    }

    public ToolchainProviderDescriptor Descriptor { get; }

    public ToolchainLifecycleState LifecycleState { get; private set; } = ToolchainLifecycleState.Created;

    public ToolchainAvailability Availability { get; private set; }

    public RoslynLanguageSession Session =>
        LifecycleState == ToolchainLifecycleState.Ready && _session is { State: LspSessionState.Ready }
            ? _session
            : throw new InvalidOperationException("The Roslyn language server is not ready.");

    public async ValueTask<ToolchainExecutableDiscoveryResult> DiscoverExecutablesAsync(
        ToolchainDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        if (context.ExecutionKind != ToolchainExecutionKind.LocalSidecarProcess)
            return UnavailableDiscovery("unsupported_execution_kind", "Roslyn is available only as an owned local sidecar.");
        if (!Directory.Exists(context.WorkspaceRoot))
            return UnavailableDiscovery("workspace_unavailable", "The selected workspace is unavailable.");

        Availability = CheckingAvailability();
        var validation = await RoslynExecutablePolicy.ValidateProvisionedAsync(_configuration, cancellationToken).ConfigureAwait(false);
        Availability = validation;
        return new ToolchainExecutableDiscoveryResult(
            validation,
            new[] { new ResolvedToolchainExecutable(
                "roslyn-language-server",
                validation.State == ToolchainAvailabilityState.Available ? _configuration.ExecutablePath : null,
                _configuration.PackageVersion,
                validation) });
    }

    public async ValueTask StartAsync(ToolchainStartContext context, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workspaceRoot = RoslynExecutablePolicy.ValidateStartContext(context);
            if (LifecycleState == ToolchainLifecycleState.Ready && _session is { State: LspSessionState.Ready })
            {
                if (!SamePath(_workspaceRoot, workspaceRoot))
                    throw new InvalidOperationException("Stop the Roslyn provider before changing workspaces.");
                return;
            }
            if (LifecycleState is ToolchainLifecycleState.Starting or ToolchainLifecycleState.Stopping)
                throw new InvalidOperationException("The Roslyn provider lifecycle is already changing.");

            var previous = Interlocked.Exchange(ref _session, null);
            _workspaceRoot = null;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            LifecycleState = ToolchainLifecycleState.Starting;
            try
            {
                var validation = await RoslynExecutablePolicy.ValidateProvisionedAsync(_configuration, cancellationToken).ConfigureAwait(false);
                Availability = validation;
                if (validation.State != ToolchainAvailabilityState.Available)
                    throw new LspSessionException(validation.SafeMessage ?? "The provisioned Roslyn language server is unavailable.");

                var launchSpec = CreateLaunchSpec(workspaceRoot);
                var transport = await _transportFactory.StartAsync(launchSpec, cancellationToken).ConfigureAwait(false);
                var session = new RoslynLanguageSession(transport);
                try
                {
                    await session.InitializeAsync(new Uri(workspaceRoot + Path.DirectorySeparatorChar).AbsoluteUri, cancellationToken).ConfigureAwait(false);
                    _session = session;
                    _workspaceRoot = workspaceRoot;
                    LifecycleState = ToolchainLifecycleState.Ready;
                }
                catch
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                _workspaceRoot = null;
                LifecycleState = ToolchainLifecycleState.Faulted;
                Availability = ErrorAvailability("roslyn_start_failed", "The provisioned Roslyn language server could not start.");
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (LifecycleState is ToolchainLifecycleState.Stopped or ToolchainLifecycleState.Created) return;
            LifecycleState = ToolchainLifecycleState.Stopping;
            var session = Interlocked.Exchange(ref _session, null);
            _workspaceRoot = null;
            if (session is not null)
            {
                try { await session.ShutdownAsync(cancellationToken).ConfigureAwait(false); }
                finally { await session.DisposeAsync().ConfigureAwait(false); }
            }
            LifecycleState = ToolchainLifecycleState.Stopped;
        }
        catch
        {
            LifecycleState = ToolchainLifecycleState.Faulted;
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or LspProtocolException or LspSessionException)
        {
        }
        _disposed = true;
        _lifecycleGate.Dispose();
    }

    internal RoslynServerLaunchSpec CreateLaunchSpec(string workspaceRoot)
    {
        var extensionLogDirectory = _configuration.ExtensionLogDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hermes",
                "developer-services",
                "roslyn-logs");
        var temporaryDirectory = _configuration.TemporaryDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hermes",
                "developer-services",
                "roslyn-temp");
        extensionLogDirectory = Path.GetFullPath(extensionLogDirectory);
        temporaryDirectory = Path.GetFullPath(temporaryDirectory);
        Directory.CreateDirectory(extensionLogDirectory);
        Directory.CreateDirectory(temporaryDirectory);
        if ((File.GetAttributes(extensionLogDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Roslyn extension log directory must not be a reparse point.");
        if ((File.GetAttributes(temporaryDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Roslyn temporary directory must not be a reparse point.");

        return new RoslynServerLaunchSpec(
            _configuration.ExecutablePath,
            new[]
            {
                "--stdio",
                "--logLevel", "Error",
                "--telemetryLevel", "off",
                "--extensionLogDirectory", extensionLogDirectory,
            },
            workspaceRoot,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DOTNET_ROOT"] = _configuration.DotnetRoot,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
                ["DOTNET_CLI_HOME"] = temporaryDirectory,
                ["PATH"] = _configuration.DotnetRoot,
                ["TEMP"] = temporaryDirectory,
                ["TMP"] = temporaryDirectory,
            },
            MaximumStandardErrorCharacters);
    }

    private ToolchainExecutableDiscoveryResult UnavailableDiscovery(string code, string message)
    {
        Availability = UnavailableAvailability(code, message);
        return new ToolchainExecutableDiscoveryResult(Availability, new[]
        {
            new ResolvedToolchainExecutable("roslyn-language-server", null, _configuration.PackageVersion, Availability),
        });
    }

    private static ToolchainAvailability UnknownAvailability() =>
        new(ToolchainAvailabilityState.Unknown, null, null, DateTimeOffset.UtcNow);

    private static ToolchainAvailability CheckingAvailability() =>
        new(ToolchainAvailabilityState.Checking, null, null, DateTimeOffset.UtcNow);

    private static ToolchainAvailability UnavailableAvailability(string code, string message) =>
        new(ToolchainAvailabilityState.Unavailable, code, message, DateTimeOffset.UtcNow);

    private static ToolchainAvailability ErrorAvailability(string code, string message) =>
        new(ToolchainAvailabilityState.Error, code, message, DateTimeOffset.UtcNow);

    private static bool SamePath(string? left, string right) => left is not null && left.Equals(
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal static class RoslynExecutablePolicy
{
    private const string ExpectedFileName = "Microsoft.CodeAnalysis.LanguageServer.exe";

    internal static RoslynLanguageServerConfiguration ValidateConfiguration(RoslynLanguageServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var installerRoot = Absolute(configuration.InstallerRoot, nameof(configuration.InstallerRoot));
        var executable = Absolute(configuration.ExecutablePath, nameof(configuration.ExecutablePath));
        var dotnetRoot = Absolute(configuration.DotnetRoot, nameof(configuration.DotnetRoot));
        if (!Path.GetFileName(executable).Equals(ExpectedFileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The configured Roslyn executable name is invalid.", nameof(configuration));
        EnsureContained(installerRoot, executable, nameof(configuration.ExecutablePath));
        EnsureContained(installerRoot, dotnetRoot, nameof(configuration.DotnetRoot));
        var hash = configuration.ExpectedSha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("The configured Roslyn SHA-256 is invalid.", nameof(configuration));
        if (string.IsNullOrWhiteSpace(configuration.PackageVersion) || configuration.PackageVersion.Length > 128 || configuration.PackageVersion.Contains('\0'))
            throw new ArgumentException("The configured Roslyn package version is invalid.", nameof(configuration));
        return configuration with
        {
            InstallerRoot = installerRoot,
            ExecutablePath = executable,
            DotnetRoot = dotnetRoot,
            ExpectedSha256 = hash,
            PackageVersion = configuration.PackageVersion.Trim(),
        };
    }

    internal static async ValueTask<ToolchainAvailability> ValidateProvisionedAsync(
        RoslynLanguageServerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(configuration.InstallerRoot)
                || !Directory.Exists(configuration.DotnetRoot)
                || !File.Exists(configuration.ExecutablePath))
                return Unavailable("roslyn_not_provisioned", "The installer-provisioned Roslyn language server is unavailable.");
            RejectReparsePoints(configuration.InstallerRoot, configuration.ExecutablePath);
            RejectReparsePoints(configuration.InstallerRoot, configuration.DotnetRoot);
            await using var stream = new FileStream(configuration.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(hash),
                    System.Text.Encoding.ASCII.GetBytes(configuration.ExpectedSha256)))
                return Unavailable("roslyn_hash_mismatch", "The provisioned Roslyn language server failed integrity verification.");
            return new ToolchainAvailability(ToolchainAvailabilityState.Available, null, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Unavailable("roslyn_verification_failed", "The provisioned Roslyn language server could not be verified.");
        }
    }

    internal static string ValidateStartContext(ToolchainStartContext context)
    {
        if (context.ExecutionKind != ToolchainExecutionKind.LocalSidecarProcess)
            throw new InvalidOperationException("Roslyn requires the owned local-sidecar execution kind.");
        var workspaceRoot = Absolute(context.WorkspaceRoot, nameof(context.WorkspaceRoot));
        if (!Directory.Exists(workspaceRoot)) throw new DirectoryNotFoundException("The Roslyn workspace is unavailable.");
        RejectReparsePoints(workspaceRoot, workspaceRoot);
        if (context.PathMappings.Count is < 1 or > 32)
            throw new ArgumentException("The Roslyn workspace mapping is invalid.", nameof(context));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var exact = false;
        foreach (var mapping in context.PathMappings)
        {
            var host = Absolute(mapping.HostPath, nameof(mapping.HostPath));
            var adapter = Absolute(mapping.AdapterPath, nameof(mapping.AdapterPath));
            if (!host.Equals(adapter, comparison))
                throw new ArgumentException("Local Roslyn mappings must preserve the exact workspace path.", nameof(context));
            if (host.Equals(workspaceRoot, comparison)) exact = true;
        }
        if (!exact) throw new ArgumentException("The Roslyn workspace root has no exact path mapping.", nameof(context));
        return workspaceRoot;
    }

    private static string Absolute(string path, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameter);
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0')) throw new ArgumentException("The configured path must be absolute.", parameter);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void EnsureContained(string root, string path, string parameter)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.Equals(root, comparison) && !path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("The configured path is outside the installer authority.", parameter);
    }

    private static void RejectReparsePoints(string root, string path)
    {
        var current = root;
        if (IsReparse(current)) throw new IOException("The configured path traverses a reparse point.");
        foreach (var component in Path.GetRelativePath(root, path).Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparse(current))
                throw new IOException("The configured path traverses a reparse point.");
        }
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static ToolchainAvailability Unavailable(string code, string message) =>
        new(ToolchainAvailabilityState.Unavailable, code, message, DateTimeOffset.UtcNow);
}

internal sealed class OwnedRoslynServerTransportFactory : IRoslynServerTransportFactory
{
    public ValueTask<IRoslynServerTransport> StartAsync(RoslynServerLaunchSpec launchSpec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transport = LspProcessTransport.Start(new LspProcessLaunchOptions(
            launchSpec.ExecutablePath,
            launchSpec.Arguments,
            launchSpec.WorkingDirectory,
            launchSpec.Environment,
            InheritEnvironment: false,
            MaximumStandardErrorCharacters: launchSpec.MaximumStandardErrorCharacters,
            GracefulExitTimeout: TimeSpan.FromSeconds(2)));
        return ValueTask.FromResult<IRoslynServerTransport>(new OwnedRoslynServerTransport(transport));
    }
}

internal sealed class OwnedRoslynServerTransport : IRoslynServerTransport
{
    private readonly LspProcessTransport _transport;

    internal OwnedRoslynServerTransport(LspProcessTransport transport) => _transport = transport;

    public bool HasExited => _transport.HasExited;

    public string StandardError => _transport.StandardError;

    public long DroppedStandardErrorCharacters => _transport.DroppedStandardErrorCharacters;

    public ValueTask SendAsync(LspOutgoingMessage message, CancellationToken cancellationToken) =>
        _transport.SendAsync(message, cancellationToken);

    public IAsyncEnumerable<LspIncomingMessage> ReadAllAsync(CancellationToken cancellationToken) =>
        _transport.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
