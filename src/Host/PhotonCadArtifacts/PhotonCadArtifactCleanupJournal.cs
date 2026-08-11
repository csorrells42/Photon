using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotonCadArtifacts;

internal sealed record PhotonCadArtifactCleanupIntent(
    string ArtifactHandle,
    string PartialPath,
    string SealedPath,
    PhotonCadWindowsFileIdentity ArtifactIdentity,
    long ByteLength,
    string ContentDigest,
    string ReasonCode,
    string JournalPath,
    PhotonCadWindowsFileIdentity JournalIdentity,
    long JournalByteLength,
    string JournalDigest);

internal sealed class PhotonCadArtifactCleanupJournal
{
    private const int MaximumRecordBytes = 2_048;
    private const int RecordVersion = 1;
    private readonly string _root;
    private readonly string _sealedRoot;
    private readonly int _maximumEntries;
    private readonly List<string> _blockedSlots = [];

    internal PhotonCadArtifactCleanupJournal(string brokerRoot, string sealedRoot, int maximumEntries)
    {
        _root = Path.Combine(brokerRoot, "transactions");
        _sealedRoot = sealedRoot;
        _maximumEntries = maximumEntries;
        PhotonCadWindowsFilePolicy.EnsureOwnedDirectory(_root);
    }

    internal IReadOnlyList<string> BlockedReasonCodes => _blockedSlots;

