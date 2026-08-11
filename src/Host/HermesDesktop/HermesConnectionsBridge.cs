using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HermesCredentialBroker;
using Microsoft.Win32;

namespace HermesDesktop;

internal sealed class HermesConnectionsBridge : IDisposable
{
    private static readonly IReadOnlyDictionary<string, ConnectionDefinition> Catalog =
        new[]
        {
            new ConnectionDefinition("openrouter", "OpenRouter", "default", CredentialAuthKind.ApiKey, ["model:openrouter", "usage"]),
            new ConnectionDefinition("openai-api", "OpenAI API", "default", CredentialAuthKind.ApiKey, ["model:openai", "usage"]),
            new ConnectionDefinition("anthropic-api", "Anthropic API / Claude", "default", CredentialAuthKind.ApiKey, ["model:anthropic", "usage"]),
            new ConnectionDefinition("google-ai-studio", "Google AI Studio / Gemini", "default", CredentialAuthKind.ApiKey, ["model:google"]),
            new ConnectionDefinition("deepseek-api", "DeepSeek API", "default", CredentialAuthKind.ApiKey, ["model:deepseek"]),
            new ConnectionDefinition("xai-api", "xAI API / Grok", "default", CredentialAuthKind.ApiKey, ["model:xai"]),
        }.ToDictionary(entry => $"{entry.ProviderId}:{entry.SlotId}", StringComparer.Ordinal);

    private readonly Action<object> _postMessage;
    private readonly CredentialPrincipalBinding _principal;
    private readonly DpapiCredentialVaultV2 _vault;
    private readonly NativeCredentialBrokerV2 _broker;
    private readonly Dictionary<string, ReviewBinding> _reviews = new(StringComparer.Ordinal);
    private readonly string _workbenchSessionId = $"wbs2_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
    private CredentialDialog? _dialog;
    private bool _disposed;

    internal event EventHandler? RuntimeBindingsChanged;

    internal CredentialPrincipalBinding Principal => _principal;

    internal ICredentialLeaseResolver LeaseResolver => _vault;

