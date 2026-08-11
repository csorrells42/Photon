using System.Diagnostics;

namespace HermesDeveloperServices;

public sealed class DotnetBuildRunner
{
    private readonly DotnetBuildRunnerOptions _options;

    public DotnetBuildRunner(DotnetBuildRunnerOptions? options = null)
    {
        _options = options ?? new DotnetBuildRunnerOptions();
        if (_options.MaximumRetainedCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The retained output limit must be positive.");
        }

        if (_options.MaximumDiagnostics <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The diagnostic limit must be positive.");
        }
    }

    /// <summary>
    /// Builds a validated target by launching the dotnet executable directly with a fixed argument
    /// vocabulary. Cancellation terminates only the process created by this invocation and its children.
    /// </summary>
    public async Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        (string WorkspaceRoot, string TargetPath) resolved;
        try
        {
            resolved = BuildTargetResolver.Resolve(request);
        }
        catch (BuildTargetValidationException exception)
        {
            return Failure(startedAt, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(startedAt, "path_validation_failed", "The build path could not be validated.");
        }

        var characterBudget = new SharedCharacterBudget(_options.MaximumRetainedCharacters);
        var standardOutput = new BoundedTextCapture(characterBudget);
        var standardError = new BoundedTextCapture(characterBudget);
        var diagnostics = new List<BuildDiagnostic>();
        var diagnosticSet = new HashSet<BuildDiagnostic>();
        var diagnosticGate = new object();
        Process? process = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = resolved.WorkspaceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add(resolved.TargetPath);
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("--tl:off");
            startInfo.ArgumentList.Add("--verbosity:minimal");
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add(request.Configuration.ToString());

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return Failure(startedAt, "dotnet_start_failed", "The dotnet build process could not be started.");
            }

            var stdoutTask = DrainAsync(process.StandardOutput, standardOutput, diagnostics, diagnosticSet, diagnosticGate);
            var stderrTask = DrainAsync(process.StandardError, standardError, diagnostics, diagnosticSet, diagnosticGate);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                StopOwnedProcess(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return CreateResult(
                    startedAt,
                    process.ExitCode,
                    diagnostics,
                    standardOutput,
                    standardError,
                    characterBudget,
                    wasCancelled: true,
                    failureCode: "build_cancelled",
                    failureMessage: "The build was cancelled.");
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            return CreateResult(
                startedAt,
                exitCode,
                diagnostics,
                standardOutput,
                standardError,
                characterBudget,
                wasCancelled: false,
                failureCode: exitCode == 0 ? null : "build_failed",
                failureMessage: exitCode == 0 ? null : "The build completed with errors.");
        }
        catch (OperationCanceledException)
        {
            if (process is { HasExited: false })
            {
                StopOwnedProcess(process);
            }

            return CreateResult(
                startedAt,
                process is { HasExited: true } ? process.ExitCode : null,
                diagnostics,
                standardOutput,
                standardError,
                characterBudget,
                wasCancelled: true,
                failureCode: "build_cancelled",
                failureMessage: "The build was cancelled.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            if (process is { HasExited: false })
            {
                StopOwnedProcess(process);
            }

            return CreateResult(
                startedAt,
                process is { HasExited: true } ? process.ExitCode : null,
                diagnostics,
                standardOutput,
                standardError,
                characterBudget,
                wasCancelled: false,
                failureCode: "build_process_failed",
                failureMessage: "The dotnet build process failed.");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task DrainAsync(
        StreamReader reader,
        BoundedTextCapture capture,
        List<BuildDiagnostic> diagnostics,
        HashSet<BuildDiagnostic> diagnosticSet,
        object diagnosticGate)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            capture.AppendLine(line);
            if (!MsBuildDiagnosticParser.TryParse(line, out var diagnostic) || diagnostic is null)
            {
                continue;
            }

            lock (diagnosticGate)
            {
                if (diagnostics.Count < _options.MaximumDiagnostics && diagnosticSet.Add(diagnostic))
                {
                    diagnostics.Add(diagnostic);
                }
            }
        }
    }

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The owned process exited between the check and the kill request.
        }
    }

    private static BuildResult CreateResult(
        DateTimeOffset startedAt,
        int? exitCode,
        IEnumerable<BuildDiagnostic> diagnostics,
        BoundedTextCapture standardOutput,
        BoundedTextCapture standardError,
        SharedCharacterBudget budget,
        bool wasCancelled,
        string? failureCode,
        string? failureMessage) =>
        new(
            Succeeded: exitCode == 0 && !wasCancelled,
            ExitCode: exitCode,
            Diagnostics: BuildResult.Freeze(diagnostics),
            Output: new BuildOutput(
                standardOutput.ToString(),
                standardError.ToString(),
                budget.Dropped > 0,
                budget.Dropped),
            WasCancelled: wasCancelled,
            FailureCode: failureCode,
            FailureMessage: failureMessage,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow);

    private static BuildResult Failure(DateTimeOffset startedAt, string code, string message) =>
        new(
            Succeeded: false,
            ExitCode: null,
            Diagnostics: Array.Empty<BuildDiagnostic>(),
            Output: new BuildOutput(string.Empty, string.Empty, false, 0),
            WasCancelled: false,
            FailureCode: code,
            FailureMessage: message,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow);
}
