namespace HermesBridgeClient;

public static class BridgeCli
{
    private const string Usage = """
Hermes bridge client

Usage:
  hermes-bridge health
  hermes-bridge status
  hermes-bridge send --text <message>
  hermes-bridge interrupt
  hermes-bridge new

health is unauthenticated. status, send, interrupt, and new read the bridge code from
%LOCALAPPDATA%\HermesWorkbench\conversation-bridge.json. send never reads from stdin.
""";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || args is ["--help"] or ["-h"] or ["help"])
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Length == 0 ? (int)BridgeExitCode.UsageOrConfiguration : (int)BridgeExitCode.Success;
        }

        try
        {
            using var client = new BridgeApiClient();
            BridgeCommandResult result;
            switch (args)
            {
                case ["health"]:
                    result = await client.HealthAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case ["status"]:
                    using (var settings = new BridgeSettingsLoader().Load())
                        result = await client.StatusAsync(settings, cancellationToken).ConfigureAwait(false);
                    break;
                case ["interrupt"]:
                    using (var settings = new BridgeSettingsLoader().Load())
                        result = await client.InterruptAsync(settings, cancellationToken).ConfigureAwait(false);
                    break;
                case ["new"]:
                    using (var settings = new BridgeSettingsLoader().Load())
                        result = await client.NewSessionAsync(settings, cancellationToken).ConfigureAwait(false);
                    break;
                case ["send", "--text", var text]:
                    using (var settings = new BridgeSettingsLoader().Load())
                        result = await client.SendAsync(settings, text, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await error.WriteLineAsync("Invalid arguments. Run 'hermes-bridge --help' for usage.").ConfigureAwait(false);
                    return (int)BridgeExitCode.UsageOrConfiguration;
            }

            if (result.IsSuccess)
                await output.WriteLineAsync(result.Output).ConfigureAwait(false);
            else
                await error.WriteLineAsync(result.Error).ConfigureAwait(false);
            return (int)result.ExitCode;
        }
        catch (BridgeSettingsException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return (int)BridgeExitCode.UsageOrConfiguration;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("The Hermes bridge command failed safely.").ConfigureAwait(false);
            return (int)BridgeExitCode.ConnectivityOrProtocol;
        }
    }
}
