using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotonCadRuntime;

public static class CadDockerRuntimeIdentity
{
    public const string PolicySchema = "photon.cad.container-policy/v1";
    public const string ReceiptSchema = "photon.cad.bundle-receipt/v2";
    public const string Platform = "linux/amd64";
    public const string RuntimeUser = "65532:65532";
    public const string WorkspaceMount = "/workspace";
    public const string GeometryBundleVersion = "0.1.0";
    public const string AssemblyBundleVersion = "0.1.1";
    public const string GeometryTag = "photon-cad-geometry:0.1.0-b123dmcp-0.3.80-8fb7cefb";
    public const string AssemblyTag = "photon-cad-assembly:0.1.1-partcad-0.7.158-b77c2c08-p1066a9ff";
    public const string GeometryRevision = "8fb7cefb28daa7f9f68b7e653244256f09b22b1f";
    public const string AssemblyRevision = "b77c2c08f4a63b97070fcaf9e26bc09f34f95037";
    public const string GeometrySourceArchiveSha256 = "d307c94e1fc94d2c634736a34ffbabc037b9186351915cf16aa60e7db8dad97f";
    public const string AssemblySourceArchiveSha256 = "ec4be69fe9b5a864ee599e9939e91524398b4cc5af59cae081a253c5f59f5560";
    public const string GeometryBaseImage = "python:3.12.11-slim-bookworm@sha256:c00fc7b44d844b6da22861ec24af43968a5200eac4ec607b4725d585165d6b49";
    public const string AssemblyBaseImage = "python:3.11.13-slim-bookworm@sha256:86adf8dbadc3d6e82ee5dd2c74bec2e1c2467cdad47886280501df722372d2e1";
    public const string GeometryBaseName = "python:3.12.11-slim-bookworm";
    public const string AssemblyBaseName = "python:3.11.13-slim-bookworm";
    public const string GeometryBaseDigest = "sha256:c00fc7b44d844b6da22861ec24af43968a5200eac4ec607b4725d585165d6b49";
    public const string AssemblyBaseDigest = "sha256:86adf8dbadc3d6e82ee5dd2c74bec2e1c2467cdad47886280501df722372d2e1";
    public const string AssemblyAdaptationKind = "offline-runtime-patch";
    public const string AssemblyAdaptationPatch = "assembly/photon_partcad_offline_patch.py";
    public const string AssemblyAdaptationPatchSha256 = "1066a9ff24d6565231c75c8fb64bab5076400b7f67fd76de0c8b64eb99ef6da8";
    public const string AssemblyAdaptationTarget = "partcad/src/partcad/runtime_python_none.py";
    public const string AssemblyAdaptationTargetSha256 = "c6249705d5d43f6cc1bac13b3933fc0af27e6803a7d97f38a8db211588768e43";
    public const string GeometryRepository = "https://github.com/pzfreo/build123d-mcp";
    public const string AssemblyRepository = "https://github.com/partcad/partcad";
    public static IReadOnlyList<string> GeometryEntrypoint { get; } = Array.AsReadOnly(
        new[] { "/opt/photon/venv/bin/python", "/opt/photon/bin/photon_geometry_entrypoint.py" });
    public static IReadOnlyList<string> AssemblyEntrypoint { get; } = Array.AsReadOnly(
        new[] { "/opt/photon/venv/bin/python", "/opt/photon/bin/photon_partcad_entrypoint.py" });
    public static IReadOnlyList<string> GeometryEnvironment { get; } = Array.AsReadOnly(new[]
    {
        "PATH=/usr/local/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        "LANG=C.UTF-8",
        "GPG_KEY=7169605F62C751356D054A26A821E680E5FA6305",
        "PYTHON_VERSION=3.12.11",
        "PYTHON_SHA256=c30bb24b7f1e9a19b11b55a546434f74e739bb4c271a3e3a80ff4380d49f7adb",
        "HOME=/session/home",
        "XDG_CACHE_HOME=/session/cache",
        "OMP_NUM_THREADS=2",
        "OPENBLAS_NUM_THREADS=2",
        "MKL_NUM_THREADS=2",
        "NUMEXPR_NUM_THREADS=2",
        "PYTHONDONTWRITEBYTECODE=1",
        "PYTHONHASHSEED=0",
        "PYTHONNOUSERSITE=1",
        "BUILD123D_TRANSPORT=stdio",
        "BUILD123D_EXEC_TIMEOUT=120",
        "BUILD123D_CPU_LIMIT_S=300",
        "BUILD123D_ALLOW_ALL_IMPORTS=0",
        "BUILD123D_NO_SANDBOX=0",
        "BUILD123D_EXPERIMENTAL=0",
    });
    public static IReadOnlyList<string> AssemblyEnvironment { get; } = Array.AsReadOnly(new[]
    {
        "PATH=/opt/photon/venv/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        "LANG=C.UTF-8",
        "GPG_KEY=A035C8C19219BA821ECEA86B64E628F8D684696D",
        "PYTHON_VERSION=3.11.13",
        "PYTHON_SHA256=8fb5f9fbc7609fa822cb31549884575db7fd9657cbffb89510b5d7975963a83a",
        "HOME=/session/home",
        "XDG_CACHE_HOME=/session/cache",
        "PYTHONPATH=/opt/photon/bin",
        "PYTHONDONTWRITEBYTECODE=1",
        "PYTHONHASHSEED=0",
        "PYTHONNOUSERSITE=1",
        "PC_PYTHON_SANDBOX=none",
        "PC_TELEMETRY_TYPE=none",
        "PC_TELEMETRY_SENTRY_DSN=",
        "PC_TELEMETRY_PERFORMANCE=false",
        "PC_TELEMETRY_FAILURES=false",
        "PC_TELEMETRY_DEBUG=false",
    });
}

