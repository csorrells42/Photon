using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotonCadRuntime;

internal sealed record CadGeometryObjectReference(string EntityId, string ObjectName, string CapabilityId);

internal sealed class CadGeometryToolReport
{
    internal CadGeometryToolReport(string summary)
    {
        Summary = ContractGuards.RequiredText(summary, nameof(summary), 65_536);
    }

    public string Summary { get; }
}

internal sealed class CadGeometryMcpClient : IAsyncDisposable
{
    private const int MaximumGeneratedCodeBytes = 512;
    public const string ProtocolVersion = "2025-06-18";
    public const string RuntimeVersion = "0.3.80";
    private static readonly IReadOnlySet<string> RequiredTools = new HashSet<string>(
        ["execute", "measure", "validate", "export"],
        StringComparer.Ordinal);

    private readonly CadJsonRpcClient _rpc;
    private int _disposed;

    private CadGeometryMcpClient(CadJsonRpcClient rpc) => _rpc = rpc;

    public static async ValueTask<CadGeometryMcpClient> ConnectAsync(
        CadDockerContainerProcess container,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container.Plan.Role != CadDockerRuntimeRole.Geometry)
            throw new CadContractException("wrong_runtime_role", nameof(container));
        var rpc = new CadJsonRpcClient(
            new CadNdjsonTransport(container.StandardInput, container.StandardOutput),
            requestTimeout,
            ["notifications/message", "notifications/progress"]);
        try
        {
            var initialized = await rpc.RequestAsync(
                "initialize",
                new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "photon-cad-host", version = "1" },
                },
                cancellationToken).ConfigureAwait(false);
            RequireObject(initialized, "initialize");
            if (!initialized.TryGetProperty("protocolVersion", out var protocol) ||
                protocol.ValueKind != JsonValueKind.String || protocol.GetString() != ProtocolVersion)
                throw new CadProtocolException("mcp_protocol_version_mismatch");
            await rpc.NotifyAsync("notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);

            var listed = await rpc.RequestAsync("tools/list", new { }, cancellationToken).ConfigureAwait(false);
            RequireObject(listed, "tools/list");
            if (!listed.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array ||
                tools.GetArrayLength() is 0 or > 256)
                throw new CadProtocolException("invalid_mcp_tool_catalog");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                    name.GetString()!.Length > 128 || !names.Add(name.GetString()!))
                    throw new CadProtocolException("invalid_mcp_tool_catalog");
            }
            if (!RequiredTools.IsSubsetOf(names))
                throw new CadProtocolException("required_mcp_tool_missing");
            return new CadGeometryMcpClient(rpc);
        }
        catch
        {
            await rpc.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<CadGeometryObjectReference> CreateBoxAsync(
        double length,
        double width,
        double height,
        CancellationToken cancellationToken)
    {
        var dimensions = new[]
        {
            RequirePositiveDimension(length, nameof(length)),
            RequirePositiveDimension(width, nameof(width)),
            RequirePositiveDimension(height, nameof(height)),
        };
        var objectName = NewObjectName();
        var code = RequireGeneratedCode(string.Create(
            CultureInfo.InvariantCulture,
            $"from build123d import *\n{objectName} = Box({dimensions[0]:R}, {dimensions[1]:R}, {dimensions[2]:R})\nshow({objectName}, '{objectName}')"));
        var report = await CallToolAsync("execute", new { code }, cancellationToken).ConfigureAwait(false);
        if (!report.Summary.Contains(objectName, StringComparison.Ordinal))
            throw new CadProtocolException("geometry_object_acknowledgement_missing");
        return new CadGeometryObjectReference(NewEntityId(), objectName, CadPinnedCapabilityCatalog.BoxCapabilityId);
    }

    internal async ValueTask<CadGeometryObjectReference> CreateCylinderAsync(
        double radius,
        double height,
        CancellationToken cancellationToken)
    {
        radius = RequirePositiveDimension(radius, nameof(radius));
        height = RequirePositiveDimension(height, nameof(height));
        var objectName = NewObjectName();
        var code = RequireGeneratedCode(string.Create(
            CultureInfo.InvariantCulture,
            $"from build123d import *\n{objectName} = Cylinder({radius:R}, {height:R})\nshow({objectName}, '{objectName}')"));
        var report = await CallToolAsync("execute", new { code }, cancellationToken).ConfigureAwait(false);
        if (!report.Summary.Contains(objectName, StringComparison.Ordinal))
            throw new CadProtocolException("geometry_object_acknowledgement_missing");
        return new CadGeometryObjectReference(NewEntityId(), objectName, CadPinnedCapabilityCatalog.CylinderCapabilityId);
    }

    internal ValueTask<CadGeometryToolReport> MeasureAsync(
        CadGeometryObjectReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return CallToolAsync("measure", new { object_name = reference.ObjectName }, cancellationToken);
    }

    internal async ValueTask<CadGeometryToolReport> ValidateAsync(
        CadGeometryObjectReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var report = await CallToolAsync("validate", new { object_name = reference.ObjectName }, cancellationToken)
            .ConfigureAwait(false);
        if (!report.Summary.Contains("PASS", StringComparison.OrdinalIgnoreCase))
            throw new CadProtocolException("geometry_validation_failed");
        return report;
    }

    internal ValueTask<CadGeometryToolReport> ExportStepAsync(
        CadGeometryObjectReference reference,
        CadRelativePath relativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!Path.GetExtension(relativePath.Value).Equals(".step", StringComparison.OrdinalIgnoreCase))
            throw new CadContractException("step_extension_required", nameof(relativePath));
        return CallToolAsync(
            "export",
            new
            {
                filename = $"{CadDockerRuntimeIdentity.WorkspaceMount}/{relativePath.Value}",
                format = "step",
                object_name = reference.ObjectName,
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _rpc.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<CadGeometryToolReport> CallToolAsync(
        string tool,
        object arguments,
        CancellationToken cancellationToken)
    {
        if (!RequiredTools.Contains(tool))
            throw new CadContractException("unapproved_geometry_tool", nameof(tool));
        var result = await _rpc.RequestAsync(
            "tools/call",
            new { name = tool, arguments },
            cancellationToken).ConfigureAwait(false);
        RequireObject(result, "toolResult");
        if (result.TryGetProperty("isError", out var isError) &&
            (isError.ValueKind is not JsonValueKind.True and not JsonValueKind.False || isError.GetBoolean()))
            throw new CadProtocolException("geometry_tool_failed");
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array ||
            content.GetArrayLength() is 0 or > 64)
            throw new CadProtocolException("invalid_geometry_tool_result");
        var fragments = new List<string>();
        var total = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
                throw new CadProtocolException("invalid_geometry_tool_result");
            if (type.GetString() != "text") continue;
            if (!item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new CadProtocolException("invalid_geometry_tool_result");
            var value = text.GetString()!;
            total = checked(total + value.Length);
            if (total > 65_536) throw new CadProtocolException("geometry_tool_result_too_large");
            fragments.Add(value);
        }
        if (fragments.Count == 0)
            throw new CadProtocolException("geometry_tool_text_missing");
        var summary = string.Join('\n', fragments);
        summary = new string(summary.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return new CadGeometryToolReport(summary);
    }

    private static void RequireObject(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new CadProtocolException($"invalid_{field.Replace('/', '_')}_result");
    }

    private static double RequirePositiveDimension(double value, string field)
    {
        value = ContractGuards.Finite(value, field, 1_000_000);
        if (value < 0.001) throw new CadContractException("dimension_below_minimum", field);
        return value;
    }

    private static string RequireGeneratedCode(string value)
    {
        if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > MaximumGeneratedCodeBytes ||
            value.Any(character => character > 0x7f || character is '\0' or '\r'))
            throw new CadContractException("generated_geometry_template_rejected", nameof(value));
        return value;
    }

    private static string NewObjectName() => $"photon_{Guid.NewGuid():N}";
    private static string NewEntityId() => $"entity_{Guid.NewGuid():N}";
}

internal sealed class CadPartCadProtocolProof
{
    internal CadPartCadProtocolProof(string version, DateTimeOffset verifiedAtUtc)
    {
        Version = ContractGuards.Revision(version, nameof(version));
        VerifiedAtUtc = ContractGuards.Utc(verifiedAtUtc, nameof(verifiedAtUtc));
    }

    public string Version { get; }
    public DateTimeOffset VerifiedAtUtc { get; }
    public int AllowedMethodCount => CadPartCadProtocolClient.AllowedMethods.Count;
    public bool ForbiddenMethodsRejected => true;
}

internal sealed class CadPartCadProtocolClient : IAsyncDisposable
{
    public const string RuntimeVersion = "0.7.158";
    public static IReadOnlySet<string> AllowedMethods { get; } = new HashSet<string>(
        [
            "context.create", "export.assembly", "export.part", "healthcheck", "info.object",
            "inspect.assembly", "inspect.object", "inspect.part", "list.objects", "list.packages", "version",
        ],
        StringComparer.Ordinal);
    public static IReadOnlySet<string> ForbiddenProbeMethods { get; } = new HashSet<string>(
        [
            "activate", "add.assembly", "add.object", "add.part", "adhoc.convert", "convert.object",
            "daemon.reset", "daemon.set.telemetry", "daemon.status", "ensure_loaded", "import.object", "init",
            "info", "inspect.file", "inspect.interface", "inspect.sketch", "install", "lint.run", "list.all",
            "list.mates", "list.providers", "package.load", "package.path", "package.refresh", "render.objects",
            "rpc.discover", "search.objects", "test", "test.run", "update",
        ],
        StringComparer.Ordinal);

    private static readonly string[] AllowedNotifications =
    [
        "log", "info", "warn", "error", "terminal", "execute", "stats", "items", "showPartDone",
        "exportPartDone", "loaded", "activateFailed", "packageLoaded", "packageLoadFailed", "needsUpdate",
        "doRestart", "installed", "installFailed",
    ];

    private readonly CadJsonRpcClient _rpc;
    private int _disposed;

    private CadPartCadProtocolClient(CadJsonRpcClient rpc, CadPartCadProtocolProof proof)
    {
        _rpc = rpc;
        Proof = proof;
    }

    internal CadPartCadProtocolProof Proof { get; }

    public static async ValueTask<CadPartCadProtocolClient> ConnectAndProveAsync(
        CadDockerContainerProcess container,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container.Plan.Role != CadDockerRuntimeRole.Assembly)
            throw new CadContractException("wrong_runtime_role", nameof(container));
        var rpc = new CadJsonRpcClient(
            new CadContentLengthTransport(container.StandardInput, container.StandardOutput),
            requestTimeout,
            AllowedNotifications);
        try
        {
            var version = await rpc.RequestAsync("version", new { }, cancellationToken).ConfigureAwait(false);
            if (version.ValueKind != JsonValueKind.Object || !version.TryGetProperty("partcad", out var value) ||
                value.ValueKind != JsonValueKind.String || value.GetString() != RuntimeVersion)
                throw new CadProtocolException("partcad_version_mismatch");
            var health = await rpc.RequestAsync(
                "healthcheck",
                new { fix = false, dry_run = true },
                cancellationToken).ConfigureAwait(false);
            if (health.ValueKind != JsonValueKind.Object)
                throw new CadProtocolException("partcad_healthcheck_failed");

            foreach (var forbidden in ForbiddenProbeMethods.Order(StringComparer.Ordinal))
            {
                try
                {
                    _ = await rpc.RequestAsync(forbidden, new { }, cancellationToken).ConfigureAwait(false);
                    throw new CadProtocolException("partcad_forbidden_method_exposed");
                }
                catch (CadRemoteProtocolException exception) when (exception.RemoteCode == -32601)
                {
                }
            }
            return new CadPartCadProtocolClient(
                rpc,
                new CadPartCadProtocolProof(RuntimeVersion, DateTimeOffset.UtcNow));
        }
        catch
        {
            await rpc.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _rpc.DisposeAsync().ConfigureAwait(false);
    }
}
