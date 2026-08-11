namespace HermesDeveloperServices.LanguageTooling;

/// <summary>
/// Adapts only the three existing provider implementations whose own discovery path verifies an
/// immutable runtime. PATH-based providers are deliberately rejected here.
/// </summary>
public sealed class KnownPinnedToolchainEvidenceSource : ILanguageToolingEvidenceSource
{
    private readonly IToolchainProvider _provider;
    private readonly bool _ownsProvider;

    private KnownPinnedToolchainEvidenceSource(
        IToolchainProvider provider,
        string frontendProviderId,
        IReadOnlyCollection<string> capabilityIds,
        bool ownsProvider)
    {
        _provider = provider;
        ProviderId = frontendProviderId;
        CapabilityIds = capabilityIds;
        _ownsProvider = ownsProvider;
    }

    public string ProviderId { get; }

    public IReadOnlyCollection<string> CapabilityIds { get; }

    public static KnownPinnedToolchainEvidenceSource Create(
        IToolchainProvider provider,
        bool ownsProvider = true)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var descriptor = provider.Descriptor;
        return descriptor.ProviderId switch
        {
            "roslyn-lsp" when descriptor.Lsp.Supported => new(
                provider,
                LanguageToolingCatalog.Dotnet,
                ["dotnet.roslyn-lsp"],
                ownsProvider),
            "hermes-dotnet-dap" when descriptor.Dap.Supported => new(
                provider,
                LanguageToolingCatalog.Dotnet,
                ["dotnet.dap"],
                ownsProvider),
            "gcc" when descriptor.Build.Supported => new(
                provider,
                LanguageToolingCatalog.Gcc,
                ["gcc.compiler"],
                ownsProvider),
            _ => throw new ArgumentException(
                "Only the pinned Roslyn, NetCoreDbg, and GNU providers may supply trusted runtime evidence.",
                nameof(provider)),
        };
    }

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var discovery = await _provider.DiscoverExecutablesAsync(
            new ToolchainDiscoveryContext(workspaceRoot, ToolchainExecutionKind.LocalSidecarProcess),
            cancellationToken).ConfigureAwait(false);
        var state = ConvertState(discovery.Availability.State);
        if (state == LanguageToolingCapabilityState.Available
            && (discovery.Executables.Count == 0 || discovery.Executables.Any(item => !IsVerifiedExecutable(item))))
        {
            state = LanguageToolingCapabilityState.Unavailable;
        }

        var code = state == LanguageToolingCapabilityState.Available
            ? "verified-pinned-runtime"
            : SafeCode(discovery.Availability.Code, "runtime-not-provisioned");
        var message = state == LanguageToolingCapabilityState.Available
            ? "The trusted desktop host verified the pinned runtime."
            : SafeMessage(discovery.Availability.SafeMessage, "The pinned runtime is unavailable.");
        var version = state == LanguageToolingCapabilityState.Available
            ? SafeVersion(discovery.Executables.Select(item => item.Version)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? _provider.Descriptor.ProviderVersion)
            : null;
        return CapabilityIds.Select(id => new LanguageToolingCapabilityStatus(id, state, code, message, version)).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsProvider) await _provider.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsVerifiedExecutable(ResolvedToolchainExecutable item)
    {
        if (item.Availability.State != ToolchainAvailabilityState.Available
            || string.IsNullOrWhiteSpace(item.ExecutablePath)
            || !Path.IsPathFullyQualified(item.ExecutablePath)
            || !File.Exists(item.ExecutablePath)) return false;
        try
        {
            return (File.GetAttributes(item.ExecutablePath) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or System.Text.Json.JsonException
            or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static LanguageToolingCapabilityState ConvertState(ToolchainAvailabilityState state) => state switch
    {
        ToolchainAvailabilityState.Available => LanguageToolingCapabilityState.Available,
        ToolchainAvailabilityState.Error => LanguageToolingCapabilityState.Error,
        _ => LanguageToolingCapabilityState.Unavailable,
    };

    internal static string SafeCode(string? value, string fallback)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned[..Math.Min(cleaned.Length, 96)];
    }

    internal static string SafeMessage(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value;
        var cleaned = new string(source.Select(character =>
            character is '\0' or '\r' or '\n' or '\t'
                || char.IsControl(character)
                || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
                ? ' '
                : character).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = fallback;
        return cleaned[..Math.Min(cleaned.Length, 512)];
    }

    internal static string? SafeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_' or '+').ToArray());
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, 64)];
    }
}

public sealed class ArduinoPinnedEvidenceSource : ILanguageToolingEvidenceSource
{
    private readonly ArduinoProviderOptions _options;

