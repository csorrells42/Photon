using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotonCadProjects;

public static class PhotonCadProjectContract
{
    public const int Version = 1;
    public const int MaximumOpenProjects = 32;
    public const int MaximumKnownWorkspaces = 128;
    public const int MaximumReopenEntries = 20;
    public const int MaximumRequestHistory = 50_000;
    public const int MaximumRecoveryCandidates = 128;
    public const int MaximumOverwriteGrants = 128;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumDisplayNameLength = 256;
    public const int MaximumPickerLabelLength = 512;
    public const int MaximumBomRows = 50_000;
    public const int MaximumOpaqueTokenLength = 256;
    public const int MaximumCanonicalProjectBytes = 512 * 1024 * 1024;
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
}

public class PhotonCadProjectException : Exception
{
    public PhotonCadProjectException(string code, string field)
        : base($"Photon CAD project field '{ProjectGuards.Field(field)}' failed closed ({SafeCode(code)}).")
    {
        Code = SafeCode(code);
        Field = ProjectGuards.Field(field);
    }

    public PhotonCadProjectException(string code, string field, Exception innerException)
        : base($"Photon CAD project field '{ProjectGuards.Field(field)}' failed closed ({SafeCode(code)}).", innerException)
    {
        Code = SafeCode(code);
        Field = ProjectGuards.Field(field);
    }

    public string Code { get; }
    public string Field { get; }

    private static string SafeCode(string? code)
    {
        if (code is null || code.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(code[0])) return "invalid_error_code";
        return code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
            ? code
            : "invalid_error_code";
    }
}

public abstract class PhotonCadOpaqueHandle
{
    protected PhotonCadOpaqueHandle(string value, string prefix)
    {
        Value = ProjectGuards.OpaqueHandle(value, nameof(value), prefix);
    }

    public string Value { get; }
    public sealed override string ToString() => Value;
    public sealed override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public sealed override bool Equals(object? obj) => obj is PhotonCadOpaqueHandle other
        && other.GetType() == GetType()
        && StringComparer.Ordinal.Equals(Value, other.Value);
}

public sealed class PhotonCadWorkspaceHandle(string value) : PhotonCadOpaqueHandle(value, "cad-workspace:");
public sealed class PhotonCadProjectHandle(string value) : PhotonCadOpaqueHandle(value, "cad-project:");
public sealed class PhotonCadReopenHandle(string value) : PhotonCadOpaqueHandle(value, "cad-reopen:");
public sealed class PhotonCadSaveReceiptHandle(string value) : PhotonCadOpaqueHandle(value, "cad-save-receipt:");
public sealed class PhotonCadStorageTargetHandle(string value) : PhotonCadOpaqueHandle(value, "cad-storage-target:");

public interface IPhotonCadHandleIssuer
{
    PhotonCadWorkspaceHandle NewWorkspace();
    PhotonCadProjectHandle NewProject();
    PhotonCadReopenHandle NewReopen();
    PhotonCadSaveReceiptHandle NewSaveReceipt();
}

public sealed class CryptographicPhotonCadHandleIssuer : IPhotonCadHandleIssuer
{
    public PhotonCadWorkspaceHandle NewWorkspace() => new(Create("cad-workspace:"));
    public PhotonCadProjectHandle NewProject() => new(Create("cad-project:"));
    public PhotonCadReopenHandle NewReopen() => new(Create("cad-reopen:"));
    public PhotonCadSaveReceiptHandle NewSaveReceipt() => new(Create("cad-save-receipt:"));

    private static string Create(string prefix)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return prefix + token;
    }
}

public enum PhotonCadProjectUnit
{
    Millimeter,
    Inch,
}

public enum PhotonCadBomUnit
{
    Each,
    Length,
}

public sealed record PhotonCadBomRow
{
    public PhotonCadBomRow(string partNumber, string description, double quantity, PhotonCadBomUnit unit, string sourceEntityId)
    {
        PartNumber = ProjectGuards.SafeText(partNumber, nameof(partNumber), PhotonCadProjectContract.MaximumDisplayNameLength, true);
        Description = ProjectGuards.SafeText(description, nameof(description), 2_048, false);
        if (!double.IsFinite(quantity) || quantity <= 0 || quantity > 1_000_000_000)
            throw new PhotonCadProjectException("invalid_quantity", nameof(quantity));
        Quantity = quantity;
        Unit = ProjectGuards.Enum(unit, nameof(unit));
        SourceEntityId = ProjectGuards.Identifier(sourceEntityId, nameof(sourceEntityId));
    }

