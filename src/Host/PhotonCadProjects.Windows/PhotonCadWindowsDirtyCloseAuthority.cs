using System.Windows;

namespace PhotonCadProjects.Windows;

/// <summary>Exact, host-only binding for a dirty-close confirmation.</summary>
public sealed record PhotonCadWindowsDirtyCloseContext
{
    public PhotonCadWindowsDirtyCloseContext(PhotonCadProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Snapshot.Dirty)
            throw new PhotonCadProjectException("dirty_project_required", nameof(document));
        ProjectHandle = document.ProjectHandle;
        DisplayName = document.DisplayName;
        SessionId = document.Snapshot.SessionId;
        ProjectId = document.Snapshot.ProjectId;
        Revision = document.Snapshot.Revision;
        LastSavedRevision = document.LastSavedRevision;
        ContentDigest = document.ContentDigest;
        LastSavedContentDigest = document.LastSavedContentDigest;
    }

    public PhotonCadProjectHandle ProjectHandle { get; }
    public string DisplayName { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long Revision { get; }
    public long LastSavedRevision { get; }
    public string ContentDigest { get; }
    public string LastSavedContentDigest { get; }
}

/// <summary>Path-free, host-only binding for an overwrite confirmation.</summary>
public sealed record PhotonCadWindowsOverwriteContext
{
    public PhotonCadWindowsOverwriteContext(
        PhotonCadWorkspaceRegistration workspace,
        PhotonCadOverwritePurpose purpose,
        PhotonCadProjectDocument? source)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        if (purpose == PhotonCadOverwritePurpose.Create && source is not null)
            throw new PhotonCadProjectException("overwrite_create_source_rejected", nameof(source));
        if (purpose == PhotonCadOverwritePurpose.SaveAs && source is null)
            throw new PhotonCadProjectException("overwrite_source_required", nameof(source));
        WorkspaceHandle = workspace.WorkspaceHandle;
        DisplayLabel = workspace.Binding.Label;
        Purpose = purpose;
        SourceProjectHandle = source?.ProjectHandle;
        SessionId = source?.Snapshot.SessionId;
        ProjectId = source?.Snapshot.ProjectId;
        Revision = source?.Snapshot.Revision;
        ContentDigest = source?.Snapshot.ContentDigest;
    }

    public PhotonCadWorkspaceHandle WorkspaceHandle { get; }
    public string DisplayLabel { get; }
    public PhotonCadOverwritePurpose Purpose { get; }
    public PhotonCadProjectHandle? SourceProjectHandle { get; }
    public string? SessionId { get; }
    public string? ProjectId { get; }
    public long? Revision { get; }
    public string? ContentDigest { get; }
}

public interface IPhotonCadWindowsDirtyCloseAuthority
{
    ValueTask<bool> ConfirmDiscardAsync(
        PhotonCadWindowsDirtyCloseContext context,
        CancellationToken cancellationToken = default);
}

public interface IPhotonCadWindowsOverwriteAuthority
{
    ValueTask<bool> ConfirmOverwriteAsync(
        PhotonCadWindowsOverwriteContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Native WPF confirmation. Renderer booleans request this prompt; they never authorize it.</summary>
public sealed class PhotonCadWindowsDirtyCloseAuthority : IPhotonCadWindowsDirtyCloseAuthority
{
    public ValueTask<bool> ConfirmDiscardAsync(
        PhotonCadWindowsDirtyCloseContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PhotonCadWindowsConfirmation.InvokeAsync(
            $"Close {context.DisplayName} and discard its unsaved changes?",
            "Photon CAD - Unsaved changes",
            MessageBoxImage.Warning,
            cancellationToken);
    }
}

/// <summary>
/// A second exact host confirmation is intentional. It closes the race between a file dialog
/// returning and the target identity being inspected/bound into the one-time overwrite grant.
/// </summary>
public sealed class PhotonCadWindowsOverwriteAuthority : IPhotonCadWindowsOverwriteAuthority
{
    public ValueTask<bool> ConfirmOverwriteAsync(
        PhotonCadWindowsOverwriteContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PhotonCadWindowsConfirmation.InvokeAsync(
            $"Replace the existing Photon CAD project named {context.DisplayLabel}?",
            "Photon CAD - Confirm replacement",
            MessageBoxImage.Warning,
            cancellationToken);
    }
}

internal static class PhotonCadWindowsConfirmation
{
    internal static async ValueTask<bool> InvokeAsync(
        string message,
        string caption,
        MessageBoxImage image,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool Show()
        {
            var owner = Application.Current?.MainWindow;
            return owner is null
                ? MessageBox.Show(message, caption, MessageBoxButton.YesNo, image, MessageBoxResult.No) == MessageBoxResult.Yes
                : MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, image, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        var dispatcher = Application.Current?.Dispatcher;
        var result = dispatcher is not null && !dispatcher.CheckAccess()
            ? await dispatcher.InvokeAsync(Show).Task.WaitAsync(cancellationToken).ConfigureAwait(false)
            : Show();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
