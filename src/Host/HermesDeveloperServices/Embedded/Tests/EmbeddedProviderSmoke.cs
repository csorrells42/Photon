#if HERMES_EMBEDDED_PROVIDER_TESTS
using System.Security.Cryptography;
using System.Text.Json;
using HermesDeveloperServices;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--config-file", StringComparer.Ordinal))
            return FakeArduino(args);
        if (args.Contains("-i", StringComparer.Ordinal))
            return FakeOpenSsh(args);

        using var fixture = EmbeddedFixture.Create();
        await ArduinoAsync(fixture);
        await RaspberryPiAsync(fixture);
        Console.WriteLine("PASS embedded provider smoke");
        return 0;
    }

    private static async Task ArduinoAsync(EmbeddedFixture fixture)
    {
        var provider = await ArduinoHostProvider.CreateAsync(fixture.ArduinoOptions);
        var inventory = await provider.InspectAsync(ArduinoInventoryKind.Boards);
        Require(inventory.Result.Succeeded, "Arduino board inventory failed.");
        Require(inventory.Entries.Contains("COM7"), "Exact Arduino port was not structured.");

        var deniedSeed = new ArduinoCompileRequest(
            fixture.Workspace, "Blink", "out-compile", "arduino:avr:uno",
            new(EmbeddedPermissionKind.UploadDevice, "wrong", "approval-denied"));
        Require(!(await provider.CompileAsync(deniedSeed)).Succeeded, "Compile accepted the wrong permission kind.");

        var compileSeed = deniedSeed with { Permission = new(EmbeddedPermissionKind.CompileWorkspace, string.Empty, "approval-compile") };
        var compile = compileSeed with
        {
            Permission = compileSeed.Permission with { Scope = ArduinoHostProvider.CompilePermissionScope(compileSeed) },
        };
        var compiled = await provider.CompileAsync(compile);
        Require(compiled.Succeeded, "Arduino compile failed.");
        Require(compiled.Diagnostics.Count == 1 && compiled.Diagnostics[0].RelativePath == "Blink/main.ino",
            "Arduino diagnostic was not normalized to a workspace-relative path.");
        Require(compiled.WorkspaceRelativeArtifacts.Contains("out-compile/firmware.hex"), "Arduino artifact was not workspace-relative.");
        Require(!compiled.StandardError.Contains(fixture.Workspace, StringComparison.OrdinalIgnoreCase), "Workspace path leaked from Arduino output.");

        var uploadSeed = new ArduinoUploadRequest(
            fixture.Workspace, "Blink", "out-upload", "arduino:avr:uno", "COM7",
            new(EmbeddedPermissionKind.UploadDevice, string.Empty, "approval-upload"));
        var upload = uploadSeed with
        {
            Permission = uploadSeed.Permission with { Scope = ArduinoHostProvider.UploadPermissionScope(uploadSeed) },
        };
        Require((await provider.UploadAsync(upload)).Succeeded, "Arduino exact-port upload failed.");
        var wrongPort = upload with
        {
            Port = "COM8",
            Permission = upload.Permission with { Scope = ArduinoHostProvider.UploadPermissionScope(upload with { Port = "COM8" }) },
        };
        Require(!(await provider.UploadAsync(wrongPort)).Succeeded, "Arduino upload accepted an unreported port.");

        var zeroSeed = new ArduinoCompileRequest(fixture.Workspace, "Blink", "out-zero", "arduino:avr:zero",
            new(EmbeddedPermissionKind.CompileWorkspace, string.Empty, "approval-zero"));
        var zero = zeroSeed with { Permission = zeroSeed.Permission with { Scope = ArduinoHostProvider.CompilePermissionScope(zeroSeed) } };
        Require(!(await provider.CompileAsync(zero)).Succeeded, "Zero-exit Arduino compile without artifacts was accepted.");

        var mismatchSeed = new ArduinoUploadRequest(fixture.Workspace, "Blink", "out-mismatch", "arduino:avr:nano", "COM7",
            new(EmbeddedPermissionKind.UploadDevice, string.Empty, "approval-mismatch"));
        var mismatch = mismatchSeed with { Permission = mismatchSeed.Permission with { Scope = ArduinoHostProvider.UploadPermissionScope(mismatchSeed) } };
        Require(!(await provider.UploadAsync(mismatch)).Succeeded, "Port/FQBN mismatch was accepted.");

        ArduinoUploadRequest Concurrent(string output, string approval)
        {
            var seed = new ArduinoUploadRequest(fixture.Workspace, "Blink", output, "arduino:avr:uno", "COM7",
                new(EmbeddedPermissionKind.UploadDevice, string.Empty, approval));
            return seed with { Permission = seed.Permission with { Scope = ArduinoHostProvider.UploadPermissionScope(seed) } };
        }
        var concurrent = await Task.WhenAll(
            provider.UploadAsync(Concurrent("out-concurrent-a", "approval-concurrent-a")),
            provider.UploadAsync(Concurrent("out-concurrent-b", "approval-concurrent-b")));
        Require(concurrent.All(result => result.Succeeded), "Same-port uploads were not serialized.");

        var cancelSeed = new ArduinoCompileRequest(fixture.Workspace, "Blink", "out-cancel", "arduino:avr:cancel",
            new(EmbeddedPermissionKind.CompileWorkspace, string.Empty, "approval-cancel"));
        var cancel = cancelSeed with { Permission = cancelSeed.Permission with { Scope = ArduinoHostProvider.CompilePermissionScope(cancelSeed) } };
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            Require(!(await provider.CompileAsync(cancel, cancellation.Token)).Succeeded, "Cancelled compile reported success.");
        Require(!Directory.EnumerateDirectories(fixture.Workspace, ".hermes-arduino-output-*").Any(), "Cancelled compile left a private output stage.");

        var log = await File.ReadAllTextAsync(Path.Combine(fixture.ArduinoState, "arduino-invocations.log"));
        Require(log.Contains("board details --fqbn arduino:avr:uno", StringComparison.Ordinal), "FQBN verification was not invoked.");
        Require(log.Contains("upload --fqbn arduino:avr:uno --port COM7", StringComparison.Ordinal), "Exact upload argv changed.");
    }

    private static async Task RaspberryPiAsync(EmbeddedFixture fixture)
    {
        var provider = await RaspberryPiTrustedHostProvider.CreateAsync(fixture.RaspberryPiOptions);
        var target = fixture.RaspberryPiTarget;
        Require((await provider.ProbeAsync(target)).Succeeded, "Raspberry Pi probe failed.");
        Require((await provider.SearchPackagesAsync(new(target, "gpiozero"))).Succeeded, "Raspberry Pi package search failed.");

        var deploySeed = new RaspberryPiDeployRequest(
            target, fixture.Workspace, "deploy/app.py", "/opt/hermes/app.py",
            new(EmbeddedPermissionKind.DeployRemote, string.Empty, "approval-deploy"));
        var deploy = deploySeed with
        {
            Permission = deploySeed.Permission with { Scope = RaspberryPiTrustedHostProvider.DeployPermissionScope(deploySeed) },
        };
        var deployed = await provider.DeployAsync(deploy);
        Require(deployed.Succeeded, "Raspberry Pi deploy failed.");
        Require(!deployed.StandardError.Contains(fixture.IdentityPath, StringComparison.OrdinalIgnoreCase), "SSH identity path leaked in a result.");
        var failSeed = deploySeed with { RemoteAbsolutePath = "/opt/hermes/fail.py" };
        var fail = failSeed with { Permission = failSeed.Permission with { Scope = RaspberryPiTrustedHostProvider.DeployPermissionScope(failSeed) } };
        Require(!(await provider.DeployAsync(fail)).Succeeded, "Failed SCP staging reported deploy success.");
        Require(!Directory.EnumerateFiles(fixture.CredentialRoot, "remote-stage-*").Any(), "Failed remote stage still exists after successful cleanup.");
        var cleanupFailSeed = deploySeed with { RemoteAbsolutePath = "/opt/hermes/cleanupfail.py" };
        var cleanupFail = cleanupFailSeed with { Permission = cleanupFailSeed.Permission with { Scope = RaspberryPiTrustedHostProvider.DeployPermissionScope(cleanupFailSeed) } };
        Require((await provider.DeployAsync(cleanupFail)).FailureCode == "deploy_cleanup_failed", "Cleanup failure was not classified distinctly.");
        var existingDirectorySeed = deploySeed with { RemoteAbsolutePath = "/opt/hermes/existing-dir" };
        var existingDirectory = existingDirectorySeed with
        {
            Permission = existingDirectorySeed.Permission with { Scope = RaspberryPiTrustedHostProvider.DeployPermissionScope(existingDirectorySeed) },
        };
        Require(!(await provider.DeployAsync(existingDirectory)).Succeeded,
            "An existing destination directory produced a false exact-file publication.");
        Require(!Directory.EnumerateFiles(fixture.CredentialRoot, "remote-stage-*")
                .Any(path => File.ReadAllText(path).Contains("existing-dir.hermes-upload-", StringComparison.Ordinal)),
            "Existing-directory publish failure left a remote staging file.");
        var directorySeed = deploySeed with { SourceRelativePath = "deploy" };
        var directory = directorySeed with { Permission = directorySeed.Permission with { Scope = RaspberryPiTrustedHostProvider.DeployPermissionScope(directorySeed) } };
        Require(!(await provider.DeployAsync(directory)).Succeeded, "RPi v1 accepted a directory deploy.");

        var badTrust = target with { HostKey = target.HostKey with { Sha256Fingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" } };
        Require(!(await provider.ProbeAsync(badTrust)).Succeeded, "Mismatched SSH host-key trust was accepted.");

        var log = await File.ReadAllTextAsync(Path.Combine(fixture.CredentialRoot, "ssh-invocations.log"));
        Require(log.Contains("StrictHostKeyChecking=yes", StringComparison.Ordinal), "Strict SSH host-key checking was omitted.");
        Require(log.Contains("HostKeyAlgorithms=ssh-ed25519", StringComparison.Ordinal), "Exact SSH host-key algorithm was not pinned.");
        Require(log.Contains("KNOWNHOSTS=1", StringComparison.Ordinal), "SSH did not receive a private exact-entry known_hosts lease.");
        Require(!log.Contains(" -l ", StringComparison.Ordinal), "SSH/SCP used a standalone user/host form.");
        Require(log.Contains("/usr/bin/rm -f -- /opt/hermes/fail.py.hermes-upload-", StringComparison.Ordinal), "Failed remote stage was not cleaned up.");
        Require(log.Contains("/usr/bin/mv -fT --", StringComparison.Ordinal), "Remote exact-file publish omitted mv -T.");
        foreach (var line in log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith("KNOWNHOSTS=")))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Require(tokens.Count(token => token == "pi@pi.local" || token.StartsWith("pi@pi.local:", StringComparison.Ordinal)) == 1,
                "SSH/SCP did not contain exactly one combined destination authority.");
            Require(!tokens.Contains("pi.local") && !tokens.Contains("pi"), "SSH/SCP emitted a standalone host or user.");
            var separator = Array.IndexOf(tokens, "--");
            Require(separator > 0, "SSH/SCP omitted the argument terminator.");
            if (tokens.Contains("-r") && tokens.Any(token => token.Contains(":/opt/hermes/", StringComparison.Ordinal)))
                Require(separator == tokens.Length - 3 && tokens[^1].StartsWith("pi@pi.local:/opt/hermes/", StringComparison.Ordinal),
                    "SCP argv was not options + -r + -- + one local + one remote destination.");
            else
                Require(separator < Array.IndexOf(tokens, "pi@pi.local") && Array.IndexOf(tokens, "pi@pi.local") < tokens.Length - 1,
                    "SSH destination/terminator/remote-command ordering changed.");
        }
        Require(log.Contains("/usr/bin/uname -a", StringComparison.Ordinal), "Probe exposed an arbitrary remote command.");
        Require(!log.Contains("camera", StringComparison.OrdinalIgnoreCase), "A camera probe entered the Raspberry Pi surface.");
        Require(!log.Contains("cmake", StringComparison.OrdinalIgnoreCase), "A compiler/build probe entered the Raspberry Pi surface.");
    }

    private static int FakeArduino(string[] args)
    {
        var configIndex = Array.IndexOf(args, "--config-file");
        var config = args[configIndex + 1];
        using var document = JsonDocument.Parse(File.ReadAllText(config));
        var data = document.RootElement.GetProperty("directories").GetProperty("data").GetString()!;
        var state = Directory.GetParent(data)!.FullName;
        var command = args.Skip(configIndex + 3).ToArray();
        File.AppendAllText(Path.Combine(state, "arduino-invocations.log"), string.Join(' ', command) + Environment.NewLine);
        if (command is ["config", "dump"]) Console.Write(File.ReadAllText(config));
        else if (command is ["board", "list"]) Console.Write("{\"detected_ports\":[{\"port\":{\"address\":\"COM7\"},\"matching_boards\":[{\"fqbn\":\"arduino:avr:uno\"}]}]}");
        else if (command is ["board", "details", "--fqbn", var fqbn]) return fqbn is "arduino:avr:uno" or "arduino:avr:zero" or "arduino:avr:nano" or "arduino:avr:cancel" ? 0 : 2;
        else if (command.FirstOrDefault() == "compile")
        {
            var output = command[Array.IndexOf(command, "--output-dir") + 1];
            Directory.CreateDirectory(output);
            if (command.Contains("arduino:avr:cancel")) Thread.Sleep(TimeSpan.FromSeconds(10));
            if (!command.Contains("arduino:avr:zero")) File.WriteAllText(Path.Combine(output, "firmware.hex"), "fixture");
            var sketch = command[^1];
            Console.Error.WriteLine($"{Path.Combine(sketch, "main.ino")}:3:5: warning: fixture-warning");
        }
        else if (command.FirstOrDefault() == "upload")
        {
            var marker = Path.Combine(state, "upload-active.lock");
            try
            {
                using var owned = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                Thread.Sleep(200);
            }
            catch (IOException) { return 23; }
            finally { try { File.Delete(marker); } catch (IOException) { } }
        }
        else Console.Write("{\"name\":\"fixture\"}");
        return 0;
    }

    private static int FakeOpenSsh(string[] args)
    {
        var identity = args[Array.IndexOf(args, "-i") + 1];
        var root = Directory.GetParent(identity)!.FullName;
        File.AppendAllText(Path.Combine(root, "ssh-invocations.log"), string.Join(' ', args) + Environment.NewLine);
        var knownOption = args.First(value => value.StartsWith("UserKnownHostsFile=", StringComparison.Ordinal));
        var knownPath = knownOption["UserKnownHostsFile=".Length..];
        File.AppendAllText(Path.Combine(root, "ssh-invocations.log"), $"KNOWNHOSTS={File.ReadLines(knownPath).Count()}" + Environment.NewLine);
        var isScp = args.Contains("-r");
        if (isScp)
        {
            var remote = args[^1][(args[^1].IndexOf(':') + 1)..];
            File.WriteAllText(RemoteMarker(root, remote), remote);
            if (remote.Contains("fail.py.hermes-upload-", StringComparison.Ordinal)
                || remote.Contains("cleanupfail.py.hermes-upload-", StringComparison.Ordinal)) return 7;
        }
        else
        {
            var commandIndex = Array.IndexOf(args, "pi@pi.local") + 1;
            var command = args.Skip(commandIndex).ToArray();
            if (command.FirstOrDefault() == "/usr/bin/rm")
            {
                var remote = command[^1];
                if (remote.Contains("cleanupfail.py.hermes-upload-", StringComparison.Ordinal)) return 8;
                File.Delete(RemoteMarker(root, remote));
            }
            else if (command.FirstOrDefault() == "/usr/bin/mv")
            {
                if (!command.Contains("-fT")) return 31;
                if (command[^1] == "/opt/hermes/existing-dir") return 32;
                File.Delete(RemoteMarker(root, command[^2]));
            }
        }
        Console.WriteLine("fixture remote result");
        Console.Error.WriteLine($"identity={identity}");
        return 0;

        static string RemoteMarker(string root, string remote)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(remote)));
            return Path.Combine(root, $"remote-stage-{hash}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }
}