    public string PartNumber { get; }
    public string Description { get; }
    public double Quantity { get; }
    public PhotonCadBomUnit Unit { get; }
    public string SourceEntityId { get; }
}

public sealed class PhotonCadCanonicalProject
{
    private readonly byte[] _canonicalBytes;

    public PhotonCadCanonicalProject(
        string sessionId,
        string projectId,
        long revision,
        string displayName,
        PhotonCadProjectUnit units,
        string contentDigest,
        string bomDigest,
        bool dirty,
        ReadOnlyMemory<byte> canonicalBytes,
        IEnumerable<PhotonCadBomRow>? bom = null)
    {
        SessionId = ProjectGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        Revision = ProjectGuards.Revision(revision, nameof(revision));
        DisplayName = ProjectGuards.DisplayName(displayName, nameof(displayName), PhotonCadProjectContract.MaximumDisplayNameLength);
        Units = ProjectGuards.Enum(units, nameof(units));
        ContentDigest = ProjectGuards.Digest(contentDigest, nameof(contentDigest));
        if (canonicalBytes.Length is <= 0 or > PhotonCadProjectContract.MaximumCanonicalProjectBytes)
            throw new PhotonCadProjectException("invalid_content_length", nameof(canonicalBytes));
        _canonicalBytes = canonicalBytes.ToArray();
        var rows = (bom ?? []).ToArray();
        if (rows.Length > PhotonCadProjectContract.MaximumBomRows || rows.Any(row => row is null))
            throw new PhotonCadProjectException("invalid_bom_size", nameof(bom));
        var rowKeys = new HashSet<string>(StringComparer.Ordinal);
        if (rows.Any(row => !rowKeys.Add($"{row.SourceEntityId}\0{row.PartNumber}")))
            throw new PhotonCadProjectException("duplicate_bom_row", nameof(bom));
        Bom = Array.AsReadOnly(rows);
        var suppliedBomDigest = ProjectGuards.Digest(bomDigest, nameof(bomDigest));
        var computedBomDigest = PhotonCadBomCanonicalizer.Compute(Units, Bom);
        if (!ProjectGuards.FixedDigestEquals(suppliedBomDigest, computedBomDigest))
            throw new PhotonCadProjectException("bom_digest_mismatch", nameof(bomDigest));
        BomDigest = computedBomDigest;
        Dirty = dirty;
    }

    public string SessionId { get; }
    public string ProjectId { get; }
    public long Revision { get; }
    public string DisplayName { get; }
    public PhotonCadProjectUnit Units { get; }
    public string ContentDigest { get; }
    public string BomDigest { get; }
    public bool Dirty { get; }
    /// <summary>
    /// Returns an isolated copy. <see cref="ReadOnlyMemory{T}"/> backed directly by the internal
    /// array is not immutable because host code can recover that array through MemoryMarshal.
    /// </summary>
    public ReadOnlyMemory<byte> CanonicalBytes => _canonicalBytes.ToArray();
    public IReadOnlyList<PhotonCadBomRow> Bom { get; }
}

public interface IPhotonCadProjectCodec
{
    ValueTask<PhotonCadCanonicalProject> CreateAsync(
        string title,
        PhotonCadProjectUnit units,
        CancellationToken cancellationToken = default);

    PhotonCadCanonicalProject Decode(ReadOnlyMemory<byte> canonicalBytes);

    PhotonCadCanonicalProject MarkSaved(PhotonCadCanonicalProject current);

    string ComputeLogicalContentDigest(ReadOnlyMemory<byte> canonicalBytes);
}

public interface IPhotonCadProjectCodecPolicy
{
    int MaximumEncodedBytes { get; }
}

public interface IPhotonCadClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemPhotonCadClock : IPhotonCadClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class PhotonCadWorkspaceBinding
{
    public PhotonCadWorkspaceBinding(PhotonCadStorageTargetHandle target, string label)
    {
        Target = target ?? throw new PhotonCadProjectException("required", nameof(target));
        Label = ProjectGuards.DisplayName(label, nameof(label), PhotonCadProjectContract.MaximumPickerLabelLength);
    }

