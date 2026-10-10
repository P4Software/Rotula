using System.Globalization;
using System.Text;

namespace Rotula.Core.Zpl.Catalog;

/// <summary>
/// Writes the ZPL coverage page (<c>docs/zpl-coverage.md</c>) from <see cref="ZplCommandCatalog"/>. The page is generated, never
/// edited by hand: <c>tools/gen-zpl-coverage</c> writes it and a test fails when the committed file differs from this output.
/// </summary>
public static class ZplCoverageReport
{
    /// <summary>Where the generated page lives, relative to the repository root.</summary>
    public const string RelativePath = "docs/zpl-coverage.md";

    /// <summary>Builds the whole page. Lines end with LF so the result is the same on every machine.</summary>
    public static string Render() => Render(ZplCommandCatalog.All, ZplCommandCatalog.Groups);

    /// <summary>Builds the page for the given commands and groups (the groups give the order).</summary>
    public static string Render(IReadOnlyList<ZplCommandInfo> commands, IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(groups);
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line("# ZPL coverage");
        Line();
        Line("> This page is generated from `src/Rotula.Core/Zpl/Catalog/commands.json`. Do not edit it by hand: change the catalogue and run");
        Line("> `dotnet run --project tools/gen-zpl-coverage` from the repository root. A test fails when this page is out of date, and other");
        Line("> tests check every status against the real parser and generator.");
        Line();
        Line("Rotula reads and writes ZPL II. This page lists every ZPL II command and says exactly what Rotula does with it. Output is");
        Line("deterministic (the same label and data always give byte-identical ZPL) and pure ASCII: text outside plain ASCII is written with");
        Line("`^FH` hex escapes, and `^CI28` (UTF-8) is written by default so accents and symbols such as the Spanish inverted marks print correctly.");
        Line();

        // ---- counts
        Line("## At a glance");
        Line();
        var counts = Enum.GetValues<ZplCommandStatus>().ToDictionary(s => s, s => commands.Count(c => c.Status == s));
        Line("| Status | Commands |");
        Line("|---|---:|");
        foreach (var status in Enum.GetValues<ZplCommandStatus>())
            Line($"| {StatusLabel(status)} | {counts[status].ToString(CultureInfo.InvariantCulture)} |");
        Line($"| **All** | **{commands.Count.ToString(CultureInfo.InvariantCulture)}** |");
        Line();
        Line("| Group | Supported | Passthrough | Planned | Out of scope | All |");
        Line("|---|---:|---:|---:|---:|---:|");
        foreach (var group in groups)
        {
            var inGroup = commands.Where(c => c.Group == group).ToList();
            if (inGroup.Count == 0) continue;
            Line($"| {group} | {N(inGroup, ZplCommandStatus.Supported)} | {N(inGroup, ZplCommandStatus.Passthrough)} | {N(inGroup, ZplCommandStatus.Planned)} | {N(inGroup, ZplCommandStatus.OutOfScope)} | {inGroup.Count.ToString(CultureInfo.InvariantCulture)} |");
        }
        Line();

        // ---- legend
        Line("## How to read this page");
        Line();
        Line("Each group below has one table. A command written `^XX / ~XX` exists with both prefixes and behaves the same way.");
        Line();
        Line("| Status | Meaning |");
        Line("|---|---|");
        Line("| **Supported** | Rotula understands the command. It is read into the label model when ZPL is opened and written back when the label is generated. |");
        Line("| **Passthrough** | The command can appear in a label file and affects printing, but Rotula does not model it. It is kept word for word and listed as a notice. |");
        Line("| **Planned** | Not modelled yet, but meant to be. Until then it behaves like Passthrough. |");
        Line("| **Out of scope** | A label designer will not model it (printer administration, queries, network, RFID, kiosk hardware). The table says why. It still behaves like Passthrough if a file contains it. |");
        Line();
        Line("| Column | Meaning |");
        Line("|---|---|");
        Line("| Generate | Yes when Rotula writes the command under its own name when it creates ZPL. |");
        Line("| Read | Yes when Rotula interprets the command when it opens ZPL. |");
        Line("| Preview | **Exact**: the on-screen preview draws what the printer prints. **Close**: drawn, but not identical. **Box**: a labelled box marks the place because only the printer can draw it. **No**: it changes the label but the preview ignores it. **n/a**: no visible effect. |");
        Line();

        // ---- what happens to unsupported commands
        Line("## What happens to commands Rotula does not support");
        Line();
        Line("Nothing is silently dropped.");
        Line();
        Line("- **Kept word for word.** A command Rotula does not interpret is stored in the label as raw ZPL, exactly as it was written, and is");
        Line("  written back in the same place when ZPL is generated. A field that uses an unsupported option is kept whole, because a");
        Line("  half-understood field would print differently from the original.");
        Line("- **Warned.** Each kept command is listed as a notice with its line and column, so you can see what Rotula did not interpret.");
        Line("  Harmless comments (`^FX`) are kept without a notice.");
        Line("- **Round trips.** Opening ZPL and generating it again keeps the geometry, text, barcodes and graphics Rotula supports, and keeps");
        Line("  every unsupported command in its original position. The one thing a ZPL file cannot carry is Rotula's own information, such as");
        Line("  `{{variable}}` placeholders: an imported label is plain text until you edit it.");
        Line("- **Stored objects.** Recalls of things kept in the printer (`^XG`, `^IM`, `^IL`, `^XF`) become placeholders and are written back");
        Line("  unchanged. When the same file downloads the graphic with `~DG` (or a plain bitmap `~DY`), the preview draws it.");
        Line("- **Printers differ.** Some commands, values and print modes exist only on some models or firmware versions. Rotula writes what the");
        Line("  ZPL guide defines; always print a test label before a production run.");
        Line();

        // ---- the tables
        Line("## Commands by group");
        foreach (var group in groups)
        {
            var inGroup = commands.Where(c => c.Group == group).ToList();
            if (inGroup.Count == 0) continue;
            Line();
            Line($"### {group}");
            Line();
            Line("| Command | Name | Status | Generate | Read | Preview | What it does |");
            Line("|---|---|---|:---:|:---:|:---:|---|");
            foreach (var c in inGroup)
            {
                string what = c.Summary;
                if (c.Status == ZplCommandStatus.OutOfScope && !string.IsNullOrWhiteSpace(c.Reason)) what = c.Summary + " *Out of scope: " + c.Reason + "*";
                else if (!string.IsNullOrWhiteSpace(c.Notes)) what = c.Summary + " " + c.Notes;
                Line($"| `{c.DisplayId}` | {Cell(c.Name)} | {StatusLabel(c.Status)} | {YesNo(c.Generator)} | {YesNo(c.Parser)} | {PreviewLabel(c.Preview)} | {Cell(what)} |");
            }
        }
        Line();

        // ---- barcode previews, derived from the table above
        Line("## Barcode previews");
        Line();
        Line("The designer draws barcodes module for module, so what you see scans. The accuracy of the printed ZPL does not depend on the");
        Line("preview: the commands and their parameters are generated and read for every symbology in the Barcodes table.");
        Line();
        Line("| Preview | Commands |");
        Line("|---|---|");
        var barcodes = commands.Where(c => c.Group == "Barcodes" && c.Code.StartsWith('B') && c.Parser).ToList();
        foreach (var preview in new[] { ZplCommandPreview.Exact, ZplCommandPreview.Approximate, ZplCommandPreview.Placeholder })
        {
            var names = barcodes.Where(c => c.Preview == preview).Select(c => $"{c.Name} (`{c.Id}`)").ToList();
            if (names.Count > 0) Line($"| {PreviewLabel(preview)} | {string.Join(", ", names)} |");
        }
        Line();

        // ---- fonts
        Line("## Fonts");
        Line();
        Line("ZPL's built-in bitmap fonts (`A` to `H`, `0`) live in the printer, not in Rotula. The preview draws text with a bundled stand-in");
        Line("typeface (Liberation Sans or Mono), so letter shapes and line breaks can differ a little from the printed label. The font width is");
        Line("honoured as a stretch factor in the preview, but the real glyph widths of the printer's bitmap fonts are not reproduced. Fonts");
        Line("assigned with `^CW` are kept and written back; the preview still uses the stand-in typeface for them.");
        Line();

        // ---- encodings
        Line("## Character sets");
        Line();
        Line("`^CI28` (UTF-8) is written by default. Opening a file that selects a national code page, or one that remaps characters, works but");
        Line("gives a notice because the mapping is approximated. Text is decoded with the `^CI` in force at that point in the file.");
        return sb.ToString();
    }

    private static string N(List<ZplCommandInfo> list, ZplCommandStatus s) => list.Count(c => c.Status == s).ToString(CultureInfo.InvariantCulture);

    private static string StatusLabel(ZplCommandStatus s) => s switch
    {
        ZplCommandStatus.Supported => "Supported",
        ZplCommandStatus.Passthrough => "Passthrough",
        ZplCommandStatus.Planned => "Planned",
        _ => "Out of scope",
    };

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string PreviewLabel(ZplCommandPreview p) => p switch
    {
        ZplCommandPreview.Exact => "Exact",
        ZplCommandPreview.Approximate => "Close",
        ZplCommandPreview.Placeholder => "Box",
        ZplCommandPreview.NotVisual => "n/a",
        _ => "No",
    };

    /// <summary>Makes text safe inside a markdown table cell.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");
}
