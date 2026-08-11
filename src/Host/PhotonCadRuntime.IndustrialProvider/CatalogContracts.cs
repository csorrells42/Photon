using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

public enum PhotonCadIndustrialParameterKind
{
    Number,
    Integer,
    Boolean,
    Choice,
}

public sealed record PhotonCadIndustrialChoice(string Token, string Label);

public sealed class PhotonCadIndustrialCatalogParameter
{
    internal PhotonCadIndustrialCatalogParameter(
        string id,
        string label,
        PhotonCadIndustrialParameterKind kind,
        bool required,
        double? minimum,
        double? maximum,
        PhotonCadIndustrialCatalogInputValue? defaultValue,
        IReadOnlyList<PhotonCadIndustrialChoice> choices)
    {
        Id = id;
        Label = label;
        Kind = kind;
        Required = required;
        Minimum = minimum;
        Maximum = maximum;
        DefaultValue = defaultValue;
        Choices = choices;
    }

    public string Id { get; }
    public string Label { get; }
    public PhotonCadIndustrialParameterKind Kind { get; }
    public bool Required { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }
    public PhotonCadIndustrialCatalogInputValue? DefaultValue { get; }
    public IReadOnlyList<PhotonCadIndustrialChoice> Choices { get; }
}

public sealed class PhotonCadIndustrialCatalogItem
{
    internal PhotonCadIndustrialCatalogItem(
        string capabilityId,
        string category,
        string title,
        IReadOnlyList<PhotonCadIndustrialCatalogParameter> parameters)
    {
        CapabilityId = capabilityId;
        Category = category;
        Title = title;
        Parameters = parameters;
    }

    public string CapabilityId { get; }
    public string Category { get; }
    public string Title { get; }
    public IReadOnlyList<PhotonCadIndustrialCatalogParameter> Parameters { get; }
}

public sealed class PhotonCadIndustrialCatalog
{
    private readonly IReadOnlyDictionary<string, IndustrialCatalogItemDefinition> _definitions;

    internal PhotonCadIndustrialCatalog(
        string digest,
        IReadOnlyList<PhotonCadIndustrialCatalogItem> items,
        IReadOnlyDictionary<string, IndustrialCatalogItemDefinition> definitions)
    {
        Digest = digest;
        Items = items;
        _definitions = definitions;
    }

    public string Digest { get; }
    public IReadOnlyList<PhotonCadIndustrialCatalogItem> Items { get; }

    internal IndustrialCatalogItemDefinition RequireDefinition(string capabilityId) =>
        _definitions.TryGetValue(capabilityId, out var definition)
            ? definition
            : throw new InvalidOperationException("industrial_catalog_capability_not_found");
}

public sealed class PhotonCadIndustrialCatalogInputValue
{
    private readonly object _value;

    private PhotonCadIndustrialCatalogInputValue(PhotonCadIndustrialParameterKind kind, object value)
    {
        Kind = kind;
        _value = value;
    }

    public PhotonCadIndustrialParameterKind Kind { get; }
    public static PhotonCadIndustrialCatalogInputValue Number(double value) =>
        new(PhotonCadIndustrialParameterKind.Number, RequireFinite(value));
    public static PhotonCadIndustrialCatalogInputValue Integer(long value) =>
        new(PhotonCadIndustrialParameterKind.Integer, value);
    public static PhotonCadIndustrialCatalogInputValue Boolean(bool value) =>
        new(PhotonCadIndustrialParameterKind.Boolean, value);
    public static PhotonCadIndustrialCatalogInputValue Choice(string token) =>
        new(PhotonCadIndustrialParameterKind.Choice, RequireToken(token));