    internal IReadOnlyList<PhotonCadArtifactCleanupIntent> Load()
    {
        var recovered = new List<PhotonCadArtifactCleanupIntent>();
        _blockedSlots.Clear();
        for (var index = 0; index < _maximumEntries; index++)
        {
            var slot = SlotPath(index);
            FileStream stream;
            try
            {
                stream = new FileStream(
                    slot,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    MaximumRecordBytes,
                    FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                throw new PhotonCadArtifactException("cleanup_journal_unavailable");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _blockedSlots.Add("cleanup_journal_inaccessible");
                continue;
            }

            using (stream)
            {
                try
                {
                    var metadata = PhotonCadWindowsFilePolicy.InspectRegularFileMetadata(stream.SafeFileHandle);
                    if (metadata.ByteLength is <= 0 or > MaximumRecordBytes)
                        throw new PhotonCadArtifactException("cleanup_journal_invalid");
                    var bytes = new byte[metadata.ByteLength];
                    stream.ReadExactly(bytes);
                    var parsed = Parse(bytes, slot, metadata);
                    recovered.Add(parsed);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    JsonException or DecoderFallbackException or PhotonCadArtifactException or FormatException or OverflowException)
                {
                    _blockedSlots.Add("cleanup_journal_invalid");
                }
            }
        }
        return recovered;
    }

    internal PhotonCadArtifactCleanupIntent Reserve(
        PhotonCadArtifactHandle handle,
        string partialPath,
        string sealedPath,
        PhotonCadWindowsFileIdentity identity,
        long byteLength,
        string digest,
        string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var token = handle.Value["cad-artifact:".Length..];
        var partialName = Path.GetFileName(partialPath);
        var sealedName = Path.GetFileName(sealedPath);
        if (!string.Equals(partialName, $".{token}.partial", StringComparison.Ordinal) ||
            !string.Equals(sealedName, $"{token}.step", StringComparison.Ordinal))
            throw new PhotonCadArtifactException("cleanup_journal_invalid");
        _ = PhotonCadArtifactGuards.ByteLength(
            byteLength,
            PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes,
            nameof(byteLength));
        digest = PhotonCadArtifactGuards.Digest(digest, nameof(digest));
        reasonCode = PhotonCadArtifactGuards.Reason(reasonCode, nameof(reasonCode));
        var bytes = Serialize(
            handle.Value,
            partialName,
            sealedName,
            identity,
            byteLength,
            digest,
            reasonCode);

        for (var index = 0; index < _maximumEntries; index++)
        {
            var slot = SlotPath(index);
            try
            {
                using var stream = new FileStream(
                    slot,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    MaximumRecordBytes,
                    FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                var metadata = PhotonCadWindowsFilePolicy.InspectRegularFileMetadata(stream.SafeFileHandle);
                PhotonCadWindowsFilePolicy.FlushDirectory(_root);
                return new PhotonCadArtifactCleanupIntent(
                    handle.Value,
                    partialPath,
                    sealedPath,
                    identity,
                    byteLength,
                    digest,
                    reasonCode,
                    slot,
                    metadata.Identity,
                    metadata.ByteLength,
                    Digest(bytes));
            }
            catch (IOException)
            {
                // Fixed-name slot is already occupied. Never inspect/delete it as a capacity shortcut.
            }
            catch (UnauthorizedAccessException)
            {
                throw new PhotonCadArtifactException("cleanup_journal_unavailable");
            }
        }
        throw new PhotonCadArtifactException("cleanup_journal_capacity");
    }

    internal async ValueTask<bool> RetireAsync(
        PhotonCadArtifactCleanupIntent intent,
        CancellationToken cancellationToken = default)
    {
        var result = await PhotonCadWindowsFilePolicy.DeleteIfExactBindingAsync(
            intent.JournalPath,
            _root,
            intent.JournalIdentity,
            intent.JournalByteLength,
            intent.JournalDigest,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome is PhotonCadExactDeleteOutcome.Deleted or PhotonCadExactDeleteOutcome.Missing)
        {
            PhotonCadWindowsFilePolicy.FlushDirectory(_root);
            return true;
        }
        return false;
    }

    private PhotonCadArtifactCleanupIntent Parse(
        byte[] bytes,
        string slot,
        PhotonCadWindowsFileMetadata journalMetadata)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new PhotonCadArtifactException("cleanup_journal_invalid");
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        string[] expected =
        [
            "version", "kind", "artifactHandle", "partialName", "sealedName", "volumeSerialNumber",
            "fileIndex", "byteLength", "contentDigest", "reasonCode",
        ];
        if (!names.SequenceEqual(expected, StringComparer.Ordinal))
            throw new PhotonCadArtifactException("cleanup_journal_invalid");
        if (root.GetProperty("version").GetInt32() != RecordVersion ||
            root.GetProperty("kind").GetString() != "broker-sealed-artifact")
            throw new PhotonCadArtifactException("cleanup_journal_invalid");
        var handle = new PhotonCadArtifactHandle(RequiredString(root, "artifactHandle", 128));
        var token = handle.Value["cad-artifact:".Length..];
        var partialName = RequiredString(root, "partialName", 80);
        var sealedName = RequiredString(root, "sealedName", 80);
        if (partialName != $".{token}.partial" || sealedName != $"{token}.step")
            throw new PhotonCadArtifactException("cleanup_journal_invalid");
        var identity = new PhotonCadWindowsFileIdentity(
            root.GetProperty("volumeSerialNumber").GetUInt32(),
            ulong.Parse(RequiredString(root, "fileIndex", 20), NumberStyles.None, CultureInfo.InvariantCulture));
        var byteLength = PhotonCadArtifactGuards.ByteLength(
            root.GetProperty("byteLength").GetInt64(),
            PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes,
            "cleanup_byte_length");
        var digest = PhotonCadArtifactGuards.Digest(RequiredString(root, "contentDigest", 71), "cleanup_digest");
        var reason = PhotonCadArtifactGuards.Reason(RequiredString(root, "reasonCode", 96), "cleanup_reason");
        var canonical = Serialize(handle.Value, partialName, sealedName, identity, byteLength, digest, reason);
        if (!CryptographicOperations.FixedTimeEquals(bytes, canonical))
            throw new PhotonCadArtifactException("cleanup_journal_noncanonical");
        return new PhotonCadArtifactCleanupIntent(
            handle.Value,
            Path.Combine(_sealedRoot, partialName),
            Path.Combine(_sealedRoot, sealedName),
            identity,
            byteLength,
            digest,
            reason,
            slot,
            journalMetadata.Identity,
            journalMetadata.ByteLength,
            Digest(bytes));
    }

    private static byte[] Serialize(
        string handle,
        string partialName,
        string sealedName,
        PhotonCadWindowsFileIdentity identity,
        long byteLength,
        string digest,
        string reason)
    {
        using var output = new MemoryStream(MaximumRecordBytes);
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", RecordVersion);
            writer.WriteString("kind", "broker-sealed-artifact");
            writer.WriteString("artifactHandle", handle);
            writer.WriteString("partialName", partialName);
            writer.WriteString("sealedName", sealedName);
            writer.WriteNumber("volumeSerialNumber", identity.VolumeSerialNumber);
            writer.WriteString("fileIndex", identity.FileIndex.ToString(CultureInfo.InvariantCulture));
            writer.WriteNumber("byteLength", byteLength);
            writer.WriteString("contentDigest", digest);
            writer.WriteString("reasonCode", reason);
            writer.WriteEndObject();
        }
        var bytes = output.ToArray();
        if (bytes.Length > MaximumRecordBytes) throw new PhotonCadArtifactException("cleanup_journal_invalid");
        return bytes;
    }

    private static string RequiredString(JsonElement root, string name, int maximumLength)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length is < 1 || text.Length > maximumLength || text.Contains('\0'))
            throw new PhotonCadArtifactException("cleanup_journal_invalid");
        return text;
    }

    private string SlotPath(int index) => Path.Combine(_root, $"cleanup-{index:D4}.intent");

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
}
