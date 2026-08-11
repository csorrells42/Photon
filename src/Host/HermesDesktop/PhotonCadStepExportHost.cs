using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using PhotonCadArtifacts;

namespace HermesDesktop;

internal sealed record PhotonCadStepExportReceipt(string ContentDigest, long ByteLength, string DestinationLabel);

internal interface IPhotonCadStepDestinationPicker
{
    ValueTask<string?> PickNewStepPathAsync(string suggestedFileName, CancellationToken cancellationToken = default);
}

internal sealed class PhotonCadStepExportHost : IAsyncDisposable, IPhotonCadArtifactContextAuthority
{
    private const int MaximumStepBytes = 64 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WindowsPhotonCadArtifactDestinationAuthority _destinations;
    private PhotonCadArtifactContext? _current;
    private int _disposed;

    internal PhotonCadStepExportHost(string journalRoot, IPhotonCadStepDestinationPicker? picker = null)
    {
        _destinations = new WindowsPhotonCadArtifactDestinationAuthority(
            new ArtifactPickerAdapter(picker ?? new OwnerBoundStepPicker()), this, journalRoot);
    }

    public PhotonCadArtifactContext? Current() => Volatile.Read(ref _current);

    internal async ValueTask<PhotonCadStepExportReceipt?> ExportAsync(
        string rendererSessionId,
        string cadSessionId,
        string projectId,
        long revision,
        string entityId,
        ReadOnlyMemory<byte> sealedStep,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PhotonCadStepExportHost));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PhotonCadArtifactContext? context = null;
        PhotonCadDestinationDescriptor? destination = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateStep(sealedStep.Span, expectedDigest);
            context = new PhotonCadArtifactContext(rendererSessionId, "photon-step-export", cadSessionId, projectId, revision);
            Volatile.Write(ref _current, context);
            var descriptor = new PhotonCadArtifactDescriptor(
                PhotonCadArtifactHandle.New(), context,
                PhotonCadArtifactContract.StepKind, PhotonCadArtifactContract.StepMediaType,
                PhotonCadArtifactContract.UnspecifiedProfile, expectedDigest, sealedStep.Length,
                entityId + ".step", DateTimeOffset.UtcNow.AddMinutes(10));
            destination = await _destinations.PickAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (destination is null) return null;
            await using var content = new MemoryStream(sealedStep.ToArray(), writable: false);
            var receipt = await _destinations.CommitAsync(
                destination.DestinationHandle, descriptor, context, content, cancellationToken).ConfigureAwait(false);
            return new PhotonCadStepExportReceipt(receipt.ContentDigest, receipt.ByteLength, receipt.DestinationLabel);
        }
        finally
        {
            if (destination is not null)
            {
                try { await _destinations.RevokeAsync(destination.DestinationHandle).ConfigureAwait(false); }
                catch { }
            }
            if (context is not null)
            {
                try { await _destinations.RevokeContextAsync(context).ConfigureAwait(false); }
                catch { }
            }
            Volatile.Write(ref _current, null);
            _gate.Release();
        }
    }

    private static void ValidateStep(ReadOnlySpan<byte> bytes, string expectedDigest)
    {
        if (bytes.Length is <= 0 or > MaximumStepBytes) throw new InvalidDataException("step_length_invalid");
        var actual = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        var left = Encoding.ASCII.GetBytes(actual);
        var right = Encoding.ASCII.GetBytes(expectedDigest.ToLowerInvariant());
        if (left.Length != right.Length || !CryptographicOperations.FixedTimeEquals(left, right))
            throw new InvalidDataException("step_digest_mismatch");
        var text = Encoding.ASCII.GetString(bytes);
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("ISO-10303-21;", StringComparison.Ordinal)
            || !trimmed.Contains("HEADER;", StringComparison.Ordinal)
            || !trimmed.Contains("DATA;", StringComparison.Ordinal)
            || !trimmed.EndsWith("END-ISO-10303-21;", StringComparison.Ordinal)
            || trimmed.Count(character => character == '\0') != 0)
            throw new InvalidDataException("step_part21_invalid");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _destinations.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private sealed class ArtifactPickerAdapter(IPhotonCadStepDestinationPicker inner) : IPhotonCadNativeDestinationPicker
    {
        public ValueTask<string?> PickNewStepPathAsync(string suggestedFileName, CancellationToken cancellationToken = default) =>
            inner.PickNewStepPathAsync(suggestedFileName, cancellationToken);
    }

    private sealed class OwnerBoundStepPicker : IPhotonCadStepDestinationPicker
    {
        public ValueTask<string?> PickNewStepPathAsync(string suggestedFileName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) return ValueTask.FromResult<string?>(null);
            var owner = Application.Current?.MainWindow ?? throw new InvalidOperationException("step_export_owner_unavailable");
            string? Pick()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new WindowInteropHelper(owner).Handle == IntPtr.Zero) throw new InvalidOperationException("step_export_owner_unavailable");
                var dialog = new SaveFileDialog
                {
                    AddExtension = true,
                    DefaultExt = ".step",
                    Filter = "STEP Part 21 (*.step)|*.step",
                    FileName = suggestedFileName,
                    Title = "Export selected Photon CAD part as STEP",
                    CheckPathExists = true,
                    OverwritePrompt = false,
                };
                return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
            }
            return ValueTask.FromResult(owner.Dispatcher.CheckAccess() ? Pick() : owner.Dispatcher.Invoke(Pick));
        }
    }
}
