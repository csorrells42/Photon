namespace AssistantConversationBus;

internal static class Program
{
    private static Task<int> Main(string[] args) => AssistantBusCli.RunAsync(args);
}
