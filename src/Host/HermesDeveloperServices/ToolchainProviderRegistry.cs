using System.Collections.ObjectModel;

namespace HermesDeveloperServices;

/// <summary>Registers version-compatible providers and resolves only exact declared capabilities.</summary>
public sealed class ToolchainProviderRegistry : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IToolchainProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void Register(IToolchainProvider provider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(provider);
        ValidateDescriptor(provider.Descriptor);

        lock (_gate)
        {
            if (!_providers.TryAdd(provider.Descriptor.ProviderId, provider))
            {
                throw new InvalidOperationException(
                    $"A toolchain provider named '{provider.Descriptor.ProviderId}' is already registered.");
            }
        }
    }

    public IReadOnlyList<ToolchainProviderDescriptor> GetDescriptors()
    {
        lock (_gate)
        {
            return Freeze(_providers.Values.Select(provider => provider.Descriptor));
        }
    }

    public ToolchainSelectionResult Select(ToolchainSelectionRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LanguageId);

        IToolchainProvider[] matches;
        lock (_gate)
        {
            matches = _providers.Values.Where(provider => Matches(provider.Descriptor, request)).ToArray();
        }

        var candidates = Freeze(matches.Select(provider => provider.Descriptor));
        return matches.Length switch
        {
            0 => new ToolchainSelectionResult(ToolchainSelectionState.NotFound, null, candidates),
            1 => new ToolchainSelectionResult(ToolchainSelectionState.Selected, matches[0], candidates),
            _ => new ToolchainSelectionResult(ToolchainSelectionState.Ambiguous, null, candidates),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IToolchainProvider[] providers;
        lock (_gate)
        {
            providers = _providers.Values.ToArray();
            _providers.Clear();
        }

        foreach (var provider in providers)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool Matches(ToolchainProviderDescriptor descriptor, ToolchainSelectionRequest request) =>
        (request.ProviderId is null
            || descriptor.ProviderId.Equals(request.ProviderId, StringComparison.OrdinalIgnoreCase))
        && descriptor.LanguageIds.Contains(request.LanguageId, StringComparer.OrdinalIgnoreCase)
        && (request.ProjectKind is null
            || descriptor.ProjectKinds.Contains(request.ProjectKind, StringComparer.OrdinalIgnoreCase))
        && (!request.RequiresBuild || descriptor.Build.Supported)
        && (!request.RequiresLsp || descriptor.Lsp.Supported)
        && (!request.RequiresDap || descriptor.Dap.Supported);

    private static void ValidateDescriptor(ToolchainProviderDescriptor descriptor)
    {
        if (descriptor.ContractVersion != DeveloperServicesProtocol.ToolchainProviderVersion)
        {
            throw new InvalidOperationException("The toolchain provider contract version is incompatible.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.ProviderVersion);
        if (descriptor.LanguageIds.Count == 0 || descriptor.ExecutionKinds.Count == 0)
        {
            throw new InvalidOperationException(
                "A toolchain provider must declare a language and at least one sidecar execution kind.");
        }

        if (descriptor.DeploymentScope != ToolchainDeploymentScope.HermesWorkbenchInternalModule)
        {
            throw new InvalidOperationException(
                "Toolchain providers must be internal modules of the complete Hermes Workbench product.");
        }

        if (descriptor.ExecutionKinds.Any(kind => !Enum.IsDefined(kind)))
        {
            throw new InvalidOperationException("A toolchain provider declared an invalid execution kind.");
        }
    }

    private static IReadOnlyList<ToolchainProviderDescriptor> Freeze(
        IEnumerable<ToolchainProviderDescriptor> descriptors) =>
        new ReadOnlyCollection<ToolchainProviderDescriptor>(descriptors.Select(Freeze).ToArray());

    private static ToolchainProviderDescriptor Freeze(ToolchainProviderDescriptor descriptor) =>
        descriptor with
        {
            LanguageIds = descriptor.LanguageIds.ToArray(),
            ProjectKinds = descriptor.ProjectKinds.ToArray(),
            ExecutionKinds = descriptor.ExecutionKinds.ToArray(),
            Build = descriptor.Build with { TargetKinds = descriptor.Build.TargetKinds.ToArray() },
        };
}
