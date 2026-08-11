using System.Text.RegularExpressions;

namespace HermesGitServices;

internal static partial class GitErrorRedactor
{
    private const int MaximumCharacters = 512;

    [GeneratedRegex(@"(?i)(https?://)[^/@\s]+@")]
    private static partial Regex UriUserInfo();

    [GeneratedRegex(@"(?i)(token|password|passwd|secret|key|sig)=([^&\s]+)")]
    private static partial Regex SensitiveAssignment();

    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\\r\n]+")]
    private static partial Regex WindowsHome();

    internal static string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "The Git operation failed.";
        var redacted = UriUserInfo().Replace(value, "$1[redacted]@");
        redacted = SensitiveAssignment().Replace(redacted, "$1=[redacted]");
        redacted = WindowsHome().Replace(redacted, "$1[redacted]");
        redacted = redacted.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return redacted.Length <= MaximumCharacters ? redacted : redacted[..MaximumCharacters];
    }
}
