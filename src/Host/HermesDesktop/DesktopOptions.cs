using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HermesDesktop;

internal sealed record DesktopOptions(
    Uri WorkbenchUri,
    string WorkspacePath,
    string ApplicationInstallRoot,
    string WorkbenchNonce)
{
    private static readonly Uri DefaultWorkbenchUri = new("http://127.0.0.1:4173/");

    public static DesktopOptions Parse(IReadOnlyList<string> args)
    {
        string? supplied = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].Equals("--url", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                supplied = args[index + 1];
                break;
            }
        }

        supplied ??= Environment.GetEnvironmentVariable("HERMES_WORKBENCH_URL");
        if (!Uri.TryCreate(supplied, UriKind.Absolute, out var uri)) uri = DefaultWorkbenchUri;
        if (!IsSupportedWorkbenchOrigin(uri))
        {
            throw new ArgumentException("HermesDesktop only opens the launcher-owned Workbench origin.");
        }
        var nonce = Environment.GetEnvironmentVariable("HERMES_WORKBENCH_NONCE")?.Trim() ?? string.Empty;
        if (!IsValidWorkbenchNonce(nonce))
        {
            throw new ArgumentException("HermesDesktop requires a launcher-issued Workbench identity nonce.");
        }

        var workspace = Environment.GetEnvironmentVariable("HERMES_WORKSPACE_PATH");
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].Equals("--workspace", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                workspace = args[index + 1];
                break;
            }
        }

        workspace = string.IsNullOrWhiteSpace(workspace) ? Directory.GetCurrentDirectory() : workspace;
        workspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException($"Hermes workspace was not found: {workspace}");
        }

        var installRoot = Environment.GetEnvironmentVariable("HERMES_INSTALL_ROOT");
        installRoot = string.IsNullOrWhiteSpace(installRoot) ? workspace : installRoot;
        installRoot = Path.GetFullPath(installRoot);
        if (!Directory.Exists(installRoot))
        {
            throw new DirectoryNotFoundException($"Hermes installation root was not found: {installRoot}");
        }
        if ((File.GetAttributes(installRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("The Hermes installation root must not be a reparse point.");
        }
        return new DesktopOptions(uri, workspace, installRoot, nonce.ToLowerInvariant());
    }

    public static bool IsTrustedWorkbenchUri(Uri uri) =>
        uri.IsAbsoluteUri
        && (uri.Scheme is "http" or "https")
        && uri.IsLoopback
        && string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsSameWorkbenchOrigin(Uri expected, Uri candidate) =>
        IsTrustedWorkbenchUri(expected)
        && IsTrustedWorkbenchUri(candidate)
        && string.Equals(expected.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)
        && EffectivePort(expected) == EffectivePort(candidate);

    public static bool IsSupportedWorkbenchOrigin(Uri candidate) =>
        IsSameWorkbenchOrigin(DefaultWorkbenchUri, candidate);

    public static bool CanAuthorizeWorkbenchNavigation(
        ulong completedNavigationId,
        ulong activeNavigationId,
        Uri expectedOrigin,
        Uri completedSource,
        Uri currentSource) =>
        completedNavigationId == activeNavigationId
        && IsSameWorkbenchOrigin(expectedOrigin, completedSource)
        && IsSameWorkbenchOrigin(expectedOrigin, currentSource);

    public static bool IsValidWorkbenchNonce(string? nonce) =>
        nonce is { Length: 64 } && nonce.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public static string CreateWorkbenchIdentityProof(string nonce, string challenge)
    {
        if (!IsValidWorkbenchNonce(nonce) || !IsValidWorkbenchNonce(challenge)) return string.Empty;
        using var hmac = new HMACSHA256(Convert.FromHexString(nonce));
        return Convert.ToHexString(hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"hermes-workbench-v1:{challenge.ToLowerInvariant()}"))).ToLowerInvariant();
    }

    private static int EffectivePort(Uri uri) => uri.IsDefaultPort
        ? uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
        : uri.Port;
}