    public PhotonCadStorageTargetHandle Target { get; }
    public string Label { get; }
}

public sealed record PhotonCadWorkspaceRegistration
{
    public PhotonCadWorkspaceRegistration(PhotonCadWorkspaceHandle workspaceHandle, PhotonCadWorkspaceBinding binding)
    {
        WorkspaceHandle = workspaceHandle ?? throw new PhotonCadProjectException("required", nameof(workspaceHandle));
        Binding = binding ?? throw new PhotonCadProjectException("required", nameof(binding));
    }

    public PhotonCadWorkspaceHandle WorkspaceHandle { get; }
    public PhotonCadWorkspaceBinding Binding { get; }
}

public sealed record PhotonCadProjectCreateRequest
{
    public PhotonCadProjectCreateRequest(string requestId, PhotonCadWorkspaceHandle workspaceHandle, string title, PhotonCadProjectUnit units)
    {
        RequestId = ProjectGuards.Identifier(requestId, nameof(requestId));
        WorkspaceHandle = workspaceHandle ?? throw new PhotonCadProjectException("required", nameof(workspaceHandle));
        Title = ProjectGuards.DisplayName(title, nameof(title), PhotonCadProjectContract.MaximumDisplayNameLength);
        Units = ProjectGuards.Enum(units, nameof(units));
    }

    public int ContractVersion => PhotonCadProjectContract.Version;
    public string RequestId { get; }
    public PhotonCadWorkspaceHandle WorkspaceHandle { get; }
    public string Title { get; }
    public PhotonCadProjectUnit Units { get; }
}

public sealed record PhotonCadProjectOpenRequest
{
    public PhotonCadProjectOpenRequest(string requestId, PhotonCadWorkspaceHandle workspaceHandle)
    {
        RequestId = ProjectGuards.Identifier(requestId, nameof(requestId));
        WorkspaceHandle = workspaceHandle ?? throw new PhotonCadProjectException("required", nameof(workspaceHandle));
    }

    public int ContractVersion => PhotonCadProjectContract.Version;
    public string RequestId { get; }
    public PhotonCadWorkspaceHandle WorkspaceHandle { get; }
}

public sealed record PhotonCadProjectReopenRequest
{
    public PhotonCadProjectReopenRequest(string requestId, PhotonCadReopenHandle reopenHandle)
    {
        RequestId = ProjectGuards.Identifier(requestId, nameof(requestId));
        ReopenHandle = reopenHandle ?? throw new PhotonCadProjectException("required", nameof(reopenHandle));
    }

    public int ContractVersion => PhotonCadProjectContract.Version;
    public string RequestId { get; }
    public PhotonCadReopenHandle ReopenHandle { get; }
}

public sealed record PhotonCadProjectRefreshRequest
{
    public PhotonCadProjectRefreshRequest(string requestId, PhotonCadProjectHandle projectHandle, string sessionId, string projectId, long knownRevision)
    {
        RequestId = ProjectGuards.Identifier(requestId, nameof(requestId));
        ProjectHandle = projectHandle ?? throw new PhotonCadProjectException("required", nameof(projectHandle));
        SessionId = ProjectGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        KnownRevision = ProjectGuards.Revision(knownRevision, nameof(knownRevision));
    }

    public int ContractVersion => PhotonCadProjectContract.Version;
    public string RequestId { get; }
    public PhotonCadProjectHandle ProjectHandle { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long KnownRevision { get; }
}

public sealed record PhotonCadProjectSaveRequest
{
    public PhotonCadProjectSaveRequest(string requestId, PhotonCadProjectHandle projectHandle, string sessionId, string projectId, long baseRevision, string contentDigest)
    {
        RequestId = ProjectGuards.Identifier(requestId, nameof(requestId));
        ProjectHandle = projectHandle ?? throw new PhotonCadProjectException("required", nameof(projectHandle));
        SessionId = ProjectGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        BaseRevision = ProjectGuards.Revision(baseRevision, nameof(baseRevision));
        ContentDigest = ProjectGuards.Digest(contentDigest, nameof(contentDigest));
    }

