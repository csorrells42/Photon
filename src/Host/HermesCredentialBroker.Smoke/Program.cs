using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HermesCredentialBroker;

var suite = new SmokeSuite();

await suite.RunAsync("opaque references are random and path-free", () =>
{
    var values = Enumerable.Range(0, 256).Select(_ => CredentialReference.Create().Value).ToArray();
    Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count(), "unique references");
    True(values.All(value => value.StartsWith("hcv2_", StringComparison.Ordinal) && value.Length == 48), "bounded opaque shape");
    True(values.All(value => !value.Contains('/') && !value.Contains('\\') && !value.Contains(':')), "no path or provider disclosure");
    ThrowsCode(() => _ = new CredentialReference("hcv2_openai-primary"), "invalid_reference");
    return Task.CompletedTask;
});

await suite.RunAsync("contract binds principal provider slot purpose and revision", () =>
{
    var principal = Principal();
    var binding = Binding(principal);
    Equal("profile-1", binding.Principal.ProfileId, "profile binding");
    Equal("openai", binding.ProviderId, "provider binding");
    Equal("primary", binding.SlotId, "slot binding");
    Equal(new[] { "model:chat", "oauth-refresh" }, binding.Purposes, "canonical purposes");
    ThrowsCode(() => _ = new CredentialPrincipalBinding("someone", "machine-1", "install-1", "profile-1"), "invalid_principal");
    ThrowsCode(() => _ = new CredentialBinding(principal, "openai", "primary", CredentialAuthKind.ApiKey, CredentialSourceKind.Native, ["arbitrary"]), "invalid_purpose");
    return Task.CompletedTask;
});

await suite.RunAsync("record storage is encrypted metadata-only and revision checked", async () =>
{
    using var fixture = new VaultFixture();
    const string sentinel = "credential-sentinel-never-render";
    var plaintext = Encoding.UTF8.GetBytes(sentinel);
    var intent = new CredentialWriteIntent("session-1", Binding(fixture.Principal), null, 0);
    var stored = await fixture.Vault.StoreAsync(intent, plaintext);
    Equal(1L, stored.Revision, "first revision");
    True(plaintext.All(value => value == 0), "caller plaintext zeroed after storage");
    var recordText = await File.ReadAllTextAsync(Directory.EnumerateFiles(fixture.Root, "*.hcv2").Single());
    True(!recordText.Contains(sentinel, StringComparison.Ordinal), "record does not contain plaintext");
    await fixture.Vault.VerifyRecordAsync(stored.ConnectionRef);
    await ThrowsCodeAsync(async () => _ = await fixture.Vault.StoreAsync(
        new CredentialWriteIntent("session-1", Binding(fixture.Principal), stored.ConnectionRef, 0),
        Encoding.UTF8.GetBytes("replacement")), "revision_conflict");
    var replaced = await fixture.Vault.StoreAsync(
        new CredentialWriteIntent("session-1", Binding(fixture.Principal), stored.ConnectionRef, 1),
        Encoding.UTF8.GetBytes("replacement"));
    Equal(2L, replaced.Revision, "replacement revision");
});

await suite.RunAsync("native change review is session-bound single-use and secret-free", async () =>
{
    using var fixture = new VaultFixture();
    using var broker = new NativeCredentialBrokerV2(fixture.Vault);
    const string sentinel = "native-only-secret";
    var ticket = broker.BeginChangeReview(
        new CredentialWriteIntent("window-1", Binding(fixture.Principal), null, 0),
        new CredentialSecret(Encoding.UTF8.GetBytes(sentinel)));
    var serializedTicket = JsonSerializer.Serialize(ticket);
    True(!serializedTicket.Contains(sentinel, StringComparison.Ordinal), "review ticket excludes secret");
    True(!serializedTicket.Contains(fixture.Principal.UserSid, StringComparison.Ordinal), "review ticket excludes Windows principal");
    var stored = await broker.CommitChangeAsync(ticket.ReviewHandle, "window-1");
    Equal(1L, stored.Revision, "review commit stored record");
    await ThrowsCodeAsync(async () => _ = await broker.CommitChangeAsync(ticket.ReviewHandle, "window-1"), "review_unavailable");

    var foreign = broker.BeginChangeReview(
        new CredentialWriteIntent("window-1", Binding(fixture.Principal), stored.ConnectionRef, 1),
        new CredentialSecret(Encoding.UTF8.GetBytes("replacement")));
    await ThrowsCodeAsync(async () => _ = await broker.CommitChangeAsync(foreign.ReviewHandle, "window-2"), "review_binding_mismatch");
    await ThrowsCodeAsync(async () => _ = await broker.CommitChangeAsync(foreign.ReviewHandle, "window-1"), "review_unavailable");
});

