using PhotonCadProjects.Codec;

namespace PhotonCadProjects.RuntimeSync;

public static class PhotonCadRuntimeSyncContract
{
    public const int Version = 1;
    public const int MaximumActiveProjects = PhotonCadProjectContract.MaximumOpenProjects;
    public const int MaximumPendingMutationsPerProject = 16;
    public const int MaximumOperationsPerMutation = 256;
    public const int MaximumEntitiesPerMutation = 1_024;
    public const int MaximumOccurrencesPerMutation = 4_096;
    public const int MaximumIssuesPerMutation = 1_024;
    public const int MaximumBomRowsPerMutation = 4_096;
    public const int MaximumArtifactsPerMutation = 1_024;
    public const int MaximumProtocolIds = 32;
    // These are format-level ceilings, not the default provider policy. The default synchronizer
    // policy is deliberately lower; an industrial provider may opt in up to the codec ceiling.
    public const int MaximumSealedArtifactBytes = PhotonCadProjectFileV1.MaximumEncodedBytes;
    public const int MaximumSealedArtifactBytesPerMutation = PhotonCadProjectFileV1.MaximumEncodedBytes;
    public const int MaximumBaseArtifactBytes = PhotonCadProjectFileV1.MaximumEncodedBytes;
    public const int MaximumBaseArtifactBytesPerRequest = PhotonCadProjectFileV1.MaximumEncodedBytes;
}

/// <summary>Explicit merge semantics for provider-returned canonical collections.</summary>
public enum PhotonCadCollectionMergeMode
{
    Append,
    ReplaceAll,
}

public sealed class PhotonCadRuntimeSyncException : PhotonCadProjectException
{
    public PhotonCadRuntimeSyncException(string code, string field)
        : base(code, field)
    {
    }

    public PhotonCadRuntimeSyncException(string code, string field, Exception innerException)
        : base(code, field, innerException)
    {
    }
}

/// <summary>
/// Host-originated logical request. It contains no native path, runtime handle, process setting,
/// executable input, source code, or renderer-provided canonical bytes.
/// </summary>
public sealed class PhotonCadRuntimeSyncRequest
{
    public PhotonCadRuntimeSyncRequest(
        string requestId,
        string sessionId,
        string projectId,
        long baseRevision,
        string capabilityId,
        PhotonCadOperationModeV1 mode,
        IEnumerable<PhotonCadSyncOperationInput>? inputs = null,
        IEnumerable<string>? targetEntityIds = null)
    {
        RequestId = RuntimeSyncGuards.Identifier(requestId, nameof(requestId));
        SessionId = RuntimeSyncGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = RuntimeSyncGuards.Identifier(projectId, nameof(projectId));
        BaseRevision = RuntimeSyncGuards.Revision(baseRevision, nameof(baseRevision));
        CapabilityId = RuntimeSyncGuards.Identifier(capabilityId, nameof(capabilityId));
        Mode = RuntimeSyncGuards.EnumValue(mode, nameof(mode));
        Inputs = RuntimeSyncGuards.Copy(inputs ?? [], nameof(inputs), PhotonCadProjectFileV1.MaximumInputsPerOperation);
        RuntimeSyncGuards.RequireUnique(Inputs.Select(input => input.Id), nameof(inputs));
        TargetEntityIds = Array.AsReadOnly(RuntimeSyncGuards.Copy(
                targetEntityIds ?? [],
                nameof(targetEntityIds),
                PhotonCadProjectFileV1.MaximumTargetsPerOperation)
            .Select(value => RuntimeSyncGuards.Identifier(value, nameof(targetEntityIds)))
            .ToArray());
        RuntimeSyncGuards.RequireUnique(TargetEntityIds, nameof(targetEntityIds));
    }

    public int ContractVersion => PhotonCadRuntimeSyncContract.Version;
    public string RequestId { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long BaseRevision { get; }
    public string CapabilityId { get; }
    public PhotonCadOperationModeV1 Mode { get; }
    public IReadOnlyList<PhotonCadSyncOperationInput> Inputs { get; }
    public IReadOnlyList<string> TargetEntityIds { get; }
}

/// <summary>
/// Exact host-only optimistic binding returned by the canonical project authority. The project
/// handle is authority routing state only and must never be persisted or projected to CAD code.
/// </summary>
public sealed class PhotonCadCanonicalMutationBinding
{
    public PhotonCadCanonicalMutationBinding(
        PhotonCadProjectHandle projectHandle,
        PhotonCadCanonicalProject current)
    {
        ProjectHandle = projectHandle ?? throw RuntimeSyncGuards.Failure("required", nameof(projectHandle));
        Current = current ?? throw RuntimeSyncGuards.Failure("required", nameof(current));
    }

