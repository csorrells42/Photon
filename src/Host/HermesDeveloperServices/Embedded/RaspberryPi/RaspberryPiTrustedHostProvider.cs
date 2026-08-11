using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HermesDeveloperServices;

public sealed partial class RaspberryPiTrustedHostProvider
{
    private readonly RaspberryPiProviderOptions _options;
    private readonly string _credentialRoot;

    private RaspberryPiTrustedHostProvider(RaspberryPiProviderOptions options, string credentialRoot)
    {
        _options = options;
        _credentialRoot = credentialRoot;
    }

    public static async Task<RaspberryPiTrustedHostProvider> CreateAsync(
        RaspberryPiProviderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var package = await PinnedToolchainReceiptLoader.StageAsync(
            options.WorkbenchRoot, options.ToolchainRelativePath, options.ReceiptFileName,
            RaspberryPiProviderOptions.ToolchainId,
            [RaspberryPiProviderOptions.SshLogicalName, RaspberryPiProviderOptions.ScpLogicalName],
            cancellationToken).ConfigureAwait(false);
        var root = TrustedToolchainPathPolicy.RequireRoot(options.CredentialRoot, "ssh_credential");
        _ = TrustedToolchainPathPolicy.ResolveExistingFile(root, options.IdentityRelativePath, "ssh_identity");
        _ = TrustedToolchainPathPolicy.ResolveExistingFile(root, options.KnownHostsRelativePath, "ssh_known_hosts");
        return new(options, root);
    }

    public static string DeployPermissionScope(RaspberryPiDeployRequest request) =>
        $"{request.Target.User}@{request.Target.Host}:{request.Target.Port}|{request.WorkspaceRoot}|{request.SourceRelativePath}|{request.RemoteAbsolutePath}";

    public Task<EmbeddedHostOperationResult> ProbeAsync(RaspberryPiTrustedHost target, CancellationToken cancellationToken = default) =>
        ExecuteReadAsync(target, "probe", ["/usr/bin/uname", "-a"], TimeSpan.FromMinutes(1), cancellationToken);

    public Task<EmbeddedHostOperationResult> SearchLibrariesAsync(RaspberryPiPackageQuery request, CancellationToken cancellationToken = default) =>
        SearchAsync(request, "search_libraries", cancellationToken);

    public Task<EmbeddedHostOperationResult> SearchPackagesAsync(RaspberryPiPackageQuery request, CancellationToken cancellationToken = default) =>
        SearchAsync(request, "search_packages", cancellationToken);

    public async Task<EmbeddedHostOperationResult> DeployAsync(RaspberryPiDeployRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EmbeddedPermissionPolicy.Allows(request.Permission, EmbeddedPermissionKind.DeployRemote, DeployPermissionScope(request)))
            return EmbeddedHostOperationResult.PermissionDenied("deploy");