await suite.RunAsync("expired and cancelled reviews zero and fail closed", async () =>
{
    using var fixture = new VaultFixture();
    var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-10T12:00:00Z"));
    using var broker = new NativeCredentialBrokerV2(fixture.Vault, clock);
    var expired = broker.BeginChangeReview(
        new CredentialWriteIntent("window-1", Binding(fixture.Principal), null, 0),
        new CredentialSecret(Encoding.UTF8.GetBytes("short-lived")),
        TimeSpan.FromSeconds(10));
    clock.Advance(TimeSpan.FromSeconds(11));
    await ThrowsCodeAsync(async () => _ = await broker.CommitChangeAsync(expired.ReviewHandle, "window-1"), "review_unavailable");

    var cancelled = broker.BeginChangeReview(
        new CredentialWriteIntent("window-1", Binding(fixture.Principal), null, 0),
        new CredentialSecret(Encoding.UTF8.GetBytes("cancelled")));
    True(broker.CancelReview(cancelled.ReviewHandle, "window-1"), "review cancelled");
    await ThrowsCodeAsync(async () => _ = await broker.CommitChangeAsync(cancelled.ReviewHandle, "window-1"), "review_unavailable");
});

await suite.RunAsync("remove requires a fresh one-use revision-bound review", async () =>
{
    using var fixture = new VaultFixture();
    using var broker = new NativeCredentialBrokerV2(fixture.Vault);
    var change = broker.BeginChangeReview(
        new CredentialWriteIntent("window-1", Binding(fixture.Principal), null, 0),
        new CredentialSecret(Encoding.UTF8.GetBytes("remove-me")));
    var stored = await broker.CommitChangeAsync(change.ReviewHandle, "window-1");
    await ThrowsCodeAsync(async () => _ = await broker.BeginRemoveReviewAsync(
        new CredentialRemoveIntent("window-1", stored.ConnectionRef, 0)), "revision_conflict");
    var remove = await broker.BeginRemoveReviewAsync(new CredentialRemoveIntent("window-1", stored.ConnectionRef, 1));
    await broker.CommitRemoveAsync(remove.ReviewHandle, "window-1");
    True(await fixture.Vault.FindAsync(stored.ConnectionRef) is null, "record removed");
    await ThrowsCodeAsync(async () => await broker.CommitRemoveAsync(remove.ReviewHandle, "window-1"), "review_unavailable");
});

await suite.RunAsync("runtime lease is exact-purpose revision and principal bound", async () =>
{
    using var fixture = new VaultFixture();
    const string sentinel = "lease-only-value";
    var stored = await fixture.Vault.StoreAsync(
        new CredentialWriteIntent("window-1", Binding(fixture.Principal), null, 0),
        Encoding.UTF8.GetBytes(sentinel));
    using var lease = await fixture.Vault.ResolveLeaseAsync(new CredentialLeaseRequest(
        fixture.Principal, stored.ConnectionRef, "model:chat", stored.Revision));
    var copied = new byte[lease.SecretLength];
    try
    {
        lease.CopySecretTo(copied);
        Equal(sentinel, Encoding.UTF8.GetString(copied), "lease value");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(copied);
    }
    await ThrowsCodeAsync(async () => _ = await fixture.Vault.ResolveLeaseAsync(new CredentialLeaseRequest(
        fixture.Principal, stored.ConnectionRef, "mcp:filesystem", stored.Revision)), "purpose_denied");
    await ThrowsCodeAsync(async () => _ = await fixture.Vault.ResolveLeaseAsync(new CredentialLeaseRequest(
        fixture.Principal, stored.ConnectionRef, "model:chat", stored.Revision + 1)), "revision_conflict");
    var foreign = new CredentialPrincipalBinding("S-1-5-21-2000", "machine-1", "install-1", "profile-1");
    await ThrowsCodeAsync(async () => _ = await fixture.Vault.ResolveLeaseAsync(new CredentialLeaseRequest(
        foreign, stored.ConnectionRef, "model:chat", stored.Revision)), "principal_mismatch");
});

await suite.RunAsync("renderer frames expose metadata and no read reveal or export operation", () =>
{
    var reference = CredentialReference.Create();
    var metadata = new CredentialMetadata(reference, "profile-1", "openai", "primary", CredentialAuthKind.ApiKey,
        CredentialSourceKind.Native, ["model:chat", "oauth-refresh"], 7, DateTimeOffset.Parse("2026-08-10T12:00:00Z"));
    var json = HermesConnectionsRendererProtocol.SerializeMetadataResult("request-1", [metadata]);
    using var document = JsonDocument.Parse(json);
    Equal("connections.list.result", document.RootElement.GetProperty("type").GetString(), "frame type");
    True(json.Contains(reference.Value, StringComparison.Ordinal), "opaque reference included");
    True(json.Contains("\"authKind\":\"api-key\"", StringComparison.Ordinal), "wire auth kind is kebab case");
    True(json.Contains("\"sourceKind\":\"native\"", StringComparison.Ordinal), "wire source kind is stable");
    foreach (var forbidden in new[] { "secret", "token", "password", "reveal", "export", "userSid", "machineId", "installId", "protectedPayload" })
    {
        True(!json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"renderer excludes {forbidden}");
    }
    return Task.CompletedTask;
});