    public PhotonCadProjectHandle ProjectHandle { get; }
    public PhotonCadCanonicalProject Current { get; }
}

/// <summary>
/// Sole canonical persistence authority used by RuntimeSync. Resolve must return the exact current
/// clean canonical project for the request identity/revision. Commit must compare the supplied
/// binding again, durably save with MatchOpenedVersion semantics, and publish in-memory state only
/// after the atomic save is proven. Ambiguous post-commit recovery must throw rather than report a
/// false success.
/// </summary>
public interface IPhotonCadCanonicalMutationAuthority
{
    ValueTask<PhotonCadCanonicalMutationBinding> ResolveAsync(
        PhotonCadRuntimeSyncRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadCanonicalProject> CommitAsync(
        PhotonCadCanonicalMutationBinding expected,
        PhotonCadCanonicalProject updated,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pathless provider input derived from the trusted canonical base. It carries exact copied
/// canonical artifact bytes and occurrence values so a provider never reconstructs trusted input
/// from mutable session state. No native path, runtime handle, or live lease is representable.
/// </summary>
public sealed class PhotonCadSealedMutationProviderRequest
{
    internal PhotonCadSealedMutationProviderRequest(
        PhotonCadRuntimeSyncRequest request,
        PhotonCadProjectUnit units,
        string baseContentDigest,
        IEnumerable<PhotonCadProviderBaseEntity> baseEntities,
        IEnumerable<PhotonCadProviderBaseOperation> baseOperations,
        IEnumerable<PhotonCadProviderBaseArtifact> baseArtifacts,
        IEnumerable<PhotonCadProviderBaseOccurrence> baseOccurrences,
        IEnumerable<PhotonCadProviderBaseBomRow> baseBom)
    {
        Request = request ?? throw RuntimeSyncGuards.Failure("required", nameof(request));
        Units = RuntimeSyncGuards.EnumValue(units, nameof(units));
        BaseContentDigest = RuntimeSyncGuards.Digest(baseContentDigest, nameof(baseContentDigest));
        BaseEntities = RuntimeSyncGuards.Copy(baseEntities, nameof(baseEntities), PhotonCadProjectFileV1.MaximumEntities);
        RuntimeSyncGuards.RequireUnique(BaseEntities.Select(value => value.Id), nameof(baseEntities));
        ExistingEntityIds = Array.AsReadOnly(BaseEntities.Select(value => value.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        BaseOperations = RuntimeSyncGuards.Copy(baseOperations, nameof(baseOperations), PhotonCadProjectFileV1.MaximumOperations);
        RuntimeSyncGuards.RequireUnique(BaseOperations.Select(value => value.Id), nameof(baseOperations));
        BaseArtifacts = RuntimeSyncGuards.Copy(
            baseArtifacts,
            nameof(baseArtifacts),
            PhotonCadProjectFileV1.MaximumBlobCount);
        BaseOccurrences = RuntimeSyncGuards.Copy(
            baseOccurrences,
            nameof(baseOccurrences),
            PhotonCadProjectFileV1.MaximumOccurrences);
        RuntimeSyncGuards.RequireUnique(BaseOccurrences.Select(value => value.OccurrenceId), nameof(baseOccurrences));
        BaseBom = RuntimeSyncGuards.Copy(baseBom, nameof(baseBom), PhotonCadProjectContract.MaximumBomRows);
        RuntimeSyncGuards.RequireUnique(BaseBom.Select(value => $"{value.SourceEntityId}\0{value.PartNumber}"), nameof(baseBom));

        long total = 0;
        try
        {
            foreach (var artifact in BaseArtifacts) total = checked(total + artifact.ByteLength);
        }
        catch (OverflowException exception)
        {
            throw new PhotonCadRuntimeSyncException("base_artifact_budget_overflow", nameof(baseArtifacts), exception);
        }
        if (total > PhotonCadRuntimeSyncContract.MaximumBaseArtifactBytesPerRequest)
            throw RuntimeSyncGuards.Failure("base_artifact_budget_rejected", nameof(baseArtifacts));
        TotalBaseArtifactBytes = total;
    }

    public PhotonCadRuntimeSyncRequest Request { get; }
    public PhotonCadProjectUnit Units { get; }
    public string BaseContentDigest { get; }
    public IReadOnlyList<PhotonCadProviderBaseEntity> BaseEntities { get; }
    public IReadOnlyList<PhotonCadProviderBaseOperation> BaseOperations { get; }
    public IReadOnlyList<string> ExistingEntityIds { get; }
    public IReadOnlyList<PhotonCadProviderBaseArtifact> BaseArtifacts { get; }
    public IReadOnlyList<PhotonCadProviderBaseOccurrence> BaseOccurrences { get; }
    public IReadOnlyList<PhotonCadProviderBaseBomRow> BaseBom { get; }
    public long TotalBaseArtifactBytes { get; }
}

/// <summary>
/// Provider-neutral typed CAD boundary. Product composition must implement it through a verified,
/// hardened container; CAD/Python/model dependencies never execute or load in the desktop host.
/// No stream, path, lease, process, provider session, or mutable artifact handle may survive.
/// </summary>
public interface IPhotonCadSealedMutationProvider
{
    ValueTask<PhotonCadSealedMutationDelta> ApplyAsync(
        PhotonCadSealedMutationProviderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Mandatory compensation boundary after a provider has returned a mutation but before canonical
/// commit succeeds. Implementations must be bounded and idempotent. They revoke provider session
/// state; they never delete or overwrite the canonical project or a customer destination.
/// </summary>
public interface IPhotonCadSealedMutationCompensator
{
    ValueTask CompensateAsync(
        PhotonCadSealedMutationDelta mutation,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed class PhotonCadRuntimeSyncResult
{
    public PhotonCadRuntimeSyncResult(
        PhotonCadCanonicalProject savedProject,
        PhotonCadSealedMutationDelta mutation)
    {
        SavedProject = savedProject ?? throw RuntimeSyncGuards.Failure("required", nameof(savedProject));
        Mutation = mutation ?? throw RuntimeSyncGuards.Failure("required", nameof(mutation));
    }

    public int ContractVersion => PhotonCadRuntimeSyncContract.Version;
    public PhotonCadCanonicalProject SavedProject { get; }
    public PhotonCadSealedMutationDelta Mutation { get; }
}
