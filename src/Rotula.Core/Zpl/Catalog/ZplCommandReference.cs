namespace Rotula.Core.Zpl.Catalog;

/// <summary>One parameter of a ZPL command: the letter the guide uses for it and what it means, in Rotula's own words.</summary>
/// <param name="Name">The parameter as it is written in the command's signature, for example <c>x</c> or <c>d:o.x</c>.</param>
/// <param name="Meaning">One short phrase saying what the value does and, where useful, its range or the letters it accepts.</param>
public sealed record ZplParameter(string Name, string Meaning);

/// <summary>
/// The parameter signature of one ZPL command, kept as C# data so the in-app reference needs no file at run time and the
/// compiler checks every entry. The name and the one-sentence summary of a command live in the JSON catalogue
/// (<see cref="ZplCommandCatalog"/>); this type only adds the parameters.
/// </summary>
public sealed class ZplCommandSignature
{
    internal ZplCommandSignature(string id, string? note, params ZplParameter[] parameters)
    {
        Id = id;
        Note = note;
        Parameters = parameters;
    }

    /// <summary>The command id with its primary prefix, for example <c>^FO</c> or <c>~DG</c>.</summary>
    public string Id { get; }

    /// <summary>The parameters in the order they are written, separated by commas in the ZPL text; empty for a command without parameters.</summary>
    public IReadOnlyList<ZplParameter> Parameters { get; }

    /// <summary>A short remark that applies to the whole command (how the data is given, a quirk), or null.</summary>
    public string? Note { get; }
}

/// <summary>
/// What a source editor shows for the command under the caret: id, name, parameters and summary, merged from the JSON catalogue
/// and the C# signature table. Either source may be missing for a given command; the texts then say what is known.
/// </summary>
public sealed class ZplCommandHint
{
    internal ZplCommandHint(string id, string name, string summary, IReadOnlyList<ZplParameter> parameters, string? note, ZplCommandStatus? status)
    {
        Id = id;
        Name = name;
        Summary = summary;
        Parameters = parameters;
        Note = note;
        Status = status;
    }

    /// <summary>The command id with the prefix as typed, for example <c>^FO</c>.</summary>
    public string Id { get; }

    /// <summary>Short name of the command.</summary>
    public string Name { get; }

    /// <summary>One or two sentences saying what the command does.</summary>
    public string Summary { get; }

    /// <summary>The parameters in order; empty when the command takes none or the signature is not catalogued.</summary>
    public IReadOnlyList<ZplParameter> Parameters { get; }

    /// <summary>A remark about the whole command, or null.</summary>
    public string? Note { get; }

    /// <summary>What Rotula does with the command, or null when the JSON catalogue does not list it.</summary>
    public ZplCommandStatus? Status { get; }

    /// <summary>The parameter names joined as they are written, for example <c>x,y,z</c>; empty for a command without parameters.</summary>
    public string ParameterList => string.Join(",", Parameters.Select(p => p.Name));

    /// <summary>The first line of the hint: the id, the parameter list and the name, for example <c>^FO x,y,z  Field origin</c>.</summary>
    public string Headline => Parameters.Count == 0 ? Id + "  " + Name : Id + " " + ParameterList + "  " + Name;

    /// <summary>Every parameter as <c>name: meaning</c>, joined with semicolons; empty when there are none.</summary>
    public string ParameterText => string.Join("; ", Parameters.Select(p => p.Name + ": " + p.Meaning));
}

/// <summary>
/// The command reference behind the hint line of the ZPL source panel: for each command Rotula supports (and the usual ones it
/// keeps verbatim) the parameters in order and what each one means. Written in Rotula's own words. <see cref="Describe"/> merges an
/// entry with the name and summary of <see cref="ZplCommandCatalog"/>; <see cref="AtCaret"/> finds the command under a caret.
/// </summary>
public static class ZplCommandReference
{
    private static ZplParameter P(string name, string meaning) => new(name, meaning);

