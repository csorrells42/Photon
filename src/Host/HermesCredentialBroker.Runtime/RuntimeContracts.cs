using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HermesCredentialBroker;

namespace HermesCredentialBroker.Runtime;

public static class CredentialRuntimeProtocol
{
    public const int Version = 2;
    public const int MaximumHandshakeBytes = 16 * 1024;
    public const int MaximumEncryptedFrameBytes = 96 * 1024;
    public static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumLeaseLifetime = TimeSpan.FromSeconds(60);
}

public sealed class CredentialRuntimeException(string code, string message) : Exception(message)
{
    public string Code { get; } = RuntimeText.Identifier(code, nameof(code), 64);
}

public sealed class CredentialContainerBinding
{
    private static readonly Regex ContainerPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    public CredentialContainerBinding(
        string containerId,
        string imageDigest,
        CredentialPrincipalBinding principal,
        DateTimeOffset expiresAt)
    {
        if (containerId is null || !ContainerPattern.IsMatch(containerId))
        {
            throw new CredentialRuntimeException("invalid_container_id", "The runtime container identity is invalid.");
        }
        if (imageDigest is null || !DigestPattern.IsMatch(imageDigest))
        {
            throw new CredentialRuntimeException("invalid_image_digest", "The runtime image identity is invalid.");
        }
        ContainerId = containerId;
        ImageDigest = imageDigest;
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        ExpiresAt = expiresAt;
    }

    public string ContainerId { get; }
    public string ImageDigest { get; }
    public CredentialPrincipalBinding Principal { get; }
    public DateTimeOffset ExpiresAt { get; }

    internal void ValidateLifetime(DateTimeOffset now)
    {
        if (ExpiresAt <= now || ExpiresAt - now > CredentialRuntimeProtocol.MaximumSessionLifetime)
        {
            throw new CredentialRuntimeException("invalid_session_lifetime", "The runtime credential session lifetime is invalid.");
        }
    }

    internal bool ExactlyMatches(CredentialContainerBinding other) =>
        StringComparer.Ordinal.Equals(ContainerId, other.ContainerId)
        && StringComparer.Ordinal.Equals(ImageDigest, other.ImageDigest)
        && StringComparer.Ordinal.Equals(Principal.UserSid, other.Principal.UserSid)
        && StringComparer.Ordinal.Equals(Principal.MachineId, other.Principal.MachineId)
        && StringComparer.Ordinal.Equals(Principal.InstallId, other.Principal.InstallId)
        && StringComparer.Ordinal.Equals(Principal.ProfileId, other.Principal.ProfileId)
        && ExpiresAt.ToUnixTimeSeconds() == other.ExpiresAt.ToUnixTimeSeconds();
}

public sealed record CredentialRuntimeLeaseRequest(
    string RequestId,
    CredentialReference ConnectionRef,
    string ProfileId,
    string Purpose,
    long ExpectedRevision,
    byte[] RequestNonce) : IDisposable
{
    public CredentialRuntimeLeaseRequest Validate(CredentialContainerBinding binding)
    {
        _ = RuntimeText.Identifier(RequestId, nameof(RequestId), 128);
        _ = new CredentialReference(ConnectionRef.Value);
        var profile = RuntimeText.Identifier(ProfileId, nameof(ProfileId), 128);
        var purpose = RuntimeText.Purpose(Purpose);
        if (!StringComparer.Ordinal.Equals(profile, binding.Principal.ProfileId))
        {
            throw new CredentialRuntimeException("profile_denied", "The runtime credential profile is not authorized.");
        }
        if (ExpectedRevision <= 0)
        {
            throw new CredentialRuntimeException("invalid_revision", "A positive credential revision is required.");
        }
        if (RequestNonce is null || RequestNonce.Length != 32 || RequestNonce.All(value => value == 0))
        {
            throw new CredentialRuntimeException("invalid_request_nonce", "The runtime request nonce is invalid.");
        }
        _ = purpose;
        return this;
    }

    public void Dispose()
    {
        if (RequestNonce is not null) CryptographicOperations.ZeroMemory(RequestNonce);
    }
}

internal static class RuntimeText
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant);

    internal static string Identifier(string? value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || !IdentifierPattern.IsMatch(value))
        {
            throw new CredentialRuntimeException("invalid_identifier", $"{name} is invalid.");
        }
        return value;
    }

    internal static string Purpose(string? value)
    {
        var purpose = Identifier(value, nameof(value), 160);
        if (!purpose.StartsWith("model:", StringComparison.Ordinal)
            && !purpose.StartsWith("mcp:", StringComparison.Ordinal)
            && !purpose.StartsWith("channel:", StringComparison.Ordinal)
            && !purpose.Equals("oauth-refresh", StringComparison.Ordinal)
            && !purpose.Equals("usage", StringComparison.Ordinal))
        {
            throw new CredentialRuntimeException("invalid_purpose", "The credential purpose is not recognized.");
        }
        return purpose;
    }
}
