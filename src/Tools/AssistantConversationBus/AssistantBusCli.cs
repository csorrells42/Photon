using System.Text.Json;

namespace AssistantConversationBus;

public static class AssistantBusCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            WriteHelp();
            return 0;
        }

        try
        {
            if (args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
                return await ServeAsync(args[1..], cancellationToken).ConfigureAwait(false);
            var settings = AssistantBusServiceSettings.LoadOrCreate();
            return args[0].ToLowerInvariant() switch
            {
                "send" => await SendAsync(settings, AssistantIdentity.Codex, args[1..], cancellationToken).ConfigureAwait(false),
                "relay" => await SendAsync(settings, AssistantIdentity.Chris, args[1..], cancellationToken).ConfigureAwait(false),
                "participants" => await ParticipantsAsync(settings, args[1..], cancellationToken).ConfigureAwait(false),
                "read" => await ReadAsync(settings, args[1..], cancellationToken).ConfigureAwait(false),
                "wait" => await WaitAsync(settings, args[1..], cancellationToken).ConfigureAwait(false),
                "cancel" => await CancelAsync(settings, args[1..], cancellationToken).ConfigureAwait(false),
                _ => Fail("Unknown assistant-bus command."),
            };
        }
        catch (AssistantBusValidationException exception)
        {
            return Fail($"{exception.Code}: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            return Fail(exception.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("The assistant-bus command was cancelled.", 6);
        }
    }

    private static async Task<int> SendAsync(
        AssistantBusServiceSettings settings,
        AssistantIdentity sender,
        string[] args,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--to", "--text");
        using var client = new AssistantBusHttpClient(settings, sender);
        var result = await client.SendAsync(
            new AssistantBusSendRequest(options["--to"], options["--text"]), cancellationToken).ConfigureAwait(false);
        WriteJson(result);
        return result.Deliveries.All(delivery => delivery.State is AssistantDeliveryState.Completed or AssistantDeliveryState.Delivered)
            ? 0
            : 5;
    }

    private static async Task<int> ParticipantsAsync(
        AssistantBusServiceSettings settings,
        string[] args,
        CancellationToken cancellationToken)
    {
        RequireNoArguments(args);
        using var client = new AssistantBusHttpClient(settings, AssistantIdentity.Codex);
        WriteJson(await client.ParticipantsAsync(cancellationToken).ConfigureAwait(false));
        return 0;
    }

    private static async Task<int> ReadAsync(
        AssistantBusServiceSettings settings,
        string[] args,
        CancellationToken cancellationToken)
    {
        var after = ParseLongOption(args, "--after", defaultValue: 0, minimum: 0, maximum: long.MaxValue);
        using var client = new AssistantBusHttpClient(settings, AssistantIdentity.Codex);
        WriteJson(await client.ReadAsync(after, AssistantBusLimits.MaximumReadMessages, cancellationToken).ConfigureAwait(false));
        return 0;
    }

    private static async Task<int> WaitAsync(
        AssistantBusServiceSettings settings,
        string[] args,
        CancellationToken cancellationToken)
    {
        var (after, seconds) = ParseWaitArguments(args);
        using var client = new AssistantBusHttpClient(settings, AssistantIdentity.Codex);
        var waitId = $"wait:{Guid.NewGuid():N}";
        Console.Error.WriteLine($"Assistant bus wait: {waitId}");
        WriteJson(await client.WaitAsync(
            after,
            checked((int)TimeSpan.FromSeconds(seconds).TotalMilliseconds),
            waitId,
            cancellationToken).ConfigureAwait(false));
        return 0;
    }

    internal static (long After, long Seconds) ParseWaitArguments(string[] args)
    {
        var options = ParseOptions(args, "--after", "--timeout");
        return (
            ParseLongValue(options["--after"], "--after", minimum: 0, maximum: long.MaxValue),
            ParseLongValue(options["--timeout"], "--timeout", minimum: 1, maximum: 1800));
    }

    private static async Task<int> CancelAsync(
        AssistantBusServiceSettings settings,
        string[] args,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--wait");
        using var client = new AssistantBusHttpClient(settings, AssistantIdentity.Codex);
        WriteJson(await client.CancelAsync(options["--wait"], cancellationToken).ConfigureAwait(false));
        return 0;
    }

    private static async Task<int> ServeAsync(string[] args, CancellationToken cancellationToken)
    {
        RequireNoArguments(args);
        var settings = AssistantBusServiceSettings.LoadOrCreate();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            await using var service = await AssistantBusHttpService.StartAsync(settings, stop.Token).ConfigureAwait(false);
            Console.WriteLine($"Assistant conversation bus ready on http://127.0.0.1:{settings.Port}/");
            try { await service.WaitForShutdownAsync(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args, params string[] required)
    {
        if (args.Length != required.Length * 2) throw new AssistantBusValidationException("invalid_arguments", "The command arguments are incomplete or duplicated.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!required.Contains(args[index], StringComparer.Ordinal) || !result.TryAdd(args[index], args[index + 1]))
                throw new AssistantBusValidationException("invalid_arguments", "The command arguments are incomplete or duplicated.");
        }
        if (required.Any(option => !result.ContainsKey(option)))
            throw new AssistantBusValidationException("invalid_arguments", "The command arguments are incomplete.");
        return result;
    }

    private static long ParseLongOption(string[] args, string name, long defaultValue, long minimum, long maximum)
    {
        if (args.Length == 0) return defaultValue;
        if (args.Length != 2 || args[0] != name)
            throw new AssistantBusValidationException("invalid_arguments", $"{name} is invalid.");
        return ParseLongValue(args[1], name, minimum, maximum);
    }

    private static long ParseLongValue(string raw, string name, long minimum, long maximum)
    {
        if (!long.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new AssistantBusValidationException("invalid_arguments", $"{name} is invalid.");
        return value;
    }

    private static void RequireNoArguments(string[] args)
    {
        if (args.Length != 0) throw new AssistantBusValidationException("invalid_arguments", "This command accepts no arguments.");
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static int Fail(string message, int exitCode = 2)
    {
        Console.Error.WriteLine(message);
        return exitCode;
    }

    private static void WriteHelp()
    {
        Console.WriteLine("Assistant conversation bus");
        Console.WriteLine("  assistant-bus send --to Photon --text <message>       # fixed sender Codex");
        Console.WriteLine("  assistant-bus relay --to Everyone --text <message>   # fixed sender Chris");
        Console.WriteLine("  assistant-bus participants");
        Console.WriteLine("  assistant-bus read --after <sequence>");
        Console.WriteLine("  assistant-bus wait --after <sequence> --timeout <seconds>");
        Console.WriteLine("  assistant-bus cancel --wait <waitId>");
        Console.WriteLine("  assistant-bus serve");
        Console.WriteLine("No command accepts --from. The bus stamps Sender->Recipient from the authenticated command.");
    }
}
