using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PhotonCadProjects.Codec;

internal sealed record PhotonCadDecodedFileV1(PhotonCadProjectStateV1 State, string LogicalDigest);

internal static class PhotonCadProjectFramingV1
{
    internal static byte[] Encode(PhotonCadManifestEncodingV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var totalLength = ValidateDeclaredLayout(manifest.Bytes.LongLength, manifest.Blobs.Select(blob => blob.ByteLength).ToArray());
        var result = GC.AllocateUninitializedArray<byte>(checked((int)totalLength));
        var span = result.AsSpan();
        PhotonCadProjectFileV1.Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], PhotonCadProjectFileV1.HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], 0);
        BinaryPrimitives.WriteUInt64BigEndian(span[24..], checked((ulong)totalLength));
        BinaryPrimitives.WriteUInt64BigEndian(span[32..], checked((ulong)manifest.Bytes.LongLength));
        BinaryPrimitives.WriteUInt32BigEndian(span[40..], checked((uint)manifest.Blobs.Count));
        BinaryPrimitives.WriteUInt32BigEndian(span[44..], 0);
        ComputeLogicalDigest(manifest.Bytes).CopyTo(span[48..80]);
        span[80..PhotonCadProjectFileV1.HeaderLength].Clear();
        var cursor = PhotonCadProjectFileV1.HeaderLength;
        manifest.Bytes.CopyTo(span[cursor..]);
        cursor = checked(cursor + manifest.Bytes.Length);
        for (var ordinal = 0; ordinal < manifest.Blobs.Count; ordinal++)
        {
            var blob = manifest.Blobs[ordinal];
            var header = span.Slice(cursor, PhotonCadProjectFileV1.BlobHeaderLength);
            header.Clear();
            header[0] = (byte)blob.Kind;
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], PhotonCadProjectFileV1.BlobHeaderLength);
            BinaryPrimitives.WriteUInt64BigEndian(header[8..], checked((ulong)blob.ByteLength));
            Convert.FromHexString(PhotonCadFileGuardsV1.RawDigestHex(blob.Digest)).CopyTo(header[16..48]);
            BinaryPrimitives.WriteUInt64BigEndian(header[48..], checked((ulong)ordinal));
            cursor = checked(cursor + PhotonCadProjectFileV1.BlobHeaderLength);
            blob.Content.Span.CopyTo(span[cursor..]);
            cursor = checked(cursor + blob.Content.Length);
        }
        if (cursor != result.Length) throw PhotonCadFileGuardsV1.Failure("framing_length_mismatch", "file");
        return result;
    }

    internal static PhotonCadDecodedFileV1 Decode(ReadOnlyMemory<byte> file)
    {
        if (file.Length < PhotonCadProjectFileV1.HeaderLength || file.Length > PhotonCadProjectFileV1.MaximumEncodedBytes)
            throw PhotonCadFileGuardsV1.Failure("invalid_file_length", "file");
        var span = file.Span;
        if (!span[..PhotonCadProjectFileV1.Magic.Length].SequenceEqual(PhotonCadProjectFileV1.Magic))
            throw PhotonCadFileGuardsV1.Failure("unsupported_file_magic", "file");
        if (BinaryPrimitives.ReadUInt32BigEndian(span[16..]) != PhotonCadProjectFileV1.HeaderLength
            || BinaryPrimitives.ReadUInt32BigEndian(span[20..]) != 0
            || BinaryPrimitives.ReadUInt32BigEndian(span[44..]) != 0
            || span[80..PhotonCadProjectFileV1.HeaderLength].IndexOfAnyExcept((byte)0) >= 0)
            throw PhotonCadFileGuardsV1.Failure("invalid_file_header", "file");
        var declaredTotal = ReadBoundedLength(span[24..], "totalLength", PhotonCadProjectFileV1.MaximumEncodedBytes);
        var manifestLength = ReadBoundedLength(span[32..], "manifestLength", PhotonCadProjectFileV1.MaximumManifestBytes);
        if (declaredTotal != file.Length || manifestLength <= 0)
            throw PhotonCadFileGuardsV1.Failure("declared_length_mismatch", "file");
        var blobCountRaw = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
        if (blobCountRaw > PhotonCadProjectFileV1.MaximumBlobCount)
            throw PhotonCadFileGuardsV1.Failure("blob_count_too_large", "file");
        var blobCount = checked((int)blobCountRaw);
        var minimum = checked((long)PhotonCadProjectFileV1.HeaderLength + manifestLength + ((long)blobCount * PhotonCadProjectFileV1.BlobHeaderLength));
        if (minimum > declaredTotal) throw PhotonCadFileGuardsV1.Failure("truncated_file_layout", "file");

        var cursor = PhotonCadProjectFileV1.HeaderLength;
        var manifest = file.Slice(cursor, checked((int)manifestLength));
        cursor = checked(cursor + (int)manifestLength);
        var logicalDigestBytes = ComputeLogicalDigest(manifest.Span);
        if (!CryptographicOperations.FixedTimeEquals(logicalDigestBytes, span[48..80]))
            throw PhotonCadFileGuardsV1.Failure("logical_digest_mismatch", "file");

        var blobs = new PhotonCadBlobV1[blobCount];
        string? previousDigest = null;
        PhotonCadArtifactKindV1 previousKind = default;
        for (var ordinal = 0; ordinal < blobCount; ordinal++)
        {
            if (file.Length - cursor < PhotonCadProjectFileV1.BlobHeaderLength)
                throw PhotonCadFileGuardsV1.Failure("truncated_blob_header", "file");
            var header = span.Slice(cursor, PhotonCadProjectFileV1.BlobHeaderLength);
            if (header[1] != 0 || BinaryPrimitives.ReadUInt16BigEndian(header[2..]) != 0
                || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != PhotonCadProjectFileV1.BlobHeaderLength
                || BinaryPrimitives.ReadUInt64BigEndian(header[48..]) != checked((ulong)ordinal)
                || BinaryPrimitives.ReadUInt64BigEndian(header[56..]) != 0)
                throw PhotonCadFileGuardsV1.Failure("invalid_blob_header", "file");
            var kind = header[0] switch
            {
                (byte)PhotonCadArtifactKindV1.Step => PhotonCadArtifactKindV1.Step,
                (byte)PhotonCadArtifactKindV1.Glb => PhotonCadArtifactKindV1.Glb,
                _ => throw PhotonCadFileGuardsV1.Failure("unsupported_blob_kind", "file"),
            };
            var payloadLength = ReadBoundedLength(header[8..], "blobLength", PhotonCadProjectFileV1.MaximumEncodedBytes);
            if (payloadLength <= 0) throw PhotonCadFileGuardsV1.Failure("empty_blob", "file");
            cursor = checked(cursor + PhotonCadProjectFileV1.BlobHeaderLength);
            if (payloadLength > file.Length - cursor) throw PhotonCadFileGuardsV1.Failure("truncated_blob", "file");
            var content = file.Slice(cursor, checked((int)payloadLength));
            cursor = checked(cursor + (int)payloadLength);
            var actualDigest = SHA256.HashData(content.Span);
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, header[16..48]))
                throw PhotonCadFileGuardsV1.Failure("blob_digest_mismatch", "file");
            var digest = $"sha256:{Convert.ToHexStringLower(actualDigest)}";
            if (previousDigest is not null)
            {
                var order = StringComparer.Ordinal.Compare(previousDigest, digest);
                if (order > 0 || (order == 0 && previousKind >= kind))
                    throw PhotonCadFileGuardsV1.Failure("noncanonical_blob_order", "file");
            }
            previousDigest = digest;
            previousKind = kind;
            blobs[ordinal] = new PhotonCadBlobV1(kind, digest, content);
        }
        if (cursor != file.Length) throw PhotonCadFileGuardsV1.Failure("trailing_file_data", "file");

        var state = PhotonCadManifestReaderV1.Parse(manifest, blobs);
        var canonical = Encode(PhotonCadManifestWriterV1.Encode(state));
        if (!canonical.AsSpan().SequenceEqual(span))
            throw PhotonCadFileGuardsV1.Failure("noncanonical_file_encoding", "file");
        return new PhotonCadDecodedFileV1(state, $"sha256:{Convert.ToHexStringLower(logicalDigestBytes)}");
    }

    internal static long ValidateDeclaredLayout(long manifestLength, IReadOnlyList<long> payloadLengths)
    {
        if (manifestLength <= 0 || manifestLength > PhotonCadProjectFileV1.MaximumManifestBytes)
            throw PhotonCadFileGuardsV1.Failure("invalid_manifest_length", "manifestLength");
        ArgumentNullException.ThrowIfNull(payloadLengths);
        if (payloadLengths.Count > PhotonCadProjectFileV1.MaximumBlobCount)
            throw PhotonCadFileGuardsV1.Failure("blob_count_too_large", "payloadLengths");
        try
        {
            var total = checked((long)PhotonCadProjectFileV1.HeaderLength + manifestLength);
            foreach (var payloadLength in payloadLengths)
            {
                if (payloadLength <= 0 || payloadLength > PhotonCadProjectFileV1.MaximumEncodedBytes)
                    throw PhotonCadFileGuardsV1.Failure("invalid_blob_length", "payloadLengths");
                total = checked(total + PhotonCadProjectFileV1.BlobHeaderLength + payloadLength);
                if (total > PhotonCadProjectFileV1.MaximumEncodedBytes)
                    throw PhotonCadFileGuardsV1.Failure("file_too_large", "file");
            }
            if (total > PhotonCadProjectFileV1.MaximumEncodedBytes)
                throw PhotonCadFileGuardsV1.Failure("file_too_large", "file");
            return total;
        }
        catch (OverflowException exception)
        {
            throw new PhotonCadProjectException("file_length_overflow", "file", exception);
        }
    }

    internal static byte[] ComputeLogicalDigest(ReadOnlySpan<byte> manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(PhotonCadProjectFileV1.LogicalDigestDomain);
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)manifest.Length));
        hash.AppendData(length);
        hash.AppendData(manifest);
        return hash.GetHashAndReset();
    }

    private static long ReadBoundedLength(ReadOnlySpan<byte> value, string field, long maximum)
    {
        var raw = BinaryPrimitives.ReadUInt64BigEndian(value);
        if (raw > checked((ulong)maximum)) throw PhotonCadFileGuardsV1.Failure("declared_length_too_large", field);
        return checked((long)raw);
    }
}
