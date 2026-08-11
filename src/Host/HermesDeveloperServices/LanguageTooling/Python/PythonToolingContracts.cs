using System.Collections.ObjectModel;

namespace HermesDeveloperServices.LanguageTooling.Python;

public sealed record PythonToolingRuntimeReceipt
{
    public PythonToolingRuntimeReceipt(
        string runtimeKind,
        string immutableRuntimeId,
        string pythonVersion,
        string receiptSha256)
    {
        RuntimeKind = runtimeKind is "container-image" or "portable-payload"
            ? runtimeKind
            : throw new ArgumentException("The Python runtime kind is unsupported.", nameof(runtimeKind));
        ImmutableRuntimeId = PythonToolingContractPolicy.RequireRuntimeId(runtimeKind, immutableRuntimeId);
        PythonVersion = PythonToolingContractPolicy.RequireVersion(pythonVersion);
        ReceiptSha256 = PythonToolingContractPolicy.RequireSha256(receiptSha256, nameof(receiptSha256));
    }

    public string RuntimeKind { get; }

    public string ImmutableRuntimeId { get; }

    public string PythonVersion { get; }

    public string ReceiptSha256 { get; }
}

/// <summary>
/// Immutable input to the runtime authority. It contains only a workspace-relative display path
/// and bounded file bytes; it never conveys an executable, host path, argv, environment, URL, or
/// package/download selection.
/// </summary>
public sealed class PythonSourceSnapshot
{
    private readonly byte[] _content;

    internal PythonSourceSnapshot(string relativePath, byte[] content, string sha256)
    {
        RelativePath = LanguageToolingRequestPolicy.RequireWorkspacePath(relativePath);
        _content = content.ToArray();
        Sha256 = PythonToolingContractPolicy.RequireSha256(sha256, nameof(sha256));
    }

    public string RelativePath { get; }

    public ReadOnlyMemory<byte> Content => _content.ToArray();

    public string Sha256 { get; }
}

public sealed record PythonSyntaxDiagnostic(
    string RelativePath,
    string Severity,
    string Code,
    string Message,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public sealed record PythonSyntaxCheckOutcome(
    string ImmutableRuntimeId,
    string ReceiptSha256,
    bool Succeeded,
    IReadOnlyList<PythonSyntaxDiagnostic> Diagnostics);

/// <summary>
/// Host-only seam for a verifier/transport that revalidates an immutable application-owned
/// runtime receipt before every operation. Implementations must not use PATH, a global Python
/// installation, a mutable image tag, or renderer-provided process configuration.
/// </summary>
public interface IPythonToolingAuthority
{
    ValueTask<PythonToolingRuntimeReceipt> VerifyAsync(CancellationToken cancellationToken);

    ValueTask<PythonSyntaxCheckOutcome> CheckSyntaxAsync(
        PythonToolingRuntimeReceipt verifiedRuntime,
        IReadOnlyList<PythonSourceSnapshot> sources,
        CancellationToken cancellationToken);
}

public sealed class PythonToolingAuthorityException : Exception
{
    public PythonToolingAuthorityException(string code, string safeMessage) : base(safeMessage) =>
        Code = KnownPinnedToolchainEvidenceSource.SafeCode(code, "python-authority-unavailable");

    public string Code { get; }
}

public sealed record PythonProjectInspection(
    string ProjectKind,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> MarkerNames,
    long AggregateSourceBytes);

public sealed record PythonLanguageToolingComponents(
    ILanguageToolingEvidenceSource EvidenceSource,
    ILanguageToolingOperationHandler OperationHandler);

internal static class PythonToolingContractPolicy
{
    internal static string RequireRuntimeId(string runtimeKind, string value)
    {
        if (runtimeKind == "container-image")
        {
            if (value.Length != 71
                || !value.StartsWith("sha256:", StringComparison.Ordinal)
                || !value[7..].All(Uri.IsHexDigit))
                throw new ArgumentException("The Python container identity must be an immutable image ID.", nameof(value));
            return value.ToLowerInvariant();
        }

        return RequireSha256(value, nameof(value));
    }

    internal static string RequireSha256(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A Python authority digest is invalid.", parameterName);
        return value.ToLowerInvariant();
    }

    internal static string RequireVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+'))
            throw new ArgumentException("The Python runtime version is invalid.", nameof(value));
        return value;
    }

    internal static IReadOnlyList<PythonSyntaxDiagnostic> RequireDiagnostics(
        IReadOnlyList<PythonSyntaxDiagnostic>? diagnostics,
        IReadOnlySet<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (diagnostics.Count > 2_000)
            throw new PythonToolingAuthorityException("python-diagnostic-limit", "The Python authority exceeded the diagnostic limit.");

        var normalized = diagnostics.Select(item =>
        {
            ArgumentNullException.ThrowIfNull(item);
            var path = LanguageToolingRequestPolicy.RequireWorkspacePath(item.RelativePath);
            if (!sourcePaths.Contains(path))
                throw new PythonToolingAuthorityException("python-diagnostic-path-invalid", "The Python authority returned a diagnostic outside the checked source set.");
            if (item.StartLine < 1 || item.StartColumn < 1 || item.EndLine < item.StartLine
                || (item.EndLine == item.StartLine && item.EndColumn < item.StartColumn))
                throw new PythonToolingAuthorityException("python-diagnostic-range-invalid", "The Python authority returned an invalid diagnostic range.");

            return item with
            {
                RelativePath = path,
                Severity = item.Severity.ToLowerInvariant() switch
                {
                    "error" => "error",
                    "warning" => "warning",
                    "info" => "info",
                    _ => "hint",
                },
                Code = KnownPinnedToolchainEvidenceSource.SafeCode(item.Code, "python-syntax"),
                Message = KnownPinnedToolchainEvidenceSource.SafeMessage(item.Message, "Python reported a syntax diagnostic."),
            };
        }).ToArray();
        return new ReadOnlyCollection<PythonSyntaxDiagnostic>(normalized);
    }
}
