using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal sealed class Compensator(SealedMutationProvider provider) : IPhotonCadSealedMutationCompensator
{
    private readonly SealedMutationProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public ValueTask CompensateAsync(
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default) =>
        _provider.RevokeAsync(mutation, reason, cancellationToken);
}
