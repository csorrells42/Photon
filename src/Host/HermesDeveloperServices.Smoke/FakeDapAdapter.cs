using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HermesDeveloperServices;

internal sealed class FakeDapAdapter : IDapMessageTransport
{
    private readonly Channel<DapIncomingMessage> _messages = Channel.CreateUnbounded<DapIncomingMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private int _nextSequence;

    public ConcurrentQueue<string> Commands { get; } = new();

    public bool HoldEvaluateResponses { get; set; }

    public ValueTask SendAsync(DapRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Enqueue(request.Command);
        switch (request.Command)
        {
            case "initialize":
                Respond(request, new DapAdapterCapabilities(
                    SupportsConfigurationDoneRequest: true,
                    SupportsEvaluateForHovers: true,
                    SupportsCancelRequest: true));
                break;

            case "launch":
            case "attach":
                Respond(request);
                Emit("initialized");
                break;

            case "setBreakpoints":
                Respond(request, new
                {
                    breakpoints = new[]
                    {
                        new DapBreakpoint(1, true, null, new DapSource("Program.cs", @"C:\fixture\Program.cs"), 10, 1),
                    },
                });
                break;

            case "configurationDone":
            case "continue":
            case "next":
            case "stepIn":
            case "stepOut":
            case "disconnect":
            case "cancel":
                Respond(request);
                break;

            case "threads":
                Respond(request, new { threads = new[] { new DapThread(1, "main") } });
                break;

            case "stackTrace":
                Respond(request, new
                {
                    stackFrames = new[]
                    {
                        new DapStackFrame(100, "Main", new DapSource("Program.cs", @"C:\fixture\Program.cs"), 10, 1),
                    },
                    totalFrames = 1,
                });
                break;

            case "scopes":
                Respond(request, new { scopes = new[] { new DapScope("Locals", 200, false) } });
                break;

            case "variables":
                Respond(request, new
                {
                    variables = new[] { new DapVariable("answer", "42", "int", 0, "answer") },
                });
                break;

            case "evaluate" when HoldEvaluateResponses:
                break;

            case "evaluate":
                Respond(request, new DapEvaluateResult("42", "int", 0));
                break;

            default:
                _messages.Writer.TryWrite(new DapIncomingResponse(new DapResponse(
                    Interlocked.Increment(ref _nextSequence),
                    request.Seq,
                    false,
                    request.Command,
                    "unsupported")));
                break;
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<DapIncomingMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    public ValueTask EmitStoppedAsync()
    {
        Emit("stopped", new { reason = "breakpoint", threadId = 1, allThreadsStopped = true });
        return ValueTask.CompletedTask;
    }

    public void Complete() => _messages.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Respond(DapRequest request, object? body = null)
    {
        var element = body is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(body);
        _messages.Writer.TryWrite(new DapIncomingResponse(new DapResponse(
            Interlocked.Increment(ref _nextSequence),
            request.Seq,
            true,
            request.Command,
            Body: element)));
    }

    private void Emit(string eventName, object? body = null)
    {
        var element = body is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(body);
        _messages.Writer.TryWrite(new DapIncomingEvent(new DapEvent(
            Interlocked.Increment(ref _nextSequence),
            eventName,
            element)));
    }
}

internal sealed class SyntheticToolchainProvider : IToolchainProvider
{
    public SyntheticToolchainProvider(string providerId, string languageId, string projectKind, bool build)
    {
        Descriptor = new ToolchainProviderDescriptor(
            DeveloperServicesProtocol.ToolchainProviderVersion,
            providerId,
            providerId,
            "1.0",
            new[] { languageId },
            new[] { projectKind },
            new ToolchainBuildCapabilities(build, build, build, build ? new[] { projectKind } : Array.Empty<string>()),
            new ToolchainLspCapabilities(true, true, true, true, true, true, true, "3.17"),
            new ToolchainDapCapabilities(true, true, true, true, true, "1.x"),
            new[] { ToolchainExecutionKind.LocalSidecarProcess });
    }

    public ToolchainProviderDescriptor Descriptor { get; }

    public ToolchainLifecycleState LifecycleState { get; private set; } = ToolchainLifecycleState.Created;

    public ToolchainAvailability Availability { get; private set; } = new(
        ToolchainAvailabilityState.Available,
        null,
        null,
        DateTimeOffset.UtcNow);

    public ValueTask<ToolchainExecutableDiscoveryResult> DiscoverExecutablesAsync(
        ToolchainDiscoveryContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ToolchainExecutableDiscoveryResult(Availability, Array.Empty<ResolvedToolchainExecutable>()));

    public ValueTask StartAsync(ToolchainStartContext context, CancellationToken cancellationToken)
    {
        LifecycleState = ToolchainLifecycleState.Ready;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        LifecycleState = ToolchainLifecycleState.Stopped;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        LifecycleState = ToolchainLifecycleState.Stopped;
        return ValueTask.CompletedTask;
    }
}