public enum CadDockerRuntimeRole
{
    Geometry,
    Assembly,
}

public sealed class CadDockerRuntimeSettings
{
    public CadDockerRuntimeSettings(
        string dockerExecutablePath,
        string dockerExecutableSha256,
        string workspacePath,
        string policyPath,
        string receiptPath,
        string receiptSha256,
        TimeSpan? commandTimeout = null,
        TimeSpan? requestTimeout = null,
        int maximumConcurrentSessions = 1)
    {
        DockerExecutablePath = CadDockerPathSecurity.RequireFile(dockerExecutablePath, nameof(dockerExecutablePath));
        if (!Path.GetExtension(DockerExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("docker_executable_required", nameof(dockerExecutablePath));
        DockerExecutableSha256 = ContractGuards.Sha256(dockerExecutableSha256, nameof(dockerExecutableSha256));
        WorkspacePath = CadDockerPathSecurity.RequireWorkspace(workspacePath, nameof(workspacePath));
        PolicyPath = CadDockerPathSecurity.RequireFile(policyPath, nameof(policyPath));
        ReceiptPath = CadDockerPathSecurity.RequireFile(receiptPath, nameof(receiptPath));
        ReceiptSha256 = ContractGuards.Sha256(receiptSha256, nameof(receiptSha256));
        CommandTimeout = RequireTimeout(commandTimeout ?? TimeSpan.FromSeconds(30), nameof(commandTimeout), 1, 120);
        RequestTimeout = RequireTimeout(requestTimeout ?? TimeSpan.FromSeconds(120), nameof(requestTimeout), 1, 300);
        MaximumConcurrentSessions = ContractGuards.Range(maximumConcurrentSessions, 1, 4, nameof(maximumConcurrentSessions));
    }

    public string DockerExecutablePath { get; }
    public string DockerExecutableSha256 { get; }
    public string WorkspacePath { get; }
    public string PolicyPath { get; }
    public string ReceiptPath { get; }
    public string ReceiptSha256 { get; }
    public TimeSpan CommandTimeout { get; }
    public TimeSpan RequestTimeout { get; }
    public int MaximumConcurrentSessions { get; }

    private static TimeSpan RequireTimeout(TimeSpan value, string field, int minimumSeconds, int maximumSeconds)
    {
        if (value < TimeSpan.FromSeconds(minimumSeconds) || value > TimeSpan.FromSeconds(maximumSeconds))
            throw new CadContractException("invalid_timeout", field);
        return value;
    }
}

public sealed class CadDockerRuntimePolicy
{
    internal CadDockerRuntimePolicy(
        string policySha256,
        int pidsLimit,
        long memoryBytes,
        long nanoCpus,
        int stopTimeoutSeconds,
        IReadOnlyList<string> tmpfs,
        IReadOnlyList<string> environmentAllowlist,
        IReadOnlyList<string> geometryEntrypoint,
        IReadOnlyList<string> assemblyEntrypoint)
    {
        PolicySha256 = policySha256;
        PidsLimit = pidsLimit;
        MemoryBytes = memoryBytes;
        NanoCpus = nanoCpus;
        StopTimeoutSeconds = stopTimeoutSeconds;
        Tmpfs = tmpfs;
        EnvironmentAllowlist = environmentAllowlist;
        GeometryEntrypoint = geometryEntrypoint;
        AssemblyEntrypoint = assemblyEntrypoint;
    }

    public string PolicySha256 { get; }
    public int PidsLimit { get; }
    public long MemoryBytes { get; }
    public long NanoCpus { get; }
    public int StopTimeoutSeconds { get; }
    public IReadOnlyList<string> Tmpfs { get; }
    public IReadOnlyList<string> EnvironmentAllowlist { get; }
    public IReadOnlyList<string> GeometryEntrypoint { get; }
    public IReadOnlyList<string> AssemblyEntrypoint { get; }
}

public sealed class CadDockerBundleReceipt
{
    internal CadDockerBundleReceipt(
        CadDockerRuntimeRole role,
        string tag,
        string imageId,
        string baseImage,
        string archivePath,
        string archiveSha256,
        long archiveByteLength,
        string sourceRevision,
        string sourceRepository)
    {
        Role = role;
        Tag = tag;
        ImageId = imageId;
        BaseImage = baseImage;
        ArchivePath = archivePath;
        ArchiveSha256 = archiveSha256;
        ArchiveByteLength = archiveByteLength;
        SourceRevision = sourceRevision;
        SourceRepository = sourceRepository;
    }

    public CadDockerRuntimeRole Role { get; }
    public string Tag { get; }
    public string ImageId { get; }
    public string BaseImage { get; }
    public string ArchivePath { get; }
    public string ArchiveSha256 { get; }
    public long ArchiveByteLength { get; }
    public string SourceRevision { get; }
    public string SourceRepository { get; }
}

public sealed class CadDockerImageInspection
{
    public CadDockerImageInspection(
        string imageId,
        IEnumerable<string> repoTags,
        string operatingSystem,
        string architecture,
        string user,
        string workingDirectory,
        IEnumerable<string> entrypoint,
        IEnumerable<string> environmentVariables,
        IEnumerable<string> declaredVolumes,
        IReadOnlyDictionary<string, string> labels)
    {
        ImageId = CadDockerEvidenceParser.ImageId(imageId, nameof(imageId));
        RepoTags = ContractGuards.Copy(repoTags, nameof(repoTags), 64, requireAny: true);
        OperatingSystem = ContractGuards.Identifier(operatingSystem, nameof(operatingSystem), 32).ToLowerInvariant();
        Architecture = ContractGuards.Identifier(architecture, nameof(architecture), 32).ToLowerInvariant();
        User = ContractGuards.RequiredText(user, nameof(user), 64);
        WorkingDirectory = ContractGuards.RequiredText(workingDirectory, nameof(workingDirectory), 256);
        Entrypoint = ContractGuards.Copy(entrypoint, nameof(entrypoint), 8, requireAny: true);
        EnvironmentVariables = ContractGuards.Copy(environmentVariables, nameof(environmentVariables), 64, requireAny: true);
        var environmentNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in EnvironmentVariables)
        {
            if (variable.Length > 1_024 || variable.Any(char.IsControl) || variable.IndexOf('=') <= 0 ||
                !environmentNames.Add(variable[..variable.IndexOf('=')]))
                throw new CadContractException("invalid_image_environment", nameof(environmentVariables));
        }
        DeclaredVolumes = ContractGuards.Copy(declaredVolumes, nameof(declaredVolumes), 16);
        if (DeclaredVolumes.Any(volume => string.IsNullOrWhiteSpace(volume) || volume.Length > 256 || volume.Any(char.IsControl)))
            throw new CadContractException("invalid_image_volume", nameof(declaredVolumes));
        if (labels is null || labels.Count > 64)
            throw new CadContractException("invalid_labels", nameof(labels));
        Labels = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            labels.ToDictionary(
                pair => ContractGuards.RequiredText(pair.Key, nameof(labels), 128),
                pair => ContractGuards.RequiredText(pair.Value, nameof(labels), 512),
                StringComparer.Ordinal));
    }

