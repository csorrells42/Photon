using System.Security.Cryptography;
using System.Text.Json;

namespace HermesDeveloperServices.LanguageTooling.Java;

internal sealed class ReceiptBoundJavaJdtRuntimeAuthority : IJavaJdtRuntimeAuthority
{
    private const string ReceiptSchema = "hermes.java-jdt.toolchain/v1";
    private const string ManifestSchema = "hermes.java-jdt.payload-manifest/v1";
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const int MaximumManifestFiles = 1_024;
    private const long MaximumPayloadBytes = 512L * 1024 * 1024;
    private readonly string _installRoot;
    private readonly SemaphoreSlim _inspectionGate = new(1, 1);
    private ValidatedRuntime? _validated;
    private bool _disposed;

    internal ReceiptBoundJavaJdtRuntimeAuthority(string installRoot)
    {
        _installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        if (!Directory.Exists(_installRoot) || IsReparse(_installRoot))
            throw new ArgumentException("The fixed Java/JDT install root is unavailable.", nameof(installRoot));
    }

    public async ValueTask<JavaJdtRuntimeInspection> InspectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            var runtime = await RequireValidatedAsync(cancellationToken).ConfigureAwait(false);
            return new(true, "verified-pinned-runtime", "The receipt-bound Eclipse JDT LS runtime is available.", runtime.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or JsonException or CryptographicException
            or ArgumentException or InvalidOperationException)
        {
            return new(false, "java-jdt-runtime-invalid", "The receipt-bound Eclipse JDT LS runtime is unavailable.");
        }
    }

    public async ValueTask<JavaJdtLanguageSession> StartSessionAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _ = TrustedToolchainPathPolicy.RequireRoot(workspaceRoot, "java_workspace");
        var runtime = await RequireValidatedAsync(cancellationToken).ConfigureAwait(false);
        var sessionRoot = CreateSessionRoot();
        try
        {
            var sessionConfiguration = Path.Combine(sessionRoot, "configuration");
            Directory.CreateDirectory(sessionConfiguration);
            File.Copy(
                Path.Combine(runtime.ConfigurationRoot, "config.ini"),
                Path.Combine(sessionConfiguration, "config.ini"),
                overwrite: false);
            var arguments = new[]
            {
                "-Declipse.application=org.eclipse.jdt.ls.core.id1",
                "-Dosgi.bundles.defaultStartLevel=4",
                "-Declipse.product=org.eclipse.jdt.ls.core.product",
                "-Dosgi.checkConfiguration=true",
                "-Xms256m",
                "-Xmx1024m",
                "--add-modules=ALL-SYSTEM",
                "--add-opens", "java.base/java.util=ALL-UNNAMED",
                "--add-opens", "java.base/java.lang=ALL-UNNAMED",
                "-jar", runtime.LauncherJar,
                "-configuration", sessionConfiguration,
                "-data", sessionRoot,
            };
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["JAVA_HOME"] = runtime.JavaHome,
                ["HOME"] = sessionRoot,
                ["USERPROFILE"] = sessionRoot,
                ["APPDATA"] = sessionRoot,
                ["LOCALAPPDATA"] = sessionRoot,
                ["TEMP"] = sessionRoot,
                ["TMP"] = sessionRoot,
                ["SystemRoot"] = systemRoot,
            };
            var transport = LspProcessTransport.Start(new LspProcessLaunchOptions(
                runtime.JavaExecutable,
                arguments,
                runtime.JdtRoot,
                environment,
                InheritEnvironment: false,
                MaximumStandardErrorCharacters: 64 * 1024,
                GracefulExitTimeout: TimeSpan.FromSeconds(3)));
            return new JavaJdtLanguageSession(new OwnedJavaJdtTransport(transport, sessionRoot));
        }
        catch
        {
            TryDeleteSessionRoot(sessionRoot);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _inspectionGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<ValidatedRuntime> RequireValidatedAsync(CancellationToken cancellationToken)
    {
        if (_validated is not null) return _validated;
        await _inspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_validated is not null) return _validated;
            var receiptPath = Path.Combine(_installRoot, JavaJdtProvisioning.ReceiptFileName);
            var manifestPath = Path.Combine(_installRoot, "payload-manifest.json");
            var receipt = ReadBoundedJson(receiptPath, 64 * 1024);
            var root = receipt.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty("schemaId").GetString() != ReceiptSchema
                || root.GetProperty("protocolVersion").GetInt32() != 1)
                throw new InvalidDataException("java-jdt-receipt-invalid");
            var version = RequireText(root, "jdtVersion", 128);
            var manifestSha = RequireSha(root, "payloadManifestSha256");
            var launcherRelative = RequireRelativePath(root, "launcherJarRelativePath");
            var launcherSha = RequireSha(root, "launcherJarSha256");
            var javaRelative = RequireRelativePath(root, "javaRelativePath");
            var javaSha = RequireSha(root, "javaSha256");
            using (receipt) { }

            if (!FixedSha256(manifestPath).Equals(manifestSha, StringComparison.Ordinal))
                throw new InvalidDataException("java-jdt-manifest-digest-mismatch");
            using var manifest = ReadBoundedJson(manifestPath, MaximumManifestBytes);
            var manifestRoot = manifest.RootElement;
            if (manifestRoot.ValueKind != JsonValueKind.Object
                || manifestRoot.GetProperty("schemaId").GetString() != ManifestSchema
                || manifestRoot.GetProperty("protocolVersion").GetInt32() != 1
                || !manifestRoot.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() is < 1 or > MaximumManifestFiles)
                throw new InvalidDataException("java-jdt-manifest-invalid");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in files.EnumerateArray())
            {
                var relative = RequireRelativePath(entry, "relativePath");
                var length = entry.GetProperty("length").GetInt64();
                var sha = RequireSha(entry, "sha256");
                if (length < 0 || !seen.Add(relative)) throw new InvalidDataException("java-jdt-manifest-invalid");
                total = checked(total + length);
                if (total > MaximumPayloadBytes) throw new InvalidDataException("java-jdt-payload-too-large");
                var full = ResolveContainedFile(relative);
                var info = new FileInfo(full);
                if (info.Length != length || !FixedSha256(full).Equals(sha, StringComparison.Ordinal))
                    throw new InvalidDataException("java-jdt-payload-mismatch");
            }
            var installed = Directory.EnumerateFiles(_installRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_installRoot, path).Replace('\\', '/'))
                .Where(path => !path.Equals(JavaJdtProvisioning.ReceiptFileName, StringComparison.OrdinalIgnoreCase)
                    && !path.Equals("payload-manifest.json", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!installed.SetEquals(seen)) throw new InvalidDataException("java-jdt-payload-set-mismatch");

            var launcher = ResolveContainedFile(launcherRelative);
            var java = ResolveContainedFile(javaRelative);
            if (!FixedSha256(launcher).Equals(launcherSha, StringComparison.Ordinal)
                || !FixedSha256(java).Equals(javaSha, StringComparison.Ordinal))
                throw new InvalidDataException("java-jdt-entrypoint-mismatch");
            var jdtRoot = Path.Combine(_installRoot, "jdtls");
            var javaHome = Path.Combine(_installRoot, "jre");
            var configurationRoot = Path.Combine(jdtRoot, "config_win");
            if (!Directory.Exists(jdtRoot) || !Directory.Exists(javaHome) || !Directory.Exists(configurationRoot)
                || IsReparse(jdtRoot) || IsReparse(javaHome) || IsReparse(configurationRoot))
                throw new InvalidDataException("java-jdt-layout-invalid");
            _validated = new(version, jdtRoot, javaHome, java, launcher, configurationRoot);
            return _validated;
        }
        finally { _inspectionGate.Release(); }
    }

    private string ResolveContainedFile(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_installRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _installRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || TraversesReparse(full))
            throw new InvalidDataException("java-jdt-path-invalid");
        return full;
    }

    private static JsonDocument ReadBoundedJson(string path, int maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 2 || info.Length > maximumBytes || IsReparse(path))
            throw new InvalidDataException("java-jdt-json-invalid");
        return JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static string RequireText(JsonElement root, string property, int maximumLength)
    {
        var value = root.GetProperty(property).GetString()?.Trim() ?? string.Empty;
        if (value.Length is < 1 || value.Length > maximumLength || value.Contains('\0'))
            throw new InvalidDataException("java-jdt-receipt-invalid");
        return value;
    }

    private static string RequireSha(JsonElement root, string property)
    {
        var value = RequireText(root, property, 64).ToLowerInvariant();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("java-jdt-receipt-invalid");
        return value;
    }

    private static string RequireRelativePath(JsonElement root, string property)
    {
        var value = RequireText(root, property, 1_024).Replace('\\', '/');
        if (Path.IsPathFullyQualified(value) || value.StartsWith('/') || value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("java-jdt-path-invalid");
        return value;
    }

    private static string FixedSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string CreateSessionRoot()
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "developer-services", "java-jdt", "sessions");
        Directory.CreateDirectory(parent);
        if (IsReparse(parent)) throw new IOException("java-jdt-session-root-invalid");
        var session = Path.Combine(parent, "jdt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        Directory.CreateDirectory(session);
        return session;
    }

    private static void TryDeleteSessionRoot(string sessionRoot)
    {
        try
        {
            var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "developer-services", "java-jdt", "sessions");
            var full = Path.GetFullPath(sessionRoot);
            var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "jdt-";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full) && !IsReparse(full))
                Directory.Delete(full, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static bool TraversesReparse(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new InvalidDataException("java-jdt-path-invalid");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparse(current)) return true;
        }
        return false;
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ValidatedRuntime(
        string Version,
        string JdtRoot,
        string JavaHome,
        string JavaExecutable,
        string LauncherJar,
        string ConfigurationRoot);

    private sealed class OwnedJavaJdtTransport(LspProcessTransport inner, string sessionRoot) : ILspMessageTransport
    {
        public ValueTask SendAsync(LspOutgoingMessage message, CancellationToken cancellationToken) =>
            inner.SendAsync(message, cancellationToken);

        public IAsyncEnumerable<LspIncomingMessage> ReadAllAsync(CancellationToken cancellationToken) =>
            inner.ReadAllAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            TryDeleteSessionRoot(sessionRoot);
        }
    }
}
