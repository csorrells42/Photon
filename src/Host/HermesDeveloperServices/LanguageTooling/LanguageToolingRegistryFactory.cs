namespace HermesDeveloperServices.LanguageTooling;

public static class LanguageToolingRegistryFactory
{
    /// <summary>
    /// Creates a complete fail-closed catalog. Callers pass only sources backed by the trusted host;
    /// every omitted capability receives explicit unavailable evidence rather than remaining unknown.
    /// </summary>
    public static LanguageToolingTrustedRegistry Create(
        string workspaceRoot,
        IEnumerable<ILanguageToolingEvidenceSource>? verifiedSources = null,
        IEnumerable<ILanguageToolingOperationHandler>? operationHandlers = null)
    {
        var sources = (verifiedSources ?? []).ToArray();
        var sourceCapabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            _ = LanguageToolingCatalog.RequireProvider(source.ProviderId);
            foreach (var capability in source.CapabilityIds)
            {
                _ = LanguageToolingCatalog.RequireCapability(source.ProviderId, capability);
                if (!sourceCapabilities.Add(capability))
                    throw new InvalidOperationException("A language-tooling capability has multiple proposed evidence authorities.");
            }
        }
        var claimed = sources.SelectMany(source => source.CapabilityIds).ToHashSet(StringComparer.Ordinal);
        var registry = new LanguageToolingTrustedRegistry(workspaceRoot);
        foreach (var source in sources) registry.RegisterEvidenceSource(source);

        foreach (var provider in LanguageToolingCatalog.Definitions)
        {
            var missing = provider.Capabilities
                .Where(capability => !claimed.Contains(capability.Id))
                .Select(capability => capability.Id)
                .ToArray();
            if (missing.Length == 0) continue;
            var (code, message) = MissingRuntimeMessage(provider.Id);
            registry.RegisterEvidenceSource(new UnavailableLanguageToolingEvidenceSource(
                provider.Id,
                missing,
                code,
                message));
        }

        foreach (var handler in operationHandlers ?? []) registry.RegisterOperationHandler(handler);
        return registry;
    }

    private static (string Code, string Message) MissingRuntimeMessage(string providerId) => providerId switch
    {
        LanguageToolingCatalog.Dotnet => (
            "dotnet-runtime-not-pinned",
            "No exact pinned runtime evidence is registered for this .NET capability."),
        LanguageToolingCatalog.JavaJdt => (
            "java-jdt-runtime-not-provisioned",
            "No installer-approved Eclipse JDT runtime and legal receipt are provisioned."),
        LanguageToolingCatalog.Arduino => (
            "arduino-runtime-not-provisioned",
            "The pinned Arduino CLI runtime and private configuration are unavailable."),
        LanguageToolingCatalog.Python => (
            "python-runtime-not-provisioned",
            "No installer-approved pinned Python tooling runtime is provisioned."),
        LanguageToolingCatalog.Gcc => (
            "gcc-runtime-not-provisioned",
            "The pinned GNU C/C++ runtime is unavailable."),
        LanguageToolingCatalog.RaspberryPi => (
            "raspberry-pi-runtime-not-provisioned",
            "The pinned SSH runtime and trusted Raspberry Pi target authority are unavailable."),
        _ => ("runtime-not-provisioned", "The pinned tooling runtime is unavailable."),
    };
}
