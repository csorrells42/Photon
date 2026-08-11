using System.Security.Cryptography;
using System.Text.Json;

namespace AssistantConversationBus;

public sealed record AssistantBusServiceSettings(
    int Port,
    IReadOnlyDictionary<AssistantIdentity, string> Tokens)
{
    private const int MaximumSettingsBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string DefaultPath => Path.Combine(AssistantBusRuntime.DefaultDataRoot, "service.json");

    public static AssistantBusServiceSettings LoadOrCreate(string? path = null, int? port = null)
    {
        var fullPath = Path.GetFullPath(path ?? DefaultPath);
        var requiredPort = port ?? AssistantBusLimits.DefaultServicePort;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (!File.Exists(fullPath)) Create(fullPath, requiredPort);
        var bytes = File.ReadAllBytes(fullPath);
        try
        {
            if (bytes.Length is <= 0 or > MaximumSettingsBytes)
                throw new InvalidDataException("Assistant bus service settings exceed the bounded size.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("port", out var portElement)
                || !portElement.TryGetInt32(out var parsedPort)
                || parsedPort != requiredPort
                || !root.TryGetProperty("tokens", out var tokensElement)
                || tokensElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Assistant bus service settings are incomplete.");
            var tokens = new Dictionary<AssistantIdentity, string>();
            foreach (var identity in AssistantIdentityPolicy.Peers)
            {
                if (!tokensElement.TryGetProperty(identity.ToString(), out var tokenElement)
                    || tokenElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Assistant bus service identity settings are incomplete.");
                var token = tokenElement.GetString() ?? string.Empty;
                if (token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
                    throw new InvalidDataException("Assistant bus service identity token is invalid.");
                tokens.Add(identity, token);
            }
            return new AssistantBusServiceSettings(parsedPort, tokens);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Assistant bus service settings are malformed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string TokenFor(AssistantIdentity identity) =>
        Tokens.TryGetValue(identity, out var token)
            ? token
            : throw new AssistantBusValidationException("invalid_sender", "The bus client identity is not configured.");

    public bool TryAuthenticate(string? token, out AssistantIdentity identity)
    {
        identity = default;
        if (string.IsNullOrEmpty(token)) return false;
        foreach (var candidate in Tokens)
        {
            if (FixedEquals(candidate.Value, token))
            {
                identity = candidate.Key;
                return true;
            }
        }
        return false;
    }

    private static void Create(string path, int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var tokens = AssistantIdentityPolicy.Peers.ToDictionary(
            identity => identity.ToString(),
            _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(new { port, tokens }, JsonOptions) + Environment.NewLine;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another bus process created the same immutable identity settings first.
        }
    }

    private static bool FixedEquals(string expected, string actual)
    {
        var left = System.Text.Encoding.ASCII.GetBytes(expected);
        var right = System.Text.Encoding.ASCII.GetBytes(actual);
        try { return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right); }
        finally
        {
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }
}