    public string ImageId { get; }
    public IReadOnlyList<string> RepoTags { get; }
    public string OperatingSystem { get; }
    public string Architecture { get; }
    public string User { get; }
    public string WorkingDirectory { get; }
    public IReadOnlyList<string> Entrypoint { get; }
    public IReadOnlyList<string> EnvironmentVariables { get; }
    public IReadOnlyList<string> DeclaredVolumes { get; }
    public IReadOnlyDictionary<string, string> Labels { get; }
}

public interface ICadDockerImageInspector
{
    ValueTask<CadDockerImageInspection> InspectAsync(
        string verifiedDockerExecutablePath,
        string exactTag,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class CadVerifiedDockerRuntimeEvidence
{
    internal CadVerifiedDockerRuntimeEvidence(
        CadDockerRuntimeSettings settings,
        CadDockerRuntimePolicy policy,
        CadDockerBundleReceipt geometry,
        CadDockerBundleReceipt assembly,
        CadDockerImageInspection geometryImage,
        CadDockerImageInspection assemblyImage,
        string receiptSha256,
        DateTimeOffset receiptCreatedAtUtc)
    {
        Settings = settings;
        Policy = policy;
        Geometry = geometry;
        Assembly = assembly;
        GeometryImage = geometryImage;
        AssemblyImage = assemblyImage;
        ReceiptSha256 = ContractGuards.Sha256(receiptSha256, nameof(receiptSha256));
        ReceiptCreatedAtUtc = ContractGuards.Utc(receiptCreatedAtUtc, nameof(receiptCreatedAtUtc));
    }

    public CadDockerRuntimeSettings Settings { get; }
    public CadDockerRuntimePolicy Policy { get; }
    public CadDockerBundleReceipt Geometry { get; }
    public CadDockerBundleReceipt Assembly { get; }
    public CadDockerImageInspection GeometryImage { get; }
    public CadDockerImageInspection AssemblyImage { get; }
    public string ReceiptSha256 { get; }
    public DateTimeOffset ReceiptCreatedAtUtc { get; }
}

public static class CadDockerRuntimeEvidenceVerifier
{
    public static async ValueTask<CadVerifiedDockerRuntimeEvidence> VerifyAsync(
        CadDockerRuntimeSettings settings,
        ICadDockerImageInspector inspector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inspector);
        await RequireFileHashAsync(
            settings.DockerExecutablePath,
            settings.DockerExecutableSha256,
            expectedLength: null,
            cancellationToken).ConfigureAwait(false);

        var receiptBytes = await ReadBoundedFileAsync(settings.ReceiptPath, 1_048_576, cancellationToken).ConfigureAwait(false);
        var receiptSha256 = Convert.ToHexString(SHA256.HashData(receiptBytes)).ToLowerInvariant();
        if (!receiptSha256.Equals(settings.ReceiptSha256, StringComparison.Ordinal))
            throw new CadContractException("receipt_hash_mismatch", nameof(settings.ReceiptPath));
        var policyBytes = await ReadBoundedFileAsync(settings.PolicyPath, 1_048_576, cancellationToken).ConfigureAwait(false);
        var policySha256 = Convert.ToHexString(SHA256.HashData(policyBytes)).ToLowerInvariant();
        var receipt = CadDockerEvidenceParser.ParseReceipt(receiptBytes, Path.GetDirectoryName(settings.ReceiptPath)!);
        if (!policySha256.Equals(receipt.PolicySha256, StringComparison.Ordinal))
            throw new CadContractException("policy_hash_mismatch", nameof(settings.ReceiptPath));
        var policy = CadDockerEvidenceParser.ParsePolicy(policyBytes, policySha256);

        await RequireFileHashAsync(
            receipt.Geometry.ArchivePath,
            receipt.Geometry.ArchiveSha256,
            receipt.Geometry.ArchiveByteLength,
            cancellationToken).ConfigureAwait(false);
        await RequireFileHashAsync(
            receipt.Assembly.ArchivePath,
            receipt.Assembly.ArchiveSha256,
            receipt.Assembly.ArchiveByteLength,
            cancellationToken).ConfigureAwait(false);

        await RequireFileHashAsync(
            settings.DockerExecutablePath,
            settings.DockerExecutableSha256,
            expectedLength: null,
            cancellationToken).ConfigureAwait(false);
        var geometryImage = await inspector.InspectAsync(
            settings.DockerExecutablePath,
            receipt.Geometry.Tag,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var assemblyImage = await inspector.InspectAsync(
            settings.DockerExecutablePath,
            receipt.Assembly.Tag,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        VerifyImage(receipt.Geometry, geometryImage, policy.GeometryEntrypoint);
        VerifyImage(receipt.Assembly, assemblyImage, policy.AssemblyEntrypoint);

        return new CadVerifiedDockerRuntimeEvidence(
            settings,
            policy,
            receipt.Geometry,
            receipt.Assembly,
            geometryImage,
            assemblyImage,
            receiptSha256,
            receipt.CreatedAtUtc);
    }

    public static async ValueTask ReverifyDockerExecutableAsync(
        CadVerifiedDockerRuntimeEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        await RequireFileHashAsync(
            evidence.Settings.DockerExecutablePath,
            evidence.Settings.DockerExecutableSha256,
            expectedLength: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyImage(
        CadDockerBundleReceipt receipt,
        CadDockerImageInspection image,
        IReadOnlyList<string> expectedEntrypoint)
    {
        if (image.ImageId != receipt.ImageId)
            throw new CadContractException("image_id_mismatch", nameof(image));
        if (!image.RepoTags.Contains(receipt.Tag, StringComparer.Ordinal))
            throw new CadContractException("image_tag_mismatch", nameof(image));
        if (image.OperatingSystem != "linux" || image.Architecture != "amd64")
            throw new CadContractException("image_platform_mismatch", nameof(image));
        if (image.User != CadDockerRuntimeIdentity.RuntimeUser)
            throw new CadContractException("image_user_mismatch", nameof(image));
        if (image.WorkingDirectory != CadDockerRuntimeIdentity.WorkspaceMount)
            throw new CadContractException("image_workdir_mismatch", nameof(image));
        if (!image.Entrypoint.SequenceEqual(expectedEntrypoint, StringComparer.Ordinal))
            throw new CadContractException("image_entrypoint_mismatch", nameof(image));
        if (!image.EnvironmentVariables.SequenceEqual(
            receipt.Role == CadDockerRuntimeRole.Geometry
                ? CadDockerRuntimeIdentity.GeometryEnvironment
                : CadDockerRuntimeIdentity.AssemblyEnvironment,
            StringComparer.Ordinal))
            throw new CadContractException("image_environment_mismatch", nameof(image));
        if (image.DeclaredVolumes.Count != 0)
            throw new CadContractException("image_volume_mismatch", nameof(image));

        RequireLabel(image, "io.photon.cad.role", receipt.Role == CadDockerRuntimeRole.Geometry ? "geometry" : "assembly");
        RequireLabel(image, "io.photon.cad.source-revision", receipt.SourceRevision);
        RequireLabel(image, "org.opencontainers.image.source", receipt.SourceRepository);
        RequireLabel(image, "org.opencontainers.image.licenses", "Apache-2.0");
        RequireLabel(image, "org.opencontainers.image.version",
            receipt.Role == CadDockerRuntimeRole.Geometry
                ? CadDockerRuntimeIdentity.GeometryBundleVersion
                : CadDockerRuntimeIdentity.AssemblyBundleVersion);
        RequireLabel(image, "org.opencontainers.image.base.name",
            receipt.Role == CadDockerRuntimeRole.Geometry
                ? CadDockerRuntimeIdentity.GeometryBaseName
                : CadDockerRuntimeIdentity.AssemblyBaseName);
        RequireLabel(image, "org.opencontainers.image.base.digest",
            receipt.Role == CadDockerRuntimeRole.Geometry
                ? CadDockerRuntimeIdentity.GeometryBaseDigest
                : CadDockerRuntimeIdentity.AssemblyBaseDigest);
    }

    private static void RequireLabel(CadDockerImageInspection image, string name, string expected)
    {
        if (!image.Labels.TryGetValue(name, out var actual) || actual != expected)
            throw new CadContractException("image_label_mismatch", nameof(image));
    }

    private static async ValueTask<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(CadDockerPathSecurity.RequireFile(path, nameof(path)));
        if (file.Length <= 0 || file.Length > maximumBytes)
            throw new CadContractException("evidence_file_size", nameof(path));
        return await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RequireFileHashAsync(
        string path,
        string expectedSha256,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        path = CadDockerPathSecurity.RequireFile(path, nameof(path));
        var file = new FileInfo(path);
        if (expectedLength is not null && file.Length != expectedLength.Value)
            throw new CadContractException("file_length_mismatch", nameof(path));
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1_048_576,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!digest.Equals(expectedSha256, StringComparison.Ordinal))
            throw new CadContractException("file_hash_mismatch", nameof(path));
    }
}

internal sealed class CadParsedDockerReceipt
{
    internal required string PolicySha256 { get; init; }
    internal required DateTimeOffset CreatedAtUtc { get; init; }
    internal required CadDockerBundleReceipt Geometry { get; init; }
    internal required CadDockerBundleReceipt Assembly { get; init; }
}

internal static class CadDockerEvidenceParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static CadDockerRuntimePolicy ParsePolicy(ReadOnlySpan<byte> bytes, string policySha256)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = Object(document.RootElement, "policy");
        ExactProperties(root, "policy",
            "schema", "platform", "workspaceMount", "runtimeUser", "network", "readOnlyRoot",
            "dropCapabilities", "securityOptions", "pidsLimit", "memoryBytes", "nanoCpus",
            "stopTimeoutSeconds", "tmpfs", "forbiddenMounts", "environmentAllowlist", "geometry", "assembly");
        RequireString(root, "schema", CadDockerRuntimeIdentity.PolicySchema);
        RequireString(root, "platform", CadDockerRuntimeIdentity.Platform);
        RequireString(root, "workspaceMount", CadDockerRuntimeIdentity.WorkspaceMount);
        RequireString(root, "runtimeUser", CadDockerRuntimeIdentity.RuntimeUser);
        RequireString(root, "network", "none");
        RequireBoolean(root, "readOnlyRoot", true);
        RequireSequence(root, "dropCapabilities", ["ALL"]);
        RequireSequence(root, "securityOptions", ["no-new-privileges:true"]);
        var pids = RequireInt32(root, "pidsLimit", 64);
        var memory = RequireInt64(root, "memoryBytes", 2_147_483_648L);
        var cpus = RequireInt64(root, "nanoCpus", 2_000_000_000L);
        var stop = RequireInt32(root, "stopTimeoutSeconds", 10);
        var tmpfs = RequireSequence(root, "tmpfs",
        [
            "/session:rw,nosuid,nodev,noexec,size=268435456,mode=0700,uid=65532,gid=65532",
            "/tmp:rw,nosuid,nodev,noexec,size=268435456,mode=0700,uid=65532,gid=65532",
        ]);
        RequireSequence(root, "forbiddenMounts",
        [
            "/var/run/docker.sock", "/run/docker.sock", "/root", "/home", "/mnt/c/Users",
        ]);
        var environment = RequireSequence(root, "environmentAllowlist",
            ["PHOTON_CAD_SESSION_ID", "PHOTON_CAD_PROJECT_ID"]);
        var geometry = ParsePolicyBundle(root.GetProperty("geometry"), "geometry",
            CadDockerRuntimeIdentity.GeometryTag, CadDockerRuntimeIdentity.GeometryEntrypoint);
        var assembly = ParsePolicyBundle(root.GetProperty("assembly"), "assembly",
            CadDockerRuntimeIdentity.AssemblyTag, CadDockerRuntimeIdentity.AssemblyEntrypoint);
        return new CadDockerRuntimePolicy(
            ContractGuards.Sha256(policySha256, nameof(policySha256)),
            pids,
            memory,
            cpus,
            stop,
            tmpfs,
            environment,
            geometry,
            assembly);
    }

    internal static CadParsedDockerReceipt ParseReceipt(ReadOnlySpan<byte> bytes, string receiptDirectory)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = Object(document.RootElement, "receipt");
        ExactProperties(root, "receipt", "schema", "createdAtUtc", "policySha256", "geometrySource",
            "assemblySource", "bundles");
        RequireString(root, "schema", CadDockerRuntimeIdentity.ReceiptSchema);
        var createdAtUtc = CadCommercialWireValue.ParseUtcInstant(String(root, "createdAtUtc"), "createdAtUtc");
        var policySha256 = ContractGuards.Sha256(String(root, "policySha256"), "policySha256");
        var geometrySource = ParseSource(root.GetProperty("geometrySource"), "geometrySource",
            CadDockerRuntimeIdentity.GeometryRepository,
            CadDockerRuntimeIdentity.GeometryRevision,
            CadDockerRuntimeIdentity.GeometrySourceArchiveSha256);
        var assemblySource = ParseAssemblySource(root.GetProperty("assemblySource"),
            CadDockerRuntimeIdentity.AssemblyRepository,
            CadDockerRuntimeIdentity.AssemblyRevision,
            CadDockerRuntimeIdentity.AssemblySourceArchiveSha256);
        var bundles = Array(root.GetProperty("bundles"), "bundles", 2, 2)
            .Select(bundle => ParseBundle(bundle, receiptDirectory, geometrySource, assemblySource)).ToArray();
        if (bundles.Select(bundle => bundle.Role).Distinct().Count() != 2)
            throw new CadContractException("bundle_roles_required", "bundles");
        return new CadParsedDockerReceipt
        {
            PolicySha256 = policySha256,
            CreatedAtUtc = createdAtUtc,
            Geometry = bundles.Single(bundle => bundle.Role == CadDockerRuntimeRole.Geometry),
            Assembly = bundles.Single(bundle => bundle.Role == CadDockerRuntimeRole.Assembly),
        };
    }

    internal static string ImageId(string value, string field)
    {
        var normalized = ContractGuards.RequiredText(value, field, 71).ToLowerInvariant();
        if (!normalized.StartsWith("sha256:", StringComparison.Ordinal) ||
            normalized.Length != 71 || normalized.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new CadContractException("invalid_image_id", field);
        return normalized;
    }

    internal static CadDockerImageInspection ParseImageInspection(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 1_048_576)
            throw new CadContractException("image_inspection_size", nameof(bytes));
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = Object(document.RootElement, "imageInspection");
        var config = Object(root.GetProperty("Config"), "Config");
        var labelsElement = Object(config.GetProperty("Labels"), "Labels");
        var labels = labelsElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : throw new CadContractException("invalid_image_label", "Labels"),
            StringComparer.Ordinal);
        IReadOnlyList<string> volumes;
        if (!config.TryGetProperty("Volumes", out var volumesElement) || volumesElement.ValueKind == JsonValueKind.Null)
        {
            volumes = System.Array.Empty<string>();
        }
        else if (volumesElement.ValueKind == JsonValueKind.Object)
        {
            volumes = System.Array.AsReadOnly(volumesElement.EnumerateObject().Select(property => property.Name).ToArray());
        }
        else
        {
            throw new CadContractException("invalid_image_volume", "Volumes");
        }
        return new CadDockerImageInspection(
            String(root, "Id"),
            Array(root.GetProperty("RepoTags"), "RepoTags", 1, 64).Select(StringValue),
            String(root, "Os"),
            String(root, "Architecture"),
            String(config, "User"),
            String(config, "WorkingDir"),
            Array(config.GetProperty("Entrypoint"), "Entrypoint", 1, 8).Select(StringValue),
            Array(config.GetProperty("Env"), "Env", 1, 64).Select(StringValue),
            volumes,
            labels);
    }

