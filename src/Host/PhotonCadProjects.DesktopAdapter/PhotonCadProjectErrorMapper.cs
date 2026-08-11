namespace PhotonCadProjects.DesktopAdapter;

public sealed record PhotonCadProjectMappedFailure(string Code, bool Retryable, bool Unavailable);

public static class PhotonCadProjectErrorMapper
{
    private static readonly HashSet<string> RetryableUnavailableCodes = new(StringComparer.Ordinal)
    {
        "project_host_reset",
        "project_host_unavailable",
        "windows-native-picker-unavailable",
        "windows-native-picker-failed",
        "windows-native-mount-unavailable",
        "open_project_capacity_reached",
        "workspace_capacity_reached",
        "workspace_selection_capacity_reached",
        "target_registry_capacity_reached",
        "target_transaction_in_progress",
        "durable_read_failed",
        "atomic_save_failed",
        "committed_recovery_required",
        "read_superseded",
    };

    private static readonly HashSet<string> AllowedRejectedCodes = new(StringComparer.Ordinal)
    {
        "unsupported_picker_purpose",
        "invalid_request_id",
        "workspace_selection_unknown_or_used",
        "workspace_selection_purpose_mismatch",
        "workspace_selection_expired",
        "storage_target_already_open",
        "destination_target_already_open",
        "save_as_same_target",
        "overwrite_confirmation_declined",
        "dirty_close_confirmation_required",
        "dirty_close_confirmation_declined",
        "discard_requires_dirty_project",
        "close_binding_mismatch",
        "project_handle_unknown",
        "workspace_handle_unknown",
        "reopen_handle_unknown",
        "reopen_binding_mismatch",
        "save_binding_mismatch",
        "known_revision_ahead",
        "refresh_conflict_unsaved_changes",
        "refresh_revision_regressed",
        "refresh_non_advancing_change",
        "storage_version_conflict",
        "target_exists",
        "target_exists_overwrite_confirmation_required",
        "hard_link_rejected",
        "reparse_point_rejected",
        "exact_path_mismatch",
        "file_identity_changed",
        "codec_storage_size_policy_mismatch",
    };

    public static PhotonCadProjectMappedFailure Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
            return new PhotonCadProjectMappedFailure("request-cancelled", true, false);
        if (exception is ObjectDisposedException)
            return new PhotonCadProjectMappedFailure("project-host-unavailable", true, true);
        if (exception is PhotonCadProjectException project)
        {
            if (RetryableUnavailableCodes.Contains(project.Code))
                return new PhotonCadProjectMappedFailure(ToWireCode(project.Code), true, true);
            if (AllowedRejectedCodes.Contains(project.Code))
                return new PhotonCadProjectMappedFailure(ToWireCode(project.Code), false, false);
            return new PhotonCadProjectMappedFailure("project-action-rejected", false, false);
        }
        if (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            return new PhotonCadProjectMappedFailure("project-storage-unavailable", true, true);
        return new PhotonCadProjectMappedFailure("project-host-failure", true, true);
    }

    private static string ToWireCode(string value) => value.Replace('_', '-');
}
