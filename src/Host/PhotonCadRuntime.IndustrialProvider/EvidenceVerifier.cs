using System.Security.Cryptography;
using System.Text.Json;
using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

internal sealed record VerifiedIndustrialEvidence(
    string ReceiptSha256,
    string DerivedImageId,
    string BaseImageId,
    string CatalogDigest,
    PhotonCadProviderEvidence ProviderEvidence);

internal static class EvidenceVerifier
{
    internal const string AcceptedSelectionSha256 = "sha256:8b199ce6b1a96f2fe0e5e8b98b6fd6136ea31d836d3270923318243dbc607fda";
    internal const string AcceptedReceiptSha256 = "sha256:70969065e209b454e4149235ad2662686629d7a23c44d584938c8a672bb2edb4";
    internal const string AcceptedDerivedImageId = "sha256:eda304290edbf75c33352e3df857a40539c48e20508d4025f63f7d4508ff25f9";
    internal const string AcceptedBaseImageId = "sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703";
    internal const string AcceptedCatalogDigest = "sha256:aae5554ce9e57133f508e3343663704d35c82e6a31e5201f6224a9b299ddf6b6";
    internal const string RedistributionStatus = "blocked-pending-license-review";
    private const int MaximumEvidenceBytes = 1024 * 1024;

    internal static async ValueTask<VerifiedIndustrialEvidence> VerifyAsync(
        string selectionPath,
        CancellationToken cancellationToken)
    {
        var selectionFullPath = RequireRegularFile(selectionPath);
        var selectionBytes = await ReadBoundedAsync(selectionFullPath, cancellationToken).ConfigureAwait(false);
        if (!ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(selectionBytes), AcceptedSelectionSha256))
            throw Failure("industrial_selection_not_accepted");

        using var selection = StrictDocument(selectionBytes);
        var root = selection.RootElement;
        Exact(root, "schema", "selectedUtc", "runId", "receipt", "report", "baseImageId", "derivedImageId", "friendlyTag", "friendlyTagVerified", "previousFriendlyImageId", "scenarioCount");
        RequireString(root, "schema", "photon.cad.industrial.evidence-selection/v1");
        var runId = Identifier(root, "runId", 32, lowerHexOnly: true);
        RequireDigest(root, "baseImageId", AcceptedBaseImageId);
        RequireDigest(root, "derivedImageId", AcceptedDerivedImageId);
        RequireBoolean(root, "friendlyTagVerified", true);
        if (Integer(root, "scenarioCount", 1, 1000) != 20) throw Failure("industrial_scenario_count_mismatch");