    public int ContractVersion => PhotonCadProjectContract.Version;
    public string RequestId { get; }
    public PhotonCadProjectHandle ProjectHandle { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long BaseRevision { get; }
    public string ContentDigest { get; }
}

public sealed record PhotonCadProjectSaveAsRequest
{
    public PhotonCadProjectSaveAsRequest(string requestId, PhotonCadProjectHandle sourceProjectHandle, PhotonCadWorkspaceHandle destinationWorkspaceHandle, string sessionId, string projectId, long baseRevision, string contentDigest)
    {
        RequestId = ProjectGuards.Identifier(requestId, nameof(requestId));
        SourceProjectHandle = sourceProjectHandle ?? throw new PhotonCadProjectException("required", nameof(sourceProjectHandle));
        DestinationWorkspaceHandle = destinationWorkspaceHandle ?? throw new PhotonCadProjectException("required", nameof(destinationWorkspaceHandle));
        SessionId = ProjectGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        BaseRevision = ProjectGuards.Revision(baseRevision, nameof(baseRevision));
        ContentDigest = ProjectGuards.Digest(contentDigest, nameof(contentDigest));
    }

    public int ContractVersion => PhotonCadProjectContract.Version;
    public string RequestId { get; }
    public PhotonCadProjectHandle SourceProjectHandle { get; }
    public PhotonCadWorkspaceHandle DestinationWorkspaceHandle { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long BaseRevision { get; }
    public string ContentDigest { get; }
}

public sealed record PhotonCadProjectCloseRequest
{
    public PhotonCadProjectCloseRequest(string requestId, PhotonCadProjectHandle projectHandle, string sessionId, string projectId, long revision, long lastSavedRevision, string contentDigest, string lastSavedContentDigest, bool discardUnsavedChanges)
    {
        RequestId = ProjectGuards.Identifier(requestId, nameof(requestId));
        ProjectHandle = projectHandle ?? throw new PhotonCadProjectException("required", nameof(projectHandle));
        SessionId = ProjectGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        Revision = ProjectGuards.Revision(revision, nameof(revision));
        LastSavedRevision = ProjectGuards.Revision(lastSavedRevision, nameof(lastSavedRevision));
        if (LastSavedRevision > Revision)
            throw new PhotonCadProjectException("last_saved_revision_ahead", nameof(lastSavedRevision));
        ContentDigest = ProjectGuards.Digest(contentDigest, nameof(contentDigest));
        LastSavedContentDigest = ProjectGuards.Digest(lastSavedContentDigest, nameof(lastSavedContentDigest));
        DiscardUnsavedChanges = discardUnsavedChanges;
    }

    public int ContractVersion => PhotonCadProjectContract.Version;
    public string RequestId { get; }
    public PhotonCadProjectHandle ProjectHandle { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long Revision { get; }
    public long LastSavedRevision { get; }
    public string ContentDigest { get; }
    public string LastSavedContentDigest { get; }
    public bool DiscardUnsavedChanges { get; }
}

public sealed record PhotonCadProjectDocument
{
    public PhotonCadProjectDocument(PhotonCadWorkspaceHandle workspaceHandle, PhotonCadProjectHandle projectHandle, PhotonCadCanonicalProject snapshot, string lastSavedContentDigest, long lastSavedRevision, DateTimeOffset openedAtUtc)
    {
        WorkspaceHandle = workspaceHandle ?? throw new PhotonCadProjectException("required", nameof(workspaceHandle));
        ProjectHandle = projectHandle ?? throw new PhotonCadProjectException("required", nameof(projectHandle));
        Snapshot = snapshot ?? throw new PhotonCadProjectException("required", nameof(snapshot));
        LastSavedContentDigest = ProjectGuards.Digest(lastSavedContentDigest, nameof(lastSavedContentDigest));
        LastSavedRevision = ProjectGuards.Revision(lastSavedRevision, nameof(lastSavedRevision));
        if (LastSavedRevision > Snapshot.Revision) throw new PhotonCadProjectException("last_saved_revision_ahead", nameof(lastSavedRevision));
        if (!Snapshot.Dirty && (LastSavedRevision != Snapshot.Revision || !ProjectGuards.FixedDigestEquals(LastSavedContentDigest, Snapshot.ContentDigest)))
            throw new PhotonCadProjectException("clean_project_save_binding_mismatch", nameof(snapshot));
        OpenedAtUtc = ProjectGuards.Utc(openedAtUtc, nameof(openedAtUtc));
    }

