using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HermesCredentialBroker.Runtime;
using HermesDesktop;

internal static class DockerExecCredentialRuntimeSocketSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        var containerId = new string('c', 64);
        var dockerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "resources", "bin", "docker.exe");
        var start = DockerExecCredentialRuntimeSocket.CreateStartInfo(dockerPath, containerId);
        var expected = new[]
        {
            "exec", "-i", "--user", "10000", containerId,
            "/opt/hermes/.venv/bin/python", "-I", "-m", "hermes_cli.workbench_credential_relay",
        };
        Equal(dockerPath, start.FileName, "fixed Docker executable");
        True(!start.UseShellExecute && start.CreateNoWindow, "shell-free hidden process");
        True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError, "all streams redirected");
        True(start.ArgumentList.SequenceEqual(expected, StringComparer.Ordinal), "exact fixed relay argv");
        var sessionSentinel = "hcs2_" + new string('S', 43);
        True(!start.ArgumentList.Any(value => value.Contains(sessionSentinel, StringComparison.Ordinal)), "session absent from argv");
        True(!start.Environment.Any(pair =>
            pair.Key.Contains(sessionSentinel, StringComparison.Ordinal)
            || pair.Value?.Contains(sessionSentinel, StringComparison.Ordinal) == true), "session absent from environment");

        var credentialEndpoint = HermesCredentialRuntimeBridge.BuildCredentialEndpoint(
            new Uri("http://127.0.0.1:4173/"), sessionSentinel);
        Equal(9119, credentialEndpoint.Port, "credential gateway port independent of Workbench UI port");
        Equal("ws", credentialEndpoint.Scheme, "credential gateway scheme");
        Equal("127.0.0.1", credentialEndpoint.Host, "credential gateway host");
        using (var loopbackPort = JsonDocument.Parse("""
            {"NetworkSettings":{"Ports":{"9119/tcp":[{"HostIp":"127.0.0.1","HostPort":"9119"}]}}}
            """))
        {
            HermesCredentialRuntimeBridge.RequireLoopbackGatewayPort(loopbackPort.RootElement);
        }
        RejectPort("""
            {"NetworkSettings":{"Ports":{"9119/tcp":[{"HostIp":"0.0.0.0","HostPort":"9119"}]}}}
            """, "non-loopback gateway publication");

        var textPayload = "hello"u8.ToArray();
        var textWire = DockerExecCredentialRuntimeSocket.EncodeHeader(
            DockerExecCredentialRuntimeSocket.RelayOpcode.Text,
            textPayload.Length).Concat(textPayload).ToArray();
        var text = DockerExecCredentialRuntimeSocket.ReadFrameAsync(new MemoryStream(textWire)).GetAwaiter().GetResult();
        True(text is not null, "text frame parsed");
        Equal(DockerExecCredentialRuntimeSocket.RelayOpcode.Text, text!.Value.Opcode, "text opcode");
        True(text.Value.Payload.SequenceEqual(textPayload), "text payload exact");

        Reject(Header(magic: "BAD!"u8.ToArray()), "bad magic");
        Reject(Header(version: 2), "bad version");
        Reject(Header(opcode: 255), "bad opcode");
        Reject(Header(opcode: (byte)DockerExecCredentialRuntimeSocket.RelayOpcode.Open, length: 1).Concat(new byte[] { 1 }).ToArray(), "non-empty control");
        Reject(Header(opcode: (byte)DockerExecCredentialRuntimeSocket.RelayOpcode.Binary, length: 96 * 1024 + 1), "oversized binary");
        Reject(Header()[..9], "truncated header");
        Reject(Header(opcode: (byte)DockerExecCredentialRuntimeSocket.RelayOpcode.Text, length: 2).Concat(new byte[] { 1 }).ToArray(), "truncated payload");
        Reject(Header(opcode: (byte)DockerExecCredentialRuntimeSocket.RelayOpcode.Text, length: 1).Concat(new byte[] { 0xff }).ToArray(), "invalid UTF-8");

        Console.WriteLine("Desktop credential relay exact argv and strict bounded HCRL framing passed.");
    }

    private static byte[] Header(
        byte[]? magic = null,
        byte version = 1,
        byte opcode = (byte)DockerExecCredentialRuntimeSocket.RelayOpcode.Close,
        int length = 0)
    {
        var header = new byte[10];
        (magic ?? "HCRL"u8.ToArray()).CopyTo(header, 0);
        header[4] = version;
        header[5] = opcode;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6), (uint)length);
        return header;
    }

    private static void Reject(byte[] wire, string message)
    {
        try
        {
            _ = DockerExecCredentialRuntimeSocket.ReadFrameAsync(new MemoryStream(wire)).GetAwaiter().GetResult();
            throw new InvalidOperationException($"Credential relay accepted {message}.");
        }
        catch (CredentialRuntimeException exception) when (exception.Code == "relay_protocol_invalid") { }
    }

    private static void RejectPort(string json, string message)
    {
        using var document = JsonDocument.Parse(json);
        try
        {
            HermesCredentialRuntimeBridge.RequireLoopbackGatewayPort(document.RootElement);
            throw new InvalidOperationException($"Credential relay accepted {message}.");
        }
        catch (CredentialRuntimeException exception) when (exception.Code == "runtime_port_untrusted") { }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Credential relay smoke failed: {message}.");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Credential relay smoke failed: {message}.");
    }
}
