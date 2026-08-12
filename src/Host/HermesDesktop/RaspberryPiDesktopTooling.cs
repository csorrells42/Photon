using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HermesDeveloperServices;
using HermesDeveloperServices.LanguageTooling;

namespace HermesDesktop;

/// <summary>
/// Native-only Raspberry Pi target custody. Renderer code can request setup and pass an opaque
/// target id to inspection, but it cannot supply SSH material, host keys, or remote commands.
/// </summary>
internal sealed class RaspberryPiDesktopTooling : ILanguageToolingEvidenceSource, ILanguageToolingOperationHandler
{
    internal const string FixedProviderId = "raspberry-pi";
    private const string CredentialProvider = "raspberry-pi";
    private readonly string _installRoot;
    private readonly RaspberryPiTargetStore _store;
    private readonly WindowsCredentialVault _vault;

    internal RaspberryPiDesktopTooling(string applicationInstallRoot, WindowsCredentialVault vault)
    {
        _installRoot = Path.GetFullPath(applicationInstallRoot);
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _store = new RaspberryPiTargetStore();
    }

    public string ProviderId => FixedProviderId;

    public IReadOnlyCollection<string> CapabilityIds { get; } = ["raspberry-pi.inspect", "raspberry-pi.deploy"];

    public IReadOnlyCollection<string> Operations { get; } = ["inspect-remote-target"];

