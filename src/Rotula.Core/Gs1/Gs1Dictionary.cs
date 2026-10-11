using System.Globalization;
using System.Text.Json;

namespace Rotula.Core.Gs1;

/// <summary>The character set a component of an application identifier value is made of.</summary>
public enum Gs1CharacterSet
{
    /// <summary>Digits 0-9 (dictionary type <c>N</c>).</summary>
    Numeric,
    /// <summary>GS1 AI encodable character set 82 (dictionary type <c>X</c>): digits, letters and !"%&amp;'()*+,-./:;&lt;=&gt;?_.</summary>
    Cset82,
    /// <summary>GS1 AI encodable character set 39 (dictionary type <c>Y</c>): digits, upper case letters and # - /.</summary>
    Cset39,
    /// <summary>Base64url with optional <c>=</c> padding (dictionary type <c>Z</c>).</summary>
    Base64Url,
}

/// <summary>One component of the value of an application identifier, for example the country code and the postal code of AI 421.</summary>
/// <param name="CharacterSet">The characters the component may contain.</param>
/// <param name="MinLength">The fewest characters (equal to <paramref name="MaxLength"/> for a fixed length component).</param>
/// <param name="MaxLength">The most characters.</param>
/// <param name="Optional">True when the component may be left out (only at the end of the value).</param>
/// <param name="Linters">Names of the syntax tests that apply to the component, in order (see <see cref="Gs1Linters"/>).</param>
public sealed record Gs1Component(Gs1CharacterSet CharacterSet, int MinLength, int MaxLength, bool Optional, IReadOnlyList<string> Linters)
{
    /// <summary>True when the component always has exactly <see cref="MaxLength"/> characters.</summary>
    public bool IsFixedLength => MinLength == MaxLength;
}

/// <summary>
/// One GS1 Application Identifier as the GS1 Barcode Syntax Dictionary describes it: the formats of its value, whether an FNC1
/// separator must follow it, and its relations to other identifiers.
/// </summary>
public sealed class Gs1AiDefinition
{
    internal Gs1AiDefinition(string ai, string dataTitle, string name, bool predefinedLength, bool digitalLinkAttribute,
        IReadOnlyList<Gs1Component> components, IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> requires, IReadOnlyList<string> excludes,
        IReadOnlyList<IReadOnlyList<string>>? digitalLinkQualifiers, bool decimalPlacesInAi)
    {
        Ai = ai;
        DataTitle = dataTitle;
        Name = name;
        PredefinedLength = predefinedLength;
        IsDigitalLinkAttribute = digitalLinkAttribute;
        Components = components;
        Requires = requires;
        Excludes = excludes;
        DigitalLinkQualifiers = digitalLinkQualifiers;
        DecimalPlacesInAi = decimalPlacesInAi;
        MinValueLength = components.Where(c => !c.Optional).Sum(c => c.MinLength);
        MaxValueLength = components.Sum(c => c.MaxLength);
    }

    /// <summary>The identifier digits, for example <c>01</c> or <c>3103</c>.</summary>
    public string Ai { get; }

    /// <summary>The short data title the dictionary gives, for example <c>BATCH/LOT</c>.</summary>
    public string DataTitle { get; }

    /// <summary>Rotula's own plain-English name, for example <c>Batch or lot number</c>; empty when none was written.</summary>
    public string Name { get; }

    /// <summary>The best name to show: <see cref="Name"/> when there is one, otherwise <see cref="DataTitle"/>, otherwise the identifier.</summary>
    public string Title => Name.Length > 0 ? Name : DataTitle.Length > 0 ? DataTitle : Ai;

    /// <summary>
    /// True when the length of the element is predefined, so no FNC1 separator follows its value even when more elements come after it
    /// (the dictionary's <c>*</c> flag; GS1 General Specifications 5.4.8.3).
    /// </summary>
    public bool PredefinedLength { get; }

    /// <summary>True when an FNC1 separator is needed after the value when another element follows (the opposite of <see cref="PredefinedLength"/>).</summary>
    public bool RequiresFnc1Separator => !PredefinedLength;

    /// <summary>True when the identifier may appear as a data attribute in a GS1 Digital Link URI (the dictionary's <c>?</c> flag).</summary>
    public bool IsDigitalLinkAttribute { get; }

