#if HERMES_LANGUAGE_TOOLING_TESTS
using System.Text.Json;
using HermesDeveloperServices.LanguageTooling.Gcc;
using HermesDeveloperServices.LanguageTooling.Operations;

namespace HermesDeveloperServices.LanguageTooling;

internal static class ContainerGccAuthoritySmoke
{
    private const string ExpectedImage = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherImage = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal static async Task RunAsync()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"HermesContainerGcc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, "native"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "native", "main.c"), "int main(void) { return 0; }");
            await File.WriteAllTextAsync(Path.Combine(workspace, "native", "main.cpp"), "int main() { return 0; }");

            var runner = new FakeContainerRunner(workspace, ExpectedImage);
            await using var authority = CreateAuthority(workspace, runner, ExpectedImage);
            var evidence = await authority.InspectAsync(workspace, CancellationToken.None);
            Require(evidence is [{ Availability: LanguageToolingCapabilityState.Available }],
                "The container GCC authority did not project verified runtime evidence.");

            var handler = new GccLanguageToolingOperationHandler(authority, workspace);
            var cpp = await handler.ExecuteAsync(new CompileLanguageToolingRequest(
                1, "gcc:container:cpp", "workspace:1", "gcc", "native/main.cpp", "release"), CancellationToken.None);
            Require(cpp.Succeeded && cpp.ArtifactPaths is { Count: 1 },
                "The container G++ authority did not produce a verified artifact.");
            Require(cpp.Diagnostics is [{ FilePath: "native/main.cpp", Severity: "warning", Code: "-Wbounded" }],
                "The container G++ authority did not project bounded diagnostics.");
            Require(runner.CompileInvocations.Single(arguments => arguments.Contains("native/main.cpp")) is var cppArguments
                && cppArguments.Contains("/usr/bin/g++")
                && cppArguments.Contains("-std=c++20")
                && cppArguments.Contains("-O2")
                && cppArguments.Contains("-DNDEBUG"),
                "The C++ operation did not use the fixed release-mode G++ contract.");

            var c = await handler.ExecuteAsync(new CompileLanguageToolingRequest(
                1, "gcc:container:c", "workspace:1", "gcc", "native/main.c", "debug"), CancellationToken.None);
            Require(c.Succeeded, "The container GCC authority did not compile a C source.");
            Require(runner.CompileInvocations.Single(arguments => arguments.Contains("native/main.c")) is var cArguments
                && cArguments.Contains("/usr/bin/gcc")
                && cArguments.Contains("-std=c17")
                && cArguments.Contains("-O0")
                && cArguments.Contains("-g"),
                "The C operation did not use the fixed debug-mode GCC contract.");

            await using var wrongImage = CreateAuthority(
                workspace,
                new FakeContainerRunner(workspace, OtherImage),
                ExpectedImage);
            var wrongImageEvidence = await wrongImage.InspectAsync(workspace, CancellationToken.None);
            Require(wrongImageEvidence is [{ Availability: LanguageToolingCapabilityState.Unavailable, Code: "gcc-container-binding-mismatch" }],
                "The container GCC authority accepted the wrong immutable image.");

            await using var wrongMount = CreateAuthority(
                workspace,
                new FakeContainerRunner(Path.Combine(workspace, "wrong"), ExpectedImage),
                ExpectedImage);
            var wrongMountEvidence = await wrongMount.InspectAsync(workspace, CancellationToken.None);
            Require(wrongMountEvidence is [{ Availability: LanguageToolingCapabilityState.Unavailable, Code: "gcc-workspace-binding-mismatch" }],
                "The container GCC authority accepted the wrong workspace mount.");

            var truncatedRunner = new FakeContainerRunner(workspace, ExpectedImage) { TruncateCompileOutput = true };
            await using var truncatedAuthority = CreateAuthority(workspace, truncatedRunner, ExpectedImage);
            var truncatedHandler = new GccLanguageToolingOperationHandler(truncatedAuthority, workspace);
            var truncated = await truncatedHandler.ExecuteAsync(new CompileLanguageToolingRequest(
                1, "gcc:container:truncated", "workspace:1", "gcc", "native/main.cpp", "debug"), CancellationToken.None);
            Require(!truncated.Succeeded && truncated.Code == "gcc-output-limit" && truncated.ArtifactPaths is { Count: 0 },
                "The container GCC authority accepted truncated compiler output.");

