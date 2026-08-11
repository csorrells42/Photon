using System.Net;
using System.Security.Cryptography;

namespace HermesBridgeClient;

public enum BridgeExitCode
{
    Success = 0,
    UsageOrConfiguration = 2,
    AuthenticationFailed = 3,
    Conflict = 4,
    ServiceUnavailable = 5,
    GatewayTimeout = 6,
    ConnectivityOrProtocol = 7,
}

public sealed record BridgeClientPolicy(
    TimeSpan Timeout,
    int MaximumResponseBytes = 1024 * 1024,
    int MaximumSettingsBytes = 16 * 1024)
{
    public static BridgeClientPolicy Default { get; } = new(TimeSpan.FromSeconds(15));

    public void Validate()
    {
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MaximumResponseBytes is < 1024 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        if (MaximumSettingsBytes is < 256 or > 64 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumSettingsBytes));
    }
}

public sealed record BridgeCommandResult(BridgeExitCode ExitCode, string? Output, string? Error)
{
    public bool IsSuccess => ExitCode == BridgeExitCode.Success;
    public static BridgeCommandResult Success(string output) => new(BridgeExitCode.Success, output, null);
    public static BridgeCommandResult Failure(BridgeExitCode code, string error) => new(code, null, error);
}

public sealed class BridgeAuthentication : IDisposable
{
    private char[]? _characters;

    private BridgeAuthentication(char[] characters) => _characters = characters;

    public static BridgeAuthentication Create(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The bridge settings contain an invalid authentication code.", nameof(token));
        return new BridgeAuthentication(token.ToCharArray());
    }

    internal void Apply(HttpRequestMessage request)
    {
        ObjectDisposedException.ThrowIf(_characters is null, this);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new string(_characters));
    }

    internal string Redact(string value)
    {
        ObjectDisposedException.ThrowIf(_characters is null, this);
        return value.Replace(new string(_characters), "[redacted]", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_characters is null) return;
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_characters.AsSpan()));
        _characters = null;
    }
}

public sealed record BridgeConnectionSettings(Uri Endpoint, BridgeAuthentication Authentication) : IDisposable
{
    public void Dispose() => Authentication.Dispose();
}

public static class BridgeEndpointPolicy
{
    public static Uri DefaultEndpoint { get; } = new("http://127.0.0.1:8972/");

    public static Uri RequireLoopback(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath != "/" || endpoint.Port is < 1024 or > 65535
            || !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("The bridge endpoint must be a literal HTTP loopback address with no path, query, user information, or fragment.", nameof(endpoint));
        }
        return endpoint;
    }
}