    internal double NumberValue => Kind == PhotonCadIndustrialParameterKind.Number
        ? (double)_value
        : throw new InvalidOperationException("industrial_catalog_value_kind_mismatch");
    internal long IntegerValue => Kind == PhotonCadIndustrialParameterKind.Integer
        ? (long)_value
        : throw new InvalidOperationException("industrial_catalog_value_kind_mismatch");
    internal bool BooleanValue => Kind == PhotonCadIndustrialParameterKind.Boolean
        ? (bool)_value
        : throw new InvalidOperationException("industrial_catalog_value_kind_mismatch");
    internal string ChoiceToken => Kind == PhotonCadIndustrialParameterKind.Choice
        ? (string)_value
        : throw new InvalidOperationException("industrial_catalog_value_kind_mismatch");

    private static double RequireFinite(double value) => double.IsFinite(value)
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value));

    private static string RequireToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("industrial_catalog_choice_token_invalid", nameof(value));
        return value;
    }
}

internal sealed record IndustrialCatalogScalar(PhotonCadIndustrialParameterKind Kind, object Value)
{
    internal void Write(Utf8JsonWriter writer)
    {
        switch (Kind)
        {
            case PhotonCadIndustrialParameterKind.Number: writer.WriteNumberValue((double)Value); break;
            case PhotonCadIndustrialParameterKind.Integer: writer.WriteNumberValue((long)Value); break;
            case PhotonCadIndustrialParameterKind.Boolean: writer.WriteBooleanValue((bool)Value); break;
            case PhotonCadIndustrialParameterKind.Choice when Value is string text: writer.WriteStringValue(text); break;
            default: throw new InvalidDataException("industrial_catalog_scalar_invalid");
        }
    }

    internal PhotonCadSyncInputValue ToSyncValue() => Kind switch
    {
        PhotonCadIndustrialParameterKind.Number => PhotonCadSyncInputValue.Number((double)Value),
        PhotonCadIndustrialParameterKind.Integer => PhotonCadSyncInputValue.Integer((long)Value),
        PhotonCadIndustrialParameterKind.Boolean => PhotonCadSyncInputValue.Boolean((bool)Value),
        PhotonCadIndustrialParameterKind.Choice => PhotonCadSyncInputValue.Choice((string)Value),
        _ => throw new InvalidDataException("industrial_catalog_scalar_invalid"),
    };
}

internal sealed record IndustrialCatalogParameterDefinition(
    PhotonCadIndustrialCatalogParameter Public,
    IReadOnlyDictionary<string, IndustrialCatalogScalar> Choices);

internal sealed record IndustrialCatalogItemDefinition(
    PhotonCadIndustrialCatalogItem Public,
    IReadOnlyDictionary<string, IndustrialCatalogParameterDefinition> Parameters);

internal static class CatalogProjection
{
    private const int MaximumMountedItems = 2_000;