    /// <summary>The components that make up the value, in order.</summary>
    public IReadOnlyList<Gs1Component> Components { get; }

    /// <summary>
    /// Associations that must be present in the data of the item (all of them): each entry is a list of alternatives, and each
    /// alternative is a list of identifier patterns that must all be present. A pattern may use <c>n</c> as a wildcard digit.
    /// The check is meant for all barcodes on the item together, not for a single symbol.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> Requires { get; }

    /// <summary>Identifier patterns that must not be combined with this one (an identifier never excludes itself).</summary>
    public IReadOnlyList<string> Excludes { get; }

    /// <summary>
    /// For a GS1 Digital Link primary key: the alternative orders of qualifier identifiers (empty list when no qualifier is accepted);
    /// null when the identifier is not a primary key.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>>? DigitalLinkQualifiers { get; }

    /// <summary>True when the identifier is a GS1 Digital Link primary key (a GTIN, SSCC, GLN, ...).</summary>
    public bool IsDigitalLinkKey => DigitalLinkQualifiers is not null;

    /// <summary>True for the identifiers whose last digit says where the decimal point goes (3103 is 3 decimals).</summary>
    public bool DecimalPlacesInAi { get; }

    /// <summary>The fewest characters the value can have.</summary>
    public int MinValueLength { get; }

    /// <summary>The most characters the value can have.</summary>
    public int MaxValueLength { get; }

    /// <summary>True when every component has a fixed length and none is optional, so the value always has <see cref="MaxValueLength"/> characters.</summary>
    public bool HasFixedValueLength => MinValueLength == MaxValueLength;

    /// <summary>True when the whole value is digits.</summary>
    public bool IsNumeric => Components.All(c => c.CharacterSet == Gs1CharacterSet.Numeric);

    /// <summary>A short description of the value format such as <c>N14</c> or <c>X..20</c> (components separated by a space).</summary>
    public string FormatText => string.Join(' ', Components.Select(c =>
    {
        string type = c.CharacterSet switch { Gs1CharacterSet.Numeric => "N", Gs1CharacterSet.Cset82 => "X", Gs1CharacterSet.Cset39 => "Y", _ => "Z" };
        string text = c.IsFixedLength ? type + c.MaxLength.ToString(CultureInfo.InvariantCulture) : type + ".." + c.MaxLength.ToString(CultureInfo.InvariantCulture);
        return c.Optional ? "[" + text + "]" : text;
    }));

    /// <inheritdoc />
    public override string ToString() => $"({Ai}) {Title}";
}

/// <summary>Who wrote the dictionary and under which licence; shown in the About text and the notices.</summary>
/// <param name="Name">The name of the dictionary.</param>
/// <param name="Url">Where it comes from.</param>
/// <param name="Release">The release tag it was built from.</param>
/// <param name="License">The licence of the source.</param>
/// <param name="Copyright">The copyright lines the source states.</param>
/// <param name="Changes">What was changed when it was converted for Rotula.</param>
public sealed record Gs1DictionarySource(string Name, string Url, string Release, string License, IReadOnlyList<string> Copyright, string Changes);

/// <summary>
/// The GS1 Application Identifier dictionary, loaded from the JSON file embedded in the assembly. The file is produced from the official
/// GS1 Barcode Syntax Dictionary by <c>tools/gs1-dictionary</c> (Apache-2.0, see THIRD-PARTY-NOTICES.md).
/// </summary>
public static class Gs1Dictionary
{
    private const string ResourceName = "Rotula.Core.Gs1.gs1-dictionary.json";
    private static readonly Lazy<Loaded> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record Loaded(Gs1DictionarySource Source, int EntryLines, IReadOnlyList<Gs1AiDefinition> Entries, Dictionary<string, Gs1AiDefinition> ByAi);

    /// <summary>Where the dictionary comes from (release, licence, copyright).</summary>
    public static Gs1DictionarySource Source => Data.Value.Source;

    /// <summary>Number of lines (single identifiers and ranges) in the source dictionary.</summary>
    public static int EntryLineCount => Data.Value.EntryLines;

    /// <summary>Every application identifier, ranges expanded, ordered by length and then value.</summary>
    public static IReadOnlyList<Gs1AiDefinition> All => Data.Value.Entries;

    /// <summary>Finds an identifier by its digits; null when it is not defined.</summary>
    public static Gs1AiDefinition? Find(string ai) => ai is not null && Data.Value.ByAi.TryGetValue(ai, out var d) ? d : null;

