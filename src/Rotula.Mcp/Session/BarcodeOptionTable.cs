using System.Text.Json;
using ModelContextProtocol;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Mcp.Session;

/// <summary>One option of a barcode that is passed through the <c>options</c> object of add_barcode and update_element.</summary>
/// <param name="Name">The option name as the assistant writes it (camelCase of the <see cref="BarcodeOption"/> member).</param>
/// <param name="Option">The catalogue option, which says which symbologies the option applies to.</param>
/// <param name="Description">What it does and which values it accepts.</param>
internal sealed record BarcodeOptionDef(string Name, BarcodeOption Option, string Description)
{
    /// <summary>Reads the current value of the option from an element.</summary>
    public required Func<BarcodeElement, object> Get { get; init; }

    /// <summary>Validates <paramref name="value"/> and stores it; throws <see cref="McpException"/> with a plain message when it is not acceptable.</summary>
    public required Action<BarcodeElement, JsonElement> Set { get; init; }
}

/// <summary>
/// The options of the later barcode symbologies, as one validated table. Why a table and not one argument each: there are 22 of
/// them, most apply to a single symbology, and the catalogue (<see cref="BarcodeCatalog"/>) already says which apply where.
/// Ranges here only reject values that cannot be a setting at all; <c>validate_label</c> reports the symbology-specific rules.
/// </summary>
internal static class BarcodeOptionTable
{
    private static BarcodeOptionDef Int(BarcodeOption o, string desc, int min, int max, Func<BarcodeElement, int> get, Action<BarcodeElement, int> set)
    {
        string name = CamelCase(o);
        return new(name, o, $"{desc} Whole number from {min} to {max}.")
        {
            Get = e => get(e),
            Set = (e, j) => set(e, ElementModel.CheckRange(name, ElementModel.ReadInt(j, name), min, max)),
        };
    }

    private static BarcodeOptionDef Flag(BarcodeOption o, string desc, Func<BarcodeElement, bool> get, Action<BarcodeElement, bool> set)
    {
        string name = CamelCase(o);
        return new(name, o, desc + " true or false.") { Get = e => get(e), Set = (e, j) => set(e, ElementModel.ReadBool(j, name)) };
    }

    private static BarcodeOptionDef Choice<T>(BarcodeOption o, string desc, Func<BarcodeElement, T> get, Action<BarcodeElement, T> set) where T : struct, Enum
    {
        string name = CamelCase(o);
        return new(name, o, $"{desc} One of: {string.Join(", ", Enum.GetNames<T>())}.")
        {
            Get = e => get(e).ToString(),
            Set = (e, j) => set(e, ElementModel.ParseEnum<T>(ElementModel.ReadString(j, name), name)),
        };
    }

    private static BarcodeOptionDef Letter(BarcodeOption o, string desc, string allowed, Func<BarcodeElement, char> get, Action<BarcodeElement, char> set)
    {
        string name = CamelCase(o);
        return new(name, o, $"{desc} One character of: {allowed}.")
        {
            Get = e => get(e).ToString(),
            Set = (e, j) =>
            {
                string s = ElementModel.ReadString(j, name).Trim().ToUpperInvariant();
                if (s.Length != 1 || !allowed.Contains(s[0])) throw new McpException($"\"{name}\" must be one character of: {allowed}.");
                set(e, s[0]);
            },
        };
    }