    public int ContractVersion => PhotonCadProjectContract.Version;
    public PhotonCadWorkspaceHandle WorkspaceHandle { get; }
    public PhotonCadProjectHandle ProjectHandle { get; }
    public PhotonCadCanonicalProject Snapshot { get; }
    public string LastSavedContentDigest { get; }
    public long LastSavedRevision { get; }
    public DateTimeOffset OpenedAtUtc { get; }
    public string DisplayName => Snapshot.DisplayName;
    public string ContentDigest => Snapshot.ContentDigest;
    public string BomDigest => Snapshot.BomDigest;
    public IReadOnlyList<PhotonCadBomRow> Bom => Snapshot.Bom;
}

public sealed record PhotonCadProjectSaveReceipt
{
    public PhotonCadProjectSaveReceipt(PhotonCadSaveReceiptHandle receiptHandle, PhotonCadProjectHandle sourceProjectHandle, PhotonCadProjectHandle projectHandle, string sessionId, string projectId, long baseRevision, long savedRevision, string contentDigest, DateTimeOffset savedAtUtc, bool atomic, string storageDigest)
    {
        ReceiptHandle = receiptHandle ?? throw new PhotonCadProjectException("required", nameof(receiptHandle));
        SourceProjectHandle = sourceProjectHandle ?? throw new PhotonCadProjectException("required", nameof(sourceProjectHandle));
        ProjectHandle = projectHandle ?? throw new PhotonCadProjectException("required", nameof(projectHandle));
        SessionId = ProjectGuards.Identifier(sessionId, nameof(sessionId));
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        BaseRevision = ProjectGuards.Revision(baseRevision, nameof(baseRevision));
        SavedRevision = ProjectGuards.Revision(savedRevision, nameof(savedRevision));
        if (SavedRevision != BaseRevision) throw new PhotonCadProjectException("saved_revision_mismatch", nameof(savedRevision));
        ContentDigest = ProjectGuards.Digest(contentDigest, nameof(contentDigest));
        SavedAtUtc = ProjectGuards.Utc(savedAtUtc, nameof(savedAtUtc));
        if (!atomic) throw new PhotonCadProjectException("non_atomic_receipt_rejected", nameof(atomic));
        Atomic = true;
        StorageDigest = ProjectGuards.Digest(storageDigest, nameof(storageDigest));
    }

    public PhotonCadSaveReceiptHandle ReceiptHandle { get; }
    public PhotonCadProjectHandle SourceProjectHandle { get; }
    public PhotonCadProjectHandle ProjectHandle { get; }
    public string SessionId { get; }
    public string ProjectId { get; }
    public long BaseRevision { get; }
    public long SavedRevision { get; }
    public string ContentDigest { get; }
    public DateTimeOffset SavedAtUtc { get; }
    public bool Atomic { get; }
    public string StorageDigest { get; }
}

public sealed record PhotonCadProjectSaveOutcome
{
    public PhotonCadProjectSaveOutcome(PhotonCadProjectSaveReceipt receipt, PhotonCadProjectDocument document)
    {
        Receipt = receipt ?? throw new PhotonCadProjectException("required", nameof(receipt));
        Document = document ?? throw new PhotonCadProjectException("required", nameof(document));
        if (!receipt.ProjectHandle.Equals(document.ProjectHandle)
            || !StringComparer.Ordinal.Equals(receipt.SessionId, document.Snapshot.SessionId)
            || !StringComparer.Ordinal.Equals(receipt.ProjectId, document.Snapshot.ProjectId)
            || receipt.SavedRevision != document.Snapshot.Revision
            || !ProjectGuards.FixedDigestEquals(receipt.ContentDigest, document.ContentDigest))
            throw new PhotonCadProjectException("save_outcome_binding_mismatch", nameof(document));
    }

    public PhotonCadProjectSaveReceipt Receipt { get; }
    public PhotonCadProjectDocument Document { get; }
}

public sealed record PhotonCadProjectReopenMetadata
{
    public PhotonCadProjectReopenMetadata(PhotonCadReopenHandle reopenHandle, string displayName, string projectId, long lastSavedRevision, string contentDigest, DateTimeOffset closedAtUtc)
    {
        ReopenHandle = reopenHandle ?? throw new PhotonCadProjectException("required", nameof(reopenHandle));
        DisplayName = ProjectGuards.DisplayName(displayName, nameof(displayName), PhotonCadProjectContract.MaximumDisplayNameLength);
        ProjectId = ProjectGuards.Identifier(projectId, nameof(projectId));
        LastSavedRevision = ProjectGuards.Revision(lastSavedRevision, nameof(lastSavedRevision));
        ContentDigest = ProjectGuards.Digest(contentDigest, nameof(contentDigest));
        ClosedAtUtc = ProjectGuards.Utc(closedAtUtc, nameof(closedAtUtc));
    }

