using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HermesDeveloperServices;

public sealed record PinnedToolchainExecutable(
    string LogicalName,
    string RelativePath,
    string Sha256);

public sealed record PinnedToolchainFile(
    string RelativePath,
    string Sha256,
    long Length);

public sealed record PinnedToolchainReceipt(
    int ReceiptVersion,
    string ToolchainId,
    IReadOnlyList<PinnedToolchainExecutable> Executables,
    IReadOnlyList<PinnedToolchainFile>? Files = null);

internal sealed record ValidatedPinnedExecutable(
    string LogicalName,
    string Path,
    string Sha256);

internal sealed record ValidatedPinnedToolchain(
    string ToolchainId,
    string Root,
    string ReceiptPath,
    IReadOnlyDictionary<string, ValidatedPinnedExecutable> Executables,
    IReadOnlyDictionary<string, PinnedToolchainFile>? Files = null);

/// <summary>
/// Holds read-denying handles for every staged package file. The staged tree is private to one
/// operation and is removed only after all owned processes have stopped.
/// </summary>
internal sealed class PinnedToolchainPackageLease : IAsyncDisposable
{
    private readonly IReadOnlyList<FileStream> _locks;
    private bool _disposed;

    internal PinnedToolchainPackageLease(
        string workbenchRoot,
        ValidatedPinnedToolchain source,
        ValidatedPinnedToolchain staged,
        IReadOnlyList<FileStream> locks)
    {
        WorkbenchRoot = workbenchRoot;
        Source = source;
        Staged = staged;
        _locks = locks;
    }

