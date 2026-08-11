using System.IO;
using System.Text;
using Porta.Pty;

namespace HermesDesktop;

internal sealed class ConPtySession : IAsyncDisposable
{
    private readonly IPtyConnection _connection;
    private readonly Action<string> _onOutput;
    private readonly Action<int> _onExit;
    private readonly EventHandler<PtyExitedEventArgs> _exitHandler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readTask;
    private int _disposed;

    private ConPtySession(IPtyConnection connection, Action<string> onOutput, Action<int> onExit)
    {
        _connection = connection;
        _onOutput = onOutput;
        _onExit = onExit;
        _exitHandler = (_, eventArgs) =>
        {
            if (Volatile.Read(ref _disposed) == 0) _onExit(eventArgs.ExitCode);
        };
        _connection.ProcessExited += _exitHandler;
        _readTask = ReadOutputAsync();
    }

    public int ProcessId => _connection.Pid;

    public static async Task<ConPtySession> StartAsync(
        string workingDirectory,
        int columns,
        int rows,
        Action<string> onOutput,
        Action<int> onExit,
        string? application = null,
        string[]? arguments = null)
    {
        application ??= Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        arguments ??= ["-NoLogo", "-NoProfile"];
        var options = new PtyOptions
        {
            Name = "Photos Agape Aphthartos",
            Cols = Math.Clamp(columns, 20, 400),
            Rows = Math.Clamp(rows, 5, 200),
            Cwd = workingDirectory,
            App = application,
            CommandLine = arguments,
            Environment = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor",
            },
        };
        var connection = await PtyProvider.SpawnAsync(options, CancellationToken.None).ConfigureAwait(false);
        return new ConPtySession(connection, onOutput, onExit);
    }

    public async Task WriteAsync(string data)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(data)) return;
        var bytes = Encoding.UTF8.GetBytes(data);
        await _connection.WriterStream.WriteAsync(bytes, 0, bytes.Length, _shutdown.Token).ConfigureAwait(false);
        await _connection.WriterStream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _connection.Resize(Math.Clamp(columns, 20, 400), Math.Clamp(rows, 5, 200));
    }

    private async Task ReadOutputAsync()
    {
        var bytes = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var count = await _connection.ReaderStream.ReadAsync(bytes, 0, bytes.Length, _shutdown.Token).ConfigureAwait(false);
                if (count == 0) break;
                decoder.Convert(bytes, 0, count, chars, 0, chars.Length, false, out _, out var charCount, out _);
                if (charCount > 0) _onOutput(new string(chars, 0, charCount));
            }
        }
        catch (Exception exception) when (
            Volatile.Read(ref _disposed) != 0 &&
            exception is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _connection.ProcessExited -= _exitHandler;
        _shutdown.Cancel();
        try
        {
            if (!_connection.WaitForExit(0)) _connection.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _connection.Dispose();
        try { await _readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
        _shutdown.Dispose();
    }
}
