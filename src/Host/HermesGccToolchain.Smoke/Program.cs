using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HermesDeveloperServices;

if (IsFakeCompiler()) return RunFakeCompiler(args);
return await RunSmokeAsync();

static bool IsFakeCompiler()
{
    var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
    return name.Equals("gcc", StringComparison.OrdinalIgnoreCase)
        || name.Equals("g++", StringComparison.OrdinalIgnoreCase);
}

static int RunFakeCompiler(string[] arguments)
{
    if (arguments.Length == 2 && arguments[0] == "--linger-child")
    {
        File.WriteAllText(arguments[1], Environment.ProcessId.ToString());
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    }

    var name = Path.GetFileName(Environment.ProcessPath);
    if (arguments.SequenceEqual(["--version"]))
    {
        Console.WriteLine($"{name} (Hermes smoke GCC) 14.2.0");
        return 0;
    }
    if (arguments.SequenceEqual(["-dumpmachine"]))
    {
        Console.WriteLine("x86_64-w64-mingw32");
        return 0;
    }

    var sources = arguments.Where(argument => Path.GetExtension(argument).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx").ToArray();
    var sourceText = sources.Select(File.ReadAllText).ToArray();
    Console.WriteLine($"compiler={name}");
    if (sourceText.Any(text => text.Contains("SLOW_CHILD", StringComparison.Ordinal)))
    {
        var source = sources[sourceText.ToList().FindIndex(text => text.Contains("SLOW_CHILD", StringComparison.Ordinal))];
        var pidFile = source + ".child.pid";
        var child = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        child.ArgumentList.Add("--linger-child");
        child.ArgumentList.Add(pidFile);
        Process.Start(child)?.Dispose();
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    }
    if (sourceText.Any(text => text.Contains("HUGE_NO_NEWLINE", StringComparison.Ordinal)))
    {
        var block = new string('X', 8 * 1024);
        for (var index = 0; index < 256; index++) Console.Out.Write(block);
    }
    if (sourceText.Any(text => text.Contains("ZERO_NO_OUTPUT", StringComparison.Ordinal))) return 0;
    var failureIndex = sourceText.ToList().FindIndex(text => text.Contains("FAIL", StringComparison.Ordinal));
    if (failureIndex >= 0)
    {
        var failure = sources[failureIndex];
        Console.Error.WriteLine($"{failure}:2:3: warning: repeated\u001b[31m warning [-Wsmoke]");
        Console.Error.WriteLine($"{failure}:2:3: warning: repeated\u001b[31m warning [-Wsmoke]");
        Console.Error.WriteLine($"{failure}:4:1: error: smoke failure");
        Console.Error.WriteLine("C:/outside/header.h:1:1: note: outside workspace diagnostic");
        return 1;
    }

    var outputIndex = Array.IndexOf(arguments, "-o");
    if (outputIndex >= 0 && outputIndex + 1 < arguments.Length)
        File.WriteAllText(arguments[outputIndex + 1], "fresh smoke executable");
    return 0;
}

static async Task<int> RunSmokeAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "HermesGccToolchainSmoke", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var workbench = Path.Combine(root, "workbench");
        var toolchain = Path.Combine(workbench, "toolchains", "gcc");
        var bin = Path.Combine(toolchain, "bin");
        var workspace = Path.Combine(root, "workspace");
        var output = Path.Combine(workspace, "output");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(output);
        CopySmokeRuntime(bin);
        var gcc = Path.Combine(bin, "gcc.exe");
        var gxx = Path.Combine(bin, "g++.exe");
        File.Copy(Environment.ProcessPath!, gcc);
        File.Copy(Environment.ProcessPath!, gxx);
        await WriteReceiptAsync(toolchain, gcc, gxx);

        await File.WriteAllTextAsync(Path.Combine(workspace, "main.c"), "int main(void) { return 0; }");
        await File.WriteAllTextAsync(Path.Combine(workspace, "broken.cpp"), "// FAIL\nint main() { return missing; }");
        await File.WriteAllTextAsync(Path.Combine(workspace, "zero.c"), "// ZERO_NO_OUTPUT\nint main(void) { return 0; }");
        await File.WriteAllTextAsync(Path.Combine(workspace, "huge.c"), "// HUGE_NO_NEWLINE\nint main(void) { return 0; }");
        await File.WriteAllTextAsync(Path.Combine(workspace, "slow.c"), "// SLOW_CHILD\nint main(void) { return 0; }");
        await File.WriteAllTextAsync(Path.Combine(workspace, "slow-stop.c"), "// SLOW_CHILD\nint main(void) { return 0; }");

        var provider = new GccToolchainProvider(new GccToolchainProviderOptions
        {
            WorkbenchRoot = workbench,
            DiscoveryTimeout = TimeSpan.FromSeconds(5),
            Build = new GccBuildRunnerOptions
            {
                Timeout = TimeSpan.FromMilliseconds(1500),
                StopTimeout = TimeSpan.FromSeconds(5),
                MaximumRetainedCharacters = 128 * 1024,
            },
        });
        await using (provider.ConfigureAwait(false))
        {
            var discovery = await provider.DiscoverExecutablesAsync(
                new ToolchainDiscoveryContext(workspace, ToolchainExecutionKind.LocalSidecarProcess),
                CancellationToken.None);
            Check(discovery.Availability.State == ToolchainAvailabilityState.Available, "receipt-backed staged discovery");
            Check(discovery.Executables.Count == 2, "both compiler identities");
            Check(discovery.Executables.All(item => item.Version!.Contains("x86_64-w64-mingw32", StringComparison.Ordinal)), "dumpmachine captured");
            Check(provider.Identity?.Gcc.Sha256.Length == 64 && provider.Identity.Gxx.Sha256.Length == 64, "SHA-256 identities");

            await provider.StartAsync(
                new ToolchainStartContext(workspace, [], ToolchainExecutionKind.LocalSidecarProcess),
                CancellationToken.None);

            var mainOutput = Path.Combine(output, "main.exe");
            await File.WriteAllTextAsync(mainOutput, "stale artifact");
            var cBuild = await provider.BuildAsync(new(workspace, "main.c", "output/main.exe"));
            Check(cBuild.Succeeded, "C build succeeds");
            Check(cBuild.Output.StandardOutput.Contains("compiler=gcc.exe", StringComparison.OrdinalIgnoreCase), ".c selects staged gcc");
            Check(await File.ReadAllTextAsync(mainOutput) == "fresh smoke executable", "fresh artifact atomically replaces stale destination");

            var cppBuild = await provider.BuildAsync(new(workspace, "broken.cpp", "output/broken.exe"));
            Check(!cppBuild.Succeeded, "C++ failure is structured");
            Check(cppBuild.Output.StandardOutput.Contains("compiler=g++.exe", StringComparison.OrdinalIgnoreCase), "C++ selects staged g++");
            Check(cppBuild.Diagnostics.Count == 2, "diagnostics are bounded and deduplicated");
            Check(cppBuild.Diagnostics.All(item => item.FilePath == "broken.cpp"), "diagnostics are workspace-relative");
            Check(cppBuild.Diagnostics.All(item => !item.Message.Any(char.IsControl)), "diagnostic controls sanitized");

            foreach (var unsafeTarget in new[]
                     {
                         Path.Combine(workspace, "main.c"), "../main.c", "main.c:stream", "@main.c",
                         "main.c&whoami", "%TEMP%/main.c", "-Wall", "CON", "con.txt", "COM1.cpp",
                         "LPT9.c", "CONIN$.c", "CONOUT$.cpp",
                         "COM\u00B9.c", "COM\u00B2.cpp", "COM\u00B3.c",
                         "LPT\u00B9.cpp", "LPT\u00B2.c", "LPT\u00B3.cpp",
                         "NUL .txt", "COM1 .cpp", "CLOCK$.c", "CONFIG$.cpp",
                     })
            {
                var rejected = await provider.BuildAsync(new(workspace, unsafeTarget, "output/rejected.exe"));
                Check(!rejected.Succeeded && rejected.FailureCode is not null, $"unsafe/device target rejected: {unsafeTarget}");
            }

            await File.AppendAllTextAsync(gcc, "replacement-after-discovery");
            var replacement = await provider.BuildAsync(new(workspace, "main.c", "output/replacement.exe"));
            Check(replacement.FailureCode == "executable_hash_mismatch", "replacement after discovery rejected before execution");
            File.Copy(Environment.ProcessPath!, gcc, overwrite: true);

            var unpinnedHelper = Path.Combine(bin, "cc1.exe");
            await File.WriteAllTextAsync(unpinnedHelper, "unpinned helper");
            var helperRejected = await provider.BuildAsync(new(workspace, "main.c", "output/helper.exe"));
            Check(helperRejected.FailureCode == "package_unpinned_file", "unpinned helper/resource closure rejected");
            File.Delete(unpinnedHelper);

            var zeroDestination = Path.Combine(output, "zero.exe");
            await File.WriteAllTextAsync(zeroDestination, "preserve stale output");
            var zero = await provider.BuildAsync(new(workspace, "zero.c", "output/zero.exe"));
            Check(zero.FailureCode == "artifact_missing", "zero-exit outputless compiler rejected");
            Check(await File.ReadAllTextAsync(zeroDestination) == "preserve stale output", "outputless success cannot replace stale destination");

            var huge = await provider.BuildAsync(new(workspace, "huge.c", "output/huge.exe"));
            Check(huge.Succeeded && huge.Output.Truncated && huge.Output.DroppedCharacters > 0, "huge no-newline output is drained and bounded");

            var timeout = await provider.BuildAsync(new(workspace, "slow.c", "output/timeout.exe"));
            Check(timeout.FailureCode == "build_timeout" && !timeout.WasCancelled, "timeout closes owned process job");
            var timeoutPid = await ReadChildPidAsync(Path.Combine(workspace, "slow.c.child.pid"));
            Check(await ProcessExitedAsync(timeoutPid), "timeout removes owned child process");

            var reparseVerified = false;
            var linkedSource = Path.Combine(workspace, "linked.c");
            try
            {
                File.CreateSymbolicLink(linkedSource, Path.Combine(workspace, "main.c"));
                var rejectedLink = await provider.BuildAsync(new(workspace, "linked.c", "output/linked.exe"));
                Check(!rejectedLink.Succeeded && rejectedLink.FailureCode!.Contains("reparse", StringComparison.Ordinal), "reparse source rejected");
                reparseVerified = true;
                File.Delete(linkedSource);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Console.WriteLine("CAPABILITY reparse creation unavailable; no runtime reparse claim");
            }

            var stopBuild = provider.BuildAsync(new(workspace, "slow-stop.c", "output/stop.exe"));
            var stopPid = await WaitForChildPidAsync(Path.Combine(workspace, "slow-stop.c.child.pid"));
            var stopwatch = Stopwatch.StartNew();
            await provider.StopAsync(CancellationToken.None);
            stopwatch.Stop();
            var stoppedBuild = await stopBuild;
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "StopAsync wait is bounded");
            Check(stoppedBuild.WasCancelled && stoppedBuild.FailureCode == "build_cancelled", "StopAsync cancels active compiler");
            Check(await ProcessExitedAsync(stopPid), "StopAsync removes owned child process");
            Check(!reparseVerified || !File.Exists(linkedSource), "reparse capability gate completed safely");
            Console.WriteLine("PASS strengthened provider smoke");
        }

        var badWorkbench = Path.Combine(root, "bad-workbench");
        var badToolchain = Path.Combine(badWorkbench, "toolchains", "gcc");
        var badBin = Path.Combine(badToolchain, "bin");
        Directory.CreateDirectory(badBin);
        CopySmokeRuntime(badBin);
        var badGcc = Path.Combine(badBin, "gcc.exe");
        var badGxx = Path.Combine(badBin, "g++.exe");
        File.Copy(Environment.ProcessPath!, badGcc);
        File.Copy(Environment.ProcessPath!, badGxx);
        await WriteReceiptAsync(badToolchain, badGcc, badGxx);
        await File.AppendAllTextAsync(badGcc, "tampered");
        await using var badProvider = new GccToolchainProvider(new() { WorkbenchRoot = badWorkbench });
        var badDiscovery = await badProvider.DiscoverExecutablesAsync(
            new ToolchainDiscoveryContext(workspace, ToolchainExecutionKind.LocalSidecarProcess),
            CancellationToken.None);
        Check(badDiscovery.Availability.Code == "executable_hash_mismatch", "tampered executable rejected");
        Console.WriteLine("PASS strengthened integrity smoke");
        return 0;
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void CopySmokeRuntime(string destination)
{
    foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
    {
        var extension = Path.GetExtension(file);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}

static async Task WriteReceiptAsync(string toolchain, string gcc, string gxx)
{
    var receiptPath = Path.Combine(toolchain, "hermes-toolchain-receipt.json");
    var files = new List<PinnedToolchainFile>();
    foreach (var file in Directory.EnumerateFiles(toolchain, "*", SearchOption.AllDirectories)
                 .Where(path => !path.Equals(receiptPath, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
    {
        files.Add(new(
            Path.GetRelativePath(toolchain, file).Replace(Path.DirectorySeparatorChar, '/'),
            await HashAsync(file),
            new FileInfo(file).Length));
    }
    var receipt = new PinnedToolchainReceipt(
        1,
        "gcc",
        [
            new("gcc", "bin/gcc.exe", await HashAsync(gcc)),
            new("g++", "bin/g++.exe", await HashAsync(gxx)),
        ],
        files);
    await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt));
}

static async Task<string> HashAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

static async Task<int> WaitForChildPidAsync(string path)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!File.Exists(path)) await Task.Delay(25, timeout.Token);
    return await ReadChildPidAsync(path);
}

static async Task<int> ReadChildPidAsync(string path)
{
    if (!File.Exists(path)) throw new InvalidOperationException($"Child PID evidence was not created: {path}");
    return int.Parse(await File.ReadAllTextAsync(path));
}

static async Task<bool> ProcessExitedAsync(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await process.WaitForExitAsync(timeout.Token);
        return process.HasExited;
    }
    catch (ArgumentException) { return true; }
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"Smoke assertion failed: {name}");
    Console.WriteLine($"PASS {name}");
}