        string? localStage = null;
        string? remoteTemporary = null;
        try
        {
            ValidateTarget(request.Target);
            var workspace = TrustedToolchainPathPolicy.RequireRoot(request.WorkspaceRoot, "workspace");
            var source = TrustedToolchainPathPolicy.ResolveExistingFile(workspace, request.SourceRelativePath, "deploy_source");
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                return EmbeddedHostOperationResult.Failure("deploy", "deploy_source_invalid", "Only one normal non-reparse file may be deployed.");
            var remoteFinal = ValidateRemotePath(request.RemoteAbsolutePath);
            remoteTemporary = remoteFinal + $".hermes-upload-{Guid.NewGuid():N}";

            var trustedStorage = TrustedToolchainPathPolicy.RequireRoot(_options.WorkbenchRoot, "workbench");
            localStage = TrustedToolchainPathPolicy.CreateOwnedTemporaryDirectory(
                trustedStorage, trustedStorage, ".hermes-rpi-deploy-", "deploy_stage");
            var stagedFile = Path.Combine(localStage, "payload");
            await using var stagedLock = await StageLockedCopyAsync(source, stagedFile, cancellationToken).ConfigureAwait(false);
            using var authority = AcquireAuthority(request.Target);
            await using var package = await StageAsync(cancellationToken).ConfigureAwait(false);

            var copy = await OwnedToolchainProcessRunner.RunAsync(
                package.Staged.Executables[RaspberryPiProviderOptions.ScpLogicalName].Path,
                workspace,
                BuildScpArguments(authority, request.Target, stagedFile, remoteTemporary),
                Bounds(TimeSpan.FromMinutes(20)), cancellationToken).ConfigureAwait(false);
            if (!Succeeded(copy))
            {
                if (!await CleanupRemoteAsync(package, authority, request.Target, remoteTemporary).ConfigureAwait(false))
                    return EmbeddedHostOperationResult.Failure("deploy", "deploy_cleanup_failed", "Remote staging failed and cleanup could not be verified.");
                return Result(copy, "deploy", "Remote staging transfer failed; no deployment success is claimed.", workspace);
            }

            var publish = await RunSshAsync(package, authority, request.Target,
                ["/usr/bin/mv", "-fT", "--", remoteTemporary, remoteFinal], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (!Succeeded(publish)
                && !await CleanupRemoteAsync(package, authority, request.Target, remoteTemporary).ConfigureAwait(false))
                return EmbeddedHostOperationResult.Failure("deploy", "deploy_cleanup_failed", "Remote publish failed and cleanup could not be verified.");
            return Result(publish, "deploy", Succeeded(publish)
                ? "The single approved file was atomically published on the trusted host."
                : "Remote atomic publish failed; staged remote data was cleaned up.", workspace);
        }
        catch (OperationCanceledException) { return EmbeddedHostOperationResult.Failure("deploy", "deploy_cancelled", "Deployment was cancelled."); }
        catch (TrustedToolchainValidationException exception) { return EmbeddedHostOperationResult.Failure("deploy", exception.Code, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return EmbeddedHostOperationResult.Failure("deploy", "deploy_authority_failed", "Deployment authority could not be established safely."); }
        finally { DeleteOwnedStage(localStage, _options.WorkbenchRoot); }
    }

    private async Task<EmbeddedHostOperationResult> SearchAsync(RaspberryPiPackageQuery request, string operation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = ValidatePackageQuery(request.Query);
        return await ExecuteReadAsync(request.Target, operation,
            ["/usr/bin/apt-cache", "search", "--names-only", query], TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
    }

    private async Task<EmbeddedHostOperationResult> ExecuteReadAsync(
        RaspberryPiTrustedHost target, string operation, IReadOnlyList<string> remote, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            ValidateTarget(target);
            using var authority = AcquireAuthority(target);
            await using var package = await StageAsync(token).ConfigureAwait(false);
            var process = await RunSshAsync(package, authority, target, remote, timeout, token).ConfigureAwait(false);
            return Result(process, operation, Succeeded(process)
                ? "The explicitly trusted host returned a bounded result."
                : "The trusted-host operation failed; no remote capability is claimed.");
        }
        catch (OperationCanceledException) { return EmbeddedHostOperationResult.Failure(operation, "remote_cancelled", "The trusted-host operation was cancelled."); }
        catch (TrustedToolchainValidationException exception) { return EmbeddedHostOperationResult.Failure(operation, exception.Code, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return EmbeddedHostOperationResult.Failure(operation, "ssh_authority_failed", "SSH authority could not be established safely."); }
    }

    private Task<PinnedToolchainPackageLease> StageAsync(CancellationToken token) =>
        PinnedToolchainReceiptLoader.StageAsync(_options.WorkbenchRoot, _options.ToolchainRelativePath, _options.ReceiptFileName,
            RaspberryPiProviderOptions.ToolchainId,
            [RaspberryPiProviderOptions.SshLogicalName, RaspberryPiProviderOptions.ScpLogicalName], token);

    private Task<OwnedToolchainProcessResult> RunSshAsync(
        PinnedToolchainPackageLease package, SshAuthorityLease authority, RaspberryPiTrustedHost target,
        IReadOnlyList<string> remote, TimeSpan timeout, CancellationToken token) =>
        OwnedToolchainProcessRunner.RunAsync(
            package.Staged.Executables[RaspberryPiProviderOptions.SshLogicalName].Path,
            package.Staged.Root, BuildSshArguments(authority, target, remote), Bounds(timeout), token);

    private async Task<bool> CleanupRemoteAsync(
        PinnedToolchainPackageLease package, SshAuthorityLease authority, RaspberryPiTrustedHost target, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cleanup = await RunSshAsync(package, authority, target,
            ["/usr/bin/rm", "-f", "--", path], TimeSpan.FromSeconds(8), timeout.Token).ConfigureAwait(false);
        return Succeeded(cleanup);
    }

    private static IReadOnlyList<string> BuildSshArguments(
        SshAuthorityLease authority, RaspberryPiTrustedHost target, IReadOnlyList<string> remote)
    {
        var args = CommonOptions(authority, target, scp: false);
        args.Add("--");
        args.Add($"{target.User}@{target.Host}");
        args.AddRange(remote);
        return args;
    }

    private static IReadOnlyList<string> BuildScpArguments(
        SshAuthorityLease authority, RaspberryPiTrustedHost target, string local, string remote)
    {
        var args = CommonOptions(authority, target, scp: true);
        args.Add("-r");
        args.Add("--");
        args.Add(local);
        args.Add($"{target.User}@{target.Host}:{remote}");
        return args;
    }

    private static List<string> CommonOptions(SshAuthorityLease authority, RaspberryPiTrustedHost target, bool scp) =>
    [
        scp ? "-P" : "-p", target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-i", authority.IdentityPath,
        "-o", "BatchMode=yes", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
        "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=yes",
        "-o", $"UserKnownHostsFile={authority.PrivateKnownHostsPath}",
        "-o", $"GlobalKnownHostsFile={(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")}",
        "-o", $"HostKeyAlgorithms={target.HostKey.Algorithm}",
        "-o", "UpdateHostKeys=no", "-o", "CheckHostIP=no", "-o", "ForwardAgent=no",
        "-o", "ClearAllForwardings=yes", "-o", "RequestTTY=no",
    ];

