namespace PhotonCadProjects.RuntimeSync;

/// <summary>
/// Host composition policy below the format ceilings. The default is the deliberately narrow
/// primitive-provider budget; a reviewed industrial composition may opt in up to the v1 codec cap.
/// </summary>
public sealed class PhotonCadRuntimeSyncPolicy
{
    public PhotonCadRuntimeSyncPolicy(
        int maximumArtifactBytes = 64 * 1024 * 1024,
        int maximumMutationArtifactBytes = 64 * 1024 * 1024,
        int maximumBaseArtifactBytes = 64 * 1024 * 1024,
        int maximumBaseArtifactBytesPerRequest = 64 * 1024 * 1024,
        TimeSpan? compensationTimeout = null)
    {
        MaximumArtifactBytes = Bounded(
            maximumArtifactBytes,
            PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytes,
            nameof(maximumArtifactBytes));
        MaximumMutationArtifactBytes = Bounded(
            maximumMutationArtifactBytes,
            PhotonCadRuntimeSyncContract.MaximumSealedArtifactBytesPerMutation,
            nameof(maximumMutationArtifactBytes));
        MaximumBaseArtifactBytes = Bounded(
            maximumBaseArtifactBytes,
            PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytes,
            nameof(maximumBaseArtifactBytes));
        MaximumBaseArtifactBytesPerRequest = Bounded(
            maximumBaseArtifactBytesPerRequest,
            PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytesPerRequest,
            nameof(maximumBaseArtifactBytesPerRequest));
        if (MaximumArtifactBytes > MaximumMutationArtifactBytes)
            throw RuntimeSyncGuards.Failure("artifact_limit_exceeds_mutation_budget", nameof(maximumArtifactBytes));
        if (MaximumBaseArtifactBytes > MaximumBaseArtifactBytesPerRequest)
            throw RuntimeSyncGuards.Failure("artifact_limit_exceeds_base_budget", nameof(maximumBaseArtifactBytes));
        var timeout = compensationTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromSeconds(30))
            throw RuntimeSyncGuards.Failure("invalid_compensation_timeout", nameof(compensationTimeout));
        CompensationTimeout = timeout;
    }

    public int MaximumArtifactBytes { get; }
    public int MaximumMutationArtifactBytes { get; }
    public int MaximumBaseArtifactBytes { get; }
    public int MaximumBaseArtifactBytesPerRequest { get; }
    public TimeSpan CompensationTimeout { get; }

    private static int Bounded(int value, int ceiling, string field)
    {
        if (value < 1 || value > ceiling) throw RuntimeSyncGuards.Failure("invalid_policy_limit", field);
        return value;
    }
}