    internal HermesConnectionsBridge(string applicationInstallRoot, Action<object> postMessage)
    {
        _postMessage = postMessage;
        var sid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User?.Value
            ?? throw new InvalidOperationException("The current Windows principal is unavailable.");
        var machineId = HashIdentifier(ReadMachineGuid());
        _ = Path.GetFullPath(applicationInstallRoot);
        var installId = HashIdentifier("photos-agape-aphthartos.workbench.credentials.v2");
        _principal = new CredentialPrincipalBinding(sid, machineId, installId, "default");
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotosAgapeAphthartos",
            "CredentialsV2");
        _vault = new DpapiCredentialVaultV2(root, _principal, new CurrentUserDpapiRecordProtector(), new StrictWindowsCredentialStorageSecurity());
        _broker = new NativeCredentialBrokerV2(_vault);
    }

    internal async Task ListAsync(int version, string? requestId)
    {
        if (!TryEnvelope(version, requestId, out var id)) return;
        try
        {
            var entries = await _broker.ListAsync().ConfigureAwait(true);
            PostJson(HermesConnectionsRendererProtocol.SerializeMetadataResult(id, entries));
        }
        catch (Exception exception) { PostFailure(id, exception); }
    }

    internal void BeginChange(
        int version,
        string? requestId,
        string? profileId,
        string? providerId,
        string? slotId,
        string? authKind,
        string? sourceKind,
        string[] purposes,
        string? existingReference,
        int expectedRevision)
    {
        if (!TryEnvelope(version, requestId, out var id)) return;
        try
        {
            var definition = RequireDefinition(profileId, providerId, slotId, authKind, sourceKind, purposes);
            CredentialReference? reference = string.IsNullOrWhiteSpace(existingReference) ? null : new CredentialReference(existingReference);
            if (expectedRevision < 0 || (reference is null ? expectedRevision != 0 : expectedRevision <= 0))
                throw new CredentialBrokerException("invalid_revision", "The connection revision is invalid.");

            _dialog?.Close();
            PruneExpiredReviews();
            _dialog = new CredentialDialog(
                definition.DisplayName,
                definition.SlotId,
                definition.Purposes,
                "WINDOWS USER-BOUND ENCRYPTED VAULT")
            { Owner = System.Windows.Application.Current.MainWindow };
            if (_dialog.ShowDialog() != true)
            {
                _dialog.ClearSecret();
                _dialog = null;
                PostError(id, "cancelled", "The native credential change was cancelled.", retryable: true);
                return;
            }

            using var secure = _dialog.TakeSecret();
            _dialog = null;
            var bytes = Utf8Bytes(secure);
            CredentialSecret? secret = null;
            try
            {
                secret = new CredentialSecret(bytes);
                var binding = new CredentialBinding(
                    _principal,
                    definition.ProviderId,
                    definition.SlotId,
                    definition.AuthKind,
                    CredentialSourceKind.Native,
                    definition.Purposes);
                var ticket = _broker.BeginChangeReview(new CredentialWriteIntent(_workbenchSessionId, binding, reference, expectedRevision), secret);
                secret = null;
                _reviews[ticket.ReviewHandle] = new ReviewBinding(ticket.Action, ticket.ExpiresAt);
                PostJson(HermesConnectionsRendererProtocol.SerializeReviewResult(id, ticket));
            }
            finally
            {
                secret?.Dispose();
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception exception) { PostFailure(id, exception); }
    }

    internal async Task BeginRemoveAsync(int version, string? requestId, string? connectionReference, int expectedRevision)
    {
        if (!TryEnvelope(version, requestId, out var id)) return;
        try
        {
            if (expectedRevision <= 0) throw new CredentialBrokerException("invalid_revision", "The connection revision is invalid.");
            var ticket = await _broker.BeginRemoveReviewAsync(new CredentialRemoveIntent(
                _workbenchSessionId,
                new CredentialReference(connectionReference ?? string.Empty),
                expectedRevision)).ConfigureAwait(true);
            PruneExpiredReviews();
            _reviews[ticket.ReviewHandle] = new ReviewBinding(ticket.Action, ticket.ExpiresAt);
            PostJson(HermesConnectionsRendererProtocol.SerializeReviewResult(id, ticket));
        }
        catch (Exception exception) { PostFailure(id, exception); }
    }

    internal async Task CommitAsync(int version, string? requestId, string? reviewHandle)
    {
        if (!TryEnvelope(version, requestId, out var id)) return;
        var handle = reviewHandle ?? string.Empty;
        PruneExpiredReviews();
        if (!_reviews.Remove(handle, out var binding))
        {
            PostError(id, "review_unavailable", "The native review is expired or already consumed.", retryable: false);
            return;
        }
        try
        {
            if (binding.Action == CredentialReviewAction.Change)
            {
                var metadata = await _broker.CommitChangeAsync(handle, _workbenchSessionId).ConfigureAwait(true);
                RuntimeBindingsChanged?.Invoke(this, EventArgs.Empty);
                _postMessage(new { type = "connections.changed", version = HermesCredentialBrokerProtocol.Version, requestId = id, entry = HermesConnectionsRendererProtocol.ToRendererMetadata(metadata) });
            }
            else
            {
                await _broker.CommitRemoveAsync(handle, _workbenchSessionId).ConfigureAwait(true);
                RuntimeBindingsChanged?.Invoke(this, EventArgs.Empty);
                _postMessage(new { type = "connections.removed", version = HermesCredentialBrokerProtocol.Version, requestId = id });
            }
        }
        catch (Exception exception) { PostFailure(id, exception); }
    }

    internal void Cancel(int version, string? requestId, string? reviewHandle)
    {
        if (!TryEnvelope(version, requestId, out var id)) return;
        var handle = reviewHandle ?? string.Empty;
        var cancelled = _broker.CancelReview(handle, _workbenchSessionId);
        _reviews.Remove(handle);
        if (!cancelled) { PostError(id, "review_unavailable", "The native review is expired or already consumed.", retryable: false); return; }
        _postMessage(new { type = "connections.review.cancelled", version = HermesCredentialBrokerProtocol.Version, requestId = id });
    }

    private static ConnectionDefinition RequireDefinition(
        string? profileId,
        string? providerId,
        string? slotId,
        string? authKind,
        string? sourceKind,
        IReadOnlyList<string> purposes)
    {
        if (profileId != "default" || sourceKind != "native" || !Catalog.TryGetValue($"{providerId}:{slotId}", out var definition))
            throw new CredentialBrokerException("connection_not_allowed", "That native connection is not in the Workbench catalog.");
        var expectedAuth = definition.AuthKind == CredentialAuthKind.ApiKey ? "api-key" : string.Empty;
        if (authKind != expectedAuth || !purposes.SequenceEqual(definition.Purposes, StringComparer.Ordinal))
            throw new CredentialBrokerException("connection_binding_mismatch", "The connection request does not match its host-owned catalog binding.");
        return definition;
    }

    private bool TryEnvelope(int version, string? requestId, out string id)
    {
        id = requestId?.Trim() ?? string.Empty;
        if (version != HermesCredentialBrokerProtocol.Version || id.Length is 0 or > 128
            || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':')))
        {
            PostError(string.Empty, "invalid_envelope", "The native connection request envelope is invalid.", retryable: false);
            return false;
        }
        return true;
    }

    private static byte[] Utf8Bytes(SecureString secret)
    {
        var pointer = IntPtr.Zero;
        var characters = new char[secret.Length];
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(secret);
            Marshal.Copy(pointer, characters, 0, characters.Length);
            return Encoding.UTF8.GetBytes(characters);
        }
        finally
        {
            Array.Clear(characters);
            if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    private void PostJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        _postMessage(document.RootElement.Clone());
    }

    private void PostFailure(string requestId, Exception exception)
    {
        if (exception is CredentialBrokerException broker) PostError(requestId, broker.Code, broker.Message, retryable: false);
        else PostError(requestId, "native_failure", "The native credential operation failed.", retryable: true);
    }

    private void PostError(string requestId, string code, string message, bool retryable) => _postMessage(new
    {
        type = "connections.error",
        version = HermesCredentialBrokerProtocol.Version,
        requestId,
        code,
        message,
        retryable,
    });

    private static string ReadMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        return key?.GetValue("MachineGuid") as string
            ?? throw new InvalidOperationException("The Windows machine identity is unavailable.");
    }

    private static string HashIdentifier(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private void PruneExpiredReviews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var handle in _reviews.Where(review => review.Value.ExpiresAt <= now).Select(review => review.Key).ToArray())
            _reviews.Remove(handle);
    }

    internal async Task<IReadOnlyList<CredentialRuntimeBindingMetadata>> GetRuntimeBindingsAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await _broker.ListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CredentialRuntimeBindingMetadata>();
        foreach (var entry in entries)
        {
            if (!entry.Configured
                || entry.ProfileId != "default"
                || entry.SlotId != "default"
                || entry.AuthKind != CredentialAuthKind.ApiKey
                || entry.SourceKind != CredentialSourceKind.Native
                || !Catalog.TryGetValue($"{entry.ProviderId}:{entry.SlotId}", out var definition))
            {
                continue;
            }

            var runtime = entry.ProviderId switch
            {
                "openrouter" => (EnvironmentName: "OPENROUTER_API_KEY", Purpose: "model:openrouter"),
                "openai-api" => (EnvironmentName: "OPENAI_API_KEY", Purpose: "model:openai"),
                "anthropic-api" => (EnvironmentName: "ANTHROPIC_API_KEY", Purpose: "model:anthropic"),
                "google-ai-studio" => (EnvironmentName: "GOOGLE_API_KEY", Purpose: "model:google"),
                "deepseek-api" => (EnvironmentName: "DEEPSEEK_API_KEY", Purpose: "model:deepseek"),
                "xai-api" => (EnvironmentName: "XAI_API_KEY", Purpose: "model:xai"),
                _ => default,
            };
            if (runtime.EnvironmentName is null
                || !definition.Purposes.Contains(runtime.Purpose, StringComparer.Ordinal)
                || !entry.Purposes.SequenceEqual(definition.Purposes, StringComparer.Ordinal))
            {
                continue;
            }
            result.Add(new CredentialRuntimeBindingMetadata(
                runtime.EnvironmentName,
                entry.ConnectionRef.Value,
                runtime.Purpose,
                entry.Revision));
        }
        return result.OrderBy(entry => entry.EnvironmentName, StringComparer.Ordinal).ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dialog?.Close();
        _dialog?.ClearSecret();
        _broker.Dispose();
        _vault.Dispose();
    }

    private sealed record ConnectionDefinition(
        string ProviderId,
        string DisplayName,
        string SlotId,
        CredentialAuthKind AuthKind,
        string[] Purposes);

    private sealed record ReviewBinding(CredentialReviewAction Action, DateTimeOffset ExpiresAt);
}

internal sealed record CredentialRuntimeBindingMetadata(
    string EnvironmentName,
    string ConnectionReference,
    string Purpose,
    long Revision);