    private static (string Repository, string Revision) ParseSource(
        JsonElement element,
        string field,
        string repository,
        string revision,
        string archiveSha256)
    {
        var source = Object(element, field);
        ExactProperties(source, field, "repository", "revision", "archiveSha256");
        RequireString(source, "repository", repository);
        RequireString(source, "revision", revision);
        RequireString(source, "archiveSha256", archiveSha256);
        return (repository, revision);
    }

    private static (string Repository, string Revision) ParseAssemblySource(
        JsonElement element,
        string repository,
        string revision,
        string archiveSha256)
    {
        var source = Object(element, "assemblySource");
        ExactProperties(source, "assemblySource", "repository", "revision", "archiveSha256", "runtimeAdaptation");
        RequireString(source, "repository", repository);
        RequireString(source, "revision", revision);
        RequireString(source, "archiveSha256", archiveSha256);
        var adaptation = Object(source.GetProperty("runtimeAdaptation"), "runtimeAdaptation");
        ExactProperties(adaptation, "runtimeAdaptation", "kind", "patch", "patchSha256", "target", "patchedTargetSha256");
        RequireString(adaptation, "kind", CadDockerRuntimeIdentity.AssemblyAdaptationKind);
        RequireString(adaptation, "patch", CadDockerRuntimeIdentity.AssemblyAdaptationPatch);
        RequireString(adaptation, "patchSha256", CadDockerRuntimeIdentity.AssemblyAdaptationPatchSha256);
        RequireString(adaptation, "target", CadDockerRuntimeIdentity.AssemblyAdaptationTarget);
        RequireString(adaptation, "patchedTargetSha256", CadDockerRuntimeIdentity.AssemblyAdaptationTargetSha256);
        return (repository, revision);
    }

