using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using HermesCredentialBroker;

namespace HermesCredentialBroker.Runtime;

public enum CredentialRuntimeMessageKind
{
    Text,
    Binary,
    Close,
}

public sealed record CredentialRuntimeMessage(CredentialRuntimeMessageKind Kind, byte[] Payload) : IDisposable
{
    public void Dispose() => CryptographicOperations.ZeroMemory(Payload);
}

public interface ICredentialRuntimeSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);
    Task SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken);
    Task SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task<CredentialRuntimeMessage> ReceiveAsync(int maximumBytes, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}

public sealed class ClientWebSocketCredentialRuntimeSocket : ICredentialRuntimeSocket
{
    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) => _socket.ConnectAsync(endpoint, cancellationToken);

    public Task SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken) =>
        _socket.SendAsync(utf8, WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, cancellationToken).AsTask();

    public Task SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        _socket.SendAsync(bytes, WebSocketMessageType.Binary, WebSocketMessageFlags.EndOfMessage, cancellationToken).AsTask();

    public async Task<CredentialRuntimeMessage> ReceiveAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var stream = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maximumBytes)];
        WebSocketMessageType? messageType = null;
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (messageType.HasValue && messageType != result.MessageType)
                {
                    throw new CredentialRuntimeException("fragment_type_changed", "The runtime socket changed message type mid-frame.");
                }
                messageType = result.MessageType;
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new CredentialRuntimeMessage(CredentialRuntimeMessageKind.Close, []);
                }
                if (stream.Length + result.Count > maximumBytes)
                {
                    throw new CredentialRuntimeException("socket_message_too_large", "The runtime socket message is too large.");
                }
                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            return new CredentialRuntimeMessage(
                messageType == WebSocketMessageType.Text ? CredentialRuntimeMessageKind.Text : CredentialRuntimeMessageKind.Binary,
                stream.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "credential session closed", cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class CredentialRuntimeClient(
    Func<ICredentialRuntimeSocket> socketFactory,
    TimeProvider? timeProvider = null)
{
    private const int MaximumRequestsPerSession = 2048;
    private readonly Func<ICredentialRuntimeSocket> _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(
        Uri endpoint,
        CredentialRuntimeBootstrap bootstrap,
        ICredentialLeaseResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ValidateLoopbackEndpoint(endpoint, bootstrap.SessionId);
        ArgumentNullException.ThrowIfNull(resolver);
        bootstrap.Binding.ValidateLifetime(_timeProvider.GetUtcNow());
        var remaining = bootstrap.Binding.ExpiresAt - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            throw new CredentialRuntimeException("session_expired", "The credential runtime session has expired.");
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expiry.CancelAfter(remaining);
        await using var socket = _socketFactory();
        await socket.ConnectAsync(endpoint, expiry.Token).ConfigureAwait(false);
        using var handshake = new CredentialRuntimeCryptography(bootstrap);
        var helloBytes = Encoding.UTF8.GetBytes(CredentialRuntimeCryptography.SerializeHello(handshake.CreateHostHello()));
        try
        {
            await socket.SendTextAsync(helloBytes, expiry.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(helloBytes);
        }
        using var serverMessage = await socket.ReceiveAsync(CredentialRuntimeProtocol.MaximumHandshakeBytes, expiry.Token).ConfigureAwait(false);
        if (serverMessage.Kind != CredentialRuntimeMessageKind.Text)
        {
            throw new CredentialRuntimeException("server_hello_type", "The runtime server hello was not a text frame.");
        }
        var serverHello = CredentialRuntimeCryptography.ParseServerHello(serverMessage.Payload);
        using var cipher = handshake.AcceptServerHello(serverHello, _timeProvider.GetUtcNow());
        var requestNonces = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (!expiry.IsCancellationRequested)
            {
                using var message = await socket.ReceiveAsync(CredentialRuntimeProtocol.MaximumEncryptedFrameBytes, expiry.Token).ConfigureAwait(false);
                if (message.Kind == CredentialRuntimeMessageKind.Close) break;
                if (message.Kind != CredentialRuntimeMessageKind.Binary)
                {
                    throw new CredentialRuntimeException("encrypted_frame_type", "The runtime credential frame was not binary.");
                }
                var plaintext = cipher.DecryptContainerRequest(message.Payload);
                try
                {
                    using var request = CredentialLeaseWireCodec.DecodeRequest(plaintext, bootstrap.Binding);
                    var nonceFingerprint = Convert.ToHexString(SHA256.HashData(request.RequestNonce));
                    if (requestNonces.Count >= MaximumRequestsPerSession || !requestNonces.Add(nonceFingerprint))
                    {
                        throw new CredentialRuntimeException("request_nonce_replay", "The runtime request nonce was replayed.");
                    }
                    var response = await ResolveAsync(request, bootstrap, resolver, expiry.Token).ConfigureAwait(false);
                    try
                    {
                        var encrypted = cipher.EncryptHostResponse(response);
                        try
                        {
                            await socket.SendBinaryAsync(encrypted, expiry.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(encrypted);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(response);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Session expiry is a normal fail-closed shutdown.
        }
        finally
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await socket.CloseAsync(closeTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            requestNonces.Clear();
        }
    }

    private async Task<byte[]> ResolveAsync(
        CredentialRuntimeLeaseRequest request,
        CredentialRuntimeBootstrap bootstrap,
        ICredentialLeaseResolver resolver,
        CancellationToken cancellationToken)
    {
        try
        {
            using var lease = await resolver.ResolveLeaseAsync(new CredentialLeaseRequest(
                bootstrap.Binding.Principal,
                request.ConnectionRef,
                request.Purpose,
                request.ExpectedRevision), cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var leaseExpiry = now.Add(CredentialRuntimeProtocol.MaximumLeaseLifetime);
            if (leaseExpiry > bootstrap.Binding.ExpiresAt) leaseExpiry = bootstrap.Binding.ExpiresAt;
            return CredentialLeaseWireCodec.EncodeSuccess(request, lease, leaseExpiry);
        }
        catch (CredentialBrokerException exception)
        {
            return CredentialLeaseWireCodec.EncodeFailure(request, exception.Code, Retryable(exception.Code));
        }
    }

    private static bool Retryable(string code) => false;

    public static void ValidateLoopbackEndpoint(Uri endpoint, string expectedSessionId)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _ = RuntimeText.Identifier(expectedSessionId, nameof(expectedSessionId), 64);
        var host = endpoint.IdnHost;
        var exactLoopback = StringComparer.OrdinalIgnoreCase.Equals(host, "localhost")
            || IPAddress.TryParse(host, out var address)
                && (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback));
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("ws" or "wss")
            || endpoint.UserInfo.Length != 0
            || !exactLoopback
            || !StringComparer.Ordinal.Equals(endpoint.AbsolutePath, "/api/workbench/credentials/v2")
            || endpoint.Fragment.Length != 0)
        {
            throw new CredentialRuntimeException("non_loopback_endpoint", "The runtime credential endpoint must be an absolute loopback WebSocket URI.");
        }
        var query = System.Web.HttpUtility.ParseQueryString(endpoint.Query);
        if (!StringComparer.Ordinal.Equals(query["session"], expectedSessionId) || query.AllKeys.Any(key => key != "session"))
        {
            throw new CredentialRuntimeException("endpoint_session_mismatch", "The runtime credential endpoint is not bound to this session.");
        }
    }
}
