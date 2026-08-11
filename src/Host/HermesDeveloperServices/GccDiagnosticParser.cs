using System.Globalization;
using System.Text.RegularExpressions;

namespace HermesDeveloperServices;

internal static class GccDiagnosticParser
{
    private static readonly Regex DiagnosticPattern = new(
        @"^(?<file>.+?):(?<line>\d+)(?::(?<column>\d+))?:\s*(?<severity>fatal error|error|warning|note):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex WarningCodePattern = new(
        @"\s+\[(?<code>-W[^\]\s]+)\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static bool TryParse(string line, string workspaceRoot, out BuildDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(line) || line.Length > 16 * 1024) return false;
        line = Sanitize(line);
        Match match;
        try { match = DiagnosticPattern.Match(line); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!match.Success
            || !int.TryParse(match.Groups["line"].Value, out var lineNumber)
            || lineNumber < 1)
        {
            return false;
        }

        var columnNumber = 1;
        if (match.Groups["column"].Success
            && (!int.TryParse(match.Groups["column"].Value, out columnNumber) || columnNumber < 1))
        {
            return false;
        }

        var rawPath = match.Groups["file"].Value.Trim();
        if (rawPath.Length > 2_048
            || rawPath.StartsWith('<')
            || !TrustedToolchainPathPolicy.TryWorkspaceRelative(workspaceRoot, rawPath, out var relativePath))
        {
            return false;
        }

        var severity = match.Groups["severity"].Value.ToLowerInvariant() switch
        {
            "warning" => DiagnosticSeverity.Warning,
            "note" => DiagnosticSeverity.Info,
            _ => DiagnosticSeverity.Error,
        };
        var message = match.Groups["message"].Value.Trim();
        if (message.Length > 4_096) message = message[..4_096];
        var code = "gcc";
        try
        {
            var codeMatch = WarningCodePattern.Match(message);
            if (codeMatch.Success)
            {
                code = codeMatch.Groups["code"].Value;
                message = message[..codeMatch.Index].TrimEnd();
            }
        }
        catch (RegexMatchTimeoutException) { }

        diagnostic = new BuildDiagnostic(
            relativePath,
            new SourceRange(
                new SourcePosition(lineNumber, columnNumber),
                new SourcePosition(lineNumber, columnNumber)),
            severity,
            code,
            message,
            Project: null,
            Source: "gcc");
        return true;
    }

    private static string Sanitize(string value)
    {
        char[]? sanitized = null;
        for (var index = 0; index < value.Length; index++)
        {
            var category = char.GetUnicodeCategory(value[index]);
            if (!char.IsControl(value[index])
                && category is not UnicodeCategory.Format
                    and not UnicodeCategory.LineSeparator
                    and not UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }
            sanitized ??= value.ToCharArray();
            sanitized[index] = ' ';
        }
        return sanitized is null ? value : new string(sanitized);
    }
}
