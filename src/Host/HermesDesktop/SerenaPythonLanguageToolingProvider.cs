using HermesDeveloperServices.LanguageTooling;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HermesDesktop;

/// <summary>
/// Proves Python language-service availability only when the launcher-owned Serena endpoint
/// actually serves a symbol from the active workspace through its fixed read-only symbol tool.
/// Serena currently owns the underlying Pyright lifecycle; renderer code cannot select the
/// endpoint, tool, executable, arguments, environment, or workspace.
/// </summary>
internal sealed partial class SerenaPythonLanguageToolingProvider :
    ILanguageToolingEvidenceSource,
    ILanguageToolingOperationHandler
{
    private const int MaximumSessions = 8;
    private static readonly HashSet<string> BlockedProofSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".refvenv", ".venv", "venv", "env", "site-packages", "__pycache__",
    };
    private readonly WorkspaceSearchPathPolicy _pathPolicy;
    private readonly SerenaWorkspaceSearchClient _client;
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal SerenaPythonLanguageToolingProvider(
        string workspaceRoot,
        SerenaWorkspaceSearchClient? client = null)
    {
        _pathPolicy = new WorkspaceSearchPathPolicy(workspaceRoot);
        _client = client ?? new SerenaWorkspaceSearchClient();
    }

    public string ProviderId => LanguageToolingCatalog.Python;

    public IReadOnlyCollection<string> CapabilityIds { get; } = ["python.lsp"];

    public IReadOnlyCollection<string> Operations { get; } = ["start-language-session", "stop-language-session"];

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!PathsEqual(workspaceRoot, _pathPolicy.WorkspaceRoot))
            return [Unavailable("python-language-workspace-mismatch", "The Python language service is bound to a different trusted workspace.")];
        try
        {
            var candidates = await FindProofCandidatesAsync(null, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
                return [Unavailable("python-language-proof-unavailable", "No bounded Python symbol is available for a trusted language-service proof.")];
            if (await FindServedProofCandidateAsync(candidates, cancellationToken).ConfigureAwait(false) is null)
                return [Unavailable("python-language-proof-failed", "Serena did not serve the selected Python symbol from the active workspace.")];
            return [new(
                "python.lsp",
                LanguageToolingCapabilityState.Available,
                "serena-python-language-engine",
                "Serena served a real Python symbol from the active workspace through its fixed language engine.")];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
            or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [Unavailable("python-language-service-unavailable", "The trusted Serena Python language service is unavailable.")];
        }
    }

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var validated = LanguageToolingRequestPolicy.Validate(request);
        if (validated.ProviderId != ProviderId)
            throw new LanguageToolingRequestException("operation-mismatch", "The Serena Python handler accepts only typed Python language-session requests.");
        return validated switch
        {
            StartLanguageToolingSessionRequest start => await StartAsync(start, cancellationToken).ConfigureAwait(false),
            StopLanguageToolingSessionRequest stop => await StopAsync(stop, cancellationToken).ConfigureAwait(false),
            _ => throw new LanguageToolingRequestException("operation-mismatch", "The Serena Python handler accepts only typed language-session requests."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _sessions.Clear();
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask<LanguageToolingOperationResult> StartAsync(
        StartLanguageToolingSessionRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = await FindProofCandidatesAsync(request.DocumentPath, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
            throw new LanguageToolingRequestException("python-language-symbol-required", "The selected Python document has no bounded symbol for language-service verification.");
        var proof = await FindServedProofCandidateAsync(candidates, cancellationToken).ConfigureAwait(false)
            ?? throw new LanguageToolingRequestException("python-language-proof-failed", "Serena did not serve the selected Python document from the active workspace.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.Count >= MaximumSessions)
                throw new LanguageToolingRequestException("python-language-session-limit", "The Python language-session limit was reached.");
            var id = "python:" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _sessions.Add(id, proof.RelativePath);
            return new(true, "python-language-session-started", "The trusted Serena Python language session started.", SessionId: id);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<LanguageToolingOperationResult> StopAsync(
        StopLanguageToolingSessionRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.Remove(request.SessionId))
                throw new LanguageToolingRequestException("python-language-session-not-found", "The Python language session is not owned by this provider.");
            return new(true, "python-language-session-stopped", "The trusted Serena Python language session stopped.");
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<SymbolProof>> FindProofCandidatesAsync(
        string? preferredPath,
        CancellationToken cancellationToken)
    {
        IEnumerable<WorkspaceSearchFile> candidates;
        if (preferredPath is not null)
        {
            if (!_pathPolicy.TryResolveRegularFile(preferredPath, out var preferred)
                || !Path.GetExtension(preferred.RelativePath).Equals(".py", StringComparison.OrdinalIgnoreCase))
                throw new LanguageToolingRequestException("python-document-required", "The Python language session requires a workspace Python source file.");
            candidates = [preferred];
        }
        else
        {
            candidates = _pathPolicy.EnumerateRegularFiles(cancellationToken).Files
                .Where(file => Path.GetExtension(file.RelativePath).Equals(".py", StringComparison.OrdinalIgnoreCase))
                .Where(file => IsProofCandidatePath(file.RelativePath))
                .Take(128);
        }

        var proofs = new List<SymbolProof>();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await _pathPolicy.ReadUtf8TextAsync(file, cancellationToken).ConfigureAwait(false);
            if (text is null) continue;
            var match = PythonSymbol().Match(text);
            if (match.Success) proofs.Add(new(file.RelativePath, match.Groups[1].Value));
        }
        return proofs;
    }

    private async Task<SymbolProof?> FindServedProofCandidateAsync(
        IReadOnlyList<SymbolProof> proofs,
        CancellationToken cancellationToken)
    {
        foreach (var batch in proofs.Chunk(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = await _client.SearchAsync(
                string.Join(' ', batch.Select(proof => proof.Symbol)),
                32,
                8,
                512,
                _pathPolicy,
                cancellationToken).ConfigureAwait(false);
            foreach (var proof in batch)
            {
                if (results.Any(result =>
                    result.Path.Equals(proof.RelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    && result.Preview.Contains(proof.Symbol, StringComparison.Ordinal)))
                {
                    return proof;
                }
            }
        }
        return null;
    }

    private static bool IsProofCandidatePath(string relativePath) =>
        !relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(BlockedProofSegments.Contains);

    private static LanguageToolingCapabilityStatus Unavailable(string code, string message) => new(
        "python.lsp", LanguageToolingCapabilityState.Unavailable, code, message);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record SymbolProof(string RelativePath, string Symbol);

    [GeneratedRegex(@"(?m)^[ \t]*(?:async[ \t]+)?(?:def|class)[ \t]+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PythonSymbol();
}
