#if HERMES_PYTHON_TOOLING_TESTS
using System.Security.Cryptography;
using System.Text;
using HermesDeveloperServices.LanguageTooling;
using HermesDeveloperServices.LanguageTooling.Python;

var workspace = Path.Combine(Path.GetTempPath(), $"HermesPythonTooling-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(workspace, "package"));
Directory.CreateDirectory(Path.Combine(workspace, "package", "__pycache__"));
try
{
    File.WriteAllText(Path.Combine(workspace, "package", "pyproject.toml"), "[project]\nname='bounded'\n");
    File.WriteAllText(Path.Combine(workspace, "package", "valid.py"), "answer = 42\n");
    File.WriteAllText(Path.Combine(workspace, "package", "broken.py"), "def broken(:\n    pass\n");
    File.WriteAllText(Path.Combine(workspace, "package", "types.pyi"), "answer: int\n");
    File.WriteAllText(Path.Combine(workspace, "package", "__pycache__", "ignored.py"), "ignored = True\n");

    await FailClosedAsync();
    await ReceiptBoundAsync();
    await HostRegistryBoundaryAsync();
    await BoundsAsync();
    Console.WriteLine("Hermes Python language-tooling smoke passed.");
}
finally
{
    if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
}

async Task FailClosedAsync()
{
    var components = PythonLanguageToolingProvider.CreateFailClosed(workspace);
    var evidence = await components.EvidenceSource.InspectAsync(workspace, CancellationToken.None);
    Require(evidence.Count == 4
        && evidence.Single(item => item.CapabilityId == "python.project").Availability == LanguageToolingCapabilityState.Available
        && evidence.Where(item => item.CapabilityId != "python.project").All(item => item.Availability == LanguageToolingCapabilityState.Unavailable),
        "The default Python provider did not isolate safe project inspection from executable tooling.");
    Require(evidence.Where(item => item.CapabilityId != "python.project").All(item => item.Code == "python-runtime-not-provisioned"),
        "The default Python provider did not report its missing immutable authority.");
}

async Task ReceiptBoundAsync()
{
    var authority = new FakeAuthority();
    var components = PythonLanguageToolingProvider.CreateReceiptBound(workspace, authority);
    var handler = components.OperationHandler;

    var inspection = await handler.ExecuteAsync(new InspectLanguageToolingProjectRequest(
        1, "python:inspect:1", "workspace:1", "python", "package"), CancellationToken.None);
    Require(inspection.Succeeded
        && inspection.SafeMessage.Contains("3 Python source files", StringComparison.Ordinal)
        && inspection.SafeMessage.Contains("1 recognized package marker", StringComparison.Ordinal),
        "Python project inspection was not bounded and truthful.");

    var wrongMode = await handler.ExecuteAsync(new CompileLanguageToolingRequest(
        1, "python:check:0", "workspace:1", "python", "package", "release"), CancellationToken.None);
    Require(!wrongMode.Succeeded && wrongMode.Code == "python-syntax-check-mode-required" && authority.Checks == 0,
        "Python tooling treated syntax checking as compilation or executed the authority early.");

    var checkedResult = await handler.ExecuteAsync(new CompileLanguageToolingRequest(
        1, "python:check:1", "workspace:1", "python", "package", "check"), CancellationToken.None);
    Require(!checkedResult.Succeeded && checkedResult.Code == "python-syntax-invalid",
        "Python syntax errors were not represented as an unsuccessful structured result.");
    Require(checkedResult.Diagnostics is { Count: 1 }
        && checkedResult.Diagnostics[0] is
        {
            FilePath: "package/broken.py", Severity: "error", Code: "syntax-error",
            StartLine: 1, StartColumn: 12, EndLine: 1, EndColumn: 13,
        }, "Python syntax diagnostics were not projected structurally.");
    Require(authority.Sources is { Count: 3 }
        && authority.Sources.All(source => !Path.IsPathFullyQualified(source.RelativePath))
        && authority.Sources.All(source => SHA256.HashData(source.Content.Span)
            .SequenceEqual(Convert.FromHexString(source.Sha256))),
        "The Python authority received a raw host path or an unbound source snapshot.");

    try
    {
        _ = await handler.ExecuteAsync(new CompileLanguageToolingRequest(
            1, "python:check:2", "workspace:1", "python", "../outside.py", "check"), CancellationToken.None);
        throw new InvalidOperationException("Workspace traversal reached the Python handler.");
    }
    catch (LanguageToolingRequestException exception)
    {
        Require(exception.Code == "invalid-path", "Workspace traversal returned the wrong rejection code.");
    }

    authority.ReturnMismatchedBinding = true;
    var mismatch = await handler.ExecuteAsync(new CompileLanguageToolingRequest(
        1, "python:check:3", "workspace:1", "python", "package/valid.py", "check"), CancellationToken.None);
    Require(!mismatch.Succeeded && mismatch.Code == "python-runtime-binding-mismatch",
        "A syntax result from a different runtime identity was accepted.");
}

async Task HostRegistryBoundaryAsync()
{
    var authority = new FakeAuthority { ReturnNoDiagnostics = true };
    var components = PythonLanguageToolingProvider.CreateReceiptBound(workspace, authority);
    await using var registry = LanguageToolingRegistryFactory.Create(
        workspace,
        [components.EvidenceSource],
        [components.OperationHandler]);
    var bridge = new LanguageToolingHostBridge(registry);
    var compile = await bridge.HandleAsync(new CompileLanguageToolingRequest(
        1, "python:bridge:1", "workspace:1", "python", "package/valid.py", "check"));
    Require(compile.Succeeded && compile.Result?.Code == "ok", "The typed host bridge did not dispatch a verified Python syntax check.");

    var project = await bridge.HandleAsync(new InspectLanguageToolingProjectRequest(
        1, "python:bridge:2", "workspace:1", "python", "package"));
    Require(project.Succeeded && project.Result?.Code == "ok",
        "The typed host bridge did not dispatch bounded Python project inspection.");
}

async Task BoundsAsync()
{
    var tooMany = Path.Combine(workspace, "too-many");
    Directory.CreateDirectory(tooMany);
    for (var index = 0; index < 513; index++)
        File.WriteAllText(Path.Combine(tooMany, $"source{index:D3}.py"), "pass\n");
    var inspector = new PythonProjectInspector(workspace);
    try
    {
        _ = inspector.Inspect("too-many");
        throw new InvalidOperationException("The Python source-count bound was not enforced.");
    }
    catch (PythonToolingAuthorityException exception)
    {
        Require(exception.Code == "python-project-source-limit", "The Python source-count bound returned the wrong code.");
    }
    await Task.CompletedTask;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeAuthority : IPythonToolingAuthority
{
    private const string ImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Receipt = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public int Checks { get; private set; }
    public IReadOnlyList<PythonSourceSnapshot>? Sources { get; private set; }
    public bool ReturnMismatchedBinding { get; set; }
    public bool ReturnNoDiagnostics { get; set; }

    public ValueTask<PythonToolingRuntimeReceipt> VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PythonToolingRuntimeReceipt("container-image", ImageId, "3.12.11", Receipt));
    }

    public ValueTask<PythonSyntaxCheckOutcome> CheckSyntaxAsync(
        PythonToolingRuntimeReceipt verifiedRuntime,
        IReadOnlyList<PythonSourceSnapshot> sources,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Checks += 1;
        Sources = sources;
        var diagnostics = ReturnNoDiagnostics || sources.All(source => source.RelativePath != "package/broken.py")
            ? Array.Empty<PythonSyntaxDiagnostic>()
            :
            [
                new("package/broken.py", "error", "syntax-error", "invalid syntax", 1, 12, 1, 13),
            ];
        return ValueTask.FromResult(new PythonSyntaxCheckOutcome(
            ReturnMismatchedBinding
                ? "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                : verifiedRuntime.ImmutableRuntimeId,
            verifiedRuntime.ReceiptSha256,
            diagnostics.Length == 0,
            diagnostics));
    }
}
#endif
