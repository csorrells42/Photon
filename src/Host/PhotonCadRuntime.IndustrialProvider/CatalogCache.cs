using System.Text.Json;

namespace PhotonCadRuntime.IndustrialProvider;

internal sealed class CatalogCache
{
    private readonly Func<CancellationToken, ValueTask<byte[]>> _loader;
    private readonly string _expectedDigest;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _catalog;

    internal CatalogCache(Func<CancellationToken, ValueTask<byte[]>> loader, string expectedDigest)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _expectedDigest = ProtocolV1.NormalizeDigest(expectedDigest);
    }

    internal async ValueTask<ReadOnlyMemory<byte>> GetAsync(CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref _catalog);
        if (snapshot is not null) return snapshot.ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = _catalog;
            if (snapshot is null)
            {
                var candidate = await _loader(cancellationToken).ConfigureAwait(false);
                if (candidate.Length <= 0 || candidate.Length > 4 * 1024 * 1024)
                    throw new InvalidDataException("industrial_catalog_size_rejected");
                using JsonDocument document = ProtocolV1.ParseCatalog(candidate, _expectedDigest);
                if (!document.RootElement.TryGetProperty("schema", out var schema)
                    || schema.GetString() != "photon.cad.industrial.catalog/v1")
                    throw new InvalidDataException("industrial_catalog_schema_rejected");
                snapshot = candidate.ToArray();
                Volatile.Write(ref _catalog, snapshot);
            }
            return snapshot.ToArray();
        }
        finally { _gate.Release(); }
    }
}