    private static CadDockerBundleReceipt ParseBundle(
        JsonElement element,
        string receiptDirectory,
        (string Repository, string Revision) geometrySource,
        (string Repository, string Revision) assemblySource)
    {
        var bundle = Object(element, "bundle");
        ExactProperties(bundle, "bundle", "role", "tag", "imageId", "platform", "baseImage", "archive", "archiveSha256", "archiveByteLength");
        var roleName = String(bundle, "role");
        var role = roleName switch
        {
            "geometry" => CadDockerRuntimeRole.Geometry,
            "assembly" => CadDockerRuntimeRole.Assembly,
            _ => throw new CadContractException("unsupported_bundle_role", "role"),
        };
        var tag = String(bundle, "tag");
        var expectedTag = role == CadDockerRuntimeRole.Geometry
            ? CadDockerRuntimeIdentity.GeometryTag
            : CadDockerRuntimeIdentity.AssemblyTag;
        if (tag != expectedTag) throw new CadContractException("bundle_tag_mismatch", "tag");
        RequireString(bundle, "platform", CadDockerRuntimeIdentity.Platform);
        var baseImage = String(bundle, "baseImage");
        var expectedBaseImage = role == CadDockerRuntimeRole.Geometry
            ? CadDockerRuntimeIdentity.GeometryBaseImage
            : CadDockerRuntimeIdentity.AssemblyBaseImage;
        if (baseImage != expectedBaseImage)
            throw new CadContractException("bundle_base_image_mismatch", "baseImage");
        var archive = new CadRelativePath(String(bundle, "archive"));
        if (archive.Segments.Count != 1)
            throw new CadContractException("archive_basename_required", "archive");
        var archivePath = CadDockerPathSecurity.RequireFile(
            Path.Combine(receiptDirectory, archive.Value),
            "archive");
        var source = role == CadDockerRuntimeRole.Geometry ? geometrySource : assemblySource;
        return new CadDockerBundleReceipt(
            role,
            tag,
            ImageId(String(bundle, "imageId"), "imageId"),
            baseImage,
            archivePath,
            ContractGuards.Sha256(String(bundle, "archiveSha256"), "archiveSha256"),
            RequirePositiveInt64(bundle, "archiveByteLength"),
            source.Revision,
            source.Repository);
    }