    /// <summary>All options, in the order of the <see cref="BarcodeOption"/> enum.</summary>
    public static readonly IReadOnlyList<BarcodeOptionDef> All = new BarcodeOptionDef[]
    {
        Int(BarcodeOption.Code11CheckDigitCount, "Code 11: number of check digits the printer adds.", 1, 2, e => e.Code11CheckDigitCount, (e, v) => e.Code11CheckDigitCount = v),
        Choice<MsiCheckDigitMode>(BarcodeOption.MsiCheckDigit, "MSI: which check digits the printer adds.", e => e.MsiCheckDigit, (e, v) => e.MsiCheckDigit = v),
        Flag(BarcodeOption.MsiCheckDigitInText, "MSI: also print the check digit(s) in the text line.", e => e.MsiCheckDigitInText, (e, v) => e.MsiCheckDigitInText = v),
        Letter(BarcodeOption.CodabarStart, "Codabar: start character (not typed in the data).", "ABCD", e => e.CodabarStart, (e, v) => e.CodabarStart = v),
        Letter(BarcodeOption.CodabarStop, "Codabar: stop character (not typed in the data).", "ABCD", e => e.CodabarStop, (e, v) => e.CodabarStop = v),
        Choice<DataBarType>(BarcodeOption.DataBarType, "GS1 DataBar variant. Expanded takes GS1 element strings such as (01)12345678901231(10)ABC; the others take up to 13 digits.", e => e.DataBarType, (e, v) => e.DataBarType = v),
        Int(BarcodeOption.DataBarSeparatorHeight, "GS1 DataBar: separator height between stacked rows, in modules.", 1, 2, e => e.DataBarSeparatorHeight, (e, v) => e.DataBarSeparatorHeight = v),
        Int(BarcodeOption.DataBarSegmentsPerRow, "GS1 DataBar Expanded: segments per row (even; below 22 the symbol is stacked).", 2, 22, e => e.DataBarSegmentsPerRow, (e, v) => e.DataBarSegmentsPerRow = v),
        Flag(BarcodeOption.AztecEci, "Aztec: the data contains ECI codes.", e => e.AztecEci, (e, v) => e.AztecEci = v),
        Int(BarcodeOption.AztecErrorControl, "Aztec: 0 = automatic, 1-99 = minimum error correction percent, 101-104 = compact size, 201-232 = full size, 300 = Rune.", 0, 300, e => e.AztecErrorControl, (e, v) => e.AztecErrorControl = v),
        Flag(BarcodeOption.AztecMenuSymbol, "Aztec: make it a menu (reader setup) symbol.", e => e.AztecMenuSymbol, (e, v) => e.AztecMenuSymbol = v),
        Int(BarcodeOption.AztecStructuredAppendCount, "Aztec: number of symbols in a structured append set (1 = a single symbol).", 1, 26, e => e.AztecStructuredAppendCount, (e, v) => e.AztecStructuredAppendCount = v),
        new("aztecStructuredAppendId", BarcodeOption.AztecStructuredAppendId, "Aztec: optional id of the structured append set, at most 24 printable characters (no comma, ^ or ~).")
        {
            Get = e => e.AztecStructuredAppendId,
            Set = (e, j) => e.AztecStructuredAppendId = ElementModel.Clip(ElementModel.ReadString(j, "aztecStructuredAppendId"), 24),
        },
        Int(BarcodeOption.MicroPdf417Mode, "MicroPDF417 mode; it fixes the columns, rows and error correction.", 0, 33, e => e.MicroPdf417Mode, (e, v) => e.MicroPdf417Mode = v),
        Choice<CodablockMode>(BarcodeOption.CodablockMode, "Codablock variant.", e => e.CodablockMode, (e, v) => e.CodablockMode = v),
        Flag(BarcodeOption.CodablockSecurity, "Codablock: add the check characters.", e => e.CodablockSecurity, (e, v) => e.CodablockSecurity = v),
        Int(BarcodeOption.MaxiCodeMode, "MaxiCode mode: 2 or 3 carry a structured carrier message, 4-6 any text.", 2, 6, e => e.MaxiCodeMode, (e, v) => e.MaxiCodeMode = v),
        Int(BarcodeOption.MaxiCodeSymbolNumber, "MaxiCode: number of this symbol in a structured append set.", 1, 8, e => e.MaxiCodeSymbolNumber, (e, v) => e.MaxiCodeSymbolNumber = v),
        Int(BarcodeOption.MaxiCodeSymbolTotal, "MaxiCode: total number of symbols in the set.", 1, 8, e => e.MaxiCodeSymbolTotal, (e, v) => e.MaxiCodeSymbolTotal = v),
        Int(BarcodeOption.Tlc39MicroModuleWidth, "TLC39: narrow bar width of the MicroPDF417 part, in dots.", 1, 10, e => e.Tlc39MicroModuleWidth, (e, v) => e.Tlc39MicroModuleWidth = v),
        Int(BarcodeOption.Tlc39MicroRowHeight, "TLC39: row height of the MicroPDF417 part, in dots.", 1, 255, e => e.Tlc39MicroRowHeight, (e, v) => e.Tlc39MicroRowHeight = v),
        Letter(BarcodeOption.Code49StartMode, "Code 49 starting mode: A = automatic, or 0-5.", "A012345", e => e.Code49StartMode, (e, v) => e.Code49StartMode = v),
    };

