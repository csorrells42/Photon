using System.Security.Cryptography;

namespace HermesUsageCollectors;

public interface IProviderSecretSource
{
    ValueTask<ProviderSecret?> GetSecretAsync(string credentialReference, CancellationToken cancellationToken = default);
}

public sealed class DelegateSecretSource(
    Func<string, CancellationToken, ValueTask<ProviderSecret?>> resolver) : IProviderSecretSource
{
    public ValueTask<ProviderSecret?> GetSecretAsync(string credentialReference, CancellationToken cancellationToken = default) =>
        resolver(credentialReference, cancellationToken);
}

public sealed class ProviderSecret : IDisposable
{
    private char[]? _characters;
    private ProviderSecret(char[] characters) => _characters = characters;

    public static ProviderSecret FromString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ProviderSecret(value.ToCharArray());
    }

    internal string Materialize()
    {
        ObjectDisposedException.ThrowIf(_characters is null, this);
        return new string(_characters);
    }

    public void Dispose()
    {
        if (_characters is null) return;
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_characters.AsSpan()));
        _characters = null;
    }
}