    public ArduinoPinnedEvidenceSource(ArduinoProviderOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public string ProviderId => LanguageToolingCatalog.Arduino;

    public IReadOnlyCollection<string> CapabilityIds { get; } = ["arduino.project", "arduino.compiler"];

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        _ = workspaceRoot;
        try
        {
            _ = await ArduinoHostProvider.CreateAsync(_options, cancellationToken).ConfigureAwait(false);
            return Available(CapabilityIds, "The trusted desktop host verified the pinned Arduino CLI package and private configuration.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TrustedToolchainValidationException exception)
        {
            return Unavailable(CapabilityIds, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or System.Text.Json.JsonException
            or System.Security.Cryptography.CryptographicException)
        {
            return Error(CapabilityIds, "arduino-verification-failed", "The pinned Arduino runtime could not be verified.");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static IReadOnlyList<LanguageToolingCapabilityStatus> Available(
        IEnumerable<string> capabilities,
        string message) => capabilities.Select(id => new LanguageToolingCapabilityStatus(
            id,
            LanguageToolingCapabilityState.Available,
            "verified-pinned-runtime",
            message)).ToArray();

    internal static IReadOnlyList<LanguageToolingCapabilityStatus> Unavailable(
        IEnumerable<string> capabilities,
        string code,
        string message) => capabilities.Select(id => new LanguageToolingCapabilityStatus(
            id,
            LanguageToolingCapabilityState.Unavailable,
            KnownPinnedToolchainEvidenceSource.SafeCode(code, "runtime-not-provisioned"),
            KnownPinnedToolchainEvidenceSource.SafeMessage(message, "The pinned runtime is unavailable."))).ToArray();

    internal static IReadOnlyList<LanguageToolingCapabilityStatus> Error(
        IEnumerable<string> capabilities,
        string code,
        string message) => capabilities.Select(id => new LanguageToolingCapabilityStatus(
            id,
            LanguageToolingCapabilityState.Error,
            code,
            message)).ToArray();
}

public sealed class RaspberryPiPinnedEvidenceSource : ILanguageToolingEvidenceSource
{
    private readonly RaspberryPiProviderOptions _options;
    private readonly IReadOnlyCollection<string> _trustedTargetIds;

    public RaspberryPiPinnedEvidenceSource(
        RaspberryPiProviderOptions options,
        IReadOnlyCollection<string> trustedTargetIds)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(trustedTargetIds);
        var targets = trustedTargetIds.Select(RequireIdentifier).Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length != trustedTargetIds.Count)
            throw new ArgumentException("Trusted Raspberry Pi target identifiers must be unique.", nameof(trustedTargetIds));
        _trustedTargetIds = targets;
    }

    public string ProviderId => LanguageToolingCatalog.RaspberryPi;

    public IReadOnlyCollection<string> CapabilityIds { get; } = ["raspberry-pi.inspect", "raspberry-pi.deploy"];

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        _ = workspaceRoot;
        if (_trustedTargetIds.Count == 0)
            return ArduinoPinnedEvidenceSource.Unavailable(
                CapabilityIds,
                "trusted-target-not-configured",
                "No host-owned Raspberry Pi target is configured.");
        try
        {
            _ = await RaspberryPiTrustedHostProvider.CreateAsync(_options, cancellationToken).ConfigureAwait(false);
            return ArduinoPinnedEvidenceSource.Available(
                CapabilityIds,
                "The trusted desktop host verified the pinned SSH runtime, credential authority, and configured target identities.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TrustedToolchainValidationException exception)
        {
            return ArduinoPinnedEvidenceSource.Unavailable(CapabilityIds, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ArduinoPinnedEvidenceSource.Error(
                CapabilityIds,
                "raspberry-pi-verification-failed",
                "The pinned Raspberry Pi runtime could not be verified.");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string RequireIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || !value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-'))
            throw new ArgumentException("A trusted Raspberry Pi target identifier is invalid.", nameof(value));
        return value;
    }
}

public sealed class UnavailableLanguageToolingEvidenceSource : ILanguageToolingEvidenceSource
{
    private readonly string _code;
    private readonly string _message;

    public UnavailableLanguageToolingEvidenceSource(
        string providerId,
        IReadOnlyCollection<string> capabilityIds,
        string code,
        string safeMessage)
    {
        ProviderId = LanguageToolingCatalog.RequireProvider(providerId).Id;
        ArgumentNullException.ThrowIfNull(capabilityIds);
        CapabilityIds = capabilityIds.Select(id => LanguageToolingCatalog.RequireCapability(providerId, id).Id).ToArray();
        if (CapabilityIds.Count == 0) throw new ArgumentException("Declare at least one unavailable capability.", nameof(capabilityIds));
        _code = KnownPinnedToolchainEvidenceSource.SafeCode(code, "runtime-not-provisioned");
        _message = KnownPinnedToolchainEvidenceSource.SafeMessage(safeMessage, "The pinned runtime is unavailable.");
    }

    public string ProviderId { get; }

    public IReadOnlyCollection<string> CapabilityIds { get; }

    public ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        _ = workspaceRoot;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ArduinoPinnedEvidenceSource.Unavailable(CapabilityIds, _code, _message));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