        var receipt = root.GetProperty("receipt");
        Exact(receipt, "path", "sha256", "immutable");
        RequireBoolean(receipt, "immutable", true);
        RequireDigest(receipt, "sha256", AcceptedReceiptSha256);
        var relative = String(receipt, "path");
        var expectedRelative = $"runs/receipt-{runId}.json";
        if (!StringComparer.Ordinal.Equals(relative, expectedRelative)
            || relative.Contains('\\') || Path.IsPathFullyQualified(relative))
            throw Failure("industrial_receipt_path_rejected");
        var rootDirectory = Path.GetDirectoryName(selectionFullPath) ?? throw Failure("industrial_evidence_root_missing");
        var receiptFullPath = Path.GetFullPath(Path.Combine(rootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        var expectedPrefix = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;
        if (!receiptFullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw Failure("industrial_receipt_path_rejected");
        receiptFullPath = RequireRegularFile(receiptFullPath);
        var receiptBytes = await ReadBoundedAsync(receiptFullPath, cancellationToken).ConfigureAwait(false);
        if (!ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(receiptBytes), AcceptedReceiptSha256))
            throw Failure("industrial_receipt_digest_mismatch");

        using var immutableReceipt = StrictDocument(receiptBytes);
        var evidence = immutableReceipt.RootElement;
        RequireString(evidence, "schema", "photon.cad.industrial.smoke-receipt/v2");
        RequireString(evidence, "runId", runId);
        RequireBoolean(evidence, "passed", true);
        var baseImage = evidence.GetProperty("baseImage");
        RequireDigest(baseImage, "exactId", AcceptedBaseImageId);
        var derivedImage = evidence.GetProperty("derivedImage");
        RequireDigest(derivedImage, "exactId", AcceptedDerivedImageId);
        RequireBoolean(derivedImage, "runsUsedExactIdOnly", true);
        var catalog = evidence.GetProperty("catalog");
        RequireDigest(catalog, "digest", AcceptedCatalogDigest);
        if (Integer(catalog, "byteLength", 1, 4 * 1024 * 1024) != 50_474)
            throw Failure("industrial_catalog_length_mismatch");
        var redistribution = evidence.GetProperty("redistribution");
        Exact(redistribution, "status", "aggregateLicenseClaim");
        RequireString(redistribution, "status", RedistributionStatus);
        RequireBoolean(redistribution, "aggregateLicenseClaim", false);
        var policy = evidence.GetProperty("runtimePolicy");
        RequireString(policy, "network", "none");
        RequireBoolean(policy, "readOnlyRoot", true);
        RequireString(policy, "user", "65532:65532");
        RequireString(policy, "capabilities", "ALL dropped");
        RequireBoolean(policy, "noNewPrivileges", true);
        RequireBoolean(policy, "exactContainerCleanup", true);

        var sourceHashes = evidence.GetProperty("sourceHashes");
        RequireDigest(sourceHashes, "src/Host/PhotonCadRuntime.Container/industrial/Dockerfile", "sha256:fae78fb2fc6bb033c33f19ec8571a5cc2ae4c619d38050a969cf4d0323353c7f");
        RequireDigest(sourceHashes, "src/Host/PhotonCadRuntime.Container/industrial/photon_industrial_adapter.py", "sha256:0a3591e6228af936eddddf9f00e0354513003e547486a4e39b3c4165490e3a12");
        RequireDigest(sourceHashes, "src/Host/PhotonCadRuntime.Container/industrial/schemas/response-v1.schema.json", "sha256:c8083bf5e666e386dd7ebf7d28958e628e37260b990c2b4c39fdee69b242bad6");

        var source = new PhotonCadSourceIdentityV1(
            "photon-cad-industrial",
            "0.1.0",
            AcceptedDerivedImageId,
            "redistribution-blocked");
        var providerEvidence = new PhotonCadProviderEvidence(
            PhotonCadBackendV1.Assembly,
            "photon.cad.industrial.docker.v1",
            ["photon.cad.industrial.protocol.v1"],
            AcceptedCatalogDigest,
            "photon.cad.industrial.container.v1",
            AcceptedReceiptSha256,
            AcceptedReceiptSha256,
            AcceptedDerivedImageId,
            AcceptedBaseImageId,
            source);
        return new VerifiedIndustrialEvidence(
            AcceptedReceiptSha256,
            AcceptedDerivedImageId,
            AcceptedBaseImageId,
            AcceptedCatalogDigest,
            providerEvidence);
    }

    private static string RequireRegularFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw Failure("industrial_evidence_path_required");
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw Failure("industrial_evidence_file_rejected");
        return full;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaximumEvidenceBytes) throw Failure("industrial_evidence_size_rejected");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw Failure("industrial_evidence_identity_changed");
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
            throw new InvalidDataException("industrial_evidence_invalid_json", exception);
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure("industrial_evidence_duplicate_member");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
        }
    }

    private static void Exact(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure("industrial_evidence_object_required");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw Failure("industrial_evidence_member_mismatch");
    }

    private static string String(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String || item.GetString() is not { } result)
            throw Failure("industrial_evidence_value_invalid");
        return result;
    }

    private static string Identifier(JsonElement value, string name, int length, bool lowerHexOnly)
    {
        var result = String(value, name);
        if (result.Length != length || (lowerHexOnly && result.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))))
            throw Failure("industrial_evidence_identifier_invalid");
        return result;
    }

    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (!StringComparer.Ordinal.Equals(String(value, name), expected)) throw Failure("industrial_evidence_value_mismatch");
    }

    private static void RequireDigest(JsonElement value, string name, string expected)
    {
        if (!ProtocolV1.FixedDigestEquals(String(value, name), expected)) throw Failure("industrial_evidence_digest_mismatch");
    }

    private static void RequireBoolean(JsonElement value, string name, bool expected)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || item.GetBoolean() != expected) throw Failure("industrial_evidence_boolean_mismatch");
    }

    private static long Integer(JsonElement value, string name, long minimum, long maximum)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.Number
            || !item.TryGetInt64(out var result) || result < minimum || result > maximum)
            throw Failure("industrial_evidence_integer_invalid");
        return result;
    }

    private static InvalidDataException Failure(string code) => new(code);
}
