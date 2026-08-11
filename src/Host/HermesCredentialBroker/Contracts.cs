using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace HermesCredentialBroker;

public static class HermesCredentialBrokerProtocol
{
    public const int Version = 2;
    public const int MaximumSecretBytes = 64 * 1024;
    public static readonly TimeSpan MaximumReviewLifetime = TimeSpan.FromMinutes(5);
}

public enum CredentialAuthKind
{
    ApiKey,
    OAuth,
    DeviceCode,
}

public enum CredentialSourceKind
{
    Native,
    OAuth,
    DeviceCode,
    ExternalCli,
}

public enum CredentialReviewAction
{
    Change,
    Remove,
}

public sealed class CredentialBrokerException(string code, string message) : Exception(message)
{
    public string Code { get; } = ContractText.Identifier(code, nameof(code), 64);
}

public readonly record struct CredentialReference
{
    private static readonly Regex Pattern = new("^hcv2_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);

    [JsonConstructor]
    public CredentialReference(string value)
    {
        if (value is null || !Pattern.IsMatch(value))
        {
            throw new CredentialBrokerException("invalid_reference", "The credential reference is invalid.");
        }
        Value = value;
    }

    public string Value { get; }

    public static CredentialReference Create()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var encoded = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        CryptographicOperations.ZeroMemory(random);
        return new CredentialReference($"hcv2_{encoded}");
    }

    public override string ToString() => Value;
}

public sealed class CredentialPrincipalBinding
{
    public CredentialPrincipalBinding(string userSid, string machineId, string installId, string profileId)
    {
        UserSid = ContractText.Sid(userSid);
        MachineId = ContractText.Identifier(machineId, nameof(machineId), 128);
        InstallId = ContractText.Identifier(installId, nameof(installId), 128);
        ProfileId = ContractText.Identifier(profileId, nameof(profileId), 128);
    }

    public string UserSid { get; }
    public string MachineId { get; }
    public string InstallId { get; }
    public string ProfileId { get; }
}

public sealed class CredentialBinding
{
    public CredentialBinding(
        CredentialPrincipalBinding principal,
        string providerId,
        string slotId,
        CredentialAuthKind authKind,
        CredentialSourceKind sourceKind,
        IReadOnlyList<string> purposes)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        ProviderId = ContractText.Identifier(providerId, nameof(providerId), 96);
        SlotId = ContractText.Identifier(slotId, nameof(slotId), 128);
        AuthKind = authKind;
        SourceKind = sourceKind;
        Purposes = purposes
            .Select(value => ContractText.Purpose(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (Purposes.Count == 0)
        {
            throw new CredentialBrokerException("missing_purpose", "At least one credential purpose is required.");
        }
    }

    public CredentialPrincipalBinding Principal { get; }
    public string ProviderId { get; }
    public string SlotId { get; }
    public CredentialAuthKind AuthKind { get; }
    public CredentialSourceKind SourceKind { get; }
    public IReadOnlyList<string> Purposes { get; }

    internal bool ExactlyMatches(CredentialBinding other) =>
        StringComparer.Ordinal.Equals(Principal.UserSid, other.Principal.UserSid)
        && StringComparer.Ordinal.Equals(Principal.MachineId, other.Principal.MachineId)
        && StringComparer.Ordinal.Equals(Principal.InstallId, other.Principal.InstallId)
        && StringComparer.Ordinal.Equals(Principal.ProfileId, other.Principal.ProfileId)
        && StringComparer.Ordinal.Equals(ProviderId, other.ProviderId)
        && StringComparer.Ordinal.Equals(SlotId, other.SlotId)
        && AuthKind == other.AuthKind
        && SourceKind == other.SourceKind
        && Purposes.SequenceEqual(other.Purposes, StringComparer.Ordinal);
}

public sealed record CredentialMetadata(
    CredentialReference ConnectionRef,
    string ProfileId,
    string ProviderId,
    string SlotId,
    CredentialAuthKind AuthKind,
    CredentialSourceKind SourceKind,
    IReadOnlyList<string> Purposes,
    long Revision,
    DateTimeOffset UpdatedAt,
    bool Configured = true);

public sealed record CredentialWriteIntent(
    string WorkbenchSessionId,
    CredentialBinding Binding,
    CredentialReference? ExistingReference,
    long ExpectedRevision);

public sealed record CredentialRemoveIntent(
    string WorkbenchSessionId,
    CredentialReference ConnectionRef,
    long ExpectedRevision);

public sealed record CredentialReviewTicket(
    string ReviewHandle,
    CredentialReviewAction Action,
    CredentialReference? ConnectionRef,
    string ProfileId,
    string ProviderId,
    string SlotId,
    long ExpectedRevision,
    DateTimeOffset ExpiresAt);

public sealed class CredentialSecret : IDisposable
{
    private byte[]? _bytes;

    public CredentialSecret(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > HermesCredentialBrokerProtocol.MaximumSecretBytes)
        {
            throw new CredentialBrokerException("invalid_secret_size", "The credential value has an invalid size.");
        }

        _bytes = bytes.ToArray();
    }

    internal byte[] Consume()
    {
        var value = Interlocked.Exchange(ref _bytes, null)
            ?? throw new CredentialBrokerException("secret_consumed", "The pending credential value is no longer available.");
        return value;
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _bytes, null);
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }
}

internal static class ContractText
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SidPattern = new("^S-[0-9-]{5,184}$", RegexOptions.CultureInvariant);

    internal static string Identifier(string? value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || !IdentifierPattern.IsMatch(value))
        {
            throw new CredentialBrokerException("invalid_identifier", $"{name} is invalid.");
        }
        return value;
    }

    internal static string Sid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SidPattern.IsMatch(value))
        {
            throw new CredentialBrokerException("invalid_principal", "The Windows principal is invalid.");
        }
        return value;
    }

    internal static string Purpose(string? value)
    {
        var purpose = Identifier(value, "purpose", 160);
        if (!purpose.StartsWith("model:", StringComparison.Ordinal)
            && !purpose.StartsWith("mcp:", StringComparison.Ordinal)
            && !purpose.StartsWith("channel:", StringComparison.Ordinal)
            && !purpose.Equals("oauth-refresh", StringComparison.Ordinal)
            && !purpose.Equals("usage", StringComparison.Ordinal))
        {
            throw new CredentialBrokerException("invalid_purpose", "The credential purpose is not recognized.");
        }
        return purpose;
    }
}
