namespace HermesDeveloperServices;

public enum ArduinoInventoryKind
{
    Version,
    Cores,
    Boards,
    Libraries,
}

public sealed record ArduinoProviderOptions(
    string WorkbenchRoot,
    string ToolchainRelativePath,
    string ReceiptFileName,
    string ConfigurationRelativePath,
    string StateRelativePath)
{
    public const string ToolchainId = "arduino-cli";
    public const string ExecutableLogicalName = "arduino-cli";
}

public sealed record ArduinoCompileRequest(
    string WorkspaceRoot,
    string SketchRelativePath,
    string OutputDirectoryRelativePath,
    string Fqbn,
    EmbeddedOperationPermission Permission);

public sealed record ArduinoUploadRequest(
    string WorkspaceRoot,
    string SketchRelativePath,
    string OutputDirectoryRelativePath,
    string Fqbn,
    string Port,
    EmbeddedOperationPermission Permission);

public sealed record ArduinoInstallRequest(
    string Package,
    string? Version,
    EmbeddedOperationPermission Permission);

public sealed record ArduinoInventoryResult(
    EmbeddedHostOperationResult Result,
    IReadOnlyList<string> Entries);
