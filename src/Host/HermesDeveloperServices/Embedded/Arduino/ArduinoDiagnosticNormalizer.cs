using System.Text.RegularExpressions;

namespace HermesDeveloperServices;

internal static partial class ArduinoDiagnosticNormalizer
{
    private const int MaximumDiagnostics = 200;

    [GeneratedRegex("^(?<file>(?:[A-Za-z]:)?[^:]+):(?<line>[0-9]+):(?:(?<column>[0-9]+):)?\\s*(?<severity>fatal error|error|warning|note):\\s*(?:(?<code>[-A-Za-z0-9_]+):\\s*)?(?<message>.+)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DiagnosticPattern();

    public static IReadOnlyList<EmbeddedDiagnostic> Parse(
        string workspaceRoot,
        params string[] streams)
    {
        var diagnostics = new List<EmbeddedDiagnostic>();
        var seen = new HashSet<EmbeddedDiagnostic>();
        foreach (var rawLine in streams.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
        {
            if (diagnostics.Count >= MaximumDiagnostics) break;
            if (rawLine.Length > 4096) continue;
            var line = Sanitize(rawLine.Trim());
            var match = DiagnosticPattern().Match(line);
            if (!match.Success
                || !TrustedToolchainPathPolicy.TryWorkspaceRelative(workspaceRoot, match.Groups["file"].Value, out var relative))
            {
                continue;
            }

            var severity = match.Groups["severity"].Value switch
            {
                "warning" => EmbeddedDiagnosticSeverity.Warning,
                "note" => EmbeddedDiagnosticSeverity.Info,
                _ => EmbeddedDiagnosticSeverity.Error,
            };
            var diagnostic = new EmbeddedDiagnostic(
                relative,
                ParsePositive(match.Groups["line"].Value),
                ParsePositive(match.Groups["column"].Value),
                severity,
                EmptyToNull(Sanitize(match.Groups["code"].Value)),
                Bound(Sanitize(match.Groups["message"].Value.Trim()), 2_000));
            if (seen.Add(diagnostic)) diagnostics.Add(diagnostic);
        }

        return diagnostics.ToArray();
    }

    public static string RedactPaths(string text, params string[] paths)
    {
        var redacted = text;
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).OrderByDescending(path => path.Length))
        {
            redacted = redacted.Replace(path, "<trusted-path>",
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        return redacted;
    }

    private static int? ParsePositive(string value) =>
        int.TryParse(value, out var number) && number > 0 ? number : null;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static string Sanitize(string value) => new(value.Where(character => !char.IsControl(character)).ToArray());
}