    public string WorkbenchRoot { get; }
    public ValidatedPinnedToolchain Source { get; }
    public ValidatedPinnedToolchain Staged { get; }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        foreach (var stream in _locks) stream.Dispose();
        DeletePrivateStage(WorkbenchRoot, Staged.Root);
        return ValueTask.CompletedTask;
    }

    private static void DeletePrivateStage(string workbenchRoot, string stageRoot)
    {
        try
        {
            var relative = Path.GetRelativePath(workbenchRoot, stageRoot);
            if (!relative.StartsWith(".hermes-toolchain-stage-", StringComparison.Ordinal)
                || relative.Contains(Path.DirectorySeparatorChar)
                || relative.Contains(Path.AltDirectorySeparatorChar)
                || !Directory.Exists(stageRoot)
                || (File.GetAttributes(stageRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
            Directory.Delete(stageRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A bounded operation must not retry with a broader or stronger deletion primitive.
        }
    }
}

/// <summary>Loads bounded receipts and creates authenticated, immutable-per-operation package stages.</summary>
internal static class PinnedToolchainReceiptLoader
{
    internal const int CurrentReceiptVersion = 1;
    private const int MaximumReceiptBytes = 64 * 1024;
    private const long MaximumExecutableBytes = 512L * 1024 * 1024;
    private const int CopyBufferBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Compatibility loader used by existing providers. Security-sensitive executions should use
    /// StageAsync immediately before each process launch.
    /// </summary>
    public static async Task<ValidatedPinnedToolchain> LoadAsync(
        string workbenchRoot,
        string toolchainRelativePath,
        string receiptFileName,
        string expectedToolchainId,
        IReadOnlyCollection<string> requiredLogicalNames,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(
            workbenchRoot,
            toolchainRelativePath,
            receiptFileName,
            expectedToolchainId,
            cancellationToken).ConfigureAwait(false);
        var executables = await ValidateExecutablesAsync(
            context.Root,
            context.Receipt,
            requiredLogicalNames,
            cancellationToken).ConfigureAwait(false);
        return new(expectedToolchainId, context.Root, context.ReceiptPath, executables);
    }

    /// <summary>
    /// Reloads the receipt, verifies the complete package closure, copies it to a fresh private
    /// Workbench-owned tree, re-verifies the copies, and pins every staged file against mutation.
    /// </summary>
    public static async Task<PinnedToolchainPackageLease> StageAsync(
        string workbenchRoot,
        string toolchainRelativePath,
        string receiptFileName,
        string expectedToolchainId,
        IReadOnlyCollection<string> requiredLogicalNames,
        CancellationToken cancellationToken,
        TrustedPackageEnumerationBounds? bounds = null)
    {
        var context = await LoadContextAsync(
            workbenchRoot,
            toolchainRelativePath,
            receiptFileName,
            expectedToolchainId,
            cancellationToken).ConfigureAwait(false);
        var sourceExecutables = await ValidateExecutablesAsync(
            context.Root,
            context.Receipt,
            requiredLogicalNames,
            cancellationToken).ConfigureAwait(false);
        var manifest = ValidateManifest(context.Root, context.ReceiptPath, context.Receipt, bounds ?? new());
        LinkExecutablesToManifest(context.Root, sourceExecutables, manifest);
        var source = new ValidatedPinnedToolchain(
            expectedToolchainId,
            context.Root,
            context.ReceiptPath,
            sourceExecutables,
            manifest);

        var stageRoot = TrustedToolchainPathPolicy.CreateOwnedTemporaryDirectory(
            context.WorkbenchRoot,
            context.WorkbenchRoot,
            ".hermes-toolchain-stage-",
            "toolchain_stage");
        var locks = new List<FileStream>();
        try
        {
            var stagedFiles = new Dictionary<string, PinnedToolchainFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = TrustedToolchainPathPolicy.ResolveExistingFile(context.Root, entry.RelativePath, "package_file");
                var destinationPath = Path.GetFullPath(Path.Combine(stageRoot, entry.RelativePath));
                var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
                Directory.CreateDirectory(destinationDirectory);
                await CopyAndVerifyAsync(sourcePath, destinationPath, entry, cancellationToken).ConfigureAwait(false);
                var pin = await OpenVerifyAndPinAsync(destinationPath, entry, cancellationToken).ConfigureAwait(false);
                locks.Add(pin);
                stagedFiles.Add(entry.RelativePath, entry);
            }

            var stagedExecutables = new Dictionary<string, ValidatedPinnedExecutable>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceExecutable in sourceExecutables.Values)
            {
                var relative = NormalizeRelative(context.Root, sourceExecutable.Path);
                var stagedPath = Path.GetFullPath(Path.Combine(stageRoot, relative));
                stagedExecutables.Add(
                    sourceExecutable.LogicalName,
                    new(sourceExecutable.LogicalName, stagedPath, sourceExecutable.Sha256));
            }
            var staged = new ValidatedPinnedToolchain(
                expectedToolchainId,
                stageRoot,
                context.ReceiptPath,
                stagedExecutables,
                stagedFiles);
            return new(context.WorkbenchRoot, source, staged, locks);
        }
        catch
        {
            foreach (var stream in locks) stream.Dispose();
            await new PinnedToolchainPackageLease(
                context.WorkbenchRoot,
                source,
                new(expectedToolchainId, stageRoot, context.ReceiptPath,
                    new Dictionary<string, ValidatedPinnedExecutable>(),
                    new Dictionary<string, PinnedToolchainFile>()),
                []).DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ReceiptContext> LoadContextAsync(
        string workbenchRoot,
        string toolchainRelativePath,
        string receiptFileName,
        string expectedToolchainId,
        CancellationToken cancellationToken)
    {
        var trustedWorkbenchRoot = TrustedToolchainPathPolicy.RequireRoot(workbenchRoot, "workbench");
        var toolchainRoot = TrustedToolchainPathPolicy.ResolveExistingTarget(
            trustedWorkbenchRoot,
            toolchainRelativePath,
            "toolchain");
        if (!Directory.Exists(toolchainRoot))
            throw new TrustedToolchainValidationException("toolchain_root_invalid", "The pinned toolchain root is not a directory.");
        var receiptPath = TrustedToolchainPathPolicy.ResolveExistingFile(toolchainRoot, receiptFileName, "receipt");
        var receipt = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        if (receipt.ReceiptVersion != CurrentReceiptVersion
            || !string.Equals(receipt.ToolchainId, expectedToolchainId, StringComparison.Ordinal))
        {
            throw new TrustedToolchainValidationException("receipt_identity_mismatch", "The toolchain receipt identity is incompatible.");
        }
        return new(trustedWorkbenchRoot, toolchainRoot, receiptPath, receipt);
    }

    private static async Task<IReadOnlyDictionary<string, ValidatedPinnedExecutable>> ValidateExecutablesAsync(
        string root,
        PinnedToolchainReceipt receipt,
        IReadOnlyCollection<string> requiredLogicalNames,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, PinnedToolchainExecutable>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in receipt.Executables ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.LogicalName) || !entries.TryAdd(entry.LogicalName, entry))
                throw new TrustedToolchainValidationException("receipt_duplicate_executable", "The toolchain receipt contains an invalid executable identity.");
        }
        var validated = new Dictionary<string, ValidatedPinnedExecutable>(StringComparer.OrdinalIgnoreCase);
        foreach (var logicalName in requiredLogicalNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(logicalName, out var entry))
                throw new TrustedToolchainValidationException("receipt_executable_missing", "The toolchain receipt is incomplete.");
            ValidateHash(entry.Sha256);
            var path = TrustedToolchainPathPolicy.ResolveExistingFile(root, entry.RelativePath, "executable");
            ValidateExecutableFileName(logicalName, path);
            var actualHash = await HashBoundedAsync(path, MaximumExecutableBytes, cancellationToken).ConfigureAwait(false);
            if (!HashesEqual(entry.Sha256, actualHash))
                throw new TrustedToolchainValidationException("executable_hash_mismatch", "A pinned toolchain executable failed its integrity check.");
            validated.Add(logicalName, new(logicalName, path, actualHash));
        }
        return validated;
    }

    private static IReadOnlyDictionary<string, PinnedToolchainFile> ValidateManifest(
        string root,
        string receiptPath,
        PinnedToolchainReceipt receipt,
        TrustedPackageEnumerationBounds bounds)
    {
        if (receipt.Files is not { Count: > 0 })
            throw new TrustedToolchainValidationException("package_manifest_missing", "Trusted execution requires a complete pinned package manifest.");
        var manifest = new Dictionary<string, PinnedToolchainFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in receipt.Files)
        {
            ValidateHash(entry.Sha256);
            if (entry.Length is < 0 || entry.Length > bounds.MaximumFileBytes)
                throw new TrustedToolchainValidationException("package_manifest_invalid", "A package manifest length is invalid.");
            var path = TrustedToolchainPathPolicy.ResolveExistingFile(root, entry.RelativePath, "package_file");
            var relative = NormalizeRelative(root, path);
            if (!manifest.TryAdd(relative, entry with { RelativePath = relative }))
                throw new TrustedToolchainValidationException("package_manifest_duplicate", "The package manifest contains a duplicate path.");
        }

        var actual = TrustedToolchainPathPolicy.EnumeratePackageFiles(root, receiptPath, bounds)
            .Select(path => NormalizeRelative(root, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actual.Any(path => !manifest.ContainsKey(path)))
            throw new TrustedToolchainValidationException("package_unpinned_file", "The toolchain package contains an unpinned helper or resource.");
        if (manifest.Keys.Any(path => !actual.Contains(path)))
            throw new TrustedToolchainValidationException("package_manifest_incomplete", "A pinned package file is missing.");
        return manifest;
    }

    private static void LinkExecutablesToManifest(
        string root,
        IReadOnlyDictionary<string, ValidatedPinnedExecutable> executables,
        IReadOnlyDictionary<string, PinnedToolchainFile> manifest)
    {
        foreach (var executable in executables.Values)
        {
            var relative = NormalizeRelative(root, executable.Path);
            if (!manifest.TryGetValue(relative, out var file) || !HashesEqual(file.Sha256, executable.Sha256))
                throw new TrustedToolchainValidationException("package_executable_unpinned", "A compiler executable is not pinned by the package manifest.");
        }
    }

    private static async Task<PinnedToolchainReceipt> ReadReceiptAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[MaximumReceiptBytes + 1];
            var total = 0;
            while (total < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(total, Math.Min(4096, bytes.Length - total)), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            if (total is 0 or > MaximumReceiptBytes || stream.ReadByte() != -1)
                throw new TrustedToolchainValidationException("receipt_size_invalid", "The toolchain receipt size is invalid.");
            return JsonSerializer.Deserialize<PinnedToolchainReceipt>(bytes.AsSpan(0, total), JsonOptions)
                ?? throw new TrustedToolchainValidationException("receipt_invalid", "The toolchain receipt is invalid.");
        }
        catch (JsonException)
        {
            throw new TrustedToolchainValidationException("receipt_invalid", "The toolchain receipt is invalid.");
        }
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        PinnedToolchainFile expected,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expected.Length)
            throw new TrustedToolchainValidationException("package_length_mismatch", "A pinned package file changed length.");
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > expected.Length)
                throw new TrustedToolchainValidationException("package_length_mismatch", "A pinned package file exceeded its declared length.");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (total != expected.Length || !HashesEqual(expected.Sha256, Convert.ToHexStringLower(hash.GetHashAndReset())))
            throw new TrustedToolchainValidationException("package_hash_mismatch", "A pinned package file failed staging integrity validation.");
    }

    private static async Task<FileStream> OpenVerifyAndPinAsync(
        string path,
        PinnedToolchainFile expected,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var actual = await HashStreamBoundedAsync(stream, expected.Length, cancellationToken).ConfigureAwait(false);
            if (!HashesEqual(expected.Sha256, actual))
                throw new TrustedToolchainValidationException("staged_hash_mismatch", "A staged package file failed its final integrity check.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<string> HashBoundedAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new TrustedToolchainValidationException("executable_size_invalid", "A pinned executable size is invalid.");
        return await HashStreamBoundedAsync(stream, stream.Length, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashStreamBoundedAsync(Stream stream, long expectedLength, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > expectedLength)
                throw new TrustedToolchainValidationException("bounded_read_exceeded", "A pinned file exceeded its bounded length.");
            hash.AppendData(buffer, 0, read);
        }
        if (total != expectedLength)
            throw new TrustedToolchainValidationException("bounded_read_incomplete", "A pinned file changed during validation.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool HashesEqual(string expected, string actual)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)); }
        catch (FormatException) { return false; }
    }

    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new TrustedToolchainValidationException("receipt_hash_invalid", "The receipt contains an invalid executable hash.");
    }

    private static void ValidateExecutableFileName(string logicalName, string path)
    {
        var extension = Path.GetExtension(path);
        if (OperatingSystem.IsWindows() && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new TrustedToolchainValidationException("executable_type_invalid", "Pinned Windows tools must be native executable files.");
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new TrustedToolchainValidationException("executable_type_invalid", "Shell and script toolchain entries are not accepted.");
        if (logicalName.IndexOfAny(['/', '\\', ':', '@']) >= 0 || logicalName.StartsWith('-'))
            throw new TrustedToolchainValidationException("executable_identity_invalid", "The executable logical identity is invalid.");
    }

    private sealed record ReceiptContext(
        string WorkbenchRoot,
        string Root,
        string ReceiptPath,
        PinnedToolchainReceipt Receipt);
}
