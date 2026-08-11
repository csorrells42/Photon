using System.Diagnostics;
using HermesGitServices;

var tests = new (string Name, Func<Task> Run)[]
{
    ("porcelain v2 parser covers branch and every change group", ParserCoversGroups),
    ("porcelain parser preserves pathological filenames", ParserPreservesPathologicalNames),
    ("porcelain parser enforces framing and entry bounds", ParserEnforcesBounds),
    ("repository resolver contains renderer paths", ResolverContainsPaths),
    ("repository resolver rejects metadata and link escapes", ResolverRejectsEscapes),
    ("status command is fixed and environment is sanitized", CommandIsFixedAndSanitized),
    ("status service uses opaque identities and normalized output", ServiceProducesNormalizedStatus),
    ("status failure, output, timeout, and cancellation are bounded", StatusFailuresAreBounded),
    ("Git Extensions mapping is fixed and repository is revalidated", GitExtensionsMappingIsFixed),
    ("availability and redaction reveal no sensitive details", AvailabilityAndRedactionAreSafe),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"HermesGitServices smoke: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

static Task ParserCoversGroups()
{
    var value = Join(
        "# branch.oid 0123456789abcdef",
        "# branch.head feature/smoke",
        "# branch.upstream origin/feature/smoke",
        "# branch.ab +2 -3",
        "# stash 4",
        "1 M. N... 100644 100644 100644 a b staged.txt",
        "1 .D N... 100644 100644 000000 a b deleted.txt",
        "1 .M S.M. 160000 160000 160000 a b submodule",
        "2 R. N... 100644 100644 100644 a b R100 renamed.txt",
        "original.txt",
        "u UU N... 100644 100644 100644 100644 a b c conflict.txt",
        "? untracked.txt");
    var parsed = GitStatusParser.Parse(value);

    Equal("feature/smoke", parsed.Branch.Head, "head");
    Equal("origin/feature/smoke", parsed.Branch.Upstream, "upstream");
    Equal(2, parsed.Branch.Ahead, "ahead");
    Equal(3, parsed.Branch.Behind, "behind");
    Equal(4, parsed.Branch.StashCount, "stash");
    Equal(6, parsed.EntryCount, "entry count");
    True(parsed.Groups.Staged.Count >= 3, "staged group");
    True(parsed.Groups.Unstaged.Count >= 3, "unstaged group");
    Equal(1, parsed.Groups.Untracked.Count, "untracked group");
    Equal(1, parsed.Groups.Conflicted.Count, "conflicted group");
    Equal(1, parsed.Groups.Renamed.Count, "renamed group");
    Equal(1, parsed.Groups.Deleted.Count, "deleted group");
    Equal(1, parsed.Groups.Submodules.Count, "submodule group");

    var detached = GitStatusParser.Parse(Join("# branch.oid abc", "# branch.head (detached)"));
    True(detached.Branch.Detached && detached.Branch.Head is null, "detached branch");
    var unborn = GitStatusParser.Parse(Join("# branch.oid (initial)", "# branch.head main"));
    True(unborn.Branch.Unborn, "unborn branch");
    return Task.CompletedTask;
}

static Task ParserPreservesPathologicalNames()
{
    var names = new[]
    {
        "space name.txt", "-leading.txt", "tab\tname.txt", "line\nbreak.txt", "quote\"name.txt",
        "back\\slash.txt", "unicodé-文件.txt", "emoji-🛰️.txt", new string('x', 2048) + ".txt",
    };
    var parsed = GitStatusParser.Parse(string.Concat(names.Select(name => $"? {name}\0")));
    Equal(names.Length, parsed.EntryCount, "pathological entry count");
    EqualSequence(names, parsed.Groups.Untracked.Select(entry => entry.Path).ToArray(), "pathological paths");
    return Task.CompletedTask;
}

static Task ParserEnforcesBounds()
{
    Throws<GitStatusFormatException>(() => GitStatusParser.Parse("? not-terminated"), "NUL framing");
    Throws<GitStatusFormatException>(() => GitStatusParser.Parse("? one\0? two\0", 1), "entry bound");
    Throws<GitStatusFormatException>(() => GitStatusParser.Parse("? ../escape\0"), "path escape");
    Throws<GitStatusFormatException>(() => GitStatusParser.Parse("2 R. N... 100644 100644 100644 a b R100 new\0"), "rename framing");
    return Task.CompletedTask;
}

static Task ResolverContainsPaths()
{
    using var fixture = new Fixture();
    Directory.CreateDirectory(Path.Combine(fixture.Repository, "nested"));
    var resolver = new RepositoryResolver(fixture.Root);
    var repository = resolver.Resolve(Path.Combine("repo", "nested"));
    Equal("repo", repository.DisplayName, "display name");
    True(repository.RepositoryId.Length == 48 && repository.RepositoryId.All(Uri.IsHexDigit), "opaque repository id");
    ThrowsCode(() => resolver.Resolve(".."), "path_traversal_rejected");
    ThrowsCode(() => resolver.Resolve(fixture.Repository), "rooted_path_rejected");
    ThrowsCode(() => resolver.Resolve("missing"), "path_not_found");
    ThrowsCode(() => resolver.Resolve("bad\0path"), "invalid_path");
    return Task.CompletedTask;
}

static Task ResolverRejectsEscapes()
{
    using var fixture = new Fixture(createGitDirectory: false);
    var outside = Path.Combine(Path.GetTempPath(), "HermesGitOutside", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    try
    {
        File.WriteAllText(Path.Combine(fixture.Repository, ".git"), $"gitdir: {outside}");
        ThrowsCode(() => new RepositoryResolver(fixture.Root).Resolve("repo"), "outside_workspace");

        File.Delete(Path.Combine(fixture.Repository, ".git"));
        Directory.CreateDirectory(Path.Combine(fixture.Repository, ".git"));
        var link = Path.Combine(fixture.Root, "linked-repo");
        try
        {
            Directory.CreateSymbolicLink(link, fixture.Repository);
            ThrowsCode(() => new RepositoryResolver(fixture.Root).Resolve("linked-repo"), "reparse_point_rejected");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Console.WriteLine("SKIP symbolic-link fixture: Windows did not grant link creation.");
        }
    }
    finally
    {
        Directory.Delete(outside, recursive: true);
    }
    return Task.CompletedTask;
}

static Task CommandIsFixedAndSanitized()
{
    using var fixture = new Fixture();
    var resolver = new RepositoryResolver(fixture.Root);
    var repository = resolver.Resolve("repo");
    var prior = Environment.GetEnvironmentVariable("GIT_DIR");
    Environment.SetEnvironmentVariable("GIT_DIR", "poisoned");
    try
    {
        var runner = new GitCommandRunner(new LocatedGitExecutable(fixture.GitPath), new RecordingGitHost());
        var info = runner.CreateStatusStartInfo(repository);
        Equal(Path.GetFullPath(fixture.GitPath), info.FileName, "absolute executable");
        True(!info.UseShellExecute, "no shell");
        EqualSequence(new[]
        {
            "--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-C", repository.RootPath,
            "status", "--porcelain=v2", "--branch", "--show-stash", "-z", "--untracked-files=all",
        }, info.ArgumentList.ToArray(), "fixed arguments");
        True(!info.Environment.ContainsKey("GIT_DIR"), "redirecting environment removed");
        Equal("0", info.Environment["GIT_TERMINAL_PROMPT"], "prompt disabled");
        Equal("0", info.Environment["GIT_OPTIONAL_LOCKS"], "optional locks disabled");
        Equal("cat", info.Environment["GIT_PAGER"], "pager neutralized");
    }
    finally
    {
        Environment.SetEnvironmentVariable("GIT_DIR", prior);
    }
    return Task.CompletedTask;
}

static async Task ServiceProducesNormalizedStatus()
{
    using var fixture = new Fixture();
    var sentinel = Path.Combine(fixture.Repository, "hook-filter-fsmonitor-sentinel.txt");
    File.WriteAllText(sentinel, "untouched");
    var host = new RecordingGitHost((_, _, _) => Task.FromResult(new GitProcessResult(
        0,
        Join("# branch.oid abc", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +1 -0", "? hello.txt"),
        string.Empty, false, false, false, false)));
    var service = CreateService(fixture, host, new RecordingGitExtensionsLauncher());
    var resolution = service.ResolveRepository("resolve-1", "repo");
    True(resolution.Succeeded && resolution.RepositoryId is not null, "repository resolution");
    True(!resolution.ToString().Contains(fixture.Root, StringComparison.OrdinalIgnoreCase), "no path in response");

    var status = await service.GetStatusAsync("status-1", resolution.RepositoryId!);
    True(status.Succeeded && status.Snapshot is not null, "status result");
    Equal("status-1", status.Snapshot!.RequestId, "correlation");
    Equal("main", status.Snapshot.Branch.Head, "normalized branch");
    Equal(1, status.Snapshot.Groups.Untracked.Count, "normalized group");
    Equal("untouched", File.ReadAllText(sentinel), "sentinel unchanged");
}

static async Task StatusFailuresAreBounded()
{
    using var fixture = new Fixture();
    var resolver = new RepositoryResolver(fixture.Root);
    var repository = resolver.Resolve("repo");
    var unsafeHost = new RecordingGitHost((_, _, _) => Task.FromResult(new GitProcessResult(
        128, string.Empty, "fatal: detected dubious ownership in repository at C:\\Users\\private\\repo",
        false, false, false, false)));
    var unsafeResult = await new GitCommandRunner(new LocatedGitExecutable(fixture.GitPath), unsafeHost)
        .RunStatusAsync(repository, CancellationToken.None);
    Equal("unsafe_ownership", unsafeResult.Error?.Code, "unsafe ownership mapping");
    True(!unsafeResult.Error!.Message.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase), "stderr withheld");

    var oversized = new RecordingGitHost((_, _, _) => Task.FromResult(new GitProcessResult(
        0, "partial", string.Empty, true, false, false, false)));
    var oversizedResult = await new GitCommandRunner(new LocatedGitExecutable(fixture.GitPath), oversized)
        .RunStatusAsync(repository, CancellationToken.None);
    Equal("output_too_large", oversizedResult.Error?.Code, "output bound");

    var timed = new RecordingGitHost((_, bounds, _) =>
    {
        True(bounds.ExecutionTimeout <= TimeSpan.FromSeconds(10), "timeout forwarded");
        return Task.FromResult(new GitProcessResult(null, string.Empty, string.Empty, false, false, false, true));
    });
    var timedResult = await new GitCommandRunner(new LocatedGitExecutable(fixture.GitPath), timed)
        .RunStatusAsync(repository, CancellationToken.None);
    Equal("timeout", timedResult.Error?.Code, "timeout mapping");

    var cancelledHost = new RecordingGitHost((_, _, token) => Task.FromResult(new GitProcessResult(
        null, string.Empty, string.Empty, false, false, token.IsCancellationRequested, false)));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var cancelled = await new GitCommandRunner(new LocatedGitExecutable(fixture.GitPath), cancelledHost)
        .RunStatusAsync(repository, cancellation.Token);
    Equal("cancelled", cancelled.Error?.Code, "owned cancellation mapping");
    Equal(1, cancelledHost.InvocationCount, "only one owned invocation touched");
}

static async Task GitExtensionsMappingIsFixed()
{
    using var fixture = new Fixture();
    var launcher = new RecordingGitExtensionsLauncher();
    var service = CreateService(fixture, new RecordingGitHost(), launcher);
    var repository = service.ResolveRepository("resolve-ge", "repo");
    var unknown = await service.OpenGitExtensionsAsync(repository.RepositoryId!, "commit");
    Equal("unsupported_surface", unknown.Error?.Code, "unknown surface rejected");
    Equal(0, launcher.InvocationCount, "unknown surface not launched");

    var opened = await service.OpenGitExtensionsAsync(repository.RepositoryId!, "browse");
    True(opened.Succeeded, "browse launched through fake boundary");
    Equal(1, launcher.InvocationCount, "one fake launch");
    Equal(Path.GetFullPath(fixture.GitExtensionsPath), launcher.LastStartInfo!.FileName, "fixed executable");
    EqualSequence(new[] { "browse", Path.GetFullPath(fixture.Repository) }, launcher.LastStartInfo.ArgumentList.ToArray(), "fixed mapping");

    Directory.Delete(Path.Combine(fixture.Repository, ".git"), recursive: true);
    var stale = await service.OpenGitExtensionsAsync(repository.RepositoryId!, "browse");
    Equal("repository_changed", stale.Error?.Code, "repository revalidated");
    Equal(1, launcher.InvocationCount, "stale repository not launched");
}

static Task AvailabilityAndRedactionAreSafe()
{
    using var fixture = new Fixture();
    var missing = new HermesGitService(
        new RepositoryResolver(fixture.Root),
        new GitExecutableLocator(new[] { Path.Combine(fixture.Root, "missing", "git.exe") }),
        new GitExtensionsLocator(new[] { Path.Combine(fixture.Root, "missing", "GitExtensions.exe") }),
        new RecordingGitHost(),
        new RecordingGitExtensionsLauncher());
    var description = missing.Describe();
    Equal(ServiceAvailability.Unavailable, description.Git.State, "Git unavailable");
    Equal(ServiceAvailability.Unavailable, description.GitExtensions.State, "Git Extensions unavailable");
    Equal(0, description.GitExtensionsSurfaces.Count, "no unavailable surfaces");

    var redacted = GitErrorRedactor.Redact(
        "https://user:password@example.test/repo?token=abc C:\\Users\\alice\\secret password=hunter2");
    True(!redacted.Contains("user:password", StringComparison.Ordinal), "URI credentials redacted");
    True(!redacted.Contains("alice", StringComparison.Ordinal), "home redacted");
    True(!redacted.Contains("hunter2", StringComparison.Ordinal), "assignment redacted");
    True(redacted.Length <= 512, "redaction bound");
    return Task.CompletedTask;
}

static HermesGitService CreateService(
    Fixture fixture,
    IGitProcessHost gitHost,
    IGitExtensionsProcessLauncher gitExtensionsLauncher) =>
    new(
        new RepositoryResolver(fixture.Root),
        new GitExecutableLocator(new[] { fixture.GitPath }),
        new GitExtensionsLocator(new[] { fixture.GitExtensionsPath }),
        gitHost,
        gitExtensionsLauncher);

static string Join(params string[] records) => string.Join('\0', records) + '\0';

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

static void EqualSequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{message}: sequences differ");
}

static void Throws<T>(Action action, string message) where T : Exception
{
    try
    {
        action();
        throw new InvalidOperationException($"{message}: no exception");
    }
    catch (T)
    {
    }
}

static void ThrowsCode(Action action, string expectedCode)
{
    try
    {
        action();
        throw new InvalidOperationException($"{expectedCode}: no exception");
    }
    catch (RepositoryResolutionException exception)
    {
        Equal(expectedCode, exception.Code, "repository error code");
    }
}

internal sealed class RecordingGitHost : IGitProcessHost
{
    private readonly Func<ProcessStartInfo, GitCommandBounds, CancellationToken, Task<GitProcessResult>> _handler;

    internal RecordingGitHost(Func<ProcessStartInfo, GitCommandBounds, CancellationToken, Task<GitProcessResult>>? handler = null)
    {
        _handler = handler ?? ((Func<ProcessStartInfo, GitCommandBounds, CancellationToken, Task<GitProcessResult>>)(
            (_, _, _) => Task.FromResult(new GitProcessResult(
                0, "# branch.oid abc\0# branch.head main\0", string.Empty, false, false, false, false))));
    }

    internal int InvocationCount { get; private set; }

    public Task<GitProcessResult> RunAsync(ProcessStartInfo startInfo, GitCommandBounds bounds, CancellationToken cancellationToken)
    {
        InvocationCount++;
        return _handler(startInfo, bounds, cancellationToken);
    }
}

internal sealed class RecordingGitExtensionsLauncher : IGitExtensionsProcessLauncher
{
    internal int InvocationCount { get; private set; }
    internal ProcessStartInfo? LastStartInfo { get; private set; }

    public bool TryStart(ProcessStartInfo startInfo)
    {
        InvocationCount++;
        LastStartInfo = startInfo;
        return true;
    }
}

internal sealed class Fixture : IDisposable
{
    internal Fixture(bool createGitDirectory = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "HermesGitServicesSmoke", Guid.NewGuid().ToString("N"));
        Repository = Path.Combine(Root, "repo");
        Directory.CreateDirectory(Repository);
        if (createGitDirectory) Directory.CreateDirectory(Path.Combine(Repository, ".git"));
        GitPath = Path.Combine(Root, "git.exe");
        GitExtensionsPath = Path.Combine(Root, "GitExtensions.exe");
        File.WriteAllText(GitPath, "synthetic fixture; never execute");
        File.WriteAllText(GitExtensionsPath, "synthetic fixture; never execute");
    }

    internal string Root { get; }
    internal string Repository { get; }
    internal string GitPath { get; }
    internal string GitExtensionsPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