    internal static PhotonCadIndustrialCatalog Parse(ReadOnlyMemory<byte> bytes, string expectedDigest)
    {
        using var document = ProtocolV1.ParseCatalog(bytes, expectedDigest);
        var root = document.RootElement;
        if (!root.TryGetProperty("schema", out var schema)
            || !StringComparer.Ordinal.Equals(schema.GetString(), "photon.cad.industrial.catalog/v1")
            || !root.TryGetProperty("items", out var itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("industrial_catalog_shape_rejected");

        var publicItems = new List<PhotonCadIndustrialCatalogItem>();
        var definitions = new Dictionary<string, IndustrialCatalogItemDefinition>(StringComparer.Ordinal);
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("availability", out var availability)
                || !StringComparer.Ordinal.Equals(availability.GetString(), "supported"))
                continue;
            var category = Text(item, "category", 128);
            var title = Text(item, "title", 256);
            if (publicItems.Count >= MaximumMountedItems)
                throw new InvalidDataException("industrial_catalog_mounted_item_limit");
            var capabilityId = Identifier(item, "id");
            var parameterElement = item.GetProperty("parameters");
            if (parameterElement.ValueKind != JsonValueKind.Array || parameterElement.GetArrayLength() > 32)
                throw new InvalidDataException("industrial_catalog_parameter_set_rejected");
            var publicParameters = new List<PhotonCadIndustrialCatalogParameter>();
            var parameterDefinitions = new Dictionary<string, IndustrialCatalogParameterDefinition>(StringComparer.Ordinal);
            foreach (var parameter in parameterElement.EnumerateArray())
            {
                var definition = ParseParameter(parameter);
                if (!parameterDefinitions.TryAdd(definition.Public.Id, definition))
                    throw new InvalidDataException("industrial_catalog_parameter_duplicate");
                publicParameters.Add(definition.Public);
            }
            publicParameters.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
            var publicItem = new PhotonCadIndustrialCatalogItem(
                capabilityId,
                category,
                title,
                Array.AsReadOnly(publicParameters.ToArray()));
            if (!definitions.TryAdd(capabilityId, new IndustrialCatalogItemDefinition(
                    publicItem,
                    new ReadOnlyDictionary<string, IndustrialCatalogParameterDefinition>(parameterDefinitions))))
                throw new InvalidDataException("industrial_catalog_item_duplicate");
            publicItems.Add(publicItem);
        }
        if (!publicItems.Any(item => item.Category == "bearings")
            || !publicItems.Any(item => item.Title == "Spur Gear"))
            throw new InvalidDataException("industrial_required_catalog_items_missing");
        publicItems.Sort((left, right) => StringComparer.Ordinal.Compare(left.CapabilityId, right.CapabilityId));
        return new PhotonCadIndustrialCatalog(
            ProtocolV1.NormalizeDigest(expectedDigest),
            Array.AsReadOnly(publicItems.ToArray()),
            new ReadOnlyDictionary<string, IndustrialCatalogItemDefinition>(definitions));
    }

    internal static IndustrialCatalogScalar Resolve(
        IndustrialCatalogParameterDefinition parameter,
        PhotonCadIndustrialCatalogInputValue value)
    {
        if (value.Kind != parameter.Public.Kind)
            throw new ArgumentException("industrial_catalog_input_kind_mismatch", nameof(value));
        IndustrialCatalogScalar scalar = value.Kind switch
        {
            PhotonCadIndustrialParameterKind.Number => new(value.Kind, value.NumberValue),
            PhotonCadIndustrialParameterKind.Integer => new(value.Kind, value.IntegerValue),
            PhotonCadIndustrialParameterKind.Boolean => new(value.Kind, value.BooleanValue),
            PhotonCadIndustrialParameterKind.Choice when parameter.Choices.TryGetValue(value.ChoiceToken, out var choice) => choice,
            _ => throw new ArgumentException("industrial_catalog_input_invalid", nameof(value)),
        };
        if (scalar.Kind is PhotonCadIndustrialParameterKind.Number or PhotonCadIndustrialParameterKind.Integer)
        {
            var number = scalar.Kind == PhotonCadIndustrialParameterKind.Number ? (double)scalar.Value : (long)scalar.Value;
            if (parameter.Public.Minimum is { } minimum && number < minimum
                || parameter.Public.Maximum is { } maximum && number > maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
        }
        return scalar;
    }

    private static IndustrialCatalogParameterDefinition ParseParameter(JsonElement parameter)
    {
        var id = Identifier(parameter, "id");
        var label = Text(parameter, "label", 256);
        var kindText = Text(parameter, "kind", 32);
        var kind = kindText switch
        {
            "number" => PhotonCadIndustrialParameterKind.Number,
            "integer" => PhotonCadIndustrialParameterKind.Integer,
            "boolean" => PhotonCadIndustrialParameterKind.Boolean,
            "choice" => PhotonCadIndustrialParameterKind.Choice,
            _ => throw new InvalidDataException("industrial_catalog_parameter_kind_rejected"),
        };
        var required = Boolean(parameter, "required");
        var minimum = OptionalNumber(parameter, "minimum");
        var maximum = OptionalNumber(parameter, "maximum");
        if (minimum is not null && maximum is not null && minimum > maximum)
            throw new InvalidDataException("industrial_catalog_parameter_range_rejected");
        var choices = new Dictionary<string, IndustrialCatalogScalar>(StringComparer.Ordinal);
        var publicChoices = new List<PhotonCadIndustrialChoice>();
        if (kind == PhotonCadIndustrialParameterKind.Choice)
        {
            var values = parameter.GetProperty("choices");
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 1000)
                throw new InvalidDataException("industrial_catalog_choice_set_rejected");
            var index = 0;
            foreach (var raw in values.EnumerateArray())
            {
                var scalar = Scalar(raw, kind);
                var token = $"choice-{index++:D4}";
                choices.Add(token, scalar);
                publicChoices.Add(new PhotonCadIndustrialChoice(token, ScalarLabel(scalar)));
            }
        }
        PhotonCadIndustrialCatalogInputValue? defaultValue = null;
        if (parameter.TryGetProperty("default", out var defaultElement) && defaultElement.ValueKind != JsonValueKind.Null)
        {
            var scalar = Scalar(defaultElement, kind);
            defaultValue = kind switch
            {
                PhotonCadIndustrialParameterKind.Number => PhotonCadIndustrialCatalogInputValue.Number((double)scalar.Value),
                PhotonCadIndustrialParameterKind.Integer => PhotonCadIndustrialCatalogInputValue.Integer((long)scalar.Value),
                PhotonCadIndustrialParameterKind.Boolean => PhotonCadIndustrialCatalogInputValue.Boolean((bool)scalar.Value),
                _ => null,
            };
        }
        var publicParameter = new PhotonCadIndustrialCatalogParameter(
            id,
            label,
            kind,
            required,
            minimum,
            maximum,
            defaultValue,
            Array.AsReadOnly(publicChoices.ToArray()));
        return new IndustrialCatalogParameterDefinition(
            publicParameter,
            new ReadOnlyDictionary<string, IndustrialCatalogScalar>(choices));
    }

