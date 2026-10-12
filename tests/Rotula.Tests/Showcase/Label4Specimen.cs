using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests.Showcase;

internal static partial class ShowcaseLabels
{
    // ======================================================================================================
    // 4. Laboratory specimen label (3 x 2 in, 600 dpi): dense, reverse header, Data Matrix and Code 128
    // ======================================================================================================

    private static ShowcaseLabel SpecimenLabel()
    {
        // 600 dpi: the label is small and full of detail, so the finer dot pitch is what keeps the 2D symbol and the micro text crisp.
        var b = new LabelBuilder(3, 2, 600, 4);
        int m = 70, inner = b.W - 2 * m;
        string accession = "ACC-26-0094817";
        string matrix = "ACC-26-0094817|MRN 0048-77213|CBC+BMP|EDTA";

        // ---- reverse header: a test tube drawn from a rounded frame and a rounded fill, the title, and a white STAT tab
        b.Fill(m, m, inner, 190, round: 2);
        b.Frame(m + 44, m + 30, 44, 130, 8, round: 8, white: true);
        b.Fill(m + 52, m + 100, 28, 52, round: 8, white: true);
        b.Text(m + 130, m + 36, "SPECIMEN LABEL", Industrial, 12, bold: true, reverse: true);
        b.Text(m + 132, m + 118, "BLOOD  ·  EDTA  ·  LAVENDER CAP", Ui, 6.5, bold: true, reverse: true);
        int tabW = 330, tabX = m + inner - tabW - 28;
        b.Fill(tabX, m + 28, tabW, 134, round: 3, white: true);
        b.TextCentered(tabX + tabW / 2, m + 62, "STAT", Industrial, 14, bold: true);

        // ---- patient, left; Data Matrix, right, both starting on the same line
        int py = m + 190 + 55;
        b.Text(m, py, "PATIENT", Ui, 5.5, bold: true);
        b.Text(m, py + 46, "ABERNATHY, Leona M.", Industrial, 15, bold: true);
        b.Text(m, py + 168, "DOB 14 MAR 1978   ·   F   ·   48 y", Ui, 8, bold: true);
        b.Text(m, py + 232, "MRN 0048-77213   ·   ENC 7710-3302", Ui, 8);
        var dm = b.Barcode(0, py, BarcodeSymbology.DataMatrix, matrix, e => e.ModuleSize = 11);
        var dmSize = LabelBuilder.BarsSize(dm);
        dm.X = m + inner - dmSize.Width;

        // ---- facts grid: a rounded frame split by hairlines into 3 x 2 cells
        int gy = py + 310, gh = 260;
        b.Frame(m, gy, inner, gh, 5, round: 2);
        int cw = inner / 3;
        b.VRule(m + cw, gy, gh, 3);
        b.VRule(m + 2 * cw, gy, gh, 3);
        b.HRule(m, gy + gh / 2 - 1, inner, 3);
        string[,] cells =
        {
            { "COLLECTED", "12 OCT  08:42" }, { "COLLECTOR", "RN J. MORALES" }, { "WARD / BED", "4 WEST  ·  12" },
            { "TEST PANEL", "CBC + BMP" }, { "FASTING", "YES  ·  12 h" }, { "ORDERED BY", "DR. P. OKAFOR" },
        };
        for (int i = 0; i < 6; i++)
        {
            int cx = m + (i % 3) * cw + 34, cy = gy + (i / 3) * (gh / 2) + 20;
            b.Text(cx, cy, cells[i, 0], Ui, 5, bold: true);
            b.Text(cx, cy + 40, cells[i, 1], Ui, 7, bold: true);
        }

        // ---- accession as Code 128 across the foot, with the print time in micro type to its right
        int by = gy + gh + 40;
        int barW = inner - 330;
        var bc = b.BarcodeFit(m, by, BarcodeSymbology.Code128, accession, barW, e => { e.Height = b.H - m - by - 54; e.HumanReadable = true; }, center: false);
        b.TextRight(m + inner, by + 6, "LAB 0F-22", Industrial, 7.5, bold: true);
        b.TextRight(m + inner, by + 66, "PRINTED", Ui, 5, bold: true);
        b.TextRight(m + inner, by + 104, "12 OCT 2026  08:44", Ui, 5.5);

        return new ShowcaseLabel("lab-specimen-label", b.Doc, new[] { Single() });
    }
}
