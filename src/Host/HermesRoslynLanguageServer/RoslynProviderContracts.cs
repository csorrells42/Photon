using System.Text.Json;
using HermesDeveloperServices;

namespace HermesRoslynLanguageServer;

public sealed record RoslynLanguageServerConfiguration(
    string InstallerRoot,
    string ExecutablePath,
    string ExpectedSha256,
    string PackageVersion,
    string DotnetRoot,
    string? ExtensionLogDirectory = null,
    string? TemporaryDirectory = null);

public sealed record RoslynNegotiatedCapabilities(
    bool DocumentSynchronization,
    bool Diagnostics,
    bool Completion,
    bool Hover,
    bool Definition,
    bool References,
    bool Rename,
    bool CodeActions)
{
    public bool SupportsRequiredSurface =>
        DocumentSynchronization && Diagnostics && Completion && Hover && Definition
        && References && Rename && CodeActions;
}

public sealed record RoslynLanguageResult(JsonElement Value);

internal sealed record RoslynServerLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    int MaximumStandardErrorCharacters);

internal interface IRoslynServerTransport : ILspMessageTransport
{
    bool HasExited { get; }

    string StandardError { get; }

    long DroppedStandardErrorCharacters { get; }
}

internal interface IRoslynServerTransportFactory
{
    ValueTask<IRoslynServerTransport> StartAsync(
        RoslynServerLaunchSpec launchSpec,
        CancellationToken cancellationToken);
}
