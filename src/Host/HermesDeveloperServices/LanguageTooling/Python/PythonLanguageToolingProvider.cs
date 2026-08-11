namespace HermesDeveloperServices.LanguageTooling.Python;

public static class PythonLanguageToolingProvider
{
    public static PythonLanguageToolingComponents CreateFailClosed(string workspaceRoot)
    {
        var authority = new UnavailablePythonToolingAuthority();
        return new(new PythonToolingEvidenceSource(authority), new PythonLanguageToolingOperationHandler(authority, null, workspaceRoot));
    }

    public static PythonLanguageToolingComponents CreateReceiptBound(
        string workspaceRoot,
        IPythonToolingAuthority authority,
        IPythonTestAuthority? testAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return new(
            new PythonToolingEvidenceSource(authority, testAuthority),
            new PythonLanguageToolingOperationHandler(authority, testAuthority, workspaceRoot));
    }
}

public sealed class PythonToolingEvidenceSource(
    IPythonToolingAuthority authority,
    IPythonTestAuthority? testAuthority = null) : ILanguageToolingEvidenceSource
{
    private readonly IPythonToolingAuthority _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    private readonly IPythonTestAuthority? _testAuthority = testAuthority;

    public string ProviderId => LanguageToolingCatalog.Python;

    public IReadOnlyCollection<string> CapabilityIds { get; } = ["python.project", "python.compiler", "python.tests"];

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        _ = workspaceRoot;
        var project = new LanguageToolingCapabilityStatus(
            "python.project",
            LanguageToolingCapabilityState.Available,
            "trusted-host-project-inspection",
            "The trusted host can inspect bounded Python project structure without executing workspace code.");
        try
        {
            var receipt = await _authority.VerifyAsync(cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(receipt);
            if (_testAuthority is not null)
            {
                var testReceipt = await _testAuthority.VerifyAsync(cancellationToken).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(testReceipt);
                if (!string.Equals(testReceipt.ImmutableRuntimeId, receipt.ImmutableRuntimeId, StringComparison.Ordinal)
                    || !string.Equals(testReceipt.ReceiptSha256, receipt.ReceiptSha256, StringComparison.Ordinal))
                    throw new PythonToolingAuthorityException(
                        "python-test-runtime-binding-mismatch",
                        "The Python test authority is not bound to the verified syntax runtime.");
            }
            return
            [
                project,
                new("python.compiler", LanguageToolingCapabilityState.Available, "verified-pinned-runtime",
                    "The trusted host verified an immutable receipt-bound Python syntax authority.", receipt.PythonVersion),
                _testAuthority is null
                    ? Unavailable("python.tests", "python-tests-not-provisioned", "No explicit Python test authority is provisioned.")
                    : new("python.tests", LanguageToolingCapabilityState.Available, "verified-pinned-test-runtime",
                        "The trusted host verified an explicit Python unittest authority.", receipt.PythonVersion),
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is PythonToolingAuthorityException
            or ArgumentException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
        {
            var code = exception is PythonToolingAuthorityException authorityException
                ? authorityException.Code
                : "python-authority-verification-failed";
            return new[] { project }.Concat(CapabilityIds.Where(id => id != "python.project").Select(id => Unavailable(
                id,
                code,
                "No immutable receipt-bound Python tooling authority is available."))).ToArray();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static LanguageToolingCapabilityStatus Unavailable(string id, string code, string message) => new(
        id,
        LanguageToolingCapabilityState.Unavailable,
        KnownPinnedToolchainEvidenceSource.SafeCode(code, "python-runtime-not-provisioned"),
        KnownPinnedToolchainEvidenceSource.SafeMessage(message, "The pinned Python runtime is unavailable."));
}

public sealed class PythonLanguageToolingOperationHandler : ILanguageToolingOperationHandler
{
    private readonly IPythonToolingAuthority _authority;
    private readonly IPythonTestAuthority? _testAuthority;
    private readonly PythonProjectInspector _inspector;

    public PythonLanguageToolingOperationHandler(
        IPythonToolingAuthority authority,
        IPythonTestAuthority? testAuthority,
        string workspaceRoot)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _testAuthority = testAuthority;
        _inspector = new PythonProjectInspector(workspaceRoot);
    }

    public string ProviderId => LanguageToolingCatalog.Python;

    public IReadOnlyCollection<string> Operations { get; } = ["inspect-project", "compile", "run-tests"];

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken)
    {
        var validated = LanguageToolingRequestPolicy.Validate(request);
        if (validated.ProviderId != ProviderId)
            throw new LanguageToolingRequestException("operation-mismatch", "The Python handler accepts only typed Python requests.");
        return validated switch
        {
            InspectLanguageToolingProjectRequest inspect => InspectProject(inspect),
            CompileLanguageToolingRequest compile => await CheckSyntaxAsync(compile, cancellationToken).ConfigureAwait(false),
            RunLanguageToolingTestsRequest tests => await RunTestsAsync(tests, cancellationToken).ConfigureAwait(false),
            _ => throw new LanguageToolingRequestException(
                "operation-mismatch", "The Python handler accepts only project inspection, syntax-check, and test requests."),
        };
    }

    private async ValueTask<LanguageToolingOperationResult> RunTestsAsync(
        RunLanguageToolingTestsRequest request,
        CancellationToken cancellationToken)
    {
        if (_testAuthority is null)
            return new(false, "python-tests-not-provisioned", "No explicit Python test authority is provisioned.");
        try
        {
            var inspection = _inspector.Inspect(request.TargetPath);
            var receipt = await _testAuthority.VerifyAsync(cancellationToken).ConfigureAwait(false);
            var outcome = await _testAuthority.RunTestsAsync(
                receipt,
                inspection.SourcePaths.Count == 1 ? inspection.SourcePaths[0] : request.TargetPath,
                request.Selection,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(outcome.ImmutableRuntimeId, receipt.ImmutableRuntimeId, StringComparison.Ordinal)
                || !string.Equals(outcome.ReceiptSha256, receipt.ReceiptSha256, StringComparison.Ordinal))
                throw new PythonToolingAuthorityException("python-runtime-binding-mismatch", "The Python test result was not bound to the verified runtime receipt.");
            if (outcome.TestsRun < 0 || outcome.Failures < 0 || outcome.Errors < 0 || outcome.Skipped < 0
                || outcome.Failures + outcome.Errors > outcome.TestsRun)
                throw new PythonToolingAuthorityException("python-test-result-invalid", "The Python test authority returned invalid counters.");
            if (outcome.TestsRun == 0)
                return new(false, "python-no-tests", "The Python unittest authority found no tests in the selected target.");
            return new(
                outcome.Succeeded,
                outcome.Succeeded ? "ok" : "python-tests-failed",
                outcome.Succeeded
                    ? $"Python unittest passed {outcome.TestsRun} test{(outcome.TestsRun == 1 ? string.Empty : "s")}."
                    : $"Python unittest ran {outcome.TestsRun} tests with {outcome.Failures} failure{(outcome.Failures == 1 ? string.Empty : "s")} and {outcome.Errors} error{(outcome.Errors == 1 ? string.Empty : "s")}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is PythonToolingAuthorityException
            or TrustedToolchainValidationException or IOException or UnauthorizedAccessException
            or ArgumentException or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException)
        {
            var code = exception is PythonToolingAuthorityException authorityException
                ? authorityException.Code
                : "python-tests-failed";
            return new(false, code, "The explicit Python test authority could not complete safely.");
        }
    }

    private LanguageToolingOperationResult InspectProject(InspectLanguageToolingProjectRequest request)
    {
        try
        {
            var inspection = _inspector.Inspect(request.ProjectPath);
            var markerSummary = inspection.MarkerNames.Count == 0
                ? "no recognized package marker"
                : $"{inspection.MarkerNames.Count} recognized package marker{(inspection.MarkerNames.Count == 1 ? string.Empty : "s")}";
            return new(true, "ok",
                $"Verified a bounded {inspection.ProjectKind} with {inspection.SourcePaths.Count} Python source file{(inspection.SourcePaths.Count == 1 ? string.Empty : "s")} and {markerSummary}.");
        }
        catch (Exception exception) when (exception is PythonToolingAuthorityException
            or TrustedToolchainValidationException or IOException or UnauthorizedAccessException)
        {
            var code = exception is PythonToolingAuthorityException authorityException
                ? authorityException.Code
                : "python-project-invalid";
            return new(false, code, "The Python project could not be inspected safely.");
        }
    }

    private async ValueTask<LanguageToolingOperationResult> CheckSyntaxAsync(
        CompileLanguageToolingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Mode != "check")
            return new(false, "python-syntax-check-mode-required", "Python tooling currently supports syntax-check mode only.");
        try
        {
            var inspection = _inspector.Inspect(request.TargetPath);
            var snapshots = await _inspector.SnapshotAsync(inspection, cancellationToken).ConfigureAwait(false);
            var receipt = await _authority.VerifyAsync(cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(receipt);
            var outcome = await _authority.CheckSyntaxAsync(receipt, snapshots, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(outcome);
            if (!string.Equals(outcome.ImmutableRuntimeId, receipt.ImmutableRuntimeId, StringComparison.Ordinal)
                || !string.Equals(outcome.ReceiptSha256, receipt.ReceiptSha256, StringComparison.Ordinal))
                throw new PythonToolingAuthorityException("python-runtime-binding-mismatch", "The Python syntax result was not bound to the verified runtime receipt.");
            var paths = snapshots.Select(item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
            var diagnostics = PythonToolingContractPolicy.RequireDiagnostics(outcome.Diagnostics, paths);
            var projected = diagnostics.Select(item => new LanguageToolingDiagnostic(
                item.RelativePath,
                item.Severity,
                item.Code,
                item.Message,
                item.StartLine,
                item.StartColumn,
                item.EndLine,
                item.EndColumn)).ToArray();
            if (outcome.Succeeded && projected.Any(item => item.Severity == "error"))
                throw new PythonToolingAuthorityException("python-result-inconsistent", "The Python authority returned an inconsistent syntax result.");
            return new(
                outcome.Succeeded,
                outcome.Succeeded ? "ok" : "python-syntax-invalid",
                outcome.Succeeded
                    ? $"The immutable Python authority syntax-checked {snapshots.Count} source file{(snapshots.Count == 1 ? string.Empty : "s")}."
                    : "Python syntax errors were found.",
                projected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is PythonToolingAuthorityException
            or TrustedToolchainValidationException or IOException or UnauthorizedAccessException
            or ArgumentException or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException)
        {
            var code = exception is PythonToolingAuthorityException authorityException
                ? authorityException.Code
                : "python-authority-failed";
            return new(false, code, "The immutable Python syntax authority could not complete safely.");
        }
    }
}

internal sealed class UnavailablePythonToolingAuthority : IPythonToolingAuthority
{
    public ValueTask<PythonToolingRuntimeReceipt> VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new PythonToolingAuthorityException(
            "python-runtime-not-provisioned",
            "No immutable receipt-bound Python tooling authority is provisioned.");
    }

    public ValueTask<PythonSyntaxCheckOutcome> CheckSyntaxAsync(
        PythonToolingRuntimeReceipt verifiedRuntime,
        IReadOnlyList<PythonSourceSnapshot> sources,
        CancellationToken cancellationToken)
    {
        _ = verifiedRuntime;
        _ = sources;
        cancellationToken.ThrowIfCancellationRequested();
        throw new PythonToolingAuthorityException(
            "python-runtime-not-provisioned",
            "No immutable receipt-bound Python tooling authority is provisioned.");
    }
}
