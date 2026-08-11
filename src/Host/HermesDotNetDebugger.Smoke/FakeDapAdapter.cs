using System.Text;
using System.Text.Json;
using HermesDeveloperServices;

namespace HermesDotNetDebugger.Smoke;

internal static class FakeDapAdapter
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        await Console.Error.WriteAsync(new string('E', 40_000)).ConfigureAwait(false);
        await Console.Error.FlushAsync().ConfigureAwait(false);

        var decoder = new DapFrameDecoder();
        var buffer = new byte[4096];
        var sequences = new SequenceCounter();
        string? scenario = null;
        while (true)
        {
            var count = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return 0;
            foreach (var payload in decoder.Append(buffer.AsSpan(0, count)))
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var requestSequence = root.GetProperty("seq").GetInt32();
                var command = root.GetProperty("command").GetString()!;
                var requestArguments = root.TryGetProperty("arguments", out var suppliedArguments)
                    ? suppliedArguments
                    : default;

                switch (command)
                {
                    case "initialize":
                        await RespondAsync(output, sequences, requestSequence, command, new
                        {
                            supportsConfigurationDoneRequest = true,
                            supportsConditionalBreakpoints = true,
                            supportsEvaluateForHovers = true,
                            supportsCancelRequest = true,
                        }).ConfigureAwait(false);
                        break;

                    case "launch":
                        scenario = Path.GetFileNameWithoutExtension(
                            requestArguments.GetProperty("program").GetString());
                        if (scenario?.Equals("malformed", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            var malformed = Encoding.ASCII.GetBytes("Content-Length: banana\r\n\r\n{}");
                            await output.WriteAsync(malformed).ConfigureAwait(false);
                            await output.FlushAsync().ConfigureAwait(false);
                            return 0;
                        }

                        if (scenario?.Equals("cancel", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            break;
                        }

                        await RespondAsync(output, sequences, requestSequence, command, null).ConfigureAwait(false);
                        await EventAsync(output, sequences, "initialized", null).ConfigureAwait(false);
                        break;

                    case "attach":
                        scenario = "attach";
                        await RespondAsync(output, sequences, requestSequence, command, null).ConfigureAwait(false);
                        await EventAsync(output, sequences, "initialized", null).ConfigureAwait(false);
                        break;

                    case "setBreakpoints":
                        var source = requestArguments.GetProperty("source").Clone();
                        var requested = requestArguments.GetProperty("breakpoints");
                        if (requested.EnumerateArray().SelectMany(item => item.EnumerateObject())
                            .Any(property => property.Value.ValueKind is JsonValueKind.Null))
                        {
                            await RespondAsync(output, sequences, requestSequence, command, null, success: false)
                                .ConfigureAwait(false);
                            break;
                        }

                        var returnedBreakpoints = requested.EnumerateArray().Select((item, index) => new
                        {
                            id = index + 1,
                            verified = true,
                            message = (string?)null,
                            source,
                            line = item.GetProperty("line").GetInt32(),
                            column = item.TryGetProperty("column", out var column) ? column.GetInt32() : (int?)null,
                        }).ToArray();
                        await RespondAsync(output, sequences, requestSequence, command, new
                        {
                            breakpoints = returnedBreakpoints,
                        }).ConfigureAwait(false);
                        break;

                    case "configurationDone":
                        await RespondAsync(output, sequences, requestSequence, command, null).ConfigureAwait(false);
                        if (scenario?.Equals("exit", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            return 0;
                        }

                        await Task.Delay(40).ConfigureAwait(false);
                        await StoppedAsync(output, sequences).ConfigureAwait(false);
                        break;

                    case "threads":
                        await RespondAsync(output, sequences, requestSequence, command, new
                        {
                            threads = new[]
                            {
                                new
                                {
                                    id = 1,
                                    name = $"fake env={Environment.GetEnvironmentVariables().Count} args={string.Join('|', arguments)}",
                                },
                            },
                        }).ConfigureAwait(false);
                        break;

                    case "stackTrace":
                        await RespondAsync(output, sequences, requestSequence, command, new
                        {
                            stackFrames = new[]
                            {
                                new
                                {
                                    id = 0,
                                    name = "Program.Main",
                                    source = new { name = "Program.cs", path = "Program.cs", sourceReference = 0 },
                                    line = 3,
                                    column = 5,
                                },
                            },
                            totalFrames = 1,
                        }).ConfigureAwait(false);
                        break;

                    case "scopes":
                        await RespondAsync(output, sequences, requestSequence, command, new
                        {
                            scopes = new[] { new { name = "Locals", variablesReference = 20, expensive = false } },
                        }).ConfigureAwait(false);
                        break;

                    case "variables":
                        await RespondAsync(output, sequences, requestSequence, command, new
                        {
                            variables = new[]
                            {
                                new
                                {
                                    name = "answer",
                                    value = "42",
                                    type = "int",
                                    variablesReference = 0,
                                    evaluateName = "answer",
                                },
                            },
                        }).ConfigureAwait(false);
                        break;

                    case "evaluate":
                        var expression = requestArguments.GetProperty("expression").GetString();
                        if (expression == "wait") break;
                        await RespondAsync(output, sequences, requestSequence, command, new
                        {
                            result = "42",
                            type = "int",
                            variablesReference = 0,
                        }).ConfigureAwait(false);
                        break;

                    case "continue":
                    case "next":
                    case "stepIn":
                    case "stepOut":
                        await EventAsync(output, sequences, "continued", new
                        {
                            threadId = 1,
                            allThreadsContinued = true,
                        }).ConfigureAwait(false);
                        await Task.Delay(40).ConfigureAwait(false);
                        await StoppedAsync(output, sequences).ConfigureAwait(false);
                        await RespondAsync(output, sequences, requestSequence, command,
                            command == "continue" ? new { allThreadsContinued = true } : null).ConfigureAwait(false);
                        break;

                    case "cancel":
                        await RespondAsync(output, sequences, requestSequence, command, null).ConfigureAwait(false);
                        break;

                    case "disconnect":
                        await RespondAsync(output, sequences, requestSequence, command, null).ConfigureAwait(false);
                        return 0;

                    default:
                        await RespondAsync(output, sequences, requestSequence, command, null, success: false)
                            .ConfigureAwait(false);
                        break;
                }
            }
        }
    }

    private static Task StoppedAsync(Stream output, SequenceCounter sequences) =>
        EventAsync(output, sequences, "stopped", new
        {
            reason = "breakpoint",
            threadId = 1,
            description = "Synthetic stop",
            allThreadsStopped = true,
        });

    private static async Task RespondAsync(
        Stream output,
        SequenceCounter sequences,
        int requestSequence,
        string command,
        object? body,
        bool success = true)
    {
        var response = new DapResponse(
            sequences.Next(),
            requestSequence,
            success,
            command,
            success ? null : "synthetic rejection",
            body is null ? null : JsonSerializer.SerializeToElement(body));
        await WriteAsync(output, response).ConfigureAwait(false);
    }

    private static async Task EventAsync(Stream output, SequenceCounter sequences, string name, object? body)
    {
        var dapEvent = new DapEvent(
            sequences.Next(),
            name,
            body is null ? null : JsonSerializer.SerializeToElement(body));
        await WriteAsync(output, dapEvent).ConfigureAwait(false);
    }

    private static async Task WriteAsync<T>(Stream output, T message)
    {
        var frame = DapMessageCodec.EncodeFrame(message);
        await output.WriteAsync(frame).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private sealed class SequenceCounter
    {
        private int _value = 100;

        public int Next() => _value++;
    }
}
