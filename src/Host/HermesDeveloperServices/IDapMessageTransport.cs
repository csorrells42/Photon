namespace HermesDeveloperServices;

/// <summary>
/// Abstracts a message transport to a single already-authorized DAP adapter. Implementations own
/// adapter I/O; the session does not open listeners, attach to processes, or launch arbitrary programs.
/// </summary>
public interface IDapMessageTransport : IAsyncDisposable
{
    ValueTask SendAsync(DapRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<DapIncomingMessage> ReadAllAsync(CancellationToken cancellationToken);
}