    private static readonly ZplParameter Rotation = P("o", "rotation: N upright, R turned 90 degrees clockwise, I upside down, B turned 90 degrees anticlockwise");
    private static readonly ZplParameter BarHeight = P("h", "bar height in dots (the ^BY value when left out)");
    private static readonly ZplParameter Interpretation = P("f", "Y prints the human-readable text, N leaves it out");
    private static readonly ZplParameter InterpretationAbove = P("g", "Y puts the human-readable text above the bars instead of below");
    private static readonly ZplParameter Colour = P("c", "line colour: B black, W white");
    private static readonly ZplParameter Device = P("d:o.x", "where the object lives on the printer: drive letter, name and extension, for example R:LOGO.GRF");

    private static readonly ZplCommandSignature[] Table =
    {
        // ---- label structure and layout
        new("^XA", "Starts a label; everything up to ^XZ belongs to it."),
        new("^XZ", "Ends the label and prints it."),
        new("^LH", null, P("x", "left offset of the label home in dots"), P("y", "top offset of the label home in dots")),
        new("^LL", null, P("y", "length of the label in dots")),
        new("^PW", null, P("w", "print width in dots")),
        new("^LS", null, P("a", "moves every field left by this many dots; a negative value moves right")),
        new("^LT", null, P("y", "moves the whole label down (positive) or up (negative) by this many dot rows")),
        new("^LR", null, P("a", "Y prints every following field reversed against its background, N stops doing so")),
        new("^PO", null, P("a", "N prints normally, I prints the label upside down")),
        new("^PM", null, P("a", "Y prints the whole label as a mirror image, N prints normally")),
        new("^MU", null, P("a", "unit of the values that follow: D dots, I inches, M millimetres"), P("b", "dots per inch the format was written for"), P("c", "dots per inch to convert to")),
        new("^PQ", null, P("q", "number of labels to print"), P("p", "pause after this many labels; 0 never pauses"), P("r", "copies of each serial number"), P("o", "Y overrides the pause count")),
        new("^FX", "Everything after the command up to the next ^ or ~ is a comment the printer ignores.", P("c", "the comment text")),
        new("^CC", null, P("x", "the character that replaces ^ as the command prefix")),
        new("^CD", null, P("x", "the character that replaces the comma between parameters")),
        new("^CT", null, P("x", "the character that replaces ~ as the prefix of immediate commands")),

        // ---- fields
        new("^FO", null, P("x", "left edge of the field in dots, from the label home"), P("y", "top edge of the field in dots"), P("z", "justification: 0 left, 1 right, 2 automatic")),
        new("^FT", null, P("x", "horizontal position of the field's baseline origin in dots"), P("y", "vertical position of the baseline origin in dots"), P("z", "justification: 0 left, 1 right, 2 automatic")),
        new("^FD", "The data runs up to the next ^ or ~, so neither can appear in it unless ^FH hex escapes are used.", P("data", "the text or barcode data of the field")),
        new("^FV", "Read exactly like ^FD; the name marks data that changes between prints of a stored format.", P("data", "the data of the field")),
        new("^FS", "Closes the field; every field needs one."),
        new("^FR", "Inverts the dots the field overlaps, so black becomes white and white becomes black."),
        new("^FH", null, P("a", "the character that starts a two-digit hexadecimal byte in the data (default _)")),
        new("^FB", null, P("a", "width of the text block in dots"), P("b", "maximum number of lines"), P("c", "extra space between lines in dots; negative tightens"), P("d", "alignment: L left, C centre, R right, J justified"), P("e", "hanging indent of the second and following lines in dots")),
        new("^TB", null, P("o", "rotation N, R, I or B"), P("w", "width of the text box in dots"), P("h", "height of the text box in dots; lines that do not fit are not printed")),
        new("^FP", null, P("d", "direction the characters advance: H horizontal, V vertical, R reverse"), P("g", "extra gap between characters in dots")),
        new("^FN", "Written as ^FN3\"Prompt\": the quoted prompt is optional.", P("n", "field number 0 to 9999; fields with the same number share their data"), P("\"p\"", "optional prompt shown on a keyboard display, letters, digits and spaces in quotes")),
        new("^FE", null, P("a", "the character that marks a field-number reference inside the data (default #)")),
        new("^FC", null, P("a", "primary clock indicator that starts a clock code in the data (default %)"), P("b", "secondary clock indicator, for the second offset clock"), P("c", "third clock indicator")),
        new("^SN", null, P("v", "start value: letters, digits, spaces, - / . with at least one digit"), P("n", "step added after each label; negative counts down"), P("z", "Y keeps leading zeros, N drops them")),
        new("^SF", null, P("m", "mask, one character per data character counted from the end: D decimal, H hexadecimal, O octal, A letter, N letter or digit, % unchanged"), P("i", "the step written in the same alphabet as the mask")),
        new("^FW", null, P("r", "default rotation for every following field: N, R, I or B"), P("z", "default justification: 0 left, 1 right, 2 automatic")),
        new("^FM", null, P("x1,y1,...", "origins of the fields of a multi-part barcode, in pairs")),

        // ---- text and fonts
        new("^A", "The font id is written right after ^A, for example ^A0N,30,30 or ^A@N,30,30,E:ARIAL.TTF.", P("f", "font id: 0 is the scalable font, A to Z and 1 to 9 are bitmap fonts, @ names a stored font file in the last parameter"), P("o", "rotation N, R, I or B"), P("h", "character height in dots"), P("w", "character width in dots; 0 keeps the proportion")),
        new("^CF", null, P("f", "default font id for fields without ^A"), P("h", "default character height in dots"), P("w", "default character width in dots")),
        new("^CI", null, P("a", "character set: 28 is UTF-8, 13 is code page 850, 27 is Windows-1252, 0 is the printer's default set"), P("s1,d1", "optional pairs that remap a source code to a destination code")),
        new("^CW", null, P("a", "the font id (letter or digit) to assign"), P("d:o.x", "the stored font file, for example E:ARIAL.TTF")),

        // ---- graphics
        new("^GB", null, P("w", "width of the box in dots"), P("h", "height of the box in dots"), P("t", "border thickness in dots; a box as thick as its width or height is a solid bar"), Colour, P("r", "corner rounding 0 (square) to 8")),
        new("^GC", null, P("d", "diameter of the circle in dots"), P("t", "border thickness in dots"), Colour),
        new("^GD", null, P("w", "width of the box the line crosses, in dots"), P("h", "height of that box in dots"), P("t", "line thickness in dots"), Colour, P("o", "lean: R or / rises to the right, L or \\ falls to the right")),
        new("^GE", null, P("w", "width of the ellipse in dots"), P("h", "height of the ellipse in dots"), P("t", "border thickness in dots"), Colour),
        new("^GS", "The ^FD data holds one letter: A registered, B copyright, C trade mark, D UL, E CSA.", Rotation, P("h", "height of the symbol in dots"), P("w", "width of the symbol in dots")),
        new("^GF", null, P("a", "how the data is written: A hexadecimal text, B binary, C compressed binary"), P("b", "total number of bytes"), P("c", "number of bytes of graphic data (normally the same as b)"), P("d", "bytes per row"), P("data", "the pixels, one bit each, 1 is black")),

        // ---- barcodes
        new("^BY", null, P("w", "module (narrow bar) width in dots, 1 to 10"), P("r", "wide-to-narrow bar ratio, 2.0 to 3.0"), P("h", "default bar height in dots for the barcodes that follow")),
        new("^B0", null, Rotation, P("b", "magnification 1 to 10"), P("c", "Y when the data carries extended channel interpretation codes"), P("d", "error control and size: 0 default, 1 to 99 minimum correction percent, 101 to 104 compact symbol with that many layers, 201 to 232 full symbol with that many layers, 300 rune"), P("e", "Y makes a menu (reader initialisation) symbol"), P("f", "number of symbols in a structured-append set, 1 to 26"), P("g", "optional id of the structured-append set")),
        new("^BO", null, Rotation, P("b", "magnification 1 to 10"), P("c", "Y when the data carries extended channel interpretation codes"), P("d", "error control and size: 0 default, 1 to 99 minimum correction percent, 101 to 104 compact symbol with that many layers, 201 to 232 full symbol with that many layers, 300 rune"), P("e", "Y makes a menu (reader initialisation) symbol"), P("f", "number of symbols in a structured-append set, 1 to 26"), P("g", "optional id of the structured-append set")),
        new("^B1", null, Rotation, P("e", "Y appends one check digit, N appends two"), BarHeight, Interpretation, InterpretationAbove),
        new("^B2", null, Rotation, BarHeight, Interpretation, InterpretationAbove, P("e", "Y appends a modulo 10 check digit")),
        new("^B3", null, Rotation, P("e", "Y appends a modulo 43 check digit"), BarHeight, Interpretation, InterpretationAbove),
        new("^B4", null, Rotation, BarHeight, Interpretation, P("m", "starting mode 0 to 5, or A to let the printer choose")),
        new("^B5", null, Rotation, BarHeight, Interpretation, InterpretationAbove),
        new("^B7", null, Rotation, P("h", "height of one row in dots"), P("s", "security (error correction) level 1 to 8"), P("c", "number of data columns, 1 to 30; 0 lets the printer choose"), P("r", "number of rows, 3 to 90; 0 lets the printer choose"), P("t", "Y prints the truncated form without the right-hand row indicators")),
        new("^B8", null, Rotation, BarHeight, Interpretation, InterpretationAbove),
        new("^B9", null, Rotation, BarHeight, Interpretation, InterpretationAbove, P("e", "Y prints the check digit in the human-readable text")),
        new("^BA", null, Rotation, BarHeight, Interpretation, InterpretationAbove, P("e", "Y prints the check digit in the human-readable text")),
        new("^BB", null, Rotation, BarHeight, P("s", "Y generates the symbol check characters"), P("c", "number of characters per row"), P("r", "number of rows"), P("m", "variant: A, E or F")),
        new("^BC", null, Rotation, BarHeight, Interpretation, InterpretationAbove, P("e", "Y adds the UCC check digit to the human-readable text"), P("m", "mode: N none, U UCC case, A automatic subset switching, D GS1-128 with an FNC1 start")),
        new("^BD", null, P("m", "mode 2 to 6: 2 and 3 are structured carrier messages, 4 to 6 plain data"), P("n", "number of this symbol in a structured-append set, 1 to 8"), P("t", "total number of symbols in the set, 1 to 8")),
        new("^BE", null, Rotation, BarHeight, Interpretation, InterpretationAbove),
        new("^BF", null, Rotation, P("h", "height of one row in dots"), P("m", "mode 0 to 33, which fixes columns, rows and error correction")),
        new("^BI", null, Rotation, BarHeight, Interpretation, InterpretationAbove),
        new("^BJ", null, Rotation, BarHeight, Interpretation, InterpretationAbove),
        new("^BK", null, Rotation, P("e", "check digit switch (the printer accepts only N)"), BarHeight, Interpretation, InterpretationAbove, P("k", "start character A, B, C or D"), P("l", "stop character A, B, C or D")),
        new("^BL", null, Rotation, BarHeight, InterpretationAbove),
        new("^BM", null, Rotation, P("e", "check digits: A none, B one modulo 10, C two modulo 10, D modulo 10 then modulo 11"), BarHeight, Interpretation, InterpretationAbove, P("e2", "Y prints the check digit in the human-readable text")),
        new("^BP", null, Rotation, P("e", "Y prints the check digit"), BarHeight, Interpretation, InterpretationAbove),
        new("^BQ", "The ^FD data starts with a header such as QA, (error level, input mode) before the content.", Rotation, P("m", "model 1 or 2"), P("s", "magnification 1 to 10"), P("e", "error correction: H highest, Q, M, L lowest"), P("k", "mask pattern 0 to 7")),
        new("^BR", null, Rotation, P("t", "DataBar variant 1 to 12 (1 omnidirectional, 7 expanded, 11 stacked omnidirectional ...)"), P("m", "magnification 1 to 10"), P("h", "height of the separator between stacked rows, 1 or 2 modules"), P("w", "bar height in dots"), P("s", "segments per row for the expanded variant, 2 to 22 in steps of two")),
        new("^BS", null, Rotation, BarHeight, Interpretation, InterpretationAbove),
        new("^BT", null, Rotation, P("w1", "narrow bar width of the Code 39 part in dots"), P("r1", "wide-to-narrow ratio of the Code 39 part"), P("h1", "height of the Code 39 part in dots"), P("w2", "narrow bar width of the MicroPDF417 part in dots"), P("h2", "row height of the MicroPDF417 part in dots")),
        new("^BU", null, Rotation, BarHeight, Interpretation, InterpretationAbove, P("e", "Y prints the check digit in the human-readable text")),
        new("^BX", null, Rotation, P("h", "size of one module in dots"), P("s", "quality level: 0, 50, 80, 100, 140 or 200 (200 is the ECC 200 standard)"), P("c", "number of columns; 0 lets the printer choose"), P("r", "number of rows; 0 lets the printer choose"), P("f", "format id of the data, 1 to 6 (only for quality levels below 200)"), P("g", "escape character used in the data, for example for FNC1 in GS1 symbols"), P("a", "shape: 1 square, 2 rectangular")),
        new("^BZ", null, Rotation, BarHeight, Interpretation, InterpretationAbove, P("t", "type: 0 Postnet, 1 Plant Bar Code, 2 reserved, 3 USPS Intelligent Mail")),

        // ---- images and stored objects
        new("~DG", null, Device, P("t", "total number of bytes"), P("w", "bytes per row"), P("data", "the pixels as hexadecimal text, 1 is black")),
        new("~DY", null, P("d:f", "drive letter and name of the object, without extension"), P("b", "how the data is written: A hexadecimal or ASCII, B binary, C compressed binary"), P("x", "kind of object: G bitmap graphic, P PNG picture, T TrueType font, X font"), P("t", "total number of bytes"), P("w", "bytes per row for a bitmap"), P("data", "the object itself")),
        new("^XG", null, Device, P("mx", "horizontal magnification 1 to 10"), P("my", "vertical magnification 1 to 10")),
        new("^IL", null, Device),
        new("^IM", null, Device),
        new("^IS", null, Device, P("p", "Y prints the label while saving it")),
        new("^ID", null, Device),
        new("^DF", "Written right after ^XA; the commands that follow are stored instead of printed.", Device),
        new("^XF", "The ^FN fields that follow it supply the data of the stored format.", Device),

        // ---- media and printer settings
        new("^MD", null, P("d", "darkness change relative to the current setting, -30 to 30")),
        new("~SD", null, P("d", "absolute darkness 0 to 30")),
        new("^MM", null, P("a", "what happens after each label: T tear-off, P peel-off, R rewind, A applicator, C cutter, D delayed cutter, F RFID, K kiosk"), P("b", "Y starts peeling before the whole label is printed")),
        new("^MN", null, P("a", "how labels are told apart: N continuous, Y or W gap or notch, M black mark, A automatic"), P("b", "offset of the black mark in dots")),
        new("^MT", null, P("a", "T thermal transfer (ribbon), D direct thermal")),
        new("^PR", null, P("p", "print speed in inches per second, 1 to 14 (or the letters A to E)"), P("s", "slew speed over blank areas"), P("b", "backfeed speed")),
        new("^SO", null, P("a", "which offset clock is set: 2 secondary, 3 third"), P("b", "months to add"), P("c", "days to add"), P("d", "years to add"), P("e", "hours to add"), P("f", "minutes to add"), P("g", "seconds to add")),
        new("^JM", null, P("a", "dot density: A normal, B half (every value is doubled)")),
        new("^MF", null, P("p", "what the printer does with the media when switched on: F feed, C calibrate, L length, N nothing"), P("r", "the same when the head is closed")),
    };

