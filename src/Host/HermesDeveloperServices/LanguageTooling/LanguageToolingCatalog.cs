using System.Collections.ObjectModel;

namespace HermesDeveloperServices.LanguageTooling;

public static class LanguageToolingCatalog
{
    public const string Dotnet = "dotnet";
    public const string JavaJdt = "java-jdt";
    public const string Arduino = "arduino";
    public const string Python = "python";
    public const string Gcc = "gcc";
    public const string RaspberryPi = "raspberry-pi";

    private static readonly IReadOnlyList<LanguageToolingProviderDefinition> DefinitionsValue =
        new ReadOnlyCollection<LanguageToolingProviderDefinition>(
        [
            Provider(Dotnet, ".NET / Roslyn",
                Capability("dotnet.roslyn-lsp", "language-server", "Roslyn language server"),
                Capability("dotnet.compiler", "compiler", ".NET build"),
                Capability("dotnet.tests", "test-runner", ".NET test runner"),
                Capability("dotnet.dap", "debug-adapter", ".NET DAP debugger")),
            Provider(JavaJdt, "Java / Eclipse JDT",
                Capability("java-jdt.lsp", "language-server", "Eclipse JDT language server")),
            Provider(Arduino, "Arduino",
                Capability("arduino.project", "project-inspection", "Arduino project understanding"),
                Capability("arduino.compiler", "compiler", "Arduino compiler")),
            Provider(Python, "Python",
                Capability("python.project", "project-inspection", "Python project understanding"),
                Capability("python.lsp", "language-server", "Python language server"),
                Capability("python.compiler", "compiler", "Python syntax compiler"),
                Capability("python.tests", "test-runner", "Python test runner")),
            Provider(Gcc, "GNU C / C++",
                Capability("gcc.compiler", "compiler", "Pinned GNU C/C++ compiler")),
            Provider(RaspberryPi, "Raspberry Pi",
                Capability("raspberry-pi.inspect", "remote-host", "Trusted Raspberry Pi inspection"),
                Capability("raspberry-pi.deploy", "deployment", "Reviewed Raspberry Pi file deployment")),
        ]);

    public static IReadOnlyList<LanguageToolingProviderDefinition> Definitions => DefinitionsValue;

    public static LanguageToolingProviderDefinition RequireProvider(string providerId) =>
        DefinitionsValue.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.Ordinal))
        ?? throw new LanguageToolingRequestException("unknown-provider", "The language-tooling provider is unknown.");

    public static LanguageToolingCapabilityDefinition RequireCapability(string providerId, string capabilityId)
    {
        var provider = RequireProvider(providerId);
        return provider.Capabilities.FirstOrDefault(item => item.Id.Equals(capabilityId, StringComparison.Ordinal))
            ?? throw new LanguageToolingRequestException(
                "unknown-capability",
                "The language-tooling capability is not declared for this provider.");
    }

    public static string CapabilityFor(LanguageToolingHostRequest request) => request switch
    {
        StartLanguageToolingSessionRequest or StopLanguageToolingSessionRequest =>
            RequireSingleKind(request.ProviderId, "language-server"),
        InspectLanguageToolingProjectRequest => RequireSingleKind(request.ProviderId, "project-inspection"),
        CompileLanguageToolingRequest => RequireSingleKind(request.ProviderId, "compiler"),
        RunLanguageToolingTestsRequest => RequireSingleKind(request.ProviderId, "test-runner"),
        StartLanguageToolingDebugRequest => RequireSingleKind(request.ProviderId, "debug-adapter"),
        InspectLanguageToolingRemoteTargetRequest => RequireSingleKind(request.ProviderId, "remote-host"),
        DeployLanguageToolingFileRequest => RequireSingleKind(request.ProviderId, "deployment"),
        _ => throw new LanguageToolingRequestException("unsupported-operation", "The language-tooling operation is unsupported."),
    };

    private static string RequireSingleKind(string providerId, string kind)
    {
        var matches = RequireProvider(providerId).Capabilities
            .Where(item => item.Kind.Equals(kind, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0].Id
            : throw new LanguageToolingRequestException(
                "capability-not-declared",
                "The provider does not declare this language-tooling operation.");
    }

    private static LanguageToolingProviderDefinition Provider(
        string id,
        string label,
        params LanguageToolingCapabilityDefinition[] capabilities) => new(id, label, capabilities);

    private static LanguageToolingCapabilityDefinition Capability(string id, string kind, string label) =>
        new(id, kind, label);
}
