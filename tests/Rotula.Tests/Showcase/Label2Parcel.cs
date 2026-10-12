using Rotula.Core.Elements;
using Rotula.Core.Model;
using ZXing;

namespace Rotula.Tests.Showcase;

internal static partial class ShowcaseLabels
{
    // ======================================================================================================
    // 2. Parcel shipping label (4 x 6 in, 300 dpi): PDF417 + Data Matrix, POSTNET routing bars, tracking Code 128
    // ======================================================================================================

    private static ShowcaseLabel ParcelLabel()
    {
        var b = new LabelBuilder(4, 6, 300, 2);
        int m = 60, inner = b.W - 2 * m;
        string tracking = "7Q4882015366";
        string zip = "981045517";
        string carrierData = "ARCLINE|PRI2|98104-5517|US|7Q4882015366|1/2|4.2KG|20261012";
        string dm = "ARCLINE-" + tracking;
        var scans = new List<ExpectedScan>();

        // ---- header: the logo is two stacked chevrons (each one a "\" over a "/"), the service sits in a white tab
        b.Fill(m, m, inner, 140, round: 2);
        for (int i = 0; i < 2; i++)
        {
            int cx = m + 34 + i * 44;
            b.Diagonal(cx, m + 32, 38, 38, 13, leanRight: false, white: true);
            b.Diagonal(cx, m + 70, 38, 38, 13, leanRight: true, white: true);
        }
        b.Text(m + 150, m + 30, "ARCLINE", Industrial, 19, bold: true, reverse: true);
        b.Text(m + 150, m + 100, "EXPRESS PARCEL", Ui, 7.5, bold: true, reverse: true);
        int tabW = 330, tabX = m + inner - tabW - 24;
        b.Fill(tabX, m + 22, tabW, 96, round: 3, white: true);
        b.TextCentered(tabX + tabW / 2, m + 38, "PRIORITY", Industrial, 14, bold: true);
        b.TextCentered(tabX + tabW / 2, m + 86, "2-DAY DELIVERY", Ui, 6.5, bold: true);

        // ---- sender and shipment facts
        int ay = 240;
        b.Text(m, ay, "FROM", Ui, 6.5, bold: true);
        b.Text(m, ay + 40, "Cobalt & Cedar Supply Co.", Ui, 10.5, bold: true);
        b.Text(m, ay + 96, "4410 Foundry Way\nPortland, OR 97209", Ui, 9, width: 600, maxLines: 2, lineSpacing: 2);
        int fx = m + 640, fw = inner - 640;
        b.Frame(fx, ay - 10, fw, 190, 4, round: 2);
        string[,] facts = { { "WEIGHT", "4.2 kg" }, { "PIECE", "1 of 2" }, { "SHIP DATE", "12 OCT 2026" } };
        for (int i = 0; i < 3; i++)
        {
            int ry = ay + 8 + i * 54;
            b.Text(fx + 20, ry + 8, facts[i, 0], Ui, 6.5, bold: true);
            b.TextRight(fx + fw - 20, ry, facts[i, 1], Industrial, 10.5, bold: true);
            if (i < 2) b.HRule(fx + 20, ry + 46, fw - 40, 2);
        }
        b.HRule(m, ay + 215, inner, 8);

        // ---- destination, with the route in a solid block
        int dy = ay + 262, blockW = 240;
        b.Text(m, dy, "SHIP TO", Ui, 6.5, bold: true);
        b.Text(m, dy + 40, "Harlow Outfitters", Ui, 15, bold: true);
        b.Text(m, dy + 112, "Attn: Receiving Dock\n2180 Pine Ridge Road, Suite 400", Ui, 10.5, width: inner - blockW - 20, maxLines: 2, lineSpacing: 4);
        b.Text(m, dy + 226, "BELLEVUE WA 98004-5517", Industrial, 13, bold: true);
        b.Fill(m + inner - blockW, dy, blockW, 250, round: 2);
        b.TextCentered(m + inner - blockW / 2, dy + 24, "ROUTE", Ui, 6.5, bold: true, reverse: true);
        b.TextCentered(m + inner - blockW / 2, dy + 66, "4B", "Impact", 34, reverse: true);
        b.TextCentered(m + inner - blockW / 2, dy + 196, "SEA-117", Industrial, 8.5, bold: true, reverse: true);

        // ---- carrier data: PDF417 on the left, Data Matrix right-aligned, sharing one top edge
        int cy = dy + 316;
        var pdf = b.Barcode(m, cy, BarcodeSymbology.Pdf417, carrierData, e => { e.ModuleWidth = 5; e.Height = 24; e.Columns = 5; e.Pdf417SecurityLevel = 3; });
        scans.Add(LabelBuilder.Expect(pdf, BarcodeFormat.PDF_417));
        var mx = b.Barcode(0, cy, BarcodeSymbology.DataMatrix, dm, e => e.ModuleSize = 10);
        var mxSize = LabelBuilder.BarsSize(mx);
        mx.X = m + inner - mxSize.Width;
        scans.Add(LabelBuilder.Expect(mx, BarcodeFormat.DATA_MATRIX));
        int rowEnd = cy + Math.Max(LabelBuilder.BarsSize(pdf).Height, mxSize.Height);

        // ---- routing bars (POSTNET of the ZIP+4) above the tracking number
        int ty = rowEnd + 50;
        b.Text(m, ty, "ROUTING BARS", Ui, 6.5, bold: true);
        b.Barcode(m, ty + 36, BarcodeSymbology.Postnet, zip, e => { e.ModuleWidth = 5; e.Height = 50; e.HumanReadable = false; });
        b.HRule(m, ty + 130, inner, 3);
        b.Text(m, ty + 160, "TRACKING NO.", Ui, 6.5, bold: true);
        b.TextRight(m + inner, ty + 156, "7Q4 8820 1536 6", Industrial, 10.5, bold: true);
        var trk = b.BarcodeFit(m, ty + 210, BarcodeSymbology.Code128, tracking, inner, e => { e.Height = b.H - m - 110 - (ty + 210) - 62; e.HumanReadable = true; }, center: true);
        scans.Add(LabelBuilder.Expect(trk, BarcodeFormat.CODE_128));

        // ---- footer: shipper reference, value and handling in one quiet line under a rule
        int fy = b.H - m - 24;
        b.HRule(m, fy - 36, inner, 3);
        b.Text(m, fy, "REF ORD-2026-118842", Ui, 6.5, bold: true);
        b.TextCentered(m + inner / 2, fy, "DECLARED 148.00 USD", Ui, 6.5, bold: true);
        b.TextRight(m + inner, fy, "NO SIGNATURE REQUIRED", Ui, 6.5, bold: true);

        return new ShowcaseLabel("parcel-shipping-label", b.Doc, new[] { Single(scans.ToArray()) });
    }
}