    private static readonly Dictionary<string, ZplCommandSignature> ById = Table.ToDictionary(s => s.Id, StringComparer.Ordinal);

    /// <summary>Every catalogued signature, in display order.</summary>
    public static IReadOnlyList<ZplCommandSignature> All => Table;

    /// <summary>The signature of a command by id (<c>^FO</c>, <c>~DG</c>), or null when it is not in the table. The lookup ignores case.</summary>
    public static ZplCommandSignature? FindSignature(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string key = id.Trim().ToUpperInvariant();
        if (ById.TryGetValue(key, out var found)) return found;
        // A command listed with both prefixes is stored once under its primary prefix; the other prefix means the same thing.
        if (key.Length >= 2 && key[0] is '^' or '~')
        {
            string other = (key[0] == '^' ? '~' : '^') + key[1..];
            if (ById.TryGetValue(other, out found) && ZplCommandCatalog.Find(key) is { } info && info.Prefixes.Length == 2) return found;
        }
        return null;
    }

    /// <summary>
    /// Describes a command for the hint line: its name and summary from <see cref="ZplCommandCatalog"/> and its parameters from this table.
    /// Returns null only when neither source knows the command.
    /// </summary>
    /// <param name="idOrCode">The command with its prefix, for example <c>^FO</c>; letters may be lower case.</param>
    public static ZplCommandHint? Describe(string idOrCode)
    {
        if (string.IsNullOrWhiteSpace(idOrCode)) return null;
        string id = idOrCode.Trim().ToUpperInvariant();
        var info = ZplCommandCatalog.Find(id);
        var signature = FindSignature(id);
        if (info is null && signature is null) return null;
        return new ZplCommandHint(id, info?.Name ?? id, info?.Summary ?? string.Empty,
            signature?.Parameters ?? Array.Empty<ZplParameter>(), signature?.Note, info?.Status);
    }

