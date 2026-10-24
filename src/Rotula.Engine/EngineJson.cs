using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rotula.Engine;

/// <summary>The JSON shape shared by the command line (<c>--json</c>, <c>--data file.json</c>) and the REST API.</summary>
public static class EngineJson
{
    /// <summary>
    /// camelCase names, enums as text, comments allowed, unknown names refused (a misspelt field must not be ignored silently: "copeis": 500
    /// would otherwise print the label's own quantity). Field values may be numbers or true/false as well as text.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    /// <summary>As <see cref="Options"/>, indented for people.</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 16,
            WriteIndented = indented,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.Converters.Add(new LenientStringConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>
    /// Reads data records from JSON text: an array of objects (one record each) or one object (the values of a single label). Values may be text,
    /// numbers, true/false or null (empty). Returns the key of the problem, or null.
    /// </summary>
    public static string? ReadData(string json, out List<Dictionary<string, string>>? records, out Dictionary<string, string>? values, out string? detail)
    {
        records = null;
        values = null;
        detail = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 8 });
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.Array:
                    records = new List<Dictionary<string, string>>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) { detail = item.ValueKind.ToString(); return "Engine.Error.DataShape"; }
                        records.Add(Fields(item));
                    }
                    return null;
                case JsonValueKind.Object:
                    values = Fields(doc.RootElement);
                    return null;
                default:
                    detail = doc.RootElement.ValueKind.ToString();
                    return "Engine.Error.DataShape";
            }
        }
        catch (JsonException ex)
        {
            detail = ex.Message;
            return "Engine.Error.DataShape";
        }
        catch (InvalidOperationException ex)
        {
            detail = ex.Message;
            return "Engine.Error.DataShape";
        }
    }

    private static Dictionary<string, string> Fields(JsonElement obj)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject()) map[p.Name] = Text(p.Value);
        return map;
    }

    private static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        // A nested object or list is not a field value; refusing it beats printing its JSON on a label.
        _ => throw new InvalidOperationException("A field value must be text, a number, true/false or null."),
    };

    /// <summary>Reads a text field that a warehouse system may send as a number (<c>"qty": 5</c>) or true/false.</summary>
    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override bool HandleNull => false;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            // As written (1.50 stays 1.50): a number token is plain ASCII, never escaped.
            JsonTokenType.Number => System.Text.Encoding.ASCII.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException("Expected text, a number or true/false."),
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
