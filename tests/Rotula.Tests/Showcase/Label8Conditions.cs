using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using ZXing;

namespace Rotula.Tests.Showcase;

internal static partial class ShowcaseLabels
{
    // ======================================================================================================
    // 8. Print conditions (4 x 3 in, 300 dpi): the DANGER panel prints only when the Hazard field has a
    //    value, and a "no special hazards" panel prints only when it is empty. Two pictures, one file.
    // ======================================================================================================

    private static ShowcaseLabel ConditionsLabel()
    {
        var b = new LabelBuilder(4, 3, 300, 8);
        int m = 48;
        var hazardous = Rule.WhenSet("Hazard");
        var harmless = Rule.WhenEmpty("Hazard");

        var withHazard = new Dictionary<string, string>
        {
            ["Product"] = "Isopropyl Alcohol 99.9%", ["Detail"] = "Laboratory grade  ·  CAS 67-63-0", ["Code"] = "OB-4410", ["Lot"] = "L2610A",
            ["Net"] = "1 L", ["Made"] = "02 SEP 2026", ["Expires"] = "SEP 2029",
            ["Hazard"] = "FLAMMABLE LIQUID AND VAPOUR",
        };
        var withoutHazard = new Dictionary<string, string>(withHazard)
        {
            ["Product"] = "Glycerine USP 99.7%", ["Detail"] = "Pharmaceutical grade  ·  CAS 56-81-5", ["Code"] = "OB-2208", ["Lot"] = "L2607C",
            ["Net"] = "500 mL", ["Made"] = "15 AUG 2026", ["Expires"] = "AUG 2031", ["Hazard"] = string.Empty,
        };
        foreach (var kv in withHazard) b.Field(kv.Key, kv.Value);

        // ---- header: a three-atom molecule (discs joined by diagonals) and the company name, the product code as a field
        b.Fill(m, m, b.W - 2 * m, 110, round: 2);
        b.Diagonal(m + 48, m + 40, 40, 30, 6, leanRight: false, white: true);
        b.Diagonal(m + 48, m + 64, 40, 30, 6, leanRight: true, white: true);
        b.Disc(m + 28, m + 36, 34, white: true);
        b.Disc(m + 80, m + 20, 28, white: true);
        b.Disc(m + 80, m + 70, 28, white: true);
        b.Text(m + 140, m + 24, "ORCHARD BRIDGE CHEMICALS", Industrial, 11, bold: true, reverse: true);
        b.Text(m + 142, m + 70, "FINE CHEMICALS  ·  SINCE 1987", Ui, 6, bold: true, reverse: true);
        b.Text(b.W - m - 24 - 260, m + 34, "{{Code}}", Industrial, 14, bold: true, width: 260, align: TextAlignment.Right, reverse: true);

        // ---- product, left column
        int lx = m, lw = 640;
        b.Text(lx, 196, "PRODUCT", Ui, 6.5, bold: true);
        var name = b.Text(lx, 236, "{{Product}}", Industrial, 21, bold: true, width: lw, maxLines: 1);
        name.Block!.Fit = new TextFitOptions { Mode = TextFitMode.Shrink, MinSize = 12, MaxSize = 21 };
        b.Text(lx, 322, "{{Detail}}", Ui, 9);
        b.HRule(lx, 376, lw, 4);

        string[,] cells = { { "LOT", "{{Lot}}" }, { "NET CONTENTS", "{{Net}}" }, { "MANUFACTURED", "{{Made}}" }, { "EXPIRES", "{{Expires}}" } };
        for (int i = 0; i < 4; i++)
        {
            int cx = lx + (i % 2) * (lw / 2), cy = 410 + (i / 2) * 98;
            b.Text(cx, cy, cells[i, 0], Ui, 6, bold: true);
            b.Text(cx, cy + 36, cells[i, 1], Ui, 11.5, bold: true);
        }

        // ---- identification codes, both built from fields
        var scans1 = new List<ExpectedScan>();
        var scans2 = new List<ExpectedScan>();
        var linear = b.BarcodeFit(lx, 640, BarcodeSymbology.Code128, "{{Code}}-{{Lot}}", 420, e => { e.Height = 120; e.HumanReadable = true; });
        var dm = b.Barcode(lx + lw - 190, 640, BarcodeSymbology.DataMatrix, "{{Code}}|{{Lot}}|{{Expires}}", e => e.ModuleSize = 9);
        foreach (var (scans, rec) in new[] { (scans1, withHazard), (scans2, withoutHazard) })
        {
            string linearText = rec["Code"] + "-" + rec["Lot"];
            string matrixText = rec["Code"] + "|" + rec["Lot"] + "|" + rec["Expires"];
            // BarcodeFit fixed the module for the sample record; the other record has the same code lengths, so the regions stay valid.
            scans.Add(LabelBuilder.Expect(linear, BarcodeFormat.CODE_128, linearText));
            scans.Add(LabelBuilder.Expect(dm, BarcodeFormat.DATA_MATRIX, matrixText));
        }

        // ---- right panel, version A: the hazard panel (prints when Hazard has a value)
        int px = 740, py = 196, pw = b.W - m - px, ph = 640;
        b.Frame(px, py, pw, ph, 9, round: 1, condition: hazardous);
        b.Fill(px, py, pw, 118, round: 1, condition: hazardous);
        b.Fill(px, py + 60, pw, 58, condition: hazardous); // squares the lower corners of the title band
        b.Text(px, py + 34, "DANGER", Industrial, 19, bold: true, width: pw, align: TextAlignment.Center, reverse: true, condition: hazardous);
        // Warning diamond: four diagonals around the centre, an exclamation mark inside.
        int dcx = px + pw / 2, dcy = py + 238, r = 84;
        b.Diagonal(dcx - r, dcy - r, r, r, 12, leanRight: true, condition: hazardous);    // left corner up to the top
        b.Diagonal(dcx, dcy - r, r, r, 12, leanRight: false, condition: hazardous);       // top down to the right corner
        b.Diagonal(dcx - r, dcy, r, r, 12, leanRight: false, condition: hazardous);       // left corner down to the bottom
        b.Diagonal(dcx, dcy, r, r, 12, leanRight: true, condition: hazardous);            // right corner down to the bottom
        b.Fill(dcx - 8, dcy - 46, 16, 54, round: 8, condition: hazardous);
        b.Disc(dcx - 10, dcy + 22, 20, condition: hazardous);
        b.Text(px + 24, py + 352, "{{Hazard}}", Ui, 8.5, bold: true, width: pw - 48, maxLines: 3, lineSpacing: 2, condition: hazardous);
        b.Text(px + 24, py + 500, "Causes serious eye irritation. Keep away from heat and flames.", Ui, 7, width: pw - 48, maxLines: 3, lineSpacing: 2, condition: hazardous);

        // ---- right panel, version B: the all-clear panel (prints when Hazard is empty)
        b.Frame(px, py, pw, ph, 4, round: 1, condition: harmless);
        b.Circle(dcx - 90, py + 130, 180, 9, condition: harmless);
        b.Diagonal(dcx - 48, py + 218, 30, 30, 14, leanRight: false, condition: harmless);
        b.Diagonal(dcx - 18, py + 158, 70, 90, 14, leanRight: true, condition: harmless);
        b.Text(px, py + 366, "NO SPECIAL HAZARDS", Industrial, 13, bold: true, width: pw, maxLines: 2, align: TextAlignment.Center, lineSpacing: 2, condition: harmless);
        b.Text(px + 24, py + 500, "Store below 30 °C in a closed container, away from direct sunlight.", Ui, 8, width: pw - 48, maxLines: 3, lineSpacing: 4, align: TextAlignment.Center, condition: harmless);

        return new ShowcaseLabel("conditions-hazard-label", b.Doc, new[]
        {
            new ShowcaseVariant("-with-hazard", withHazard, scans1, "Hazard field filled in: the DANGER panel prints"),
            new ShowcaseVariant("-no-hazard", withoutHazard, scans2, "Hazard field empty: the all-clear panel prints instead"),
        }, SheetLayout.Column);
    }
}