    /// <summary>
    /// Finds the command the caret is on and describes it. A caret is "on" a command from its prefix character up to the start of
    /// the next command, so the hint stays while the parameters are typed. Returns null when the caret is on plain text before the
    /// first command, outside the text, or on a command neither catalogue knows.
    /// </summary>
    /// <param name="text">The ZPL source.</param>
    /// <param name="caretIndex">Zero-based caret position; a value equal to the length means "at the end".</param>
    public static ZplCommandHint? AtCaret(string? text, int caretIndex) =>
        CommandTokenAtCaret(text, caretIndex) is { } token ? Describe(token) : null;

    /// <summary>
    /// The command token (prefix and code, upper case, for example <c>^FO</c>) the caret belongs to, whether or not any catalogue knows it;
    /// null when the caret is on plain text before the first command or outside the text. Lets an editor say "unknown command" for a typo.
    /// </summary>
    /// <param name="text">The ZPL source.</param>
    /// <param name="caretIndex">Zero-based caret position; a value equal to the length means "at the end".</param>
    public static string? CommandTokenAtCaret(string? text, int caretIndex)
    {
        if (string.IsNullOrEmpty(text) || caretIndex < 0 || caretIndex > text.Length) return null;
        // The caret sits between characters; the command it belongs to starts at the nearest prefix at or before it.
        int start = caretIndex < text.Length && text[caretIndex] is '^' or '~' ? caretIndex : text.LastIndexOfAny(new[] { '^', '~' }, Math.Max(0, caretIndex - 1));
        return start < 0 ? null : CommandAt(text, start);
    }

    /// <summary>The command token (prefix and code) that starts at <paramref name="start"/>, following the parser's own rule that <c>^A</c> is one letter.</summary>
    private static string CommandAt(string text, int start)
    {
        char prefix = text[start];
        int i = start + 1;
        if (i >= text.Length) return prefix.ToString();
        if (prefix == '^' && char.ToUpperInvariant(text[i]) == 'A') return "^A";
        int end = i;
        while (end < text.Length && end - i < 2 && text[end] is not ('^' or '~')) end++;
        return prefix + text[i..end].ToUpperInvariant();
    }
}
