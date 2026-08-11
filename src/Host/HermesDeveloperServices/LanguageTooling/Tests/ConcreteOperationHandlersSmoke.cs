#if HERMES_LANGUAGE_TOOLING_TESTS
using HermesDeveloperServices.LanguageTooling.Operations;

namespace HermesDeveloperServices.LanguageTooling;

internal static class ConcreteOperationHandlersSmoke
{
    internal static async Task RunAsync()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"HermesConcreteHandlers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, "native"));
        Directory.CreateDirectory(Path.Combine(workspace, "Blink"));
        Directory.CreateDirectory(Path.Combine(workspace, "dotnet"));
        try
        {
            File.WriteAllText(Path.Combine(workspace, "native", "main.c"), "int main(void) { return 0; }");
            File.WriteAllText(Path.Combine(workspace, "Blink", "Blink.ino"), "void setup() {} void loop() {}");
            File.WriteAllText(Path.Combine(workspace, "dotnet", "Dragon.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(workspace, "dotnet", "Program.cs"), "Console.WriteLine(\"dragon\");");

            var dotnet = new DotnetLanguageToolingOperationHandler(workspace);
            var dotnetBuild = await dotnet.ExecuteAsync(new CompileLanguageToolingRequest(
                1, "dotnet:compile:1", "workspace:1", "dotnet", "dotnet/Dragon.csproj", "release"), CancellationToken.None);
            Require(dotnetBuild.Succeeded, $"Concrete .NET handler did not complete the fixed build operation ({dotnetBuild.Code}: {dotnetBuild.SafeMessage}).");
            var dotnetTests = await dotnet.ExecuteAsync(new RunLanguageToolingTestsRequest(
                1, "dotnet:tests:1", "workspace:1", "dotnet", "dotnet/Dragon.csproj"), CancellationToken.None);
            Require(dotnetTests.Succeeded, $"Concrete .NET handler did not complete the fixed test operation ({dotnetTests.Code}: {dotnetTests.SafeMessage}).");

            var gccAuthority = new FakeGccAuthority();
            var gcc = new GccLanguageToolingOperationHandler(gccAuthority, workspace);
            var gccResult = await gcc.ExecuteAsync(new CompileLanguageToolingRequest(
                1, "gcc:compile:1", "workspace:1", "gcc", "native/main.c", "release"), CancellationToken.None);
            Require(gccResult.Succeeded, "Concrete GCC handler did not return its verified build result.");
            Require(gccAuthority.StartedWorkspace == Path.GetFullPath(workspace), "GCC handler changed the trusted workspace root.");
            var gccRequest = gccAuthority.Request ?? throw new InvalidOperationException("GCC authority was not invoked.");
            Require(gccRequest is { TargetPath: "native/main.c", Configuration: BuildConfiguration.Release },
                "GCC handler did not preserve the typed target/configuration.");
            Require(gccRequest.OutputPath.Replace('\\', '/').StartsWith(".hermes-gcc-release-", StringComparison.Ordinal),
                "GCC handler did not choose a private host-owned output path.");
            Require(gccResult.ArtifactPaths is { Count: 1 }
                && gccResult.ArtifactPaths[0] == gccRequest.OutputPath.Replace('\\', '/'),
                "GCC handler did not return only its host-owned artifact path.");

            var arduinoAuthority = new FakeArduinoAuthority();
            var arduino = new ArduinoLanguageToolingOperationHandler(arduinoAuthority, workspace);
            var inspection = await arduino.ExecuteAsync(new InspectLanguageToolingProjectRequest(
                1, "arduino:inspect:1", "workspace:1", "arduino", "Blink"), CancellationToken.None);
            Require(inspection.Succeeded && inspection.SafeMessage.Contains("1 sketch file", StringComparison.Ordinal),
                "Concrete Arduino project inspection did not verify the bounded sketch.");
            var arduinoResult = await arduino.ExecuteAsync(new CompileLanguageToolingRequest(
                1, "arduino:compile:1", "workspace:1", "arduino", "Blink", "check", "arduino:avr:uno"), CancellationToken.None);
            Require(arduinoResult.Succeeded, "Concrete Arduino handler did not return its verified compile result.");
            var arduinoRequest = arduinoAuthority.Request ?? throw new InvalidOperationException("Arduino authority was not invoked.");
            Require(arduinoRequest.SketchRelativePath == "Blink" && arduinoRequest.Fqbn == "arduino:avr:uno",
                "Arduino handler changed the typed sketch/FQBN.");
            Require(arduinoRequest.OutputDirectoryRelativePath.Replace('\\', '/').StartsWith(".hermes-arduino-release-", StringComparison.Ordinal),
                "Arduino handler did not choose a private host-owned output directory.");
            Require(arduinoRequest.Permission.Kind == EmbeddedPermissionKind.CompileWorkspace
                && arduinoRequest.Permission.Scope == ArduinoHostProvider.CompilePermissionScope(arduinoRequest)
                && arduinoRequest.Permission.ApprovalId.Length == 32,
                "Arduino handler did not bind its host-issued compile permission to the exact operation.");

            var missingBoard = await arduino.ExecuteAsync(new CompileLanguageToolingRequest(
                1, "arduino:compile:2", "workspace:1", "arduino", "Blink", "check"), CancellationToken.None);
            Require(!missingBoard.Succeeded && missingBoard.Code == "board-required" && arduinoAuthority.Invocations == 1,
                "Arduino handler did not reject an unbound board request before provider invocation.");
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeGccAuthority : IGccCompilerAuthority
    {
        public string? StartedWorkspace { get; private set; }
        public GccBuildRequest? Request { get; private set; }

        public ValueTask StartAsync(string workspaceRoot, CancellationToken cancellationToken)
        {
            StartedWorkspace = workspaceRoot;
            return ValueTask.CompletedTask;
        }

        public Task<BuildResult> BuildAsync(GccBuildRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            var artifact = Path.Combine(request.WorkspaceRoot, request.OutputPath);
            File.WriteAllText(artifact, "verified-gcc-artifact");
            return Task.FromResult(new BuildResult(
                true,
                0,
                [new BuildDiagnostic(
                    Path.Combine(request.WorkspaceRoot, "native", "main.c"),
                    new SourceRange(new SourcePosition(1, 1), new SourcePosition(1, 2)),
                    DiagnosticSeverity.Warning,
                    "gcc-warning",
                    "bounded warning",
                    null,
                    "gcc")],
                new BuildOutput(string.Empty, string.Empty, false, 0),
                false,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeArduinoAuthority : IArduinoCompilerAuthority
    {
        public ArduinoCompileRequest? Request { get; private set; }
        public int Invocations { get; private set; }

        public Task EnsureAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<EmbeddedHostOperationResult> CompileAsync(ArduinoCompileRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Invocations += 1;
            var artifactPath = Path.Combine(request.WorkspaceRoot, request.OutputDirectoryRelativePath, "Blink.hex");
            File.WriteAllText(artifactPath, "verified-arduino-artifact");
            var relativeArtifact = Path.GetRelativePath(request.WorkspaceRoot, artifactPath).Replace('\\', '/');
            return Task.FromResult(new EmbeddedHostOperationResult(
                true, "compile", "The Arduino sketch compiled.", null, 0,
                string.Empty, string.Empty, false, 0, false, false,
                [new EmbeddedDiagnostic("Blink/Blink.ino", 1, 1, EmbeddedDiagnosticSeverity.Info, "arduino", "compiled")],
                [relativeArtifact]));
        }
    }
}
#endif
