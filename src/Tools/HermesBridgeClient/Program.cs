namespace HermesBridgeClient;

internal static class Program
{
    public static async Task<int> Main(string[] args) =>
        await BridgeCli.RunAsync(args, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);
}