if (OperatingSystem.IsWindows())
{
    await suite.RunAsync("CurrentUser DPAPI and strict ACL storage round-trip on Windows", async () =>
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows SID unavailable.");
        var root = Path.Combine(Path.GetTempPath(), $"hcb-v2-smoke-{Guid.NewGuid():N}");
        try
        {
            var principal = new CredentialPrincipalBinding(sid, "smoke-machine", "smoke-install", "profile-1");
            using var vault = new DpapiCredentialVaultV2(root, principal, new CurrentUserDpapiRecordProtector(), new StrictWindowsCredentialStorageSecurity());
            var stored = await vault.StoreAsync(
                new CredentialWriteIntent("window-1", Binding(principal), null, 0),
                Encoding.UTF8.GetBytes("dpapi-smoke-sentinel"));
            await vault.VerifyRecordAsync(stored.ConnectionRef);
            var recordText = await File.ReadAllTextAsync(Directory.EnumerateFiles(root, "*.hcv2").Single());
            True(!recordText.Contains("dpapi-smoke-sentinel", StringComparison.Ordinal), "DPAPI record excludes plaintext");
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(root);
            var expectedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolvedRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolvedRoot).StartsWith("hcb-v2-smoke-", StringComparison.Ordinal)
                && Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, recursive: true);
        }
    });
}

suite.Complete();

static CredentialPrincipalBinding Principal() => new("S-1-5-21-1000", "machine-1", "install-1", "profile-1");

static CredentialBinding Binding(CredentialPrincipalBinding principal) => new(
    principal,
    "openai",
    "primary",
    CredentialAuthKind.OAuth,
    CredentialSourceKind.Native,
    ["oauth-refresh", "model:chat", "model:chat"]);

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (expected is IEnumerable<string> expectedStrings && actual is IEnumerable<string> actualStrings)
    {
        if (!expectedStrings.SequenceEqual(actualStrings, StringComparer.Ordinal)) throw new InvalidOperationException(message);
        return;
    }
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void ThrowsCode(Action action, string code)
{
    try { action(); }
    catch (CredentialBrokerException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException($"Expected credential error {code}.");
}

static async Task ThrowsCodeAsync(Func<Task> action, string code)
{
    try { await action(); }
    catch (CredentialBrokerException exception) when (exception.Code == code) { return; }
    throw new InvalidOperationException($"Expected credential error {code}.");
}

sealed class VaultFixture : IDisposable
{
    internal VaultFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"hcb-v2-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Principal = new CredentialPrincipalBinding("S-1-5-21-1000", "machine-1", "install-1", "profile-1");
        Vault = new DpapiCredentialVaultV2(Root, Principal, new SmokeProtector(), new SmokeStorageSecurity());
    }

    internal string Root { get; }
    internal CredentialPrincipalBinding Principal { get; }
    internal DpapiCredentialVaultV2 Vault { get; }

    public void Dispose()
    {
        Vault.Dispose();
        var resolvedRoot = Path.GetFullPath(Root);
        var expectedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (resolvedRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(resolvedRoot).StartsWith("hcb-v2-fake-", StringComparison.Ordinal)
            && Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, recursive: true);
    }
}

sealed class SmokeProtector : ICredentialRecordProtector
{
    public byte[] Protect(byte[] plaintext, byte[] entropy) => Transform(plaintext, entropy);
    public byte[] Unprotect(byte[] protectedBytes, byte[] entropy) => Transform(protectedBytes, entropy);

    private static byte[] Transform(byte[] source, byte[] entropy)
    {
        var key = SHA256.HashData(entropy);
        try
        {
            var result = new byte[source.Length];
            for (var index = 0; index < source.Length; index++) result[index] = (byte)(source[index] ^ key[index % key.Length] ^ 0xA5);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

sealed class SmokeStorageSecurity : ICredentialStorageSecurity
{
    public void PrepareRoot(string rootPath, string expectedUserSid) => Directory.CreateDirectory(rootPath);
    public void SecureFile(string rootPath, string filePath, string expectedUserSid) => ValidatePath(rootPath, filePath, expectedUserSid);
    public void ValidatePath(string rootPath, string path, string expectedUserSid)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("path escaped fake root");
    }
}

sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
{
    private DateTimeOffset _current = current;
    public override DateTimeOffset GetUtcNow() => _current;
    internal void Advance(TimeSpan amount) => _current = _current.Add(amount);
}

sealed class SmokeSuite
{
    private int _passed;
    private int _failed;

    internal async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal void Complete()
    {
        Console.WriteLine($"RESULT {_passed} passed, {_failed} failed");
        if (_failed != 0) Environment.ExitCode = 1;
    }
}
