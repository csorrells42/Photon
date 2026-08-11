using System.Security.Cryptography;

namespace HermesDeveloperServices.LanguageTooling.Java;

/// <summary>
/// Trusted-host Java/JDT evidence and operation adapter. The public construction path is
/// intentionally unavailable until release-owned provenance pins are added in this lane.
/// </summary>
public sealed class JavaJdtLanguageToolingProvider :
    ILanguageToolingEvidenceSource,
    ILanguageToolingOperationHandler
{
    private const int MaximumSessions = 4;
    private readonly string _workspaceRoot;
    private readonly IJavaJdtRuntimeAuthority _authority;
    private readonly Dictionary<string, JavaJdtLanguageSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private JavaJdtLanguageToolingProvider(string workspaceRoot, IJavaJdtRuntimeAuthority authority)
    {
        _workspaceRoot = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "workspace");
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public string ProviderId => LanguageToolingCatalog.JavaJdt;

    public IReadOnlyCollection<string> CapabilityIds { get; } = ["java-jdt.lsp"];

    public IReadOnlyCollection<string> Operations { get; } = ["start-language-session", "stop-language-session"];

    public static JavaJdtLanguageToolingProvider CreateUnprovisioned(string workspaceRoot) =>
        new(workspaceRoot, JavaJdtProvisioning.CreateUnavailableAuthority());

    internal static JavaJdtLanguageToolingProvider CreateForTrustedAuthority(
        string workspaceRoot,
        IJavaJdtRuntimeAuthority authority) => new(workspaceRoot, authority);

    internal int ActiveSessionCount => _sessions.Count;

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var inspectedRoot = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "workspace");
        if (!inspectedRoot.Equals(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
            return [Unavailable("java-jdt-workspace-mismatch", "The Java/JDT provider is bound to a different trusted workspace.")];
        var inspection = await _authority.InspectAsync(cancellationToken).ConfigureAwait(false);
        return [new LanguageToolingCapabilityStatus(
            "java-jdt.lsp",
            inspection.Available ? LanguageToolingCapabilityState.Available : LanguageToolingCapabilityState.Unavailable,
            inspection.Code,
            inspection.SafeMessage,
            inspection.Available ? inspection.Version : null)];
    }

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(
        LanguageToolingHostRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            var validated = LanguageToolingRequestPolicy.Validate(request);
            if (!validated.ProviderId.Equals(ProviderId, StringComparison.Ordinal))
                throw new LanguageToolingRequestException("operation-mismatch", "The Java/JDT handler accepts only typed Java/JDT requests.");
            return validated switch
            {
                StartLanguageToolingSessionRequest start => await StartAsync(start, cancellationToken).ConfigureAwait(false),
                StopLanguageToolingSessionRequest stop => await StopAsync(stop, cancellationToken).ConfigureAwait(false),
                _ => throw new LanguageToolingRequestException("operation-mismatch", "The Java/JDT handler accepts only typed session requests."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (LanguageToolingRequestException) { throw; }
        catch (TrustedToolchainValidationException exception)
        {
            throw new LanguageToolingRequestException(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or LspProtocolException
            or LspSessionException
            or InvalidOperationException)
        {
            throw new LanguageToolingRequestException(
                "java-session-failed",
                "The trusted Java/JDT session could not complete the structured operation.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
            _sessions.Clear();
            await _authority.DisposeAsync().ConfigureAwait(false);
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
        var document = TrustedToolchainPathPolicy.ResolveExistingFile(_workspaceRoot, request.DocumentPath, "java_document");
        if (!Path.GetExtension(document).Equals(".java", StringComparison.OrdinalIgnoreCase))
            throw new LanguageToolingRequestException("java-document-required", "The Java/JDT session requires a workspace Java source file.");
        _ = TrustedToolchainPathPolicy.RequireNormalFile(
            _workspaceRoot,
            document,
            "java_document",
            minimumBytes: 0,
            maximumBytes: LspSession.MaximumDocumentCharacters * 4L);
        var text = await File.ReadAllTextAsync(document, cancellationToken).ConfigureAwait(false);
        if (text.Length > LspSession.MaximumDocumentCharacters || text.Contains('\0'))
            throw new LanguageToolingRequestException("java-document-invalid", "The Java source file is not valid bounded text.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.Count >= MaximumSessions)
                throw new LanguageToolingRequestException("java-session-limit", "The Java/JDT session limit was reached.");
            var session = await _authority.StartSessionAsync(_workspaceRoot, cancellationToken).ConfigureAwait(false);
            try
            {
                await session.InitializeAsync(new Uri(_workspaceRoot + Path.DirectorySeparatorChar).AbsoluteUri, cancellationToken).ConfigureAwait(false);
                await session.OpenDocumentAsync(new Uri(document).AbsoluteUri, 1, text, cancellationToken).ConfigureAwait(false);
                var sessionId = "java:" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                _sessions.Add(sessionId, session);
                return new LanguageToolingOperationResult(true, "java-session-started", "The trusted Java/JDT session started.", SessionId: sessionId);
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
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
            if (!_sessions.Remove(request.SessionId, out var session))
                throw new LanguageToolingRequestException("java-session-not-found", "The Java/JDT session is not owned by this provider.");
            await session.DisposeAsync().ConfigureAwait(false);
            return new LanguageToolingOperationResult(true, "java-session-stopped", "The trusted Java/JDT session stopped.");
        }
        finally { _gate.Release(); }
    }

    private static LanguageToolingCapabilityStatus Unavailable(string code, string message) => new(
        "java-jdt.lsp", LanguageToolingCapabilityState.Unavailable, code, message);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
