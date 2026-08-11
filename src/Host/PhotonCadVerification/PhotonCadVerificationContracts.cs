namespace PhotonCadVerification;

public static class PhotonCadVerificationContract
{
    public const int Version = 1;
    public const int MaximumCanonicalBytes = 128 * 1024 * 1024;
    public const int MaximumChecks = 32;
}

public enum PhotonCadVerificationStatus
{
    Passed,
    Failed,
    Unavailable,
}

public sealed class PhotonCadVerificationCheck
{
    public PhotonCadVerificationCheck(string id, PhotonCadVerificationStatus status, string reason, long subjectCount)
    {
        Id = VerificationGuards.Code(id, nameof(id));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        Reason = VerificationGuards.Code(reason, nameof(reason));
        if (subjectCount < 0) throw new ArgumentOutOfRangeException(nameof(subjectCount));
        SubjectCount = subjectCount;
    }

    public string Id { get; }
    public PhotonCadVerificationStatus Status { get; }
    public string Reason { get; }
    public long SubjectCount { get; }
}

public sealed class PhotonCadVerificationRequest
{
    public PhotonCadVerificationRequest(
        long expectedByteLength,
        string expectedFileSha256,
        string expectedContentDigest,
        string expectedBomDigest,
        bool requireClean = true)
    {
        if (expectedByteLength is < 1 or > PhotonCadVerificationContract.MaximumCanonicalBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedByteLength));
        ExpectedByteLength = expectedByteLength;
        ExpectedFileSha256 = VerificationGuards.Digest(expectedFileSha256, nameof(expectedFileSha256));
        ExpectedContentDigest = VerificationGuards.Digest(expectedContentDigest, nameof(expectedContentDigest));
        ExpectedBomDigest = VerificationGuards.Digest(expectedBomDigest, nameof(expectedBomDigest));
        RequireClean = requireClean;
    }

    public int ContractVersion => PhotonCadVerificationContract.Version;
    public long ExpectedByteLength { get; }
    public string ExpectedFileSha256 { get; }
    public string ExpectedContentDigest { get; }
    public string ExpectedBomDigest { get; }
    public bool RequireClean { get; }
}

public sealed class PhotonCadVerificationReport
{
    public PhotonCadVerificationReport(
        bool verified,
        bool complete,
        string reason,
        string? projectId,
        long? revision,
        IReadOnlyList<PhotonCadVerificationCheck> checks)
    {
        if (checks is null || checks.Count is < 1 or > PhotonCadVerificationContract.MaximumChecks)
            throw new ArgumentException("verification_checks_invalid", nameof(checks));
        if (checks.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != checks.Count)
            throw new ArgumentException("verification_checks_duplicate", nameof(checks));
        var expectedVerified = checks.All(value => value.Status != PhotonCadVerificationStatus.Failed);
        var expectedComplete = checks.All(value => value.Status == PhotonCadVerificationStatus.Passed);
        if (verified != expectedVerified || complete != expectedComplete)
            throw new ArgumentException("verification_report_status_contradiction", nameof(verified));
        if ((projectId is null) != (revision is null) || revision < 0)
            throw new ArgumentException("verification_project_binding_invalid", nameof(projectId));
        Verified = verified;
        Complete = complete;
        Reason = VerificationGuards.Code(reason, nameof(reason));
        ProjectId = projectId is null ? null : VerificationGuards.Identifier(projectId, nameof(projectId));
        Revision = revision;
        Checks = Array.AsReadOnly(checks.ToArray());
    }

    public int ContractVersion => PhotonCadVerificationContract.Version;
    public bool Verified { get; }
    public bool Complete { get; }
    public string Reason { get; }
    public string? ProjectId { get; }
    public long? Revision { get; }
    public IReadOnlyList<PhotonCadVerificationCheck> Checks { get; }
}

internal static class VerificationGuards
{
    internal static string Digest(string? value, string field)
    {
        var safe = value?.ToLowerInvariant();
        if (safe?.StartsWith("sha256:", StringComparison.Ordinal) == true) safe = safe[7..];
        if (safe is null || safe.Length != 64 || safe.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("verification_digest_invalid", field);
        return $"sha256:{safe}";
    }

    internal static string Code(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
            throw new ArgumentException("verification_code_invalid", field);
        return value;
    }

    internal static string Identifier(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not ':'))
            throw new ArgumentException("verification_identifier_invalid", field);
        return value;
    }
}
