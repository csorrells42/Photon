using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace PhotonCadProjects.Windows;

public sealed record PhotonCadWindowsDialogResult(bool Accepted, string? ExactPath);

public interface IPhotonCadWindowsFileDialog
{
    PhotonCadWindowsDialogResult Show(string purpose);
}

public sealed class PhotonCadWindowsFileDialog : IPhotonCadWindowsFileDialog
{
    private readonly Func<Window?> _ownerProvider;
    private readonly Func<FileDialog, Window, bool?> _showDialog;

    public PhotonCadWindowsFileDialog(
        Func<Window?> ownerProvider,
        Func<FileDialog, Window, bool?>? showDialog = null)
    {
        _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
        _showDialog = showDialog ?? ((dialog, owner) => dialog.ShowDialog(owner));
    }

    public PhotonCadWindowsDialogResult Show(string purpose)
    {
        if (!OperatingSystem.IsWindows()) return new PhotonCadWindowsDialogResult(false, null);
        var owner = _ownerProvider()
            ?? throw new InvalidOperationException("A live Photon CAD owner window is required.");
        PhotonCadWindowsDialogResult Invoke()
        {
            if (new WindowInteropHelper(owner).Handle == IntPtr.Zero)
                throw new InvalidOperationException("The Photon CAD owner window is not live.");
            FileDialog dialog = purpose == "open"
                ? new OpenFileDialog { CheckFileExists = true, Multiselect = false }
                : new SaveFileDialog { AddExtension = true, OverwritePrompt = true };
            dialog.DefaultExt = ".photoncad";
            dialog.Filter = "Photon CAD projects (*.photoncad)|*.photoncad";
            dialog.CheckPathExists = true;
            dialog.Title = purpose switch
            {
                "new" => "Create Photon CAD project",
                "open" => "Open Photon CAD project",
                "save-as" => "Save Photon CAD project as",
                _ => throw new PhotonCadProjectException("unsupported_picker_purpose", nameof(purpose)),
            };
            var accepted = _showDialog(dialog, owner) == true;
            return new PhotonCadWindowsDialogResult(accepted, accepted ? dialog.FileName : null);
        }
        var dispatcher = owner.Dispatcher;
        return dispatcher is not null && !dispatcher.CheckAccess() ? dispatcher.Invoke(Invoke) : Invoke();
    }
}

public sealed class PhotonCadWindowsWorkspacePicker : IPhotonCadNativeWorkspacePicker
{
    private readonly PhotonCadWindowsTargetRegistry _registry;
    private readonly IPhotonCadWindowsFileDialog _dialog;

    public PhotonCadWindowsWorkspacePicker(PhotonCadWindowsTargetRegistry registry, IPhotonCadWindowsFileDialog? dialog = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dialog = dialog ?? new PhotonCadWindowsFileDialog(() => Application.Current?.MainWindow);
    }

    public ValueTask<PhotonCadNativeWorkspaceSelection> ChooseAsync(string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (purpose is not ("new" or "open" or "save-as")) return ValueTask.FromResult(Rejected("unsupported-picker-purpose"));
        if (!_registry.Available) return ValueTask.FromResult(Unavailable("windows-native-picker-unavailable"));
        try
        {
            var result = _dialog.Show(purpose);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Accepted)
                return ValueTask.FromResult(new PhotonCadNativeWorkspaceSelection(PhotonCadNativeServiceStatus.Cancelled, "native-picker-cancelled", null));
            if (string.IsNullOrWhiteSpace(result.ExactPath)) return ValueTask.FromResult(Rejected("native-picker-returned-no-path"));
            var binding = _registry.RegisterExactPath(result.ExactPath);
            return ValueTask.FromResult(new PhotonCadNativeWorkspaceSelection(PhotonCadNativeServiceStatus.Selected, "native-workspace-selected", binding));
        }
        catch (PhotonCadProjectException exception)
        {
            return ValueTask.FromResult(Rejected(exception.Code));
        }
        catch (ObjectDisposedException)
        {
            return ValueTask.FromResult(Unavailable("windows-native-picker-unavailable"));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(Unavailable("windows-native-picker-failed"));
        }
    }

    private static PhotonCadNativeWorkspaceSelection Rejected(string reason) =>
        new(PhotonCadNativeServiceStatus.Rejected, SafeReason(reason), null);

    private static PhotonCadNativeWorkspaceSelection Unavailable(string reason) =>
        new(PhotonCadNativeServiceStatus.Unavailable, SafeReason(reason), null);

    private static string SafeReason(string value)
    {
        var safe = new string(value.Take(64).Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ToArray());
        return safe.Length > 0 && char.IsAsciiLetterOrDigit(safe[0]) ? safe : "native-service-failed";
    }
}

public sealed class PhotonCadWindowsWorkspaceMount : IPhotonCadNativeWorkspaceMount
{
    private readonly PhotonCadWindowsTargetRegistry _registry;

    public PhotonCadWindowsWorkspaceMount(PhotonCadWindowsTargetRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public ValueTask<PhotonCadNativeWorkspaceSelection> MountAsync(string hostOwnedSelectionToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.Available)
            return ValueTask.FromResult(new PhotonCadNativeWorkspaceSelection(PhotonCadNativeServiceStatus.Unavailable, "windows-native-mount-unavailable", null));
        try
        {
            var binding = _registry.ConsumeMountToken(hostOwnedSelectionToken);
            return ValueTask.FromResult(new PhotonCadNativeWorkspaceSelection(PhotonCadNativeServiceStatus.Selected, "native-workspace-mounted", binding));
        }
        catch (PhotonCadProjectException exception)
        {
            var safe = new string(exception.Code.Take(64).Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ToArray());
            return ValueTask.FromResult(new PhotonCadNativeWorkspaceSelection(
                PhotonCadNativeServiceStatus.Rejected,
                safe.Length == 0 ? "native-mount-rejected" : safe,
                null));
        }
        catch (ObjectDisposedException)
        {
            return ValueTask.FromResult(new PhotonCadNativeWorkspaceSelection(
                PhotonCadNativeServiceStatus.Unavailable,
                "windows-native-mount-unavailable",
                null));
        }
    }
}
