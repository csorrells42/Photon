using System.Security.Cryptography;

namespace HermesCredentialBroker;

public sealed record CredentialLeaseRequest(
    CredentialPrincipalBinding Principal,
    CredentialReference ConnectionRef,
    string Purpose,
    long ExpectedRevision)
{
    public CredentialLeaseRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Principal);
        _ = new CredentialReference(ConnectionRef.Value);
        _ = ContractText.Purpose(Purpose);
        if (ExpectedRevision <= 0)
        {
            throw new CredentialBrokerException("invalid_revision", "A positive credential revision is required.");
        }
        return this;
    }
}

public interface ICredentialLeaseResolver
{
    Task<CredentialLease> ResolveLeaseAsync(CredentialLeaseRequest request, CancellationToken cancellationToken = default);
}

public sealed class CredentialLease : IDisposable
{
    private byte[]? _secret;

    internal CredentialLease(CredentialMetadata metadata, string purpose, byte[] secret)
    {
        Metadata = metadata;
        Purpose = ContractText.Purpose(purpose);
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        if (_secret.Length == 0 || _secret.Length > HermesCredentialBrokerProtocol.MaximumSecretBytes)
        {
            Dispose();
            throw new CredentialBrokerException("record_integrity_failure", "The credential record failed integrity validation.");
        }
    }

    public CredentialMetadata Metadata { get; }
    public string Purpose { get; }
    public int SecretLength => _secret?.Length ?? throw new ObjectDisposedException(nameof(CredentialLease));

    public void CopySecretTo(Span<byte> destination)
    {
        var secret = _secret ?? throw new ObjectDisposedException(nameof(CredentialLease));
        if (destination.Length != secret.Length)
        {
            throw new ArgumentException("The destination must exactly match the credential length.", nameof(destination));
        }
        secret.CopyTo(destination);
    }

    public void Dispose()
    {
        var secret = Interlocked.Exchange(ref _secret, null);
        if (secret is not null) CryptographicOperations.ZeroMemory(secret);
    }
}
