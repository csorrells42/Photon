using System.Collections.ObjectModel;

namespace HermesDeveloperServices;

public enum BuildConfiguration
{
    Debug,
    Release,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record SourcePosition(int Line, int Column);

public sealed record SourceRange(SourcePosition Start, SourcePosition End);

public sealed record BuildDiagnostic(
    string FilePath,
    SourceRange Range,
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Project,
    string Source = "msbuild");

/// <summary>
/// Describes one guarded build. <paramref name="WorkspaceRoot"/> is the only trusted filesystem
/// boundary; <paramref name="TargetPath"/> may be absolute or relative but is always revalidated.
/// </summary>
public sealed record BuildRequest(
    string WorkspaceRoot,
    string TargetPath,
    BuildConfiguration Configuration = BuildConfiguration.Debug);

public sealed record BuildOutput(
    string StandardOutput,
    string StandardError,
    bool Truncated,
    long DroppedCharacters);

public sealed record BuildResult(
    bool Succeeded,
    int? ExitCode,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    BuildOutput Output,
    bool WasCancelled,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    internal static IReadOnlyList<BuildDiagnostic> Freeze(IEnumerable<BuildDiagnostic> diagnostics) =>
        new ReadOnlyCollection<BuildDiagnostic>(diagnostics.ToArray());
}

public sealed record DotnetBuildRunnerOptions
{
    public const int DefaultMaximumRetainedCharacters = 256 * 1024;
    public const int DefaultMaximumDiagnostics = 2_000;

    public int MaximumRetainedCharacters { get; init; } = DefaultMaximumRetainedCharacters;

    public int MaximumDiagnostics { get; init; } = DefaultMaximumDiagnostics;
}