    private SshAuthorityLease AcquireAuthority(RaspberryPiTrustedHost target)
    {
        var identityPath = TrustedToolchainPathPolicy.ResolveExistingFile(_credentialRoot, _options.IdentityRelativePath, "ssh_identity");
        var knownPath = TrustedToolchainPathPolicy.ResolveExistingFile(_credentialRoot, _options.KnownHostsRelativePath, "ssh_known_hosts");
        var identity = OpenLockedNormalFile(identityPath, "ssh_identity_invalid");
        try
        {
            var known = OpenLockedNormalFile(knownPath, "ssh_known_hosts_invalid");
            try
            {
                var exact = FindExactKnownHost(known, target);
                var privateDirectory = TrustedToolchainPathPolicy.CreateOwnedTemporaryDirectory(
                    _credentialRoot, _credentialRoot, ".hermes-ssh-authority-", "ssh_authority");
                var privatePath = Path.Combine(privateDirectory, "known_hosts");
                File.WriteAllText(privatePath, exact + Environment.NewLine, new UTF8Encoding(false));
                var privateLock = OpenLockedNormalFile(privatePath, "ssh_authority_invalid");
                return new(identityPath, privatePath, privateDirectory, identity, known, privateLock);
            }
            catch { known.Dispose(); throw; }
        }
        catch { identity.Dispose(); throw; }
    }