    private static IReadOnlyList<string> ParsePolicyBundle(
        JsonElement element,
        string field,
        string tag,
        IReadOnlyList<string> entrypoint)
    {
        var bundle = Object(element, field);
        ExactProperties(bundle, field, "tag", "entrypoint");
        RequireString(bundle, "tag", tag);
        return RequireSequence(bundle, "entrypoint", entrypoint);
    }

    private static JsonElement Object(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new CadContractException("object_required", field);
        return element;
    }

    private static IReadOnlyList<JsonElement> Array(JsonElement element, string field, int minimum, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new CadContractException("array_required", field);
        var values = element.EnumerateArray().ToArray();
        if (values.Length < minimum || values.Length > maximum)
            throw new CadContractException("invalid_collection_size", field);
        return System.Array.AsReadOnly(values);
    }

    private static void ExactProperties(JsonElement element, string field, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Except(expected, StringComparer.Ordinal).Any())
            throw new CadContractException("unexpected_json_property", field);
    }

    private static string String(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new CadContractException("string_required", name);
        try
        {
            return StrictUtf8.GetString(StrictUtf8.GetBytes(value.GetString()!));
        }
        catch (EncoderFallbackException)
        {
            throw new CadContractException("invalid_unicode", name);
        }
    }

    private static string StringValue(JsonElement element) => element.ValueKind == JsonValueKind.String
        ? element.GetString()!
        : throw new CadContractException("string_required", "arrayItem");

    private static void RequireString(JsonElement element, string name, string expected)
    {
        if (String(element, name) != expected)
            throw new CadContractException("evidence_value_mismatch", name);
    }

    private static void RequireBoolean(JsonElement element, string name, bool expected)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not JsonValueKind.True and not JsonValueKind.False || value.GetBoolean() != expected)
            throw new CadContractException("evidence_value_mismatch", name);
    }

    private static int RequireInt32(JsonElement element, string name, int expected)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var actual) || actual != expected)
            throw new CadContractException("evidence_value_mismatch", name);
        return actual;
    }

    private static long RequireInt64(JsonElement element, string name, long expected)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var actual) || actual != expected)
            throw new CadContractException("evidence_value_mismatch", name);
        return actual;
    }

    private static long RequirePositiveInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var actual) || actual <= 0)
            throw new CadContractException("positive_integer_required", name);
        return actual;
    }

    private static IReadOnlyList<string> RequireSequence(
        JsonElement element,
        string name,
        IReadOnlyList<string> expected)
    {
        if (!element.TryGetProperty(name, out var value))
            throw new CadContractException("required", name);
        var actual = Array(value, name, expected.Count, expected.Count).Select(StringValue).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new CadContractException("evidence_value_mismatch", name);
        return System.Array.AsReadOnly(actual);
    }
}