internal sealed class EmbeddedFixture : IDisposable
{
    private EmbeddedFixture(string root) => Root = root;

    public string Root { get; }
    public string Workbench => Path.Combine(Root, "workbench");
    public string Workspace => Path.Combine(Root, "workspace");
    public string ArduinoState => Path.Combine(Workbench, "arduino-state");
    public string CredentialRoot => Path.Combine(Root, "credentials");
    public string IdentityPath => Path.Combine(CredentialRoot, "id_fixture");
    public ArduinoProviderOptions ArduinoOptions { get; private set; } = null!;
    public RaspberryPiProviderOptions RaspberryPiOptions { get; private set; } = null!;
    public RaspberryPiTrustedHost RaspberryPiTarget { get; private set; } = null!;

    public static EmbeddedFixture Create()
    {
        var fixture = new EmbeddedFixture(Path.Combine(Path.GetTempPath(), $"HermesEmbeddedSmoke-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(fixture.Workbench);
        Directory.CreateDirectory(fixture.Workspace);
        Directory.CreateDirectory(Path.Combine(fixture.Workspace, "Blink"));
        foreach (var output in new[] { "out-compile", "out-upload", "out-zero", "out-mismatch", "out-concurrent-a", "out-concurrent-b", "out-cancel" })
            Directory.CreateDirectory(Path.Combine(fixture.Workspace, output));
        Directory.CreateDirectory(Path.Combine(fixture.Workspace, "deploy"));
        File.WriteAllText(Path.Combine(fixture.Workspace, "Blink", "main.ino"), "void setup(){} void loop(){}");
        File.WriteAllText(Path.Combine(fixture.Workspace, "deploy", "app.py"), "print('fixture')");

        Directory.CreateDirectory(fixture.ArduinoState);
        foreach (var child in new[] { "data", "downloads", "user" }) Directory.CreateDirectory(Path.Combine(fixture.ArduinoState, child));
        var arduinoRoot = Path.Combine(fixture.Workbench, "arduino");
        Directory.CreateDirectory(arduinoRoot);
        var config = new
        {
            directories = new
            {
                data = Path.Combine(fixture.ArduinoState, "data"),
                downloads = Path.Combine(fixture.ArduinoState, "downloads"),
                user = Path.Combine(fixture.ArduinoState, "user"),
            },
            board_manager = new { enable_unsafe_install = false },
            library = new { enable_unsafe_install = false },
        };
        File.WriteAllText(Path.Combine(arduinoRoot, "arduino-config.json"), JsonSerializer.Serialize(config));
        CreatePackage(arduinoRoot, ArduinoProviderOptions.ToolchainId, [ArduinoProviderOptions.ExecutableLogicalName]);
        fixture.ArduinoOptions = new(fixture.Workbench, "arduino", "receipt.json", "arduino-config.json", "arduino-state");

        Directory.CreateDirectory(fixture.CredentialRoot);
        File.WriteAllText(fixture.IdentityPath, "fixture-private-key-not-a-real-secret");
        var key = System.Text.Encoding.ASCII.GetBytes("fixture-host-key");
        var keyBase64 = Convert.ToBase64String(key);
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
        File.WriteAllText(Path.Combine(fixture.CredentialRoot, "known_hosts"), $"pi.local ssh-ed25519 {keyBase64}\n");
        var sshRoot = Path.Combine(fixture.Workbench, "openssh");
        Directory.CreateDirectory(sshRoot);
        CreatePackage(sshRoot, RaspberryPiProviderOptions.ToolchainId,
            [RaspberryPiProviderOptions.SshLogicalName, RaspberryPiProviderOptions.ScpLogicalName]);
        fixture.RaspberryPiOptions = new(fixture.Workbench, "openssh", "receipt.json", fixture.CredentialRoot, "id_fixture", "known_hosts");
        fixture.RaspberryPiTarget = new("pi.local", "pi", 22, new("ssh-ed25519", fingerprint));
        return fixture;
    }

    private static void CreatePackage(string packageRoot, string toolchainId, IReadOnlyList<string> logicalNames)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Smoke executable is unavailable.");
        var baseDirectory = AppContext.BaseDirectory;
        var runtimeFiles = new[]
        {
            executable,
            Path.Combine(baseDirectory, "HermesEmbeddedProviders.Smoke.dll"),
            Path.Combine(baseDirectory, "HermesEmbeddedProviders.Smoke.deps.json"),
            Path.Combine(baseDirectory, "HermesEmbeddedProviders.Smoke.runtimeconfig.json"),
            Path.Combine(baseDirectory, "HermesDeveloperServices.dll"),
        };
        foreach (var source in runtimeFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            File.Copy(source, Path.Combine(packageRoot, Path.GetFileName(source)), overwrite: true);
        var executableName = Path.GetFileName(executable);
        var files = Directory.EnumerateFiles(packageRoot)
            .Where(path => !Path.GetFileName(path).Equals("receipt.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => new PinnedToolchainFile(Path.GetFileName(path), Hash(path), new FileInfo(path).Length))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var executableHash = files.Single(file => file.RelativePath.Equals(executableName, StringComparison.OrdinalIgnoreCase)).Sha256;
        var receipt = new PinnedToolchainReceipt(
            1,
            toolchainId,
            logicalNames.Select(name => new PinnedToolchainExecutable(name, executableName, executableHash)).ToArray(),
            files);
        File.WriteAllText(Path.Combine(packageRoot, "receipt.json"), JsonSerializer.Serialize(receipt));
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
#endif
