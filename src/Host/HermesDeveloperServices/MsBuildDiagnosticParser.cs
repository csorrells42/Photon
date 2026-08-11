using System.Globalization;
using System.Text.RegularExpressions;

namespace HermesDeveloperServices;

public static partial class MsBuildDiagnosticParser
{
    [GeneratedRegex(
        @"^(?<file>.+)\((?<startLine>\d+),(?<startColumn>\d+)(?:,(?<endLine>\d+),(?<endColumn>\d+))?\):\s*(?<severity>error|warning|info)\s+(?<code>[A-Za-z0-9_.-]+):\s*(?<message>.*?)(?:\s+\[(?<project>[^\[\]]+)\])?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticPattern();

    /// <summary>
    /// Parses only the canonical MSBuild diagnostic shape. Non-diagnostic progress text is ignored.
    /// Line and column values remain one-based to match MSBuild and Monaco marker contracts.
    /// </summary>
    public static bool TryParse(string? line, out BuildDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = DiagnosticPattern().Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (!TryPositiveInt(match, "startLine", out var startLine)
            || !TryPositiveInt(match, "startColumn", out var startColumn))
        {
            return false;
        }

        var endLine = startLine;
        var endColumn = startColumn;
        if (match.Groups["endLine"].Success
            && (!TryPositiveInt(match, "endLine", out endLine)
                || !TryPositiveInt(match, "endColumn", out endColumn)))
        {
            return false;
        }

        var severity = match.Groups["severity"].Value.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            "info" => DiagnosticSeverity.Info,
            _ => throw new InvalidOperationException("The diagnostic severity is unsupported."),
        };

        diagnostic = new BuildDiagnostic(
            match.Groups["file"].Value,
            new SourceRange(
                new SourcePosition(startLine, startColumn),
                new SourcePosition(endLine, endColumn)),
            severity,
            match.Groups["code"].Value,
            match.Groups["message"].Value,
            match.Groups["project"].Success ? match.Groups["project"].Value : null);
        return true;
    }

    private static bool TryPositiveInt(Match match, string groupName, out int value) =>
        int.TryParse(
            match.Groups[groupName].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value)
        && value > 0;
}
