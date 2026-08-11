namespace HermesDesktop;

internal sealed class NativeTerminalBridge(string workspacePath, Action<object> postMessage) : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConPtySession? _session;
    private string? _sessionId;

    public async Task StartAsync(int columns, int rows)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session is not null)
            {
                postMessage(new { type = "terminal.ready", version = ProtocolVersion, sessionId = _sessionId, processId = _session.ProcessId, shell = "Windows PowerShell", cwd = workspacePath });
                return;
            }

            var sessionId = Guid.NewGuid().ToString("N");
            var session = await ConPtySession.StartAsync(
                workspacePath,
                ClampColumns(columns),
                ClampRows(rows),
                data => postMessage(new { type = "terminal.output", version = ProtocolVersion, sessionId, data }),
                exitCode =>
                {
                    postMessage(new { type = "terminal.exit", version = ProtocolVersion, sessionId, exitCode });
                    _ = ClearExitedSessionAsync(sessionId);
                });
            _sessionId = sessionId;
            _session = session;
            postMessage(new { type = "terminal.ready", version = ProtocolVersion, sessionId, processId = session.ProcessId, shell = "Windows PowerShell", cwd = workspacePath });
        }
        catch (Exception exception)
        {
            postMessage(new { type = "terminal.error", version = ProtocolVersion, message = exception.Message });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(string data)
    {
        if (data.Length > 64 * 1024)
        {
            postMessage(new { type = "terminal.error", version = ProtocolVersion, message = "Terminal input exceeded the 64 KB frame limit." });
            return;
        }

        var session = _session;
        if (session is null)
        {
            postMessage(new { type = "terminal.error", version = ProtocolVersion, message = "Start the terminal before sending input." });
            return;
        }
        try { await session.WriteAsync(data); }
        catch (Exception exception) { postMessage(new { type = "terminal.error", version = ProtocolVersion, message = exception.Message }); }
    }

    public void Resize(int columns, int rows)
    {
        try { _session?.Resize(ClampColumns(columns), ClampRows(rows)); }
        catch (Exception exception) { postMessage(new { type = "terminal.error", version = ProtocolVersion, message = exception.Message }); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var session = _session;
            var sessionId = _sessionId;
            _session = null;
            _sessionId = null;
            if (session is not null)
            {
                await session.DisposeAsync();
                postMessage(new { type = "terminal.exit", version = ProtocolVersion, sessionId, exitCode = 0, message = "Terminal stopped." });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ClearExitedSessionAsync(string sessionId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_sessionId != sessionId) return;
            var session = _session;
            _session = null;
            _sessionId = null;
            if (session is not null) await session.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    private static int ClampColumns(int value) => Math.Clamp(value, 20, 400);
    private static int ClampRows(int value) => Math.Clamp(value, 5, 200);
}