            var cancelledRunner = new FakeContainerRunner(workspace, ExpectedImage) { CancelCompile = true };
            await using var cancelledAuthority = CreateAuthority(workspace, cancelledRunner, ExpectedImage);
            var cancelledHandler = new GccLanguageToolingOperationHandler(cancelledAuthority, workspace);
            var cancelled = await cancelledHandler.ExecuteAsync(new CompileLanguageToolingRequest(
                1, "gcc:container:cancelled", "workspace:1", "gcc", "native/main.cpp", "debug"), CancellationToken.None);
            Require(!cancelled.Succeeded && cancelled.Code == "gcc-build-cancelled" && cancelled.ArtifactPaths is { Count: 0 },
                "The container GCC authority did not preserve cancellation semantics.");

            var privateDirectories = Directory.EnumerateDirectories(workspace, ".hermes-gcc-release-*", SearchOption.TopDirectoryOnly).ToArray();
            Require(privateDirectories.All(directory => Directory.EnumerateFileSystemEntries(directory).Any()),
                "A failed container GCC operation retained an empty private output directory.");
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    private static HermesContainerGccToolingAuthority CreateAuthority(
        string workspace,
        IGccContainerProcessRunner runner,
        string expectedImage) => new(new HermesContainerGccToolingOptions
        {
            DockerExecutablePath = Path.Combine(workspace, "docker.exe"),
            ExpectedImageId = expectedImage,
            WorkspaceRoot = workspace,
        }, runner);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeContainerRunner(string mountSource, string actualImage) : IGccContainerProcessRunner
    {
        private const string ContainerId = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

        public List<IReadOnlyList<string>> CompileInvocations { get; } = [];
        public bool TruncateCompileOutput { get; init; }
        public bool CancelCompile { get; init; }

        public Task<GccContainerProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = timeout;
            if (arguments.SequenceEqual(["inspect", "--type", "container", "hermes"]))
            {
                var json = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = ContainerId,
                        Image = actualImage,
                        State = new { Running = true },
                        Mounts = new[] { new { Destination = "/workspace", RW = true, Source = mountSource } },
                    },
                });
                return Completed(json);
            }

            if (arguments.Contains("--version"))
                return Completed(arguments.Contains("/usr/bin/g++") ? "g++ (Debian 12.2.0) 12.2.0\n" : "gcc (Debian 12.2.0) 12.2.0\n");
            if (arguments.Contains("-dumpmachine")) return Completed("x86_64-linux-gnu\n");

            CompileInvocations.Add(arguments.ToArray());
            var outputIndex = arguments.ToList().IndexOf("-o");
            if (outputIndex < 0 || outputIndex + 1 >= arguments.Count)
                return Task.FromResult(new GccContainerProcessResult(2, string.Empty, string.Empty, false, 0, false, false, "process_failed"));
            var output = Path.Combine(workingDirectory, arguments[outputIndex + 1].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, [0x7f, (byte)'E', (byte)'L', (byte)'F']);
            var source = arguments.First(argument => argument.EndsWith(".c", StringComparison.OrdinalIgnoreCase)
                || argument.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase));
            var diagnostics = $"{source}:1:1: warning: bounded warning [-Wbounded]\n";
            return Task.FromResult(new GccContainerProcessResult(
                0,
                string.Empty,
                diagnostics,
                TruncateCompileOutput,
                TruncateCompileOutput ? 1 : 0,
                CancelCompile,
                false,
                CancelCompile ? "process_cancelled" : null));
        }

        private static Task<GccContainerProcessResult> Completed(string output) => Task.FromResult(
            new GccContainerProcessResult(0, output, string.Empty, false, 0, false, false, null));
    }
}
#endif
