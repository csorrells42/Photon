using System.Security.Cryptography;
using System.Text.Json;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;
using PhotonCadRuntime.IndustrialProvider;

namespace PhotonCadRuntime.ManualProvider;

internal sealed record VerifiedManualEvidence(
    string ReceiptSha256,
    string DerivedImageId,
    string BaseImageId,
    PhotonCadProviderEvidence ProviderEvidence);

internal static class ManualEvidenceVerifier
{
    internal const string AcceptedSelectionSha256 = "sha256:f8a41c033f4d3c4425616b9d78061b2d805fd37026d3f7ff4e220e66dfb1aa95";
    private const string AcceptedNormalizedSelectionSha256 = "sha256:f8a41c033f4d3c4425616b9d78061b2d805fd37026d3f7ff4e220e66dfb1aa95";
    internal const string AcceptedReceiptSha256 = "sha256:fd831217e23baf638c78a29326ccd570b1e9e67037b4e1f840da1ae47761d65b";
    internal const string AcceptedDerivedImageId = "sha256:1f5b532d241cc7139e22e03ba7d3a2773acd58b5fcaacef93e7f0e2d741371e0";
    internal const string AcceptedBaseImageId = "sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703";
    internal const string AcceptedAdapterSha256 = "sha256:f62b85f7a42778eaf7c66f48341f2196362923b3f389575f5694596de42d1f04";
    internal const string AcceptedRequestSchemaSha256 = "sha256:93b0e36cd8310651ae530c77aae994a11a2d3fbab6b03d9ef1a30193a0f392f9";
    internal const string AcceptedResponseSchemaSha256 = "sha256:740bf73ee0961efc07e31c290966124af78fddb0f352009c5f48571d9581ab9a";
    private const int MaximumEvidenceBytes = 1024 * 1024;

