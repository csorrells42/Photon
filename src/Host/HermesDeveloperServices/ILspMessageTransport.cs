namespace HermesDeveloperServices;

/// <summary>
/// Abstracts one already-authorized LSP stdio or named-pipe adapter. Implementations own the child
/// process and I/O. The session never opens a listener, discovers arbitrary executables, or kills by
/// process name.
/// </summary>
public interface ILspMessageTransport : IAsyncDisposable
{
    ValueTask SendAsync(LspOutgoingMessage message, CancellationToken cancellationToken);

    IAsyncEnumerable<LspIncomingMessage> ReadAllAsync(CancellationToken cancellationToken);
}
