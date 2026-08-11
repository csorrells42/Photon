using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HermesDeveloperServices.LanguageTooling.Python;

public sealed record HermesContainerPythonToolingOptions
{
    public required string DockerExecutablePath { get; init; }
    public required string ExpectedImageId { get; init; }
    public required string WorkspaceRoot { get; init; }
    public string ContainerName { get; init; } = "hermes";
    public string ContainerWorkspaceRoot { get; init; } = "/workspace";
    public string PythonExecutablePath { get; init; } = "/opt/hermes/.venv/bin/python";
    public TimeSpan InspectionTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaximumRetainedCharacters { get; init; } = 256 * 1024;
}

/// <summary>
/// Fixed adapter for the launcher-approved Hermes runtime container. It accepts only immutable
/// source snapshots for syntax checks and a validated workspace-relative target for explicit
/// unittest execution. No renderer-controlled executable, image, container, arguments,
/// environment, URL, shell text, or host path crosses this seam.
/// </summary>
public sealed class HermesContainerPythonToolingAuthority : IPythonToolingAuthority, IPythonTestAuthority
{
    private const string VersionScript = "import json,sys;print(json.dumps({'version':'.'.join(map(str,sys.version_info[:3]))}))";
    private const string SyntaxScript = """
        import base64,json,sys
        payload=json.load(sys.stdin)
        diagnostics=[]
        for item in payload.get('sources',[]):
            path=item['path']
            content=base64.b64decode(item['content'],validate=True)
            try:
                compile(content,path,'exec',dont_inherit=True,optimize=0)
            except SyntaxError as error:
                line=max(1,int(error.lineno or 1))
                column=max(1,int(error.offset or 1))
                end_line=max(line,int(error.end_lineno or line))
                end_column=max(column,int(error.end_offset or column+1))
                diagnostics.append({'path':path,'line':line,'column':column,'endLine':end_line,'endColumn':end_column})
        print(json.dumps({'succeeded':not diagnostics,'diagnostics':diagnostics},separators=(',',':')))
        """;
    private const string TestScript = """
        import hashlib,importlib.util,io,json,os,sys,unittest
        payload=json.load(sys.stdin)
        target=payload['target']
        selection=payload.get('selection')
        sys.path.insert(0,'/workspace')
        loader=unittest.TestLoader()
        if target.lower().endswith('.py'):
            full=os.path.join('/workspace',target)
            parent=os.path.dirname(full)
            sys.path.insert(0,parent)
            name='hermes_python_test_'+hashlib.sha256(target.encode('utf-8')).hexdigest()[:16]
            spec=importlib.util.spec_from_file_location(name,full)
            if spec is None or spec.loader is None:
                raise RuntimeError('test-module-unavailable')
            module=importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            suite=loader.loadTestsFromModule(module)
        else:
            start=os.path.join('/workspace',target)
            sys.path.insert(0,start)
            suite=loader.discover(start_dir=start,pattern='test*.py',top_level_dir=start)
        if selection:
            def cases(value):
                for item in value:
                    if isinstance(item,unittest.TestSuite):
                        yield from cases(item)
                    else:
                        yield item
            selected=[item for item in cases(suite) if selection.lower() in item.id().lower()]
            suite=unittest.TestSuite(selected)
        stream=io.StringIO()
        result=unittest.TextTestRunner(stream=stream,verbosity=0).run(suite)
        print(json.dumps({'succeeded':result.wasSuccessful(),'testsRun':result.testsRun,'failures':len(result.failures),'errors':len(result.errors),'skipped':len(result.skipped)},separators=(',',':')))
        """;

    private readonly HermesContainerPythonToolingOptions _options;
    private readonly string _dockerExecutable;
    private readonly string _workspaceRoot;
    private readonly string _expectedImageId;

