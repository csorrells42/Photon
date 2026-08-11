using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using HermesDeveloperServices;

namespace HermesDotNetDebugger;

internal sealed class NetCoreDbgProcessTransportFactory : INetCoreDbgTransportFactory
{
    public async ValueTask<IOwnedDapMessageTransport> StartAsync(
        NetCoreDbgInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var status = await installation.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsAvailable)
        {
            throw new DotNetDebuggerUnavailableException(status.SafeMessage);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return NetCoreDbgProcessTransport.Start(installation);
    }
}

/// <summary>
/// Owns exactly one fixed NetCoreDbg stdio child. It never invokes a shell, opens a listener,
/// discovers an executable, accepts caller arguments, or terminates a process by name.
/// </summary>
internal sealed class NetCoreDbgProcessTransport : IOwnedDapMessageTransport
{
    internal const int MaximumPayloadBytes = 1024 * 1024;
    internal const int MaximumStandardErrorCharacters = 32 * 1024;
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _standardErrorPump;
    private readonly object _standardErrorGate = new();
    private readonly StringBuilder _standardError = new();
    private long _droppedStandardErrorCharacters;
    private int _readerClaimed;
    private int _disposed;

    private NetCoreDbgProcessTransport(Process process)
    {
        _process = process;
        _standardErrorPump = PumpStandardErrorAsync(_lifetime.Token);
    }

    public int? ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public string StandardError
    {
        get
        {
            lock (_standardErrorGate) return _standardError.ToString();
        }
    }

    public long DroppedStandardErrorCharacters => Interlocked.Read(ref _droppedStandardErrorCharacters);

    internal static NetCoreDbgProcessTransport Start(NetCoreDbgInstallation installation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.ExecutablePath,
            WorkingDirectory = installation.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment.Clear();
        startInfo.ArgumentList.Add(NetCoreDbgProvisioning.FixedAdapterArgument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new DotNetDebuggerOperationException("The fixed .NET debug adapter did not start.");
            }

            return new NetCoreDbgProcessTransport(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(DapRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (HasExited)
        {
            throw new DapSessionException("The .NET debug adapter has exited.");
        }

        var frame = DapMessageCodec.EncodeFrame(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(frame, linked.Token).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new DapSessionException("The .NET debug adapter input is unavailable.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<DapIncomingMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _readerClaimed, 1) != 0)
        {
            throw new InvalidOperationException("The .NET debug adapter output already has a reader.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var decoder = new DapFrameDecoder(
            DapFrameDecoder.DefaultMaximumHeaderBytes,
            MaximumPayloadBytes);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int count;
            try
            {
                count = await _process.StandardOutput.BaseStream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                if (linked.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                {
                    yield break;
                }

                throw new DapSessionException("The .NET debug adapter output failed.");
            }

            if (count == 0)
            {
                yield break;
            }

            foreach (var payload in decoder.Append(buffer.AsSpan(0, count)))
            {
                yield return DapMessageCodec.DecodeIncoming(payload);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        try { _process.StandardInput.Close(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException) { }

        if (!HasExited)
        {
            using var wait = new CancellationTokenSource(GracefulExitTimeout);
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }

        try { await _process.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        try { await _standardErrorPump.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }

        _process.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task PumpStandardErrorAsync(CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (true)
        {
            int count;
            try
            {
                count = await _process.StandardError.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            if (count == 0)
            {
                return;
            }

            lock (_standardErrorGate)
            {
                var retained = Math.Min(count, MaximumStandardErrorCharacters - _standardError.Length);
                if (retained > 0)
                {
                    _standardError.Append(buffer, 0, retained);
                }

                if (retained < count)
                {
                    Interlocked.Add(ref _droppedStandardErrorCharacters, count - retained);
                }
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
