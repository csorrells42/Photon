using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AssistantConversationBus;

public sealed record AssistantPeerConnection(
    AssistantIdentity Identity,
    Uri Endpoint,
    string AuthenticationToken,
    string Capability);

public static class AssistantPeerSettings
{
    private const int MaximumSettingsBytes = 64 * 1024;

    public static IAssistantPeerBridge[] LoadInstalledPeerBridges(string? localRoot = null)
    {
        var local = localRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            LoadOrOffline(AssistantIdentity.Photon, Path.Combine(local, "HermesWorkbench", "conversation-bridge.json"), "visible-turn"),
            LoadOrOffline(AssistantIdentity.Ali, Path.Combine(local, "AliFiles", "Settings", "ConversationBridge", "conversation-bridge.json"), "invoke-only"),
            LoadOrOffline(AssistantIdentity.Scarlett, Path.Combine(local, "ScarlettFiles", "Settings", "ConversationBridge", "conversation-bridge.json"), "invoke-only"),
        ];
    }

    public static IReadOnlyList<AssistantPeerConnection> LoadInstalledPeers()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            Load(AssistantIdentity.Photon, Path.Combine(local, "HermesWorkbench", "conversation-bridge.json"), "visible-turn"),
            Load(AssistantIdentity.Ali, Path.Combine(local, "AliFiles", "Settings", "ConversationBridge", "conversation-bridge.json"), "invoke-only"),
            Load(AssistantIdentity.Scarlett, Path.Combine(local, "ScarlettFiles", "Settings", "ConversationBridge", "conversation-bridge.json"), "invoke-only"),
        ];
    }

    public static AssistantPeerConnection Load(
        AssistantIdentity identity,
        string path,
        string capability)
    {
        AssistantIdentityPolicy.RequireSender(identity);
        if (identity is AssistantIdentity.Chris or AssistantIdentity.Codex)
            throw new AssistantBusValidationException("invalid_peer_identity", "Bus client identities cannot be registered as application peers.");
        var payload = ReadBounded(path);
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("port", out var portElement)
                || !portElement.TryGetInt32(out var port)
                || port is < 1024 or > 65535
                || !root.TryGetProperty("authenticationToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"{identity} bridge settings are incomplete.");
            }
            var token = tokenElement.GetString();
            if (token is null || token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"{identity} bridge authentication is invalid.");
            var endpoint = RequireLoopback(new Uri($"http://127.0.0.1:{port}/"));
            return new AssistantPeerConnection(identity, endpoint, token, capability);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{identity} bridge settings are malformed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static Uri RequireLoopback(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath != "/"
            || endpoint.Port is < 1024 or > 65535
            || !IPAddress.TryParse(endpoint.Host, out var address)
            || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("Assistant peer endpoints must be literal HTTP loopback roots.", nameof(endpoint));
        }
        return endpoint;
    }

    private static byte[] ReadBounded(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumSettingsBytes)
                throw new InvalidDataException("Assistant peer settings exceed the bounded size.");
            var payload = new byte[checked((int)stream.Length)];
            stream.ReadExactly(payload);
            return payload;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException("Assistant peer settings are not installed.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidDataException("Assistant peer settings cannot be read by the current user.", exception);
        }
    }

    private static IAssistantPeerBridge LoadOrOffline(
        AssistantIdentity identity,
        string path,
        string capability)
    {
        try { return new LoopbackAssistantPeerBridge(Load(identity, path, capability)); }
        catch (InvalidDataException) { return new OfflineAssistantPeerBridge(identity, capability); }
    }
}

internal sealed class OfflineAssistantPeerBridge(
    AssistantIdentity identity,
    string capability) : IAssistantPeerBridge
{
    public AssistantIdentity Identity { get; } = identity;

    public Task<AssistantParticipantStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AssistantParticipantStatus(Identity, false, false, capability, null, null));

    public Task<AssistantPeerReply> SendAsync(AssistantBusMessage message, CancellationToken cancellationToken) =>
        Task.FromResult(new AssistantPeerReply(AssistantDeliveryState.Offline, Code: "peer_not_installed"));

    public Task<AssistantDeliveryState> ObserveAsync(AssistantBusMessage message, CancellationToken cancellationToken) =>
        Task.FromResult(AssistantDeliveryState.Offline);
}