    public HermesContainerPythonToolingAuthority(HermesContainerPythonToolingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dockerExecutable = Path.GetFullPath(options.DockerExecutablePath);
        if (!Path.IsPathFullyQualified(_dockerExecutable)
            || !Path.GetFileName(_dockerExecutable).Equals(OperatingSystem.IsWindows() ? "docker.exe" : "docker", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(_dockerExecutable))
            throw new ArgumentException("The fixed Docker executable is unavailable.", nameof(options));
        _workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(options.WorkspaceRoot, "python_workspace");
        _expectedImageId = RequireImageId(options.ExpectedImageId);
        if (string.IsNullOrWhiteSpace(options.ContainerName) || options.ContainerName.Length > 64
            || !options.ContainerName.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            throw new ArgumentException("The fixed container name is invalid.", nameof(options));
        if (options.ContainerWorkspaceRoot != "/workspace" || options.PythonExecutablePath != "/opt/hermes/.venv/bin/python")
            throw new ArgumentException("The Python container paths must match the fixed Hermes runtime contract.", nameof(options));
        if (options.InspectionTimeout <= TimeSpan.Zero || options.InspectionTimeout > TimeSpan.FromSeconds(30)
            || options.OperationTimeout <= TimeSpan.Zero || options.OperationTimeout > TimeSpan.FromMinutes(10)
            || options.MaximumRetainedCharacters is < 4_096 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
    }

    public async ValueTask<PythonToolingRuntimeReceipt> VerifyAsync(CancellationToken cancellationToken)
    {
        var runtime = await InspectRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var probe = await RunDockerAsync(
            ["exec", "--user", "10000", "--workdir", _options.ContainerWorkspaceRoot, runtime.ContainerId,
                _options.PythonExecutablePath, "-I", "-c", VersionScript],
            null,
            _options.InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (probe.ExitCode != 0 || probe.WasTruncated)
            throw new PythonToolingAuthorityException("python-runtime-probe-failed", "The fixed Hermes Python runtime did not answer its identity probe.");
        string version;
        try
        {
            using var document = JsonDocument.Parse(probe.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("version", out var versionValue)
                || versionValue.ValueKind != JsonValueKind.String)
                throw new JsonException();
            version = PythonToolingContractPolicy.RequireVersion(versionValue.GetString() ?? string.Empty);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new PythonToolingAuthorityException("python-runtime-probe-invalid", "The fixed Hermes Python runtime returned an invalid identity probe.");
        }
        var receipt = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{_expectedImageId}\n{_options.PythonExecutablePath}\n{version}\n")));
        return new PythonToolingRuntimeReceipt("container-image", _expectedImageId, version, receipt);
    }

    public async ValueTask<PythonSyntaxCheckOutcome> CheckSyntaxAsync(
        PythonToolingRuntimeReceipt verifiedRuntime,
        IReadOnlyList<PythonSourceSnapshot> sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifiedRuntime);
        ArgumentNullException.ThrowIfNull(sources);
        var current = await RequireCurrentReceiptAsync(verifiedRuntime, cancellationToken).ConfigureAwait(false);
        var runtime = await InspectRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new
        {
            sources = sources.Select(source => new
            {
                path = source.RelativePath,
                content = Convert.ToBase64String(source.Content.Span),
            }),
        });
        var process = await RunDockerAsync(
            ["exec", "-i", "--user", "10000", "--workdir", _options.ContainerWorkspaceRoot, runtime.ContainerId,
                _options.PythonExecutablePath, "-I", "-c", SyntaxScript],
            payload,
            _options.OperationTimeout,
            cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || process.WasTruncated)
            throw new PythonToolingAuthorityException("python-syntax-process-failed", "The fixed Hermes Python syntax process did not complete successfully.");

        var diagnostics = new List<PythonSyntaxDiagnostic>();
        bool succeeded;
        try
        {
            using var document = JsonDocument.Parse(process.StandardOutput, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            succeeded = root.GetProperty("succeeded").GetBoolean();
            foreach (var item in root.GetProperty("diagnostics").EnumerateArray())
            {
                diagnostics.Add(new PythonSyntaxDiagnostic(
                    item.GetProperty("path").GetString() ?? string.Empty,
                    "error",
                    "syntax-error",
                    "Python reported a syntax error.",
                    item.GetProperty("line").GetInt32(),
                    item.GetProperty("column").GetInt32(),
                    item.GetProperty("endLine").GetInt32(),
                    item.GetProperty("endColumn").GetInt32()));
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new PythonToolingAuthorityException("python-syntax-result-invalid", "The fixed Hermes Python syntax process returned an invalid result.");
        }
        return new(current.ImmutableRuntimeId, current.ReceiptSha256, succeeded, diagnostics);
    }

    public async ValueTask<PythonTestOutcome> RunTestsAsync(
        PythonToolingRuntimeReceipt verifiedRuntime,
        string targetPath,
        string? selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifiedRuntime);
        var target = LanguageToolingRequestPolicy.RequireWorkspacePath(targetPath);
        var current = await RequireCurrentReceiptAsync(verifiedRuntime, cancellationToken).ConfigureAwait(false);
        var runtime = await InspectRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new { target, selection });
        var process = await RunDockerAsync(
            ["exec", "-i", "--user", "10000", "--workdir", _options.ContainerWorkspaceRoot, runtime.ContainerId,
                _options.PythonExecutablePath, "-I", "-c", TestScript],
            payload,
            _options.OperationTimeout,
            cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || process.WasTruncated)
            throw new PythonToolingAuthorityException("python-test-process-failed", "The fixed Hermes Python test process did not complete successfully.");
        try
        {
            using var document = JsonDocument.Parse(process.StandardOutput, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            return new PythonTestOutcome(
                current.ImmutableRuntimeId,
                current.ReceiptSha256,
                root.GetProperty("succeeded").GetBoolean(),
                root.GetProperty("testsRun").GetInt32(),
                root.GetProperty("failures").GetInt32(),
                root.GetProperty("errors").GetInt32(),
                root.GetProperty("skipped").GetInt32());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new PythonToolingAuthorityException("python-test-result-invalid", "The fixed Hermes Python test process returned an invalid result.");
        }
    }

    private async ValueTask<PythonToolingRuntimeReceipt> RequireCurrentReceiptAsync(
        PythonToolingRuntimeReceipt expected,
        CancellationToken cancellationToken)
    {
        var current = await VerifyAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.ImmutableRuntimeId, expected.ImmutableRuntimeId, StringComparison.Ordinal)
            || !string.Equals(current.ReceiptSha256, expected.ReceiptSha256, StringComparison.Ordinal))
            throw new PythonToolingAuthorityException("python-runtime-binding-mismatch", "The fixed Hermes Python runtime identity changed before execution.");
        return current;
    }

    private async Task<VerifiedContainer> InspectRuntimeAsync(CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(
            ["inspect", "--type", "container", _options.ContainerName],
            null,
            _options.InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.WasTruncated)
            throw new PythonToolingAuthorityException("python-container-unavailable", "The launcher-owned Hermes runtime container is unavailable.");
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
                throw new JsonException();
            var container = document.RootElement[0];
            var id = container.GetProperty("Id").GetString() ?? string.Empty;
            var image = RequireImageId(container.GetProperty("Image").GetString() ?? string.Empty);
            if (id.Length != 64 || !id.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9')
                || !string.Equals(image, _expectedImageId, StringComparison.Ordinal)
                || container.GetProperty("State").GetProperty("Running").ValueKind != JsonValueKind.True)
                throw new PythonToolingAuthorityException("python-container-binding-mismatch", "The running Hermes container does not match the launcher-approved runtime identity.");
            var matchingMounts = container.GetProperty("Mounts").EnumerateArray().Where(mount =>
                mount.GetProperty("Destination").GetString() == _options.ContainerWorkspaceRoot).ToArray();
            if (matchingMounts.Length != 1
                || matchingMounts[0].GetProperty("RW").ValueKind != JsonValueKind.True
                || !PathsEqual(matchingMounts[0].GetProperty("Source").GetString() ?? string.Empty, _workspaceRoot))
                throw new PythonToolingAuthorityException("python-workspace-binding-mismatch", "The Hermes container is not bound to the active trusted workspace.");
            return new VerifiedContainer(id, image);
        }
        catch (PythonToolingAuthorityException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new PythonToolingAuthorityException("python-container-inspection-invalid", "Docker returned invalid Hermes runtime evidence.");
        }
    }

    private async Task<ProcessResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = _dockerExecutable,
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_dockerExecutable)!,
        };
        start.Environment.Clear();
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start())
            throw new PythonToolingAuthorityException("python-container-process-failed", "The fixed Docker client could not start.");
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stdout = ReadBoundedAsync(process.StandardOutput, linked.Token);
        var stderr = ReadBoundedAsync(process.StandardError, linked.Token);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), linked.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new PythonToolingAuthorityException("python-container-timeout", "The fixed Python container operation exceeded its time limit.");
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output.Text, error.Text, output.Truncated || error.Truncated);
    }

    private async Task<BoundedText> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(16 * 1024, _options.MaximumRetainedCharacters));
        var buffer = new char[8 * 1024];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = _options.MaximumRetainedCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining) truncated = true;
        }
        return new BoundedText(builder.ToString(), truncated);
    }

    private static string RequireImageId(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || !value[7..].All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new ArgumentException("The Hermes runtime image ID is invalid.", nameof(value));
        return value;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private sealed record VerifiedContainer(string ContainerId, string ImageId);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool WasTruncated);
    private sealed record BoundedText(string Text, bool Truncated);
}
