using System.Text.Json;

namespace HermesBridgeClient;

public sealed class BridgeSettingsLoader(BridgeClientPolicy? policy = null)
{
    private readonly BridgeClientPolicy _policy = policy ?? BridgeClientPolicy.Default;

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "hermes", "conversation-bridge.json");

    public BridgeConnectionSettings Load(string? path = null)
    {
        _policy.Validate();
        var settingsPath = path ?? DefaultSettingsPath;
        byte[] payload;
        try
        {
            using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            if (stream.Length is <= 0 || stream.Length > _policy.MaximumSettingsBytes)
                throw new BridgeSettingsException("Hermes bridge settings are empty or exceed the safe size limit.");
            payload = new byte[checked((int)stream.Length)];
            stream.ReadExactly(payload);
        }
        catch (BridgeSettingsException) { throw; }
        catch (FileNotFoundException)
        {
            throw new BridgeSettingsException("Hermes bridge settings do not exist. Launch Hermes Workbench once and try again.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new BridgeSettingsException("Hermes bridge settings do not exist. Launch Hermes Workbench once and try again.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BridgeSettingsException("Hermes bridge settings could not be read safely.");
        }

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("port", out var portElement) || !portElement.TryGetInt32(out var port)
                || port is < 1024 or > 65535
                || !root.TryGetProperty("authenticationToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String)
            {
                throw new BridgeSettingsException("Hermes bridge settings are incomplete or invalid.");
            }

            var token = tokenElement.GetString();
            if (token is null) throw new BridgeSettingsException("Hermes bridge settings are incomplete or invalid.");
            BridgeAuthentication authentication;
            try { authentication = BridgeAuthentication.Create(token); }
            catch (ArgumentException)
            {
                throw new BridgeSettingsException("Hermes bridge settings contain an invalid authentication code.");
            }
            return new BridgeConnectionSettings(
                BridgeEndpointPolicy.RequireLoopback(new Uri($"http://127.0.0.1:{port}/")), authentication);
        }
        catch (BridgeSettingsException) { throw; }
        catch (Exception exception) when (exception is JsonException or UriFormatException or ArgumentException)
        {
            throw new BridgeSettingsException("Hermes bridge settings are incomplete or invalid.");
        }
        finally
        {
            Array.Clear(payload);
        }
    }
}

public sealed class BridgeSettingsException(string message) : Exception(message);
