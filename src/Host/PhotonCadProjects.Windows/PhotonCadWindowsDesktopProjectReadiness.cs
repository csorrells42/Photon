namespace PhotonCadProjects.Windows;

/// <summary>
/// Path-free capability evidence for the desktop project lifecycle. This is safe to project into
/// the trusted renderer; it never carries native paths, storage identities, overwrite grants, or
/// runtime/container handles.
/// </summary>
public sealed record PhotonCadWindowsDesktopProjectReadiness
{
    public PhotonCadWindowsDesktopProjectReadiness(
        bool available,
        string reason,
        bool runtimeHydrationAvailable = false)
    {
        Available = available;
        Reason = SafeReason(reason);
        RuntimeHydrationAvailable = runtimeHydrationAvailable;
    }

    public int ProtocolVersion => PhotonCadProjectContract.Version;
    public bool Available { get; }
    public string Reason { get; }
    public int MaximumOpenProjects => PhotonCadProjectContract.MaximumOpenProjects;
    public int MaximumKnownWorkspaces => PhotonCadProjectContract.MaximumKnownWorkspaces;
    public int MaximumReopenEntries => PhotonCadProjectContract.MaximumReopenEntries;
    public int MaximumRequestHistory => PhotonCadProjectContract.MaximumRequestHistory;

    /// <summary>
    /// False until a trusted host-to-container hydration/replay seam is independently accepted.
    /// Nonzero-revision opens remain view/save-only while this is false.
    /// </summary>
    public bool RuntimeHydrationAvailable { get; }

    private static string SafeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !char.IsAsciiLetterOrDigit(value[0]))
            return "project-host-unavailable";
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : "project-host-unavailable";
    }
}

/// <summary>Result of a native, host-owned workspace selection.</summary>
public sealed record PhotonCadDesktopProjectPickerOutcome
{
    public PhotonCadDesktopProjectPickerOutcome(
        PhotonCadNativeServiceStatus status,
        string reason,
        PhotonCadWorkspaceRegistration? workspace)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (status == PhotonCadNativeServiceStatus.Selected && workspace is null)
            throw new PhotonCadProjectException("selected_workspace_required", nameof(workspace));
        if (status != PhotonCadNativeServiceStatus.Selected && workspace is not null)
            throw new PhotonCadProjectException("unexpected_workspace", nameof(workspace));
        Status = status;
        Reason = SafeReason(reason);
        Workspace = workspace;
    }

    public PhotonCadNativeServiceStatus Status { get; }
    public string Reason { get; }
    public PhotonCadWorkspaceRegistration? Workspace { get; }

    private static string SafeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !char.IsAsciiLetterOrDigit(value[0]))
            return "native-service-failed";
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : "native-service-failed";
    }
}

/// <summary>
/// Pathless host contract consumed by the WebView dispatcher. Implementations are native
/// authorities; renderers must never receive or implement this interface.
/// </summary>
public interface IPhotonCadDesktopProjectHost : IAsyncDisposable
{
    PhotonCadWindowsDesktopProjectReadiness Readiness { get; }

    ValueTask<PhotonCadDesktopProjectPickerOutcome> ChooseWorkspaceAsync(
        string requestId,
        string purpose,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadProjectDocument> CreateProjectAsync(
        PhotonCadProjectCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadProjectDocument> OpenProjectAsync(
        PhotonCadProjectOpenRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadProjectDocument> ReopenProjectAsync(
        PhotonCadProjectReopenRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadProjectDocument> RefreshProjectAsync(
        PhotonCadProjectRefreshRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsync(
        PhotonCadProjectSaveRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadProjectSaveOutcome> SaveProjectAsAsync(
        PhotonCadProjectSaveAsRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<PhotonCadProjectCloseOutcome> CloseProjectAsync(
        PhotonCadProjectCloseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the current renderer generation while leaving the application-level host reusable.
    /// </summary>
    ValueTask ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Injected revocation seam for preview/artifact references owned outside persistence.</summary>
public interface IPhotonCadDesktopProjectReferenceRevoker
{
    ValueTask RevokeProjectAsync(PhotonCadProjectHandle projectHandle, CancellationToken cancellationToken = default);
    ValueTask RevokeAllAsync(CancellationToken cancellationToken = default);
}

public sealed class NullPhotonCadDesktopProjectReferenceRevoker : IPhotonCadDesktopProjectReferenceRevoker
{
    public static NullPhotonCadDesktopProjectReferenceRevoker Instance { get; } = new();
    private NullPhotonCadDesktopProjectReferenceRevoker() { }
    public ValueTask RevokeProjectAsync(PhotonCadProjectHandle projectHandle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectHandle);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask RevokeAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