    private static string FindExactKnownHost(FileStream stream, RaspberryPiTrustedHost target)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
        var alias = target.Port == 22 ? target.Host : $"[{target.Host}]:{target.Port}";
        for (var count = 0; count < 512 && reader.ReadLine() is { } line; count++)
        {
            if (line.Length is 0 or > 8192 || line.StartsWith('#')) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !fields[0].Split(',').Contains(alias, StringComparer.Ordinal)
                || !fields[1].Equals(target.HostKey.Algorithm, StringComparison.Ordinal)) continue;
            byte[] key; try { key = Convert.FromBase64String(fields[2]); } catch (FormatException) { continue; }
            var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
            if (fingerprint.Equals(target.HostKey.Sha256Fingerprint, StringComparison.Ordinal)) return $"{alias} {fields[1]} {fields[2]}";
        }
        throw new TrustedToolchainValidationException("ssh_host_key_mismatch", "The explicit host-key trust does not match the authority-owned entry.");
    }

    private static async Task<FileStream> StageLockedCopyAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (input.Length is <= 0 or > 64L * 1024 * 1024)
            throw new TrustedToolchainValidationException("deploy_source_size", "The deployment file size is invalid.");
        byte[] sourceHash;
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            using var sourceHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                sourceHasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            if (output.Length != input.Length) throw new TrustedToolchainValidationException("deploy_stage_mismatch", "The staged deployment file is incomplete.");
            sourceHash = sourceHasher.GetHashAndReset();
        }
        var locked = OpenLockedNormalFile(destination, "deploy_stage_invalid");
        try
        {
            var stagedHash = await SHA256.HashDataAsync(locked, token).ConfigureAwait(false);
            locked.Position = 0;
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, stagedHash))
                throw new TrustedToolchainValidationException("deploy_stage_mismatch", "The staged deployment file failed integrity verification.");
            return locked;
        }
        catch { locked.Dispose(); throw; }
    }

    private static FileStream OpenLockedNormalFile(string path, string code)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new TrustedToolchainValidationException(code, "An authority file is not a normal file.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 64L * 1024 * 1024) { stream.Dispose(); throw new TrustedToolchainValidationException(code, "An authority file size is invalid."); }
        return stream;
    }

    private static void DeleteOwnedStage(string? path, string root)
    {
        if (path is null) return;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.GetFullPath(path);
            if (full.StartsWith(fullRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Path.GetFileName(full).StartsWith(".hermes-rpi-deploy-", StringComparison.Ordinal)
                && Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(full, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static bool Succeeded(OwnedToolchainProcessResult result) =>
        result.ExitCode == 0 && result.FailureCode is null && !result.WasCancelled && !result.TimedOut;

    private EmbeddedHostOperationResult Result(OwnedToolchainProcessResult process, string operation, string summary, string? workspace = null) =>
        new(Succeeded(process), operation, summary, process.FailureCode, process.ExitCode,
            Redact(process.StandardOutput, workspace), Redact(process.StandardError, workspace),
            process.OutputTruncated, process.DroppedCharacters, process.WasCancelled, process.TimedOut, [], []);

    private string Redact(string text, string? workspace)
    {
        foreach (var path in new[] { _credentialRoot, _options.WorkbenchRoot, workspace ?? string.Empty }.Where(value => value.Length > 0).OrderByDescending(value => value.Length))
            text = text.Replace(path, "<trusted-path>", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return text;
    }

    private static OwnedToolchainProcessBounds Bounds(TimeSpan timeout) => new() { Timeout = timeout, CleanupTimeout = TimeSpan.FromSeconds(5), MaximumRetainedCharacters = 512 * 1024 };
    private static string ValidatePackageQuery(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return PackagePattern().IsMatch(value) ? value : throw new ArgumentException("Use one bounded package token.");
    }

    private static string ValidateRemotePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return RemotePathPattern().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal)
            ? value : throw new ArgumentException("Use one absolute remote file path.");
    }

    private static void ValidateTarget(RaspberryPiTrustedHost target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!HostPattern().IsMatch(target.Host) || !UserPattern().IsMatch(target.User) || target.Port is < 1 or > 65535
            || !AlgorithmPattern().IsMatch(target.HostKey.Algorithm) || !FingerprintPattern().IsMatch(target.HostKey.Sha256Fingerprint))
            throw new ArgumentException("The trusted-host identity is invalid.");
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex HostPattern();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,31}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex UserPattern();
    [GeneratedRegex("^(?:ssh-ed25519|ssh-rsa|rsa-sha2-256|rsa-sha2-512|ecdsa-sha2-nistp(?:256|384|521))$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex AlgorithmPattern();
    [GeneratedRegex("^SHA256:[A-Za-z0-9+/]{20,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex FingerprintPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.+-]{0,79}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex PackagePattern();
    [GeneratedRegex("^/[A-Za-z0-9_./-]{1,240}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex RemotePathPattern();

    private sealed class SshAuthorityLease(
        string identityPath, string privateKnownHostsPath, string privateDirectory,
        FileStream identity, FileStream sourceKnownHosts, FileStream privateKnownHosts) : IDisposable
    {
        public string IdentityPath { get; } = identityPath;
        public string PrivateKnownHostsPath { get; } = privateKnownHostsPath;
        public void Dispose()
        {
            privateKnownHosts.Dispose(); sourceKnownHosts.Dispose(); identity.Dispose();
            try { if (Directory.Exists(privateDirectory) && (File.GetAttributes(privateDirectory) & FileAttributes.ReparsePoint) == 0) Directory.Delete(privateDirectory, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