    internal static async ValueTask<VerifiedManualEvidence> VerifyAsync(
        string selectionPath,
        CancellationToken cancellationToken)
    {
        var fullSelection = RequireRegularFile(selectionPath);
        var selectionBytes = await ReadBoundedAsync(fullSelection, cancellationToken).ConfigureAwait(false);
        RequireAcceptedSelection(selectionBytes);
        using var selection = StrictDocument(selectionBytes);
        var root = selection.RootElement;
        RequireString(root, "schema", "photon.cad.industrial.evidence-selection/v1");
        var runId = String(root, "runId");
        if (runId.Length != 32 || runId.Any(value => !char.IsAsciiHexDigit(value) || char.IsAsciiLetterUpper(value)))
            throw Failure("manual_evidence_run_id_invalid");
        RequireDigest(String(root, "baseImageId"), AcceptedBaseImageId, "manual_base_image_mismatch");
        RequireDigest(String(root, "derivedImageId"), AcceptedDerivedImageId, "manual_derived_image_mismatch");
        if (!root.TryGetProperty("scenarioCount", out var scenarioCount)
            || !scenarioCount.TryGetInt32(out var count)
            || count < 22)
            throw Failure("manual_scenario_count_mismatch");

        var receipt = root.GetProperty("receipt");
        var relative = String(receipt, "path");
        if (!StringComparer.Ordinal.Equals(relative, $"runs/receipt-{runId}.json")
            || relative.Contains('\\')
            || Path.IsPathFullyQualified(relative))
            throw Failure("manual_receipt_path_rejected");
        RequireDigest(String(receipt, "sha256"), AcceptedReceiptSha256, "manual_receipt_selection_mismatch");
        if (!receipt.TryGetProperty("immutable", out var immutable)
            || immutable.ValueKind != JsonValueKind.True)
            throw Failure("manual_receipt_not_immutable");
        var rootDirectory = Path.GetDirectoryName(fullSelection) ?? throw Failure("manual_evidence_root_missing");
        var receiptPath = Path.GetFullPath(Path.Combine(rootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!receiptPath.StartsWith(Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Failure("manual_receipt_path_rejected");
        var receiptBytes = await ReadBoundedAsync(RequireRegularFile(receiptPath), cancellationToken).ConfigureAwait(false);
        RequireDigest(Sha256(receiptBytes), AcceptedReceiptSha256, "manual_receipt_digest_mismatch");
        using var document = StrictDocument(receiptBytes);
        var evidence = document.RootElement;
        RequireString(evidence, "schema", "photon.cad.industrial.smoke-receipt/v2");
        RequireString(evidence, "runId", runId);
        if (!evidence.TryGetProperty("passed", out var passed) || passed.ValueKind != JsonValueKind.True)
            throw Failure("manual_receipt_not_passing");
        RequireDigest(String(evidence.GetProperty("baseImage"), "exactId"), AcceptedBaseImageId, "manual_receipt_base_image_mismatch");
        var derived = evidence.GetProperty("derivedImage");
        RequireDigest(String(derived, "exactId"), AcceptedDerivedImageId, "manual_receipt_image_mismatch");
        if (!derived.TryGetProperty("runsUsedExactIdOnly", out var exactRuns) || exactRuns.ValueKind != JsonValueKind.True)
            throw Failure("manual_receipt_image_identity_unproven");
        var redistribution = evidence.GetProperty("redistribution");
        RequireString(redistribution, "status", "blocked-pending-license-review");
        if (!redistribution.TryGetProperty("aggregateLicenseClaim", out var aggregate) || aggregate.ValueKind != JsonValueKind.False)
            throw Failure("manual_redistribution_claim_invalid");
        var policy = evidence.GetProperty("runtimePolicy");
        RequireString(policy, "network", "none");
        RequireString(policy, "user", "65532:65532");
        RequireTrue(policy, "readOnlyRoot");
        RequireTrue(policy, "noNewPrivileges");
        RequireTrue(policy, "exactContainerCleanup");
        var hashes = evidence.GetProperty("sourceHashes");
        RequireDigest(String(hashes, "src/Host/PhotonCadRuntime.Container/industrial/photon_industrial_adapter.py"), AcceptedAdapterSha256, "manual_adapter_hash_mismatch");
        RequireDigest(String(hashes, "src/Host/PhotonCadRuntime.Container/industrial/schemas/request-v1.schema.json"), AcceptedRequestSchemaSha256, "manual_request_schema_hash_mismatch");
        RequireDigest(String(hashes, "src/Host/PhotonCadRuntime.Container/industrial/schemas/response-v1.schema.json"), AcceptedResponseSchemaSha256, "manual_response_schema_hash_mismatch");

        var source = new PhotonCadSourceIdentityV1(
            "photon-cad-manual",
            "1.0.0",
            AcceptedDerivedImageId,
            "redistribution-blocked");
        var providerEvidence = new PhotonCadProviderEvidence(
            PhotonCadBackendV1.Geometry,
            "photon.cad.manual.docker.v1",
            [PhotonCadManualProtocolRequirements.ProtocolId, "photon.cad.industrial.protocol.v1"],
            "manual-v1",
            "photon.cad.manual.container.v1",
            AcceptedReceiptSha256,
            AcceptedReceiptSha256,
            AcceptedDerivedImageId,
            AcceptedBaseImageId,
            source);
        return new VerifiedManualEvidence(
            AcceptedReceiptSha256,
            AcceptedDerivedImageId,
            AcceptedBaseImageId,
            providerEvidence);
    }

    private static string RequireRegularFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw Failure("manual_evidence_path_required");
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw Failure("manual_evidence_file_rejected");
        return full;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumEvidenceBytes || stream.Length > int.MaxValue)
            throw Failure("manual_evidence_size_rejected");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw Failure("manual_evidence_identity_changed");
        return bytes;
    }

    private static JsonDocument StrictDocument(byte[] bytes)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            RejectDuplicates(document.RootElement);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("manual_evidence_invalid_json", exception);
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure("manual_evidence_duplicate_member");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
        }
    }

    private static string String(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } result)
            throw Failure("manual_evidence_value_invalid");
        return result;
    }

    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (!StringComparer.Ordinal.Equals(String(value, name), expected))
            throw Failure("manual_evidence_value_mismatch");
    }

    private static void RequireTrue(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.True)
            throw Failure("manual_evidence_boolean_mismatch");
    }

    private static void RequireDigest(string actual, string expected, string code)
    {
        if (!ProtocolV1.FixedDigestEquals(actual, expected)) throw Failure(code);
    }

    private static void RequireAcceptedSelection(ReadOnlySpan<byte> bytes)
    {
        if (ProtocolV1.FixedDigestEquals(Sha256(bytes), AcceptedSelectionSha256)) return;
        RequireDigest(Sha256(NormalizeSelectionLineEndings(bytes)), AcceptedNormalizedSelectionSha256,
            "manual_selection_not_accepted");
    }

    private static byte[] NormalizeSelectionLineEndings(ReadOnlySpan<byte> bytes)
    {
        var normalized = new byte[bytes.Length];
        var write = 0;
        for (var read = 0; read < bytes.Length; read++)
        {
            var value = bytes[read];
            if (value != (byte)'\r')
            {
                normalized[write++] = value;
                continue;
            }

            if (++read >= bytes.Length || bytes[read] != (byte)'\n')
                throw Failure("manual_selection_line_endings_invalid");
            normalized[write++] = (byte)'\n';
        }

        return write == normalized.Length ? normalized : normalized[..write];
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private static InvalidDataException Failure(string code) => new(code);
}
