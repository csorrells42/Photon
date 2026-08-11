using System.Security.Cryptography;
using System.Text.Json;

namespace HermesDotNetDebugger;

/// <summary>
/// Immutable provenance for the only debugger artifact this provider accepts. The installer, not
/// this library, acquires and extracts the archive after verifying <see cref="ArchiveSha256"/>.
/// </summary>
public static class NetCoreDbgProvisioning
{
    public const int ReceiptSchemaVersion = 1;
    public const string Component = "Samsung/netcoredbg";
    public const string Version = "3.1.3-1062";
    public const string SourceCommit = "8b8b22200fecdb1aec5f47af63215462d8c79a4b";
    public const string ArchiveName = "netcoredbg-win64.zip";
    public const long ArchiveSizeBytes = 3_475_639;
    public const string ArchiveSha256 = "c67ae052e0bcb9ce37000f261e2d397a0d5b6615cafe30c868239a78598dfb37";
    public const string FixedAdapterArgument = "--interpreter=vscode";
    public const string ReceiptFileName = "hermes-provisioning.json";
    public static readonly string RelativeDirectory = Path.Combine("debuggers", "netcoredbg", Version);
    public static readonly string RelativeExecutablePath = Path.Combine(RelativeDirectory, "netcoredbg.exe");
    public static readonly string RelativeReceiptPath = Path.Combine(RelativeDirectory, ReceiptFileName);
}

public sealed record NetCoreDbgProvisioningReceipt(
    int SchemaVersion,
    string Component,
    string Version,
    string SourceCommit,
    string ArchiveName,
    string ArchiveSha256,
    string ExecutableSha256);

public sealed record NetCoreDbgInstallationStatus(
    bool IsAvailable,
    string Code,
    string SafeMessage,
    string? ExecutablePath = null);

internal sealed class NetCoreDbgInstallation
{
    private const int MaximumReceiptBytes = 4 * 1024;
    private readonly string _installRoot;

    public NetCoreDbgInstallation(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        if (!Path.IsPathFullyQualified(installRoot))
        {
            throw new ArgumentException("The application install root must be absolute.", nameof(installRoot));
        }

        _installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        ExecutablePath = Path.GetFullPath(Path.Combine(_installRoot, NetCoreDbgProvisioning.RelativeExecutablePath));
        ReceiptPath = Path.GetFullPath(Path.Combine(_installRoot, NetCoreDbgProvisioning.RelativeReceiptPath));
    }

    public string ExecutablePath { get; }

    public string ReceiptPath { get; }

    public string WorkingDirectory => Path.GetDirectoryName(ExecutablePath)!;

    public async ValueTask<NetCoreDbgInstallationStatus> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_installRoot))
        {
            return Unavailable("install-root-missing", "The Hermes installation root is unavailable.");
        }

        if (!File.Exists(ExecutablePath))
        {
            return Unavailable("adapter-missing", "The pinned .NET debugger is not installed.");
        }

        if (!File.Exists(ReceiptPath))
        {
            return Unavailable("receipt-missing", "The .NET debugger provisioning receipt is missing.");
        }

        if (HasReparsePoint(
                _installRoot,
                Path.Combine(_installRoot, "debuggers"),
                Path.Combine(_installRoot, "debuggers", "netcoredbg"),
                WorkingDirectory,
                ExecutablePath,
                ReceiptPath))
        {
            return Unavailable("reparse-point", "The .NET debugger installation is not a trusted fixed path.");
        }

        try
        {
            var receiptInfo = new FileInfo(ReceiptPath);
            if (receiptInfo.Length <= 0 || receiptInfo.Length > MaximumReceiptBytes)
            {
                return Unavailable("receipt-invalid", "The .NET debugger provisioning receipt is invalid.");
            }

            await using var receiptStream = new FileStream(
                ReceiptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var receipt = await JsonSerializer.DeserializeAsync<NetCoreDbgProvisioningReceipt>(
                receiptStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!MatchesPin(receipt))
            {
                return Unavailable("receipt-mismatch", "The .NET debugger provisioning receipt does not match the approved release.");
            }

            await using var executableStream = new FileStream(
                ExecutablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(executableStream, cancellationToken).ConfigureAwait(false));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(receipt!.ExecutableSha256)))
            {
                return Unavailable("executable-hash-mismatch", "The installed .NET debugger failed its integrity check.");
            }

            return new NetCoreDbgInstallationStatus(
                true,
                "available",
                "The pinned .NET debugger is provisioned.",
                ExecutablePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return Unavailable("provisioning-unreadable", "The .NET debugger installation could not be verified.");
        }
    }

    private static bool MatchesPin(NetCoreDbgProvisioningReceipt? receipt) =>
        receipt is not null
        && receipt.SchemaVersion == NetCoreDbgProvisioning.ReceiptSchemaVersion
        && string.Equals(receipt.Component, NetCoreDbgProvisioning.Component, StringComparison.Ordinal)
        && string.Equals(receipt.Version, NetCoreDbgProvisioning.Version, StringComparison.Ordinal)
        && string.Equals(receipt.SourceCommit, NetCoreDbgProvisioning.SourceCommit, StringComparison.OrdinalIgnoreCase)
        && string.Equals(receipt.ArchiveName, NetCoreDbgProvisioning.ArchiveName, StringComparison.Ordinal)
        && string.Equals(receipt.ArchiveSha256, NetCoreDbgProvisioning.ArchiveSha256, StringComparison.OrdinalIgnoreCase)
        && receipt.ExecutableSha256.Length == 64
        && receipt.ExecutableSha256.All(Uri.IsHexDigit);

    private static bool HasReparsePoint(params string[] paths)
    {
        foreach (var path in paths)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static NetCoreDbgInstallationStatus Unavailable(string code, string message) =>
        new(false, code, message);
}