    internal RaspberryPiSetupResult Configure(Window owner)
    {
        var dialog = new RaspberryPiSetupDialog { Owner = owner };
        if (dialog.ShowDialog() != true)
            return new(false, "cancelled", "Raspberry Pi setup was cancelled.", null);

        RaspberryPiSetupDraft? draft = null;
        try
        {
            draft = dialog.TakeDraft();
            var target = RaspberryPiTargetStore.ValidateDraft(draft);
            _vault.Save(CredentialProvider, target.CredentialId, draft.PrivateKey);
            _store.Save(target);
            return new(true, "configured", "The trusted Raspberry Pi target was saved. Inspection becomes available when the receipt-bound OpenSSH runtime is installed.", target.TargetId);
        }
        catch (ArgumentException exception)
        {
            return new(false, "invalid-configuration", exception.Message, null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(false, "credential-save-failed", "Windows Credential Manager could not save the SSH credential reference.", null);
        }
        catch (IOException)
        {
            return new(false, "configuration-save-failed", "The trusted Raspberry Pi target configuration could not be saved.", null);
        }
        finally
        {
            draft?.PrivateKey.Dispose();
            dialog.ClearSensitiveValues();
        }
    }

    public async ValueTask<IReadOnlyList<LanguageToolingCapabilityStatus>> InspectAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        _ = workspaceRoot;
        if (!_store.TryLoad(out var target))
            return Unavailable("trusted-target-not-configured", "Configure one host-owned Raspberry Pi target and SSH credential reference.");
        if (!File.Exists(Path.Combine(_installRoot, "toolchains", "openssh", "hermes-toolchain-receipt.json")))
            return Unavailable("openssh-receipt-missing", "Receipt-bound Windows OpenSSH is not installed for Raspberry Pi inspection.");

        try
        {
            await using var lease = CreateLease(target);
            _ = await RaspberryPiTrustedHostProvider.CreateAsync(lease.Options, cancellationToken).ConfigureAwait(false);
            return
            [
                new("raspberry-pi.inspect", LanguageToolingCapabilityState.Available, "ok", "The trusted host verified the receipt-bound OpenSSH runtime and configured Raspberry Pi target."),
                new("raspberry-pi.deploy", LanguageToolingCapabilityState.Unavailable, "review-required", "Deployment remains unavailable until an explicit native review and one-use commit authority is mounted."),
            ];
        }
        catch (RaspberryPiCredentialException exception)
        {
            return Unavailable(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or CryptographicException or InvalidOperationException)
        {
            return Unavailable("raspberry-pi-verification-failed", "The trusted Raspberry Pi runtime could not be verified.");
        }
    }

    public async ValueTask<LanguageToolingOperationResult> ExecuteAsync(LanguageToolingHostRequest request, CancellationToken cancellationToken)
    {
        if (request.ProviderId != FixedProviderId || request is not InspectLanguageToolingRemoteTargetRequest inspect)
            throw new LanguageToolingRequestException("operation-mismatch", "The Raspberry Pi host accepts only typed target inspection requests.");
        if (!_store.TryLoad(out var target) || !target.TargetId.Equals(inspect.TargetId, StringComparison.Ordinal))
            return new(false, "target-not-configured", "The requested Raspberry Pi target is not configured by this desktop host.");

        try
        {
            await using var lease = CreateLease(target);
            var provider = await RaspberryPiTrustedHostProvider.CreateAsync(lease.Options, cancellationToken).ConfigureAwait(false);
            var result = await provider.ProbeAsync(target.ToTrustedHost(), cancellationToken).ConfigureAwait(false);
            return new(result.Succeeded, result.FailureCode ?? (result.Succeeded ? "ok" : "raspberry-pi-inspection-failed"), result.Summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RaspberryPiCredentialException exception)
        {
            return new(false, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return new(false, "raspberry-pi-authority-failed", "The trusted Raspberry Pi authority could not be established safely.");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private RaspberryPiCredentialLease CreateLease(RaspberryPiTargetConfiguration target)
    {
        var privateKey = _vault.ReadSecret(CredentialProvider, target.CredentialId);
        if (string.IsNullOrWhiteSpace(privateKey))
            throw new RaspberryPiCredentialException("credential-not-configured", "The host-owned SSH credential reference is unavailable.");
        return RaspberryPiCredentialLease.Create(_installRoot, target, privateKey);
    }

    private static IReadOnlyList<LanguageToolingCapabilityStatus> Unavailable(string code, string message) =>
    [
        new("raspberry-pi.inspect", LanguageToolingCapabilityState.Unavailable, code, message),
        new("raspberry-pi.deploy", LanguageToolingCapabilityState.Unavailable, code, message),
    ];
}

internal sealed record RaspberryPiSetupResult(bool Succeeded, string Code, string Message, string? TargetId);

internal sealed record RaspberryPiSetupDraft(
    string TargetId, string Host, string User, string Port, string HostKeyAlgorithm,
    string HostKeyFingerprint, string KnownHostEntry, string CredentialId, SecureString PrivateKey);

internal sealed record RaspberryPiTargetConfiguration(
    string TargetId, string Host, string User, int Port, string HostKeyAlgorithm,
    string HostKeyFingerprint, string KnownHostEntry, string CredentialId)
{
    internal RaspberryPiTrustedHost ToTrustedHost() => new(Host, User, Port, new(HostKeyAlgorithm, HostKeyFingerprint));
}

internal sealed partial class RaspberryPiTargetStore
{
    private const string FileName = "trusted-target.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HermesWorkbench", "raspberry-pi");

    internal bool TryLoad(out RaspberryPiTargetConfiguration target)
    {
        target = null!;
        var path = Path.Combine(_root, FileName);
        if (!File.Exists(path)) return false;
        try
        {
            var value = JsonSerializer.Deserialize<RaspberryPiTargetConfiguration>(File.ReadAllText(path), SerializerOptions);
            if (value is null) return false;
            target = Validate(value);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    internal void Save(RaspberryPiTargetConfiguration target)
    {
        target = Validate(target);
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, FileName);
        var temporary = Path.Combine(_root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(target, SerializerOptions), new UTF8Encoding(false));
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static RaspberryPiTargetConfiguration ValidateDraft(RaspberryPiSetupDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.PrivateKey.Length == 0) throw new ArgumentException("An SSH private key is required.");
        return Validate(new RaspberryPiTargetConfiguration(
            draft.TargetId, draft.Host, draft.User,
            int.TryParse(draft.Port, out var port) ? port : 0,
            draft.HostKeyAlgorithm, draft.HostKeyFingerprint, draft.KnownHostEntry, draft.CredentialId));
    }

    private static RaspberryPiTargetConfiguration Validate(RaspberryPiTargetConfiguration target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var targetId = RequireIdentifier(target.TargetId, "target ID");
        var credentialId = RequireIdentifier(target.CredentialId, "credential reference");
        var host = target.Host.Trim();
        var user = target.User.Trim();
        var algorithm = target.HostKeyAlgorithm.Trim();
        var fingerprint = target.HostKeyFingerprint.Trim();
        if (!HostPattern().IsMatch(host) || !UserPattern().IsMatch(user) || target.Port is < 1 or > 65535
            || !AlgorithmPattern().IsMatch(algorithm) || !FingerprintPattern().IsMatch(fingerprint))
            throw new ArgumentException("The trusted Raspberry Pi host identity is invalid.");

        var entry = target.KnownHostEntry.Trim();
        if (entry.Length is 0 or > 8_192 || entry.Any(char.IsControl))
            throw new ArgumentException("The pinned known-host entry is invalid.");
        var fields = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var expectedAlias = target.Port == 22 ? host : $"[{host}]:{target.Port}";
        if (fields.Length != 3 || !fields[0].Equals(expectedAlias, StringComparison.Ordinal)
            || !fields[1].Equals(algorithm, StringComparison.Ordinal))
            throw new ArgumentException("The pinned known-host entry must exactly match this host, port, and algorithm.");
        byte[] encoded;
        try { encoded = Convert.FromBase64String(fields[2]); }
        catch (FormatException) { throw new ArgumentException("The pinned known-host key is not valid base64."); }
        var actualFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(encoded)).TrimEnd('=');
        if (!actualFingerprint.Equals(fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("The pinned known-host key does not match the supplied SHA-256 fingerprint.");
        return new(targetId, host, user, target.Port, algorithm, fingerprint, entry, credentialId);
    }

    private static string RequireIdentifier(string value, string label)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is 0 or > 128 || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            throw new ArgumentException($"The {label} is invalid.");
        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex HostPattern();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,31}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex UserPattern();
    [GeneratedRegex("^(?:ssh-ed25519|ssh-rsa|rsa-sha2-256|rsa-sha2-512|ecdsa-sha2-nistp(?:256|384|521))$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex AlgorithmPattern();
    [GeneratedRegex("^SHA256:[A-Za-z0-9+/]{20,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex FingerprintPattern();
}

internal sealed class RaspberryPiCredentialLease : IAsyncDisposable
{
    private readonly string _directory;
    internal RaspberryPiProviderOptions Options { get; }

    private RaspberryPiCredentialLease(string directory, RaspberryPiProviderOptions options) => (_directory, Options) = (directory, options);

    internal static RaspberryPiCredentialLease Create(string installRoot, RaspberryPiTargetConfiguration target, string privateKey)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HermesWorkbench", "raspberry-pi", "sessions");
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "identity"), privateKey, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "known_hosts"), target.KnownHostEntry + Environment.NewLine, new UTF8Encoding(false));
            return new RaspberryPiCredentialLease(directory, new RaspberryPiProviderOptions(
                installRoot, "toolchains/openssh", "hermes-toolchain-receipt.json", directory, "identity", "known_hosts"));
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        TryDelete(_directory);
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) Directory.Delete(directory, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class RaspberryPiCredentialException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class RaspberryPiSetupDialog : Window
{
    private readonly TextBox _targetId = Field("shop-pi");
    private readonly TextBox _host = Field("raspberrypi.local");
    private readonly TextBox _user = Field("pi");
    private readonly TextBox _port = Field("22");
    private readonly TextBox _algorithm = Field("ssh-ed25519");
    private readonly TextBox _fingerprint = Field();
    private readonly TextBox _knownHost = Field();
    private readonly TextBox _credentialId = Field("primary");
    private readonly PasswordBox _privateKey = new() { MinWidth = 410, MinHeight = 100 };

    internal RaspberryPiSetupDialog()
    {
        Title = "Configure trusted Raspberry Pi";
        Width = 570; Height = 720; MinWidth = 540; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Trusted Raspberry Pi setup", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "The SSH private key is saved directly to Windows Credential Manager. It is never sent to the Workbench renderer or stored in this target configuration.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        Add(panel, "Target ID", _targetId); Add(panel, "Host", _host); Add(panel, "SSH user", _user); Add(panel, "Port", _port);
        Add(panel, "Host-key algorithm", _algorithm); Add(panel, "Host-key SHA-256 fingerprint", _fingerprint); Add(panel, "Pinned known-host entry", _knownHost); Add(panel, "Credential reference", _credentialId);
        Add(panel, "SSH private key", _privateKey);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Save trusted target", IsDefault = true, MinWidth = 150 };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    internal RaspberryPiSetupDraft TakeDraft() => new(_targetId.Text, _host.Text, _user.Text, _port.Text, _algorithm.Text, _fingerprint.Text, _knownHost.Text, _credentialId.Text, _privateKey.SecurePassword.Copy());
    internal void ClearSensitiveValues() => _privateKey.Clear();

    private static TextBox Field(string value = "") => new() { Text = value, MinWidth = 410 };
    private static void Add(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 3) });
        panel.Children.Add(control);
    }
}
