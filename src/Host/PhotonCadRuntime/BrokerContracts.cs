namespace PhotonCadRuntime;

public sealed class CadError
{
    public CadError(string code, string message, bool retryable)
    {
        Code = ContractGuards.Identifier(code, nameof(code), 96);
        Message = ContractGuards.RequiredText(message, nameof(message), CadContractLimits.MessageLength);
        Retryable = retryable;
    }

    public string Code { get; }
    public string Message { get; }
    public bool Retryable { get; }
}

public sealed class CadResult<T> where T : class
{
    private CadResult(T? value, CadError? error)
    {
        if ((value is null) == (error is null))
            throw new InvalidOperationException("A CAD result must contain exactly one value or error.");
        Value = value;
        Error = error;
    }

    public bool Succeeded => Value is not null;
    public T? Value { get; }
    public CadError? Error { get; }

    public static CadResult<T> Success(T value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), null);

    public static CadResult<T> Failure(CadError error) =>
        new(null, error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class CadRuntimeDescription
{
    public CadRuntimeDescription(
        CadRuntimeAvailability availability,
        string reasonCode,
        string message,
        CadRuntimeBundleSetIdentity? activeBundles = null,
        CadCapabilityCatalog? catalog = null)
    {
        Availability = ContractGuards.EnumValue(availability, nameof(availability));
        ReasonCode = ContractGuards.Identifier(reasonCode, nameof(reasonCode), 96);
        Message = ContractGuards.RequiredText(message, nameof(message), CadContractLimits.MessageLength);
        ActiveBundles = activeBundles;
        Catalog = catalog;
        if (availability == CadRuntimeAvailability.Ready && (activeBundles is null || catalog is null))
            throw new CadContractException("bundle_identity_required", nameof(activeBundles));
        if (availability != CadRuntimeAvailability.Ready && (activeBundles is not null || catalog is not null))
            throw new CadContractException("inactive_bundle_identity_rejected", nameof(activeBundles));
    }

    public int ContractVersion => CadContractVersions.Host;
    public string Status => Availability switch
    {
        CadRuntimeAvailability.Ready => "available",
        CadRuntimeAvailability.NotProvisioned => "unavailable",
        CadRuntimeAvailability.Rejected => "error",
        _ => "error",
    };
    public string Reason => ReasonCode;
    public string? GeometryBundleId => ActiveBundles?.Geometry.BundleId;
    public string? AssemblyBundleId => ActiveBundles?.Assembly.BundleId;
    public CadRuntimeAvailability Availability { get; }
    public string ReasonCode { get; }
    public string Message { get; }
    public CadRuntimeBundleSetIdentity? ActiveBundles { get; }
    public CadCapabilityCatalog? Catalog { get; }
}

public sealed class CadCloseSessionRequest
{
    public CadCloseSessionRequest(CadRequestId requestId, CadSessionHandle session)
    {
        RequestId = requestId ?? throw new CadContractException("required", nameof(requestId));
        Session = session ?? throw new CadContractException("required", nameof(session));
    }

    public CadRequestId RequestId { get; }
    public CadSessionHandle Session { get; }
}

public interface ICadRuntimeBroker
{
    CadRuntimeDescription Describe();

    ValueTask<CadResult<CadProjectDescriptor>> OpenProjectAsync(
        CadOpenProjectRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CadResult<CadSessionDescriptor>> StartSessionAsync(
        CadStartSessionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CadResult<CadCapabilityCatalog>> GetCatalogAsync(
        CadRequestId requestId,
        CancellationToken cancellationToken = default);

    ValueTask<CadResult<CadOperationResult>> ExecuteAsync(
        CadOperationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CadResult<CadArtifactDescriptor>> GetArtifactReceiptAsync(
        CadArtifactReadRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CadResult<CadArtifactReadLease>> OpenArtifactReadAsync(
        CadArtifactReadRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CadResult<CadVerificationResult>> VerifyAsync(
        CadVerificationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CadResult<CadSessionDescriptor>> CloseSessionAsync(
        CadCloseSessionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableCadRuntimeBroker : ICadRuntimeBroker
{
    private static readonly CadRuntimeDescription Description = new(
        CadRuntimeAvailability.NotProvisioned,
        "verified_runtime_bundles_missing",
        "Verified CAD runtime bundles are not provisioned.");

    private static readonly CadError UnavailableError = new(
        "cad_runtime_unavailable",
        "Verified CAD runtime bundles are not provisioned.",
        retryable: true);

    private static readonly CadError CancelledError = new(
        "cancelled",
        "The CAD request was cancelled.",
        retryable: true);

    public CadRuntimeDescription Describe() => Description;

    public ValueTask<CadResult<CadProjectDescriptor>> OpenProjectAsync(
        CadOpenProjectRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<CadProjectDescriptor>(cancellationToken);

    public ValueTask<CadResult<CadSessionDescriptor>> StartSessionAsync(
        CadStartSessionRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<CadSessionDescriptor>(cancellationToken);

    public ValueTask<CadResult<CadCapabilityCatalog>> GetCatalogAsync(
        CadRequestId requestId,
        CancellationToken cancellationToken = default) =>
        Failure<CadCapabilityCatalog>(cancellationToken);

    public ValueTask<CadResult<CadOperationResult>> ExecuteAsync(
        CadOperationRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<CadOperationResult>(cancellationToken);

    public ValueTask<CadResult<CadArtifactDescriptor>> GetArtifactReceiptAsync(
        CadArtifactReadRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<CadArtifactDescriptor>(cancellationToken);

    public ValueTask<CadResult<CadArtifactReadLease>> OpenArtifactReadAsync(
        CadArtifactReadRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<CadArtifactReadLease>(cancellationToken);

    public ValueTask<CadResult<CadVerificationResult>> VerifyAsync(
        CadVerificationRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<CadVerificationResult>(cancellationToken);

    public ValueTask<CadResult<CadSessionDescriptor>> CloseSessionAsync(
        CadCloseSessionRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<CadSessionDescriptor>(cancellationToken);

    private static ValueTask<CadResult<T>> Failure<T>(CancellationToken cancellationToken) where T : class =>
        ValueTask.FromResult(CadResult<T>.Failure(cancellationToken.IsCancellationRequested
            ? CancelledError
            : UnavailableError));
}
