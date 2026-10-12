using Rotula.Core.Elements;
using Rotula.Core.Gs1;
using Rotula.Core.Model;
using ZXing;

namespace Rotula.Tests.Showcase;

internal static partial class ShowcaseLabels
{
    // ======================================================================================================
    // 1. GS1-128 logistics label (4 x 6 in, 300 dpi)
    // ======================================================================================================

    private static ShowcaseLabel PalletLabel()
    {
        var b = new LabelBuilder(4, 6, 300, 1);
        int m = 60, inner = b.W - 2 * m;

        // The three GS1-128 element strings. The SSCC comes from Rotula's own key builder, so its check digit is right.
        string sscc = Gs1Keys.Sscc(3, "0614141", "000417258");
        string content = "(02)09506000134352(37)48";
        string dates = "(15)271031(10)KV24A117";
        string ssccData = $"(00){sscc}";

        // ---- header band with a logo mark drawn from shapes (ring + centre dot)
        b.Fill(m, m, inner, 140, round: 2);
        b.Circle(m + 32, m + 28, 84, 10, white: true);
        b.Disc(m + 32 + 26, m + 28 + 26, 32, white: true);
        b.Text(m + 150, m + 30, "KESTREL VALLEY", Industrial, 17, bold: true, reverse: true);
        b.Text(m + 150, m + 96, "FOODS  ·  DISTRIBUTION", Ui, 7.5, bold: true, reverse: true);
        b.TextRight(m + inner - 32, m + 30, "PALLET", Industrial, 17, bold: true, reverse: true);
        b.TextRight(m + inner - 32, m + 96, "LOGISTICS LABEL", Ui, 7.5, bold: true, reverse: true);

        // ---- ship from / ship to, two columns split by a hairline
        int top = 250, colGap = 40, half = (inner - colGap) / 2;
        b.Text(m, top, "SHIP FROM", Ui, 6.5, bold: true);
        b.Text(m, top + 44, "Kestrel Valley Foods", Ui, 11, bold: true);
        b.Text(m, top + 104, "Distribution Centre 4\n18 Harrow Quay\nTilbury, Essex RM18 7AA", Ui, 9, width: half, maxLines: 3, lineSpacing: 4);
        int col2 = m + half + colGap;
        b.VRule(m + half + colGap / 2 - 1, top, 250, 3);
        b.Text(col2, top, "SHIP TO", Ui, 6.5, bold: true);
        b.Text(col2, top + 44, "Marlow & Finch Grocers", Ui, 11, bold: true);
        b.Text(col2, top + 104, "Goods-in Bay 7\n201 Canal Street\nManchester M1 5QA", Ui, 9, width: half, maxLines: 3, lineSpacing: 4);
        b.HRule(m, top + 270, inner, 3);

        // ---- product description
        int prod = top + 310;
        b.Text(m, prod, "CONTENT", Ui, 6.5, bold: true);
        b.Text(m, prod + 44, "ORGANIC OAT FLAKES", Industrial, 22, bold: true);
        b.Text(m, prod + 128, "12 × 750 g retail cartons  ·  ambient storage", Ui, 10);

        // ---- facts grid: four cells sharing one top edge and one gutter, each as wide as its text needs plus an equal share of the rest
        int gy = prod + 190, gh = 130, gutter = 12;
        string[,] cells =
        {
            { "BATCH / LOT", "KV24A117" },
            { "BEST BEFORE", "31 OCT 2027" },
            { "CASES", "48" },
            { "GROSS WEIGHT", "612.4 kg" },
        };
        var needs = new int[4];
        for (int i = 0; i < 4; i++)
            needs[i] = Math.Max(b.TextWidth(cells[i, 0], Ui, 6.5, true), b.TextWidth(cells[i, 1], Industrial, 13.5, true)) + 44;
        var widths = LabelBuilder.Distribute(inner, gutter, needs);
        int cx = m;
        for (int i = 0; i < 4; i++)
        {
            b.Frame(cx, gy, widths[i], gh, 4, round: 2);
            b.Text(cx + 24, gy + 20, cells[i, 0], Ui, 6.5, bold: true);
            b.Text(cx + 24, gy + 62, cells[i, 1], Industrial, 13.5, bold: true);
            cx += widths[i] + gutter;
        }

        // ---- barcodes, widest module that fits the column, each under a small heading; the SSCC closes the label as the GS1 layout asks
        int y = gy + gh + 48;
        var scans = new List<ExpectedScan>();
        void Gs1(string title, string data, int height)
        {
            b.Text(m, y, title, Ui, 6.5, bold: true);
            var bar = b.BarcodeFit(m, y + 38, BarcodeSymbology.Code128, data, inner, e => { e.Code128Mode = Code128Mode.Ean; e.Height = height; e.HumanReadable = true; }, center: true);
            scans.Add(LabelBuilder.Expect(bar, BarcodeFormat.CODE_128));
            y += 38 + height + 80;
        }
        Gs1("PRODUCT (GTIN-14) AND COUNT", content, 120);
        Gs1("BEST BEFORE AND BATCH", dates, 120);
        Gs1("SSCC – SERIAL SHIPPING CONTAINER CODE", ssccData, b.H - m - y - 38 - 62 + 18);

        return new ShowcaseLabel("gs1-pallet-label", b.Doc, new[]
        {
            Single(scans.ToArray()),
        });
    }
}
