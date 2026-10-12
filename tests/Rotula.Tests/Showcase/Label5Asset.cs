using Rotula.Core.Elements;
using Rotula.Core.Model;
using ZXing;

namespace Rotula.Tests.Showcase;

internal static partial class ShowcaseLabels
{
    // ======================================================================================================
    // 5. Asset tag (3 x 2 in, 300 dpi): rounded frame, cloud-shaped mark from primitives, QR code and Code 128
    // ======================================================================================================

    private static ShowcaseLabel AssetLabel()
    {
        var b = new LabelBuilder(3, 2, 300, 5);
        string assetId = "NW-004172";
        string url = "https://assets.nimbusworks.example/a/NW-004172";
        var scans = new List<ExpectedScan>();

        // ---- rounded frame around everything, a heavier outline than the inner rules
        b.Frame(12, 12, b.W - 24, b.H - 24, 8, round: 1);

        // ---- header band: a cloud made of three discs and a rounded base, then the wordmark
        int hx = 40, hy = 40, hw = b.W - 80, hh = 100;
        b.Fill(hx, hy, hw, hh, round: 2);
        int cx = hx + 26, cy = hy + 22;
        b.Disc(cx, cy + 22, 34, white: true);
        b.Disc(cx + 20, cy + 4, 50, white: true);
        b.Disc(cx + 58, cy + 18, 38, white: true);
        b.Fill(cx + 8, cy + 38, 82, 22, round: 8, white: true);
        b.Text(hx + 150, hy + 16, "NIMBUS WORKS", Industrial, 16, bold: true, reverse: true);
        b.Text(hx + 152, hy + 74, "IT ASSET  ·  PROPERTY OF NIMBUS WORKS LTD", Ui, 5.5, bold: true, reverse: true);

        // ---- QR code on the left with its caption centred under it
        int qy = hy + hh + 28;
        var qr = b.Barcode(hx, qy, BarcodeSymbology.QrCode, url, e => { e.ModuleSize = 6; e.QrErrorCorrection = QrErrorCorrection.Medium; });
        var qrSize = LabelBuilder.BarsSize(qr);
        b.TextCentered(hx + qrSize.Width / 2, qy + qrSize.Height + 18, "SCAN TO REPORT", Ui, 5.5, bold: true);
        scans.Add(LabelBuilder.Expect(qr, BarcodeFormat.QR_CODE));

        // ---- identity, to the right of the QR code
        int rx = hx + qrSize.Width + 40, rw = hx + hw - rx - 40;
        b.Text(rx, qy, "ASSET ID", Ui, 5.5, bold: true);
        b.Text(rx, qy + 28, assetId, Industrial, 20, bold: true);
        b.HRule(rx, qy + 106, rw, 4);
        string[,] facts =
        {
            { "ITEM", "Latitude 5440 laptop" }, { "LOCATION", "HQ-3  ·  Desk 3.14" }, { "ASSIGNED", "Finance – R. Alvarez" },
        };
        for (int i = 0; i < 3; i++)
        {
            int ry = qy + 134 + i * 48;
            b.Text(rx, ry, facts[i, 0], Ui, 5, bold: true);
            b.Text(rx + 150, ry - 4, facts[i, 1], Ui, 7.5, bold: true);
        }

        // ---- Code 128 along the foot, a warranty note beside it
        int by = b.H - 40 - 118;
        var bc = b.BarcodeFit(hx, by, BarcodeSymbology.Code128, "NW004172", 360, e => { e.Height = 70; e.HumanReadable = true; });
        scans.Add(LabelBuilder.Expect(bc, BarcodeFormat.CODE_128));
        b.VRule(hx + 395, by + 4, 98, 3);
        b.Text(hx + 425, by + 4, "PURCHASED", Ui, 5, bold: true);
        b.Text(hx + 425, by + 34, "12 SEP 2025", Ui, 7, bold: true);
        b.Text(hx + 425, by + 74, "WARRANTY TO 09 / 2028", Ui, 5, bold: true);

        // ---- tamper strip along the right edge: text turned 90 degrees, its box set directly by its top-left corner
        b.Text(b.W - 66, qy, "VOID IF REMOVED", Ui, 5, bold: true, rotation: FieldRotation.Rotated90);

        return new ShowcaseLabel("asset-tag", b.Doc, new[] { Single(scans.ToArray()) });
    }
}