    public PhotonCadReopenHandle ReopenHandle { get; }
    public string DisplayName { get; }
    public string ProjectId { get; }
    public long LastSavedRevision { get; }
    public string ContentDigest { get; }
    public DateTimeOffset ClosedAtUtc { get; }
}

public sealed record PhotonCadProjectCloseOutcome
{
    public PhotonCadProjectCloseOutcome(PhotonCadProjectHandle projectHandle, PhotonCadProjectReopenMetadata reopen)
    {
        ProjectHandle = projectHandle ?? throw new PhotonCadProjectException("required", nameof(projectHandle));
        Reopen = reopen ?? throw new PhotonCadProjectException("required", nameof(reopen));
    }

    public PhotonCadProjectHandle ProjectHandle { get; }
    public PhotonCadProjectReopenMetadata Reopen { get; }
}

internal static partial class ProjectGuards
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^(?:sha256:)?[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    internal static string Field(string? value) => value is not null && value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? value : "field";

    internal static string Identifier(string? value, string field, int maximum = PhotonCadProjectContract.MaximumIdentifierLength)
    {
        var normalized = SafeText(value, field, maximum, true);
        if (!IdentifierPattern().IsMatch(normalized)) throw new PhotonCadProjectException("invalid_identifier", field);
        return normalized;
    }

    internal static string Digest(string? value, string field)
    {
        var normalized = SafeText(value, field, 71, true).ToLowerInvariant();
        if (!DigestPattern().IsMatch(normalized)) throw new PhotonCadProjectException("invalid_digest", field);
        return normalized.StartsWith("sha256:", StringComparison.Ordinal) ? normalized : $"sha256:{normalized}";
    }

    internal static string OpaqueHandle(string? value, string field, string prefix)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) throw new PhotonCadProjectException("invalid_handle", field);
        var token = value[prefix.Length..];
        if (token.Length is < 32 or > 160 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new PhotonCadProjectException("invalid_handle", field);
        return value;
    }

    internal static string DisplayName(string? value, string field, int maximum)
    {
        var normalized = SafeText(value, field, maximum, true);
        if (normalized.Contains('/') || normalized.Contains('\\') || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
            throw new PhotonCadProjectException("path_like_display_name", field);
        return normalized;
    }

    internal static string SafeText(string? value, string field, int maximum, bool required)
    {
        if (value is null) throw new PhotonCadProjectException("required", field);
        string normalized;
        try { normalized = value.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException exception) { throw new PhotonCadProjectException("invalid_unicode", field, exception); }
        if (normalized.Length > maximum || (required && string.IsNullOrWhiteSpace(normalized)))
            throw new PhotonCadProjectException(normalized.Length > maximum ? "too_long" : "required", field);
        foreach (var character in normalized)
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) || category == UnicodeCategory.Format)
                throw new PhotonCadProjectException("unsafe_character", field);
        }
        return normalized;
    }

    internal static long Revision(long value, string field)
    {
        if (value is < 0 or > PhotonCadProjectContract.MaximumSafeInteger) throw new PhotonCadProjectException("invalid_revision", field);
        return value;
    }

    internal static T Enum<T>(T value, string field) where T : struct, Enum
    {
        if (!System.Enum.IsDefined(value)) throw new PhotonCadProjectException("unsupported_enum", field);
        return value;
    }

    internal static string Sha256(ReadOnlySpan<byte> value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    internal static DateTimeOffset Utc(DateTimeOffset value, string field)
    {
        if (value != DateTimeOffset.MinValue && value != DateTimeOffset.MaxValue) return value.ToUniversalTime();
        throw new PhotonCadProjectException("invalid_timestamp", field);
    }

    internal static bool FixedDigestEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(Digest(left, nameof(left)));
        var rightBytes = Encoding.ASCII.GetBytes(Digest(right, nameof(right)));
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
