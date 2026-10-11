namespace Rotula.Core.Barcodes.QrContent;

/// <summary>What kind of input a field wants; the dialog uses it to choose the editor, the keyboard and the {x} button.</summary>
public enum QrInputHint
{
    /// <summary>One line of text.</summary>
    Text,
    /// <summary>Several lines of text.</summary>
    MultilineText,
    /// <summary>A phone number.</summary>
    Phone,
    /// <summary>An e-mail address.</summary>
    Email,
    /// <summary>A web address.</summary>
    Url,
    /// <summary>A decimal number.</summary>
    Number,
    /// <summary>Digits only (a GTIN, a date as YYMMDD).</summary>
    Digits,
    /// <summary>A date, or a date with time and time zone (ISO 8601, for example 2026-03-14T09:00:00-05:00).</summary>
    DateTime,
    /// <summary>One of <see cref="QrFieldSchema.Choices"/>.</summary>
    Choice,
    /// <summary>true or false.</summary>
    Flag,
}

/// <summary>One field of a content kind.</summary>
/// <param name="Name">The name used in <see cref="QrContentSpec.Fields"/>.</param>
/// <param name="LabelKey">Resource key of the field's label.</param>
/// <param name="Required">True when the content cannot be built without it.</param>
/// <param name="Hint">The kind of editor.</param>
/// <param name="MaxLength">Most characters of the resolved value; 0 for no limit of its own.</param>
/// <param name="Choices">For <see cref="QrInputHint.Choice"/>: the allowed values; each has the label key <c>QrContent.Choice.&lt;value&gt;</c>.</param>
/// <param name="Sample">A valid example value, used to check a field that holds a placeholder and to fill the live preview.</param>
public sealed record QrFieldSchema(string Name, string LabelKey, bool Required, QrInputHint Hint, int MaxLength, IReadOnlyList<string> Choices, string Sample);

/// <summary>A content kind with its fields.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="LabelKey">Resource key of the kind's name (the chip text).</param>
/// <param name="Fields">The fields, in the order the form shows them.</param>
public sealed record QrKindSchema(QrContentKind Kind, string LabelKey, IReadOnlyList<QrFieldSchema> Fields)
{
    /// <summary>The field called <paramref name="name"/>, or null.</summary>
    public QrFieldSchema? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);
}

/// <summary>The fields of every content kind as data, so the content builder dialog can be built and translated from it.</summary>
public static class QrContentSchema
{
    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    private static QrFieldSchema F(string name, QrInputHint hint, string sample, bool required = false, int max = 0, string[]? choices = null) =>
        new(name, "QrContent.Field." + char.ToUpperInvariant(name[0]) + name[1..], required, hint, max, choices ?? None, sample);

    private static QrKindSchema K(QrContentKind kind, params QrFieldSchema[] fields) => new(kind, "QrContent.Kind." + kind, fields);

    private static QrFieldSchema[] Address() => new[]
    {
        F("street", QrInputHint.Text, "Calle 50 #12", max: 100), F("city", QrInputHint.Text, "Panama", max: 60), F("region", QrInputHint.Text, "Panama", max: 60),
        F("postalCode", QrInputHint.Text, "0801", max: 20), F("country", QrInputHint.Text, "PA", max: 60),
    };

    /// <summary>Every kind, in the order of the chips.</summary>
    public static IReadOnlyList<QrKindSchema> All { get; } = new[]
    {
        K(QrContentKind.Url, F("url", QrInputHint.Url, "https://example.com/menu", true, 2000), F("stripTracking", QrInputHint.Flag, "false")),
        K(QrContentKind.Text, F("text", QrInputHint.MultilineText, "Hello", true)),
        K(QrContentKind.Wifi, F("ssid", QrInputHint.Text, "CafeGuest", true, 32), F("password", QrInputHint.Text, "welcome2026", false, 64),
            F("security", QrInputHint.Choice, "Wpa", choices: new[] { "Wpa", "Wpa3Sae", "Wep", "None" }), F("hidden", QrInputHint.Flag, "false")),
        K(QrContentKind.VCard, new[]
        {
            F("firstName", QrInputHint.Text, "Ana", max: 60), F("lastName", QrInputHint.Text, "Perez", max: 60), F("organization", QrInputHint.Text, "Rotula", max: 100),
            F("title", QrInputHint.Text, "Logistics", max: 100), F("phone", QrInputHint.Phone, "+5071234567"), F("mobile", QrInputHint.Phone, "+50761234567"),
            F("email", QrInputHint.Email, "ana@example.com", max: 100), F("url", QrInputHint.Url, "https://example.com", max: 200),
        }.Concat(Address()).Append(F("note", QrInputHint.MultilineText, "Note", max: 500)).ToArray()),
        K(QrContentKind.MeCard, new[]
        {
            F("firstName", QrInputHint.Text, "Ana", max: 60), F("lastName", QrInputHint.Text, "Perez", max: 60), F("phone", QrInputHint.Phone, "+5071234567"),
            F("email", QrInputHint.Email, "ana@example.com", max: 100), F("url", QrInputHint.Url, "https://example.com", max: 200),
        }.Concat(Address()).Append(F("note", QrInputHint.MultilineText, "Note", max: 500)).ToArray()),
        K(QrContentKind.GS1DigitalLink, F("prefix", QrInputHint.Url, "https://id.gs1.org", max: 200), F("gtin", QrInputHint.Digits, "09506000134352", true, 14),
            F("lot", QrInputHint.Text, "ABC123", max: 20), F("serial", QrInputHint.Text, "SER001", max: 20), F("expiry", QrInputHint.Digits, "251231", max: 6),
            F("bestBefore", QrInputHint.Digits, "251231", max: 6), F("productionDate", QrInputHint.Digits, "250101", max: 6)),
        K(QrContentKind.Email, F("to", QrInputHint.Email, "ana@example.com", true, 200), F("subject", QrInputHint.Text, "Hello", max: 200), F("body", QrInputHint.MultilineText, "Body", max: 1000)),
        K(QrContentKind.Phone, F("number", QrInputHint.Phone, "+50761234567", true)),
        K(QrContentKind.Sms, F("number", QrInputHint.Phone, "+50761234567", true), F("message", QrInputHint.MultilineText, "Hello", max: 500)),
        K(QrContentKind.Location, F("latitude", QrInputHint.Number, "8.983333", true), F("longitude", QrInputHint.Number, "-79.516667", true), F("label", QrInputHint.Text, "Panama City", max: 100)),
        K(QrContentKind.CalendarEvent, F("title", QrInputHint.Text, "Team lunch", true, 200), F("start", QrInputHint.DateTime, "2026-03-14T09:00:00-05:00", true),
            F("end", QrInputHint.DateTime, "2026-03-14T10:00:00-05:00", true), F("location", QrInputHint.Text, "Panama City", max: 200),
            F("description", QrInputHint.MultilineText, "Bring slides", max: 1000), F("allDay", QrInputHint.Flag, "false")),
    };

    /// <summary>The schema of <paramref name="kind"/>.</summary>
    public static QrKindSchema For(QrContentKind kind) => All.First(k => k.Kind == kind);
}
