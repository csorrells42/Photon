using System.Security.Cryptography;
using System.Text;

namespace HermesDeveloperServices.LanguageTooling.Operations;

/// <summary>
/// Projects a typed Arduino compile intent onto the pinned Arduino authority. Upload is excluded
/// until the host contract supplies a reviewed opaque configured-port identity.
/// </summary>
public sealed class ArduinoLanguageToolingOperationHandler : ILanguageToolingOperationHandler
{
    private readonly IArduinoCompilerAuthority _authority;
    private readonly string _workspaceRoot;

    public ArduinoLanguageToolingOperationHandler(ArduinoProviderOptions options, string workspaceRoot)
        : this(new ArduinoCompilerAuthority(options), workspaceRoot)
    {
    }

    internal ArduinoLanguageToolingOperationHandler(IArduinoCompilerAuthority authority, string workspaceRoot)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "workspace");
    }

    public string ProviderId => LanguageToolingCatalog.Arduino;

    public IReadOnlyCollection<string> Operations { get; } = ["inspect-project", "compile"];

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProviderId != ProviderId)
            throw new LanguageToolingRequestException("operation-mismatch", "The Arduino handler accepts only typed Arduino requests.");
        if (request is InspectLanguageToolingProjectRequest inspect)
            return await InspectProjectAsync(inspect, cancellationToken).ConfigureAwait(false);
        if (request is not CompileLanguageToolingRequest compile)
            throw new LanguageToolingRequestException("operation-mismatch", "The Arduino handler accepts only project inspection and compile requests.");
        if (string.IsNullOrWhiteSpace(compile.BoardFqbn))
            return new(false, "board-required", "Select an exact verified Arduino board identifier before compiling.");

        var outputDirectory = TrustedToolchainPathPolicy.CreateOwnedTemporaryDirectory(
            _workspaceRoot, _workspaceRoot, ".hermes-arduino-release-", "arduino_release");
        var outputRelative = Path.GetRelativePath(_workspaceRoot, outputDirectory);
        var approvalId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(compile.RequestId)))[..32];
        var seed = new ArduinoCompileRequest(
            _workspaceRoot,
            compile.TargetPath,
            outputRelative,
            compile.BoardFqbn,
            new EmbeddedOperationPermission(EmbeddedPermissionKind.CompileWorkspace, string.Empty, approvalId));
        var authorized = seed with
        {
            Permission = seed.Permission with { Scope = ArduinoHostProvider.CompilePermissionScope(seed) },
        };
        try
        {
            var result = await _authority.CompileAsync(authorized, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded) GccLanguageToolingOperationHandler.DeleteEmptyReleaseDirectory(_workspaceRoot, outputDirectory, ".hermes-arduino-release-");
            return new LanguageToolingOperationResult(
                result.Succeeded,
                result.FailureCode ?? "ok",
                result.Summary,
                result.Diagnostics.Select(diagnostic => new LanguageToolingDiagnostic(
                    diagnostic.RelativePath,
                    diagnostic.Severity.ToString().ToLowerInvariant(),
                    diagnostic.Code ?? string.Empty,
                    diagnostic.Message,
                    diagnostic.Line ?? 1,
                    diagnostic.Column ?? 1,
                    diagnostic.Line ?? 1,
                    (diagnostic.Column ?? 1) + 1)).ToArray(),
                result.WorkspaceRelativeArtifacts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            GccLanguageToolingOperationHandler.DeleteEmptyReleaseDirectory(_workspaceRoot, outputDirectory, ".hermes-arduino-release-");
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or TrustedToolchainValidationException)
        {
            GccLanguageToolingOperationHandler.DeleteEmptyReleaseDirectory(_workspaceRoot, outputDirectory, ".hermes-arduino-release-");
            return new(false, "arduino-authority-failed", "The pinned Arduino compile authority could not be established safely.");
        }
    }

    private async Task<LanguageToolingOperationResult> InspectProjectAsync(
        InspectLanguageToolingProjectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
            var target = TrustedToolchainPathPolicy.ResolveExistingTarget(_workspaceRoot, request.ProjectPath, "arduino_project");
            var sketches = Directory.Exists(target)
                ? Directory.EnumerateFiles(target, "*.ino", SearchOption.TopDirectoryOnly).Take(65).ToArray()
                : Path.GetExtension(target).Equals(".ino", StringComparison.OrdinalIgnoreCase) ? [target] : [];
            if (sketches.Length is 0 or > 64 || sketches.Any(path =>
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 4 * 1024 * 1024))
                return new(false, "arduino-project-invalid", "The selected target is not a bounded Arduino sketch project.");
            return new(true, "ok", $"The pinned Arduino authority verified a project with {sketches.Length} sketch file{(sketches.Length == 1 ? string.Empty : "s")}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or TrustedToolchainValidationException)
        {
            return new(false, "arduino-project-invalid", "The pinned Arduino project could not be inspected safely.");
        }
    }
}

internal interface IArduinoCompilerAuthority
{
    Task EnsureAvailableAsync(CancellationToken cancellationToken);

    Task<EmbeddedHostOperationResult> CompileAsync(ArduinoCompileRequest request, CancellationToken cancellationToken);
}

internal sealed class ArduinoCompilerAuthority(ArduinoProviderOptions options) : IArduinoCompilerAuthority
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ArduinoHostProvider? _provider;

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken) =>
        _ = await GetProviderAsync(cancellationToken).ConfigureAwait(false);

    public async Task<EmbeddedHostOperationResult> CompileAsync(
        ArduinoCompileRequest request,
        CancellationToken cancellationToken)
    {
        var provider = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
        return await provider.CompileAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArduinoHostProvider> GetProviderAsync(CancellationToken cancellationToken)
    {
        var provider = _provider;
        if (provider is null)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                provider = _provider ??= await ArduinoHostProvider.CreateAsync(options, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        return provider;
    }
}
