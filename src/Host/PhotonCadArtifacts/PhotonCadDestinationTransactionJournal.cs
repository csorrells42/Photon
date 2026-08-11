using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotonCadArtifacts;

internal interface IPhotonCadDestinationJournalProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

internal sealed class PhotonCadCurrentUserDpapiProtector : IPhotonCadDestinationJournalProtector
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PhotonCadArtifacts.destination-journal.v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PhotonCadArtifactException("windows_host_required");
        if (input.IsEmpty || input.Length > PhotonCadDestinationTransactionJournal.MaximumPlaintextBytes)
            throw new PhotonCadArtifactException("destination_journal_invalid");
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var entropyPointer = Marshal.AllocHGlobal(Entropy.Length);
        var inputBytes = input.ToArray();
        try
        {
            Marshal.Copy(inputBytes, 0, inputPointer, input.Length);
            Marshal.Copy(Entropy, 0, entropyPointer, Entropy.Length);
            var inputBlob = new DataBlob(input.Length, inputPointer);
            var entropyBlob = new DataBlob(Entropy.Length, entropyPointer);
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "Photon CAD destination transaction",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
                throw new PhotonCadArtifactException("destination_journal_protection_failed");
            try
            {
                if (outputBlob.Data == IntPtr.Zero ||
                    outputBlob.Length is <= 0 or > PhotonCadDestinationTransactionJournal.MaximumCiphertextBytes)
                    throw new PhotonCadArtifactException("destination_journal_protection_failed");
                var output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero) _ = LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            Marshal.FreeHGlobal(inputPointer);
            Marshal.FreeHGlobal(entropyPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob
    {
        internal DataBlob(int length, IntPtr data)
        {
            Length = length;
            Data = data;
        }

        internal readonly int Length;
        internal readonly IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed record PhotonCadDestinationTransaction(
    long CreatedUnixMilliseconds,
    long ExpiresUnixMilliseconds,
    PhotonCadDestinationHandle DestinationHandle,
    PhotonCadArtifactHandle ArtifactHandle,
    PhotonCadArtifactContext Context,
    string DisplayLabel,
    string TargetPath,
    string ParentPath,
    PhotonCadWindowsFileIdentity ParentIdentity,
    string TemporaryPath,
    PhotonCadWindowsFileIdentity OutputIdentity,
    long ByteLength,
    string ContentDigest,
    string JournalPath,
    PhotonCadWindowsFileIdentity JournalIdentity,
    long JournalByteLength,
    string JournalDigest);

internal sealed class PhotonCadDestinationTransactionJournal
{
    internal const int MaximumPlaintextBytes = 16_384;
    internal const int MaximumCiphertextBytes = 32_768;
    private const int HeaderBytes = 12;
    private const int MaximumRecordBytes = HeaderBytes + MaximumCiphertextBytes;
    private const int RecordVersion = 1;
    private static ReadOnlySpan<byte> Magic => "PCDJNL1\0"u8;
    private readonly string _root;
    private readonly int _maximumEntries;
    private readonly IPhotonCadDestinationJournalProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly List<string> _blockedReasons = [];

    internal PhotonCadDestinationTransactionJournal(
        string root,
        int maximumEntries = 32,
        IPhotonCadDestinationJournalProtector? protector = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(root) || maximumEntries is < 1 or > 128)
            throw new PhotonCadArtifactException("invalid_destination_journal_policy");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _maximumEntries = maximumEntries;
        _protector = protector ?? new PhotonCadCurrentUserDpapiProtector();
        _timeProvider = timeProvider ?? TimeProvider.System;
        PhotonCadWindowsFilePolicy.EnsureOwnedDirectory(_root);
    }

    internal IReadOnlyList<string> BlockedReasons => _blockedReasons;

    internal IReadOnlyList<PhotonCadDestinationTransaction> Load()
    {
        var transactions = new List<PhotonCadDestinationTransaction>();
        _blockedReasons.Clear();
        for (var index = 0; index < _maximumEntries; index++)
        {
            var path = SlotPath(index);
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    MaximumRecordBytes,
                    FileOptions.SequentialScan);
                var metadata = PhotonCadWindowsFilePolicy.InspectRegularFileMetadata(stream.SafeFileHandle);
                if (metadata.ByteLength is < HeaderBytes or > MaximumRecordBytes)
                    throw new PhotonCadArtifactException("destination_journal_invalid");
                var framed = new byte[checked((int)metadata.ByteLength)];
                stream.ReadExactly(framed);
                transactions.Add(ParseFramed(framed, path, metadata));
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or
                UnauthorizedAccessException or JsonException or DecoderFallbackException or
                PhotonCadArtifactException or FormatException or OverflowException or CryptographicException)
            {
                _blockedReasons.Add("destination_journal_invalid");
            }
        }
        return transactions;
    }

    internal PhotonCadDestinationTransaction Reserve(
        PhotonCadDestinationDescriptor destination,
        string targetPath,
        string parentPath,
        PhotonCadWindowsFileIdentity parentIdentity,
        string temporaryPath,
        PhotonCadWindowsFileIdentity outputIdentity,
        long byteLength,
        string contentDigest)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var created = _timeProvider.GetUtcNow().ToUniversalTime();
        var expires = created + TimeSpan.FromHours(24);
        var plain = Serialize(
            created.ToUnixTimeMilliseconds(),
            expires.ToUnixTimeMilliseconds(),
            destination.DestinationHandle.Value,
            destination.ArtifactHandle.Value,
            destination.Context,
            destination.DisplayLabel,
            targetPath,
            parentPath,
            parentIdentity,
            Path.GetFileName(temporaryPath),
            outputIdentity,
            byteLength,
            contentDigest);
        byte[] protectedRecord;
        try
        {
            protectedRecord = _protector.Protect(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        var framed = Frame(protectedRecord);

        for (var index = 0; index < _maximumEntries; index++)
        {
            var path = SlotPath(index);
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    MaximumRecordBytes,
                    FileOptions.WriteThrough);
                stream.Write(framed);
                stream.Flush(flushToDisk: true);
                var metadata = PhotonCadWindowsFilePolicy.InspectRegularFileMetadata(stream.SafeFileHandle);
                PhotonCadWindowsFilePolicy.FlushDirectory(_root);
                return new PhotonCadDestinationTransaction(
                    created.ToUnixTimeMilliseconds(),
                    expires.ToUnixTimeMilliseconds(),
                    destination.DestinationHandle,
                    destination.ArtifactHandle,
                    destination.Context,
                    destination.DisplayLabel,
                    targetPath,
                    parentPath,
                    parentIdentity,
                    temporaryPath,
                    outputIdentity,
                    byteLength,
                    PhotonCadArtifactGuards.Digest(contentDigest, nameof(contentDigest)),
                    path,
                    metadata.Identity,
                    metadata.ByteLength,
                    Digest(framed));
            }
            catch (IOException)
            {
                // Fixed slot is occupied. Never replace or delete an unvalidated record for capacity.
            }
            catch (UnauthorizedAccessException)
            {
                throw new PhotonCadArtifactException("destination_journal_unavailable");
            }
        }
        throw new PhotonCadArtifactException("destination_journal_capacity");
    }

    internal async ValueTask<bool> RetireAsync(
        PhotonCadDestinationTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var result = await PhotonCadWindowsFilePolicy.DeleteIfExactBindingAsync(
            transaction.JournalPath,
            _root,
            transaction.JournalIdentity,
            transaction.JournalByteLength,
            transaction.JournalDigest,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome is not (PhotonCadExactDeleteOutcome.Deleted or PhotonCadExactDeleteOutcome.Missing))
            return false;
        PhotonCadWindowsFilePolicy.FlushDirectory(_root);
        return true;
    }

    internal bool IsExpired(PhotonCadDestinationTransaction transaction)
    {
        var created = DateTimeOffset.FromUnixTimeMilliseconds(transaction.CreatedUnixMilliseconds);
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(transaction.ExpiresUnixMilliseconds);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return expires <= created || expires - created > TimeSpan.FromHours(24) || now < created || now >= expires;
    }

    private PhotonCadDestinationTransaction ParseFramed(
        byte[] framed,
        string path,
        PhotonCadWindowsFileMetadata journalMetadata)
    {
        if (!framed.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new PhotonCadArtifactException("destination_journal_invalid");
        var cipherLength = BinaryPrimitives.ReadInt32BigEndian(framed.AsSpan(Magic.Length, 4));
        if (cipherLength is <= 0 or > MaximumCiphertextBytes || HeaderBytes + cipherLength != framed.Length)
            throw new PhotonCadArtifactException("destination_journal_invalid");
        var plain = _protector.Unprotect(framed.AsSpan(HeaderBytes, cipherLength));
        try
        {
            return ParsePlaintext(plain, path, journalMetadata, framed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private PhotonCadDestinationTransaction ParsePlaintext(
        byte[] plain,
        string journalPath,
        PhotonCadWindowsFileMetadata journalMetadata,
        byte[] framed)
    {
        if (plain.Length is <= 0 or > MaximumPlaintextBytes)
            throw new PhotonCadArtifactException("destination_journal_invalid");
        using var document = JsonDocument.Parse(plain, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 5,
        });
        var root = document.RootElement;
        string[] expected =
        [
            "version", "kind", "action", "createdUnixMilliseconds", "expiresUnixMilliseconds",
            "destinationHandle", "artifactHandle", "rendererSessionId", "controllerId", "sessionId",
            "projectId", "revision", "displayLabel", "targetPath", "parentPath", "parentVolumeSerialNumber",
            "parentFileIndex", "temporaryName", "outputVolumeSerialNumber", "outputFileIndex", "byteLength",
            "contentDigest",
        ];
        if (root.ValueKind != JsonValueKind.Object ||
            !root.EnumerateObject().Select(property => property.Name).SequenceEqual(expected, StringComparer.Ordinal) ||
            root.GetProperty("version").GetInt32() != RecordVersion ||
            Required(root, "kind", 64) != "customer-step-output" ||
            Required(root, "action", 64) != "create-only")
            throw new PhotonCadArtifactException("destination_journal_invalid");
        var created = root.GetProperty("createdUnixMilliseconds").GetInt64();
        var expires = root.GetProperty("expiresUnixMilliseconds").GetInt64();
        var destination = new PhotonCadDestinationHandle(Required(root, "destinationHandle", 128));
        var artifact = new PhotonCadArtifactHandle(Required(root, "artifactHandle", 128));
        var context = new PhotonCadArtifactContext(
            Required(root, "rendererSessionId", 128),
            Required(root, "controllerId", 128),
            Required(root, "sessionId", 128),
            Required(root, "projectId", 128),
            root.GetProperty("revision").GetInt64());
        var label = PhotonCadArtifactGuards.DisplayName(Required(root, "displayLabel", 128));
        var parent = Required(root, "parentPath", 32_767);
        var target = PhotonCadWindowsFilePolicy.RequireExactDestination(
            Required(root, "targetPath", 32_767),
            parent);
        var temporaryName = Required(root, "temporaryName", 192);
        if (!temporaryName.StartsWith(".", StringComparison.Ordinal) || !temporaryName.EndsWith(".tmp", StringComparison.Ordinal) ||
            Path.GetFileName(temporaryName) != temporaryName)
            throw new PhotonCadArtifactException("destination_journal_invalid");
        var temporary = Path.Combine(parent, temporaryName);
        var parentIdentity = new PhotonCadWindowsFileIdentity(
            root.GetProperty("parentVolumeSerialNumber").GetUInt32(),
            ulong.Parse(Required(root, "parentFileIndex", 20), NumberStyles.None, CultureInfo.InvariantCulture));
        var outputIdentity = new PhotonCadWindowsFileIdentity(
            root.GetProperty("outputVolumeSerialNumber").GetUInt32(),
            ulong.Parse(Required(root, "outputFileIndex", 20), NumberStyles.None, CultureInfo.InvariantCulture));
        var byteLength = PhotonCadArtifactGuards.ByteLength(
            root.GetProperty("byteLength").GetInt64(),
            PhotonCadArtifactContract.AbsoluteMaximumArtifactBytes,
            "journal_byte_length");
        var digest = PhotonCadArtifactGuards.Digest(Required(root, "contentDigest", 71), "journal_digest");
        var canonical = Serialize(
            created,
            expires,
            destination.Value,
            artifact.Value,
            context,
            label,
            target,
            parent,
            parentIdentity,
            temporaryName,
            outputIdentity,
            byteLength,
            digest);
        if (!CryptographicOperations.FixedTimeEquals(plain, canonical))
            throw new PhotonCadArtifactException("destination_journal_noncanonical");
        return new PhotonCadDestinationTransaction(
            created,
            expires,
            destination,
            artifact,
            context,
            label,
            target,
            parent,
            parentIdentity,
            temporary,
            outputIdentity,
            byteLength,
            digest,
            journalPath,
            journalMetadata.Identity,
            journalMetadata.ByteLength,
            Digest(framed));
    }

    private static byte[] Serialize(
        long created,
        long expires,
        string destinationHandle,
        string artifactHandle,
        PhotonCadArtifactContext context,
        string displayLabel,
        string targetPath,
        string parentPath,
        PhotonCadWindowsFileIdentity parentIdentity,
        string temporaryName,
        PhotonCadWindowsFileIdentity outputIdentity,
        long byteLength,
        string contentDigest)
    {
        using var output = new MemoryStream(MaximumPlaintextBytes);
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", RecordVersion);
            writer.WriteString("kind", "customer-step-output");
            writer.WriteString("action", "create-only");
            writer.WriteNumber("createdUnixMilliseconds", created);
            writer.WriteNumber("expiresUnixMilliseconds", expires);
            writer.WriteString("destinationHandle", destinationHandle);
            writer.WriteString("artifactHandle", artifactHandle);
            writer.WriteString("rendererSessionId", context.RendererSessionId);
            writer.WriteString("controllerId", context.ControllerId);
            writer.WriteString("sessionId", context.CadSessionId);
            writer.WriteString("projectId", context.ProjectId);
            writer.WriteNumber("revision", context.Revision);
            writer.WriteString("displayLabel", displayLabel);
            writer.WriteString("targetPath", targetPath);
            writer.WriteString("parentPath", parentPath);
            writer.WriteNumber("parentVolumeSerialNumber", parentIdentity.VolumeSerialNumber);
            writer.WriteString("parentFileIndex", parentIdentity.FileIndex.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("temporaryName", temporaryName);
            writer.WriteNumber("outputVolumeSerialNumber", outputIdentity.VolumeSerialNumber);
            writer.WriteString("outputFileIndex", outputIdentity.FileIndex.ToString(CultureInfo.InvariantCulture));
            writer.WriteNumber("byteLength", byteLength);
            writer.WriteString("contentDigest", contentDigest);
            writer.WriteEndObject();
        }
        var bytes = output.ToArray();
        if (bytes.Length > MaximumPlaintextBytes) throw new PhotonCadArtifactException("destination_journal_invalid");
        return bytes;
    }

    private static byte[] Frame(byte[] ciphertext)
    {
        if (ciphertext.Length is <= 0 or > MaximumCiphertextBytes)
            throw new PhotonCadArtifactException("destination_journal_invalid");
        var framed = new byte[checked(HeaderBytes + ciphertext.Length)];
        Magic.CopyTo(framed);
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(Magic.Length, 4), ciphertext.Length);
        ciphertext.CopyTo(framed, HeaderBytes);
        CryptographicOperations.ZeroMemory(ciphertext);
        return framed;
    }

    private static string Required(JsonElement root, string name, int maximumLength)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } value ||
            value.Length is < 1 || value.Length > maximumLength || value.Contains('\0'))
            throw new PhotonCadArtifactException("destination_journal_invalid");
        return value;
    }

    private string SlotPath(int index) => Path.Combine(_root, $"destination-{index:D3}.txn");

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
}
