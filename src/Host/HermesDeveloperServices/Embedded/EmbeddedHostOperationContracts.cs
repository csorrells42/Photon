namespace HermesDeveloperServices;

public enum EmbeddedPermissionKind
{
    CompileWorkspace,
    UploadDevice,
    InstallCore,
    InstallLibrary,
    DeployRemote,
}

/// <summary>A host-issued, operation-specific grant. Providers require an exact kind and scope match.</summary>
public sealed record EmbeddedOperationPermission(
    EmbeddedPermissionKind Kind,
    string Scope,
    string ApprovalId);

public enum EmbeddedDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record EmbeddedDiagnostic(
    string RelativePath,
    int? Line,
    int? Column,
    EmbeddedDiagnosticSeverity Severity,
    string? Code,
    string Message);

public sealed record EmbeddedHostOperationResult(
    bool Succeeded,
    string Operation,
    string Summary,
    string? FailureCode,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    long DroppedCharacters,
    bool WasCancelled,
    bool TimedOut,
    IReadOnlyList<EmbeddedDiagnostic> Diagnostics,
    IReadOnlyList<string> WorkspaceRelativeArtifacts)
{
    internal static EmbeddedHostOperationResult PermissionDenied(string operation) =>
        Failure(operation, "permission_denied", "The required host permission was not granted.");

    internal static EmbeddedHostOperationResult Failure(string operation, string code, string summary) =>
        new(false, operation, summary, code, null, string.Empty, string.Empty, false, 0, false, false, [], []);
}

internal static class EmbeddedPermissionPolicy
{
    public static bool Allows(
        EmbeddedOperationPermission? permission,
        EmbeddedPermissionKind kind,
        string scope) =>
        permission is not null
        && permission.Kind == kind
        && !string.IsNullOrWhiteSpace(permission.ApprovalId)
        && permission.ApprovalId.Length <= 128
        && string.Equals(permission.Scope, scope, StringComparison.Ordinal);
}
