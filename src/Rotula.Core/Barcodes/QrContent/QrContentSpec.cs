using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rotula.Core.Barcodes.QrContent;

/// <summary>The kinds of QR content the content builder offers.</summary>
public enum QrContentKind
{
    /// <summary>A web address.</summary>
    Url,
    /// <summary>Plain text.</summary>
    Text,
    /// <summary>Wi-Fi settings.</summary>
    Wifi,
    /// <summary>A contact card (vCard 3.0).</summary>
    VCard,
    /// <summary>A contact card (MeCard).</summary>
    MeCard,
    /// <summary>A GS1 Digital Link web address built from a GTIN and its qualifiers.</summary>
    GS1DigitalLink,
    /// <summary>An e-mail.</summary>
    Email,
    /// <summary>A phone number to call.</summary>
    Phone,
    /// <summary>A text message.</summary>
    Sms,
    /// <summary>A map position.</summary>
    Location,
    /// <summary>A calendar event.</summary>
    CalendarEvent,
}

/// <summary>
/// The definition made in the content builder: a kind and named field values. A value is fixed text, a <c>{{field}}</c> placeholder bound to a
/// data column, or both mixed (<c>Mr. {{name}}</c>). The spec is stored in the barcode element (<c>BarcodeElement.QrContentSpec</c>, as JSON) so
/// the builder can be reopened; the payload is produced from it per record by <see cref="QrContentBuilder"/>.
/// </summary>
public sealed record QrContentSpec(QrContentKind Kind, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>Limits applied when reading a spec from a file, which may come from anywhere.</summary>
    private const int MaxFields = 40, MaxValueLength = 5000;

    private static readonly Regex Token = new(@"\{\{\s*(#?[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates a spec from pairs; later pairs replace earlier ones with the same name.</summary>
    public static QrContentSpec Create(QrContentKind kind, params (string Name, string Value)[] fields) =>
        new(kind, fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal));

    /// <summary>The value of a field, or an empty string when it is not set.</summary>
    public string Get(string name) => Fields.TryGetValue(name, out var v) ? v : string.Empty;

    /// <summary>The names of the data fields used by placeholders in any value, in order of appearance, without duplicates.</summary>
    public IReadOnlyList<string> Placeholders() =>
        Fields.OrderBy(f => f.Key, StringComparer.Ordinal).SelectMany(f => Token.Matches(f.Value).Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Replaces the placeholders of <paramref name="value"/> using <paramref name="lookup"/>; unknown names give an empty string and are added to <paramref name="missing"/>.</summary>
    internal static string Substitute(string value, Func<string, string?> lookup, ICollection<string> missing) =>
        Token.Replace(value, m =>
        {
            string? found = lookup(m.Groups[1].Value);
            if (found is null) { missing.Add(m.Groups[1].Value); return string.Empty; }
            return found;
        });

    /// <summary>The spec as JSON (kind by name, fields sorted), the form stored in the element.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("kind", Kind.ToString());
            w.WriteStartObject("fields");
            foreach (var f in Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Where(f => f.Value.Length > 0)) w.WriteString(f.Key, f.Value);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads JSON written by <see cref="ToJson"/>. Returns false for anything else, an unknown kind, or an oversized spec; never throws.</summary>
    public static bool TryParse(string? json, out QrContentSpec spec)
    {
        spec = new QrContentSpec(QrContentKind.Url, new Dictionary<string, string>());
        if (string.IsNullOrWhiteSpace(json) || json.Length > 200_000) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String) return false;
            string? name = k.GetString();
            if (name is null || name.Length == 0 || char.IsDigit(name[0]) || !Enum.TryParse<QrContentKind>(name, false, out var kind) || !Enum.IsDefined(kind)) return false;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
                foreach (var p in f.EnumerateObject())
                {
                    if (fields.Count >= MaxFields) break;
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } v && v.Length <= MaxValueLength && p.Name.Length <= 40) fields[p.Name] = v;
                }
            spec = new QrContentSpec(kind, fields);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