    private static readonly Dictionary<string, BarcodeOptionDef> ByName = All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Option names that have their own argument or property (they are not part of the options object).</summary>
    private static readonly HashSet<BarcodeOption> Direct = new()
    {
        BarcodeOption.Height, BarcodeOption.ModuleWidth, BarcodeOption.WideBarRatio, BarcodeOption.HumanReadable, BarcodeOption.HumanReadablePosition,
        BarcodeOption.CheckDigit, BarcodeOption.Code128Mode, BarcodeOption.QrErrorCorrection, BarcodeOption.QrModel, BarcodeOption.ModuleSize,
        BarcodeOption.Columns, BarcodeOption.Rows, BarcodeOption.Pdf417SecurityLevel, BarcodeOption.Pdf417Truncate,
    };

    /// <summary>The name a catalogue option has in the tool arguments and in update_element.</summary>
    public static string ToolName(BarcodeOption option) => option switch
    {
        BarcodeOption.ModuleWidth => "moduleWidthDots",
        BarcodeOption.Pdf417SecurityLevel => "pdf417SecurityLevel",
        _ => CamelCase(option),
    };

    /// <summary>True when the option has its own argument; false when it goes into the options object.</summary>
    public static bool IsDirect(BarcodeOption option) => Direct.Contains(option);

    /// <summary>The definitions of the options object that apply to <paramref name="symbology"/>.</summary>
    public static IEnumerable<BarcodeOptionDef> For(BarcodeSymbology symbology)
    {
        if (!Enum.IsDefined(symbology)) return Enumerable.Empty<BarcodeOptionDef>();
        var info = BarcodeCatalog.Get(symbology);
        return All.Where(d => info.Supports(d.Option));
    }

    /// <summary>
    /// Applies the <c>options</c> object to <paramref name="element"/>. Every entry must name an option of the element's
    /// current symbology and carry an acceptable value; otherwise nothing is applied by that entry and the error lists what is valid.
    /// </summary>
    public static void Apply(BarcodeElement element, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object)
            throw new McpException("\"options\" must be an object such as {\"codabarStart\":\"B\"}. Call list_symbologies for the options of each type.");
        var valid = For(element.Symbology).ToList();
        int count = 0;
        foreach (var entry in options.EnumerateObject())
        {
            if (++count > 40) throw new McpException("Too many options in one call (at most 40).");
            var def = valid.FirstOrDefault(d => string.Equals(d.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (def is null)
            {
                string known = ByName.ContainsKey(entry.Name) ? "that option belongs to another barcode type" : "unknown option";
                string list = valid.Count == 0 ? "This barcode type has no extra options." : "Valid options for " + element.Symbology + ": " + string.Join(", ", valid.Select(d => d.Name)) + ".";
                throw new McpException($"\"{entry.Name}\": {known}. {list}");
            }
            def.Set(element, entry.Value);
        }
    }

    private static string CamelCase(BarcodeOption option)
    {
        string s = option.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}
