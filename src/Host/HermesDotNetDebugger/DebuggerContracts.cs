using HermesDeveloperServices;

namespace HermesDotNetDebugger;

public sealed record DotNetDebugLaunchRequest(
    string ProgramPath,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? Arguments = null,
    bool StopAtEntry = false);

public sealed record DotNetDebugAttachRequest(int ProcessId);

public interface IDotNetDebugAuthorizationPolicy
{
    ValueTask<bool> AuthorizeLaunchAsync(
        string workspaceRoot,
        DotNetDebugLaunchRequest request,
        CancellationToken cancellationToken);

    ValueTask<bool> AuthorizeAttachAsync(
        string workspaceRoot,
        DotNetDebugAttachRequest request,
        CancellationToken cancellationToken);
}

public sealed class DotNetDebuggerAuthorizationException : Exception
{
    public DotNetDebuggerAuthorizationException(string message) : base(message) { }
}

public sealed class DotNetDebuggerUnavailableException : Exception
{
    public DotNetDebuggerUnavailableException(string message) : base(message) { }
}

public sealed class DotNetDebuggerOperationException : Exception
{
    public DotNetDebuggerOperationException(string message) : base(message) { }

    public DotNetDebuggerOperationException(string message, Exception innerException) : base(message, innerException) { }
}

internal interface IOwnedDapMessageTransport : IDapMessageTransport
{
    int? ProcessId { get; }

    bool HasExited { get; }

    string StandardError { get; }

    long DroppedStandardErrorCharacters { get; }
}

internal interface INetCoreDbgTransportFactory
{
    ValueTask<IOwnedDapMessageTransport> StartAsync(
        NetCoreDbgInstallation installation,
        CancellationToken cancellationToken);
}
