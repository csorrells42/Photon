using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace HermesDeveloperServices;

/// <summary>
/// Defines one explicitly authorized language-server child process. The executable and working
/// directory must be absolute existing paths; arguments are passed without a command shell.
/// </summary>
public sealed record LspProcessLaunchOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    bool InheritEnvironment = false,
    int MaximumStandardErrorCharacters = 32 * 1024,
    TimeSpan? GracefulExitTimeout = null);

/// <summary>
/// Owns one exact stdio language-server child process. It never discovers executables, opens a
/// listener, invokes a command shell, or terminates a process by name.
/// </summary>
public sealed class LspProcessTransport : ILspMessageTransport
{
    public const int MaximumArguments = 128;
    public const int MaximumArgumentCharacters = 32 * 1024;
    public const int MaximumEnvironmentEntries = 128;

    private static readonly TimeSpan DefaultGracefulExitTimeout = TimeSpan.FromSeconds(2);
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _standardErrorPump;
    private readonly object _standardErrorGate = new();
    private readonly StringBuilder _standardError = new();
    private readonly int _maximumStandardErrorCharacters;
    private readonly TimeSpan _gracefulExitTimeout;
    private long _droppedStandardErrorCharacters;
    private int _readerClaimed;
    private int _disposed;

    private LspProcessTransport(Process process, LspProcessLaunchOptions options)
    {
        _process = process;
        _maximumStandardErrorCharacters = options.MaximumStandardErrorCharacters;
        _gracefulExitTimeout = options.GracefulExitTimeout ?? DefaultGracefulExitTimeout;
        _standardErrorPump = PumpStandardErrorAsync(_lifetime.Token);
    }

    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public long DroppedStandardErrorCharacters => Interlocked.Read(ref _droppedStandardErrorCharacters);

    public string StandardError
    {
        get
        {
            lock (_standardErrorGate) return _standardError.ToString();
        }
    }

    public static LspProcessTransport Start(LspProcessLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var executablePath = ValidateExecutable(options.ExecutablePath);
        var workingDirectory = ValidateWorkingDirectory(options.WorkingDirectory);
        ValidateArguments(options.Arguments);
        ValidateEnvironment(options.Environment);
        if (options.MaximumStandardErrorCharacters < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumStandardErrorCharacters));
        var exitTimeout = options.GracefulExitTimeout ?? DefaultGracefulExitTimeout;
        if (exitTimeout < TimeSpan.Zero || exitTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(options.GracefulExitTimeout));

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!options.InheritEnvironment) startInfo.Environment.Clear();
        foreach (var argument in options.Arguments) startInfo.ArgumentList.Add(argument);
        if (options.Environment is not null)
        {
            foreach (var entry in options.Environment) startInfo.Environment[entry.Key] = entry.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new LspSessionException("The language-server process did not start.");
            return new LspProcessTransport(process, options);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(LspOutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        if (HasExited) throw new LspSessionException("The language-server process has exited.");
        var frame = LspMessageCodec.EncodeFrame(message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(frame, linked.Token).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new LspSessionException("The language-server input stream is unavailable.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<LspIncomingMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _readerClaimed, 1) != 0)
            throw new InvalidOperationException("The language-server output stream already has a reader.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var decoder = new LspFrameDecoder();
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
                if (linked.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) yield break;
                throw new LspSessionException("The language-server output stream failed.");
            }

            if (count == 0) yield break;
            foreach (var payload in decoder.Append(buffer.AsSpan(0, count)))
                yield return LspMessageCodec.DecodeIncoming(payload);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try { _process.StandardInput.Close(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException) { }

        if (!HasExited)
        {
            using var wait = new CancellationTokenSource(_gracefulExitTimeout);
            try { await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
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
            try { count = await _process.StandardError.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { return; }
            if (count == 0) return;

            lock (_standardErrorGate)
            {
                var retained = Math.Min(count, _maximumStandardErrorCharacters - _standardError.Length);
                if (retained > 0) _standardError.Append(buffer, 0, retained);
                if (retained < count) Interlocked.Add(ref _droppedStandardErrorCharacters, count - retained);
            }
        }
    }

    private static string ValidateExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The language-server executable path must be absolute.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The language-server executable does not exist.", fullPath);
        return fullPath;
    }

    private static string ValidateWorkingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The language-server working directory must be absolute.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("The language-server working directory does not exist.");
        return fullPath;
    }

    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > MaximumArguments) throw new ArgumentOutOfRangeException(nameof(arguments));
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Length > MaximumArgumentCharacters || argument.Contains('\0'))
                throw new ArgumentException("A language-server argument is invalid.", nameof(arguments));
        }
    }

    private static void ValidateEnvironment(IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null) return;
        if (environment.Count > MaximumEnvironmentEntries) throw new ArgumentOutOfRangeException(nameof(environment));
        foreach (var entry in environment)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Contains('=') || entry.Key.Contains('\0')
                || (entry.Value?.Contains('\0') ?? false))
                throw new ArgumentException("A language-server environment entry is invalid.", nameof(environment));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