internal static class CadDockerPathSecurity
{
    internal static string RequireFile(string value, string field) => Require(value, field, requireDirectory: false);
    internal static string RequireWorkspace(string value, string field)
    {
        var path = Require(value, field, requireDirectory: true);
        if (path.Contains(',', StringComparison.Ordinal) || path.Any(char.IsControl))
            throw new CadContractException("unsafe_mount_path", field);
        var root = Path.GetPathRoot(path);
        if (root is null || path.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("broad_workspace_rejected", field);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) &&
            path.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile)), StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("broad_workspace_rejected", field);
        return path;
    }

    private static string Require(string value, string field, bool requireDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || IsDeviceOrNetwork(value))
            throw new CadContractException("absolute_local_path_required", field);
        string path;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CadContractException("absolute_local_path_required", field);
        }
        var root = Path.GetPathRoot(path);
        if (root is null || path.AsSpan(root.Length).IndexOf(':') >= 0)
            throw new CadContractException("alternate_data_stream_rejected", field);
        if (requireDirectory ? !Directory.Exists(path) : !File.Exists(path))
            throw new CadContractException(requireDirectory ? "directory_missing" : "file_missing", field);
        RejectReparseAncestors(path, field);
        return path;
    }

    private static void RejectReparseAncestors(string path, string field)
    {
        var cursor = path;
        while (!string.IsNullOrEmpty(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new CadContractException("reparse_point_rejected", field);
            var parent = Path.GetDirectoryName(cursor);
            if (parent is null || parent.Equals(cursor, StringComparison.OrdinalIgnoreCase)) break;
            cursor = parent;
        }
    }

    private static bool IsDeviceOrNetwork(string value) =>
        value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) ||
        value.StartsWith("\\?\\", StringComparison.Ordinal) || value.StartsWith("\\.\\", StringComparison.Ordinal) ||
        value.StartsWith("\\??\\", StringComparison.Ordinal);
}
