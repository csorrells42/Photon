namespace PhotonCadProjects.DesktopAdapter;

public static class PhotonCadProjectDesktopProtocol
{
    public const int Version = 1;
    public const int MaximumActiveRequests = 8;
    public const int MaximumReplayEntries = 50_000;
    public static readonly TimeSpan ReplayTimeToLive = TimeSpan.FromMinutes(30);

    public static TimeSpan Deadline(PhotonCadProjectDesktopOperation operation) => operation switch
    {
        PhotonCadProjectDesktopOperation.Refresh or PhotonCadProjectDesktopOperation.Close => TimeSpan.FromMinutes(2),
        PhotonCadProjectDesktopOperation.Cancel => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromMinutes(10),
    };
}

public enum PhotonCadProjectDesktopOperation
{
    Picker,
    Create,
    Open,
    Reopen,
    Refresh,
    Save,
    SaveAs,
    Close,
    Cancel,
}

public abstract record PhotonCadProjectDesktopRequest(
    PhotonCadProjectDesktopOperation Operation,
    string RequestId);

public sealed record PhotonCadProjectDesktopPickerRequest(string RequestId, string Purpose, string? SuggestedName)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.Picker, RequestId);

public sealed record PhotonCadProjectDesktopCreateRequest(PhotonCadProjectCreateRequest Value)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.Create, Value.RequestId);

public sealed record PhotonCadProjectDesktopOpenRequest(PhotonCadProjectOpenRequest Value)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.Open, Value.RequestId);

public sealed record PhotonCadProjectDesktopReopenRequest(PhotonCadProjectReopenRequest Value)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.Reopen, Value.RequestId);

public sealed record PhotonCadProjectDesktopRefreshRequest(PhotonCadProjectRefreshRequest Value)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.Refresh, Value.RequestId);

public sealed record PhotonCadProjectDesktopSaveRequest(PhotonCadProjectSaveRequest Value)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.Save, Value.RequestId);

public sealed record PhotonCadProjectDesktopSaveAsRequest(PhotonCadProjectSaveAsRequest Value)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.SaveAs, Value.RequestId);

public sealed record PhotonCadProjectDesktopCloseRequest(PhotonCadProjectCloseRequest Value)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.Close, Value.RequestId);

public sealed record PhotonCadProjectDesktopCancelRequest(
    string RequestId,
    string TargetRequestId,
    PhotonCadProjectDesktopOperation TargetOperation)
    : PhotonCadProjectDesktopRequest(PhotonCadProjectDesktopOperation.Cancel, RequestId);
