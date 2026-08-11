namespace HermesDeveloperServices.LanguageTooling.Java;

/// <summary>
/// Records why Java/JDT remains fail-closed until the release process supplies an authenticated,
/// app-owned JRE and Eclipse JDT LS bundle. No mutable URL, PATH lookup, or machine Java install is
/// accepted as runtime evidence.
/// </summary>
public static class JavaJdtProvisioning
{
    public const string ToolchainId = "eclipse-jdtls";
    public const string BundleRelativePath = "developer-services/java-jdt";
    public const string ReceiptFileName = "hermes-toolchain-receipt.json";

    public static JavaJdtProvisioningBlocker CurrentBlocker { get; } = new(
        "java-jdt-provenance-not-pinned",
        "No release-approved Eclipse JDT LS and app-owned Java runtime provenance lock is present.",
        [
            "An immutable official Eclipse JDT LS distribution identity and digest.",
            "An immutable official app-owned Java runtime identity and digest.",
            "A complete bounded file manifest covering both payloads, licenses, and notices.",
            "A release-owned receipt digest consumed by the trusted desktop host.",
        ]);

    internal static IJavaJdtRuntimeAuthority CreateUnavailableAuthority() =>
        new UnprovisionedJavaJdtRuntimeAuthority(CurrentBlocker);

    internal static IJavaJdtRuntimeAuthority CreateReceiptBoundAuthority(string installRoot) =>
        new ReceiptBoundJavaJdtRuntimeAuthority(installRoot);
}

public sealed record JavaJdtProvisioningBlocker(
    string Code,
    string SafeMessage,
    IReadOnlyList<string> MissingAuthorities);

internal sealed record JavaJdtRuntimeInspection(
    bool Available,
    string Code,
    string SafeMessage,
    string? Version = null);

internal interface IJavaJdtRuntimeAuthority : IAsyncDisposable
{
    ValueTask<JavaJdtRuntimeInspection> InspectAsync(CancellationToken cancellationToken);

    ValueTask<JavaJdtLanguageSession> StartSessionAsync(
        string workspaceRoot,
        CancellationToken cancellationToken);
}

internal sealed class UnprovisionedJavaJdtRuntimeAuthority(JavaJdtProvisioningBlocker blocker)
    : IJavaJdtRuntimeAuthority
{
    public ValueTask<JavaJdtRuntimeInspection> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new JavaJdtRuntimeInspection(false, blocker.Code, blocker.SafeMessage));
    }

    public ValueTask<JavaJdtLanguageSession> StartSessionAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        _ = workspaceRoot;
        cancellationToken.ThrowIfCancellationRequested();
        throw new LanguageToolingRequestException(blocker.Code, blocker.SafeMessage);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