    /// <summary>The definition of <paramref name="ai"/>.</summary>
    /// <exception cref="KeyNotFoundException">The identifier is not defined.</exception>
    public static Gs1AiDefinition Get(string ai) => Find(ai) ?? throw new KeyNotFoundException($"({ai}) is not a GS1 Application Identifier.");

    /// <summary>True when <paramref name="ai"/> is a defined identifier.</summary>
    public static bool IsKnown(string ai) => Find(ai) is not null;

    /// <summary>
    /// The identifier that starts <paramref name="digits"/> (a message without parentheses), or null when none does. Identifiers are
    /// prefix-free, so the first match of 2, 3 or 4 digits is the only one.
    /// </summary>
    public static Gs1AiDefinition? FindAtStart(ReadOnlySpan<char> digits)
    {
        for (int len = 2; len <= 4 && len <= digits.Length; len++)
            if (Data.Value.ByAi.TryGetValue(digits[..len].ToString(), out var d)) return d;
        return null;
    }

    /// <summary>True when no FNC1 separator is needed after the value of <paramref name="ai"/> (a defined identifier with a predefined length).</summary>
    public static bool IsPredefinedLength(string ai) => Find(ai)?.PredefinedLength ?? false;

    /// <summary>True when <paramref name="ai"/> matches <paramref name="pattern"/>, where <c>n</c> in the pattern stands for any digit.</summary>
    public static bool Matches(string pattern, string ai)
    {
        if (pattern.Length != ai.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i] != 'n' && pattern[i] != ai[i]) return false;
        return true;
    }

    private static Loaded Load()
    {
        using var stream = typeof(Gs1Dictionary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The GS1 dictionary resource is missing from Rotula.Core.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var src = root.GetProperty("source");
        var source = new Gs1DictionarySource(
            src.GetProperty("name").GetString()!, src.GetProperty("url").GetString()!, src.GetProperty("release").GetString()!,
            src.GetProperty("license").GetString()!, src.GetProperty("copyright").EnumerateArray().Select(e => e.GetString()!).ToList(),
            src.GetProperty("changes").GetString()!);

        var entries = new List<Gs1AiDefinition>();
        foreach (var e in root.GetProperty("entries").EnumerateArray())
        {
            var components = e.GetProperty("components").EnumerateArray().Select(c => new Gs1Component(
                c.GetProperty("cset").GetString() switch { "N" => Gs1CharacterSet.Numeric, "X" => Gs1CharacterSet.Cset82, "Y" => Gs1CharacterSet.Cset39, _ => Gs1CharacterSet.Base64Url },
                c.GetProperty("min").GetInt32(), c.GetProperty("max").GetInt32(), c.GetProperty("optional").GetBoolean(),
                c.GetProperty("linters").EnumerateArray().Select(l => l.GetString()!).ToList())).ToList();
            var requires = e.GetProperty("requires").EnumerateArray()
                .Select(req => (IReadOnlyList<IReadOnlyList<string>>)req.EnumerateArray()
                    .Select(alt => (IReadOnlyList<string>)alt.EnumerateArray().Select(x => x.GetString()!).ToList()).ToList()).ToList();
            var excludes = e.GetProperty("excludes").EnumerateArray().Select(x => x.GetString()!).ToList();
            IReadOnlyList<IReadOnlyList<string>>? qualifiers = null;
            if (e.GetProperty("digitalLinkKey") is { ValueKind: JsonValueKind.Array } dl)
                qualifiers = dl.EnumerateArray().Select(seq => (IReadOnlyList<string>)seq.EnumerateArray().Select(x => x.GetString()!).ToList()).ToList();
            entries.Add(new Gs1AiDefinition(e.GetProperty("ai").GetString()!, e.GetProperty("title").GetString()!, e.GetProperty("name").GetString()!,
                e.GetProperty("fixedLength").GetBoolean(), e.GetProperty("digitalLinkAttribute").GetBoolean(), components, requires, excludes,
                qualifiers, e.GetProperty("decimalPlacesInAi").GetBoolean()));
        }
        return new Loaded(source, root.GetProperty("entryLines").GetInt32(), entries, entries.ToDictionary(x => x.Ai, StringComparer.Ordinal));
    }
}