    private static IndustrialCatalogScalar Scalar(JsonElement value, PhotonCadIndustrialParameterKind kind) => kind switch
    {
        PhotonCadIndustrialParameterKind.Number when value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number) => new(kind, number),
        PhotonCadIndustrialParameterKind.Integer when value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var integer) => new(kind, integer),
        PhotonCadIndustrialParameterKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
            new(kind, value.GetBoolean()),
        PhotonCadIndustrialParameterKind.Choice when value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 and <= 256 } text => new(kind, text),
        PhotonCadIndustrialParameterKind.Choice when value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var integer) => new(PhotonCadIndustrialParameterKind.Integer, integer),
        PhotonCadIndustrialParameterKind.Choice when value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number) => new(PhotonCadIndustrialParameterKind.Number, number),
        PhotonCadIndustrialParameterKind.Choice when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
            new(PhotonCadIndustrialParameterKind.Boolean, value.GetBoolean()),
        _ => throw new InvalidDataException("industrial_catalog_scalar_rejected"),
    };

    private static string ScalarLabel(IndustrialCatalogScalar value) => value.Value switch
    {
        string text => text,
        bool boolean => boolean ? "True" : "False",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("industrial_catalog_choice_label_rejected"),
    };

    private static string Identifier(JsonElement value, string name)
    {
        var result = Text(value, name, 128);
        if (!char.IsAsciiLetterOrDigit(result[0])
            || result.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new InvalidDataException("industrial_catalog_identifier_rejected");
        return result;
    }

    private static string Text(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String
            || item.GetString() is not { } result || string.IsNullOrWhiteSpace(result) || result.Length > maximum
            || result.Any(char.IsControl))
            throw new InvalidDataException("industrial_catalog_text_rejected");
        return result;
    }

    private static bool Boolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("industrial_catalog_boolean_rejected");
        return item.GetBoolean();
    }

    private static double? OptionalNumber(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var result) || !double.IsFinite(result))
            throw new InvalidDataException("industrial_catalog_number_rejected");
        return result;
    }
}
