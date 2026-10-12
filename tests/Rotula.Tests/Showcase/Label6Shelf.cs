using Rotula.Core.Elements;
using Rotula.Core.Gs1;
using Rotula.Core.Model;
using Rotula.Core.Text;

namespace Rotula.Tests.Showcase;

internal static partial class ShowcaseLabels
{
    // ======================================================================================================
    // 6. Retail shelf tag (3 x 2 in, 300 dpi) with data merge: three records, the SALE band and the old
    //    price only exist for the records that have a "WasPrice"
    // ======================================================================================================

    private static ShowcaseLabel ShelfLabel()
    {
        var b = new LabelBuilder(3, 2, 300, 6);
        int m = 28;
        var onSale = Rule.WhenSet("WasPrice");

        // Fields as the designer shows them: the sample values are the first record, so the file opens looking right.
        var records = new[]
        {
            Record("CASA VERDE", "Cold-Pressed Olive Oil", "Extra virgin  ·  500 ml bottle", "6.49", "4", "99", "1.50", "$0.99 per 100 ml", "PN-204817", "501234567890", "PANTRY  ·  AISLE 4"),
            Record("HONEYBROOK", "Wildflower Honey", "Raw, unfiltered  ·  340 g jar", "", "7", "49", "", "$2.21 per 100 g", "PN-118302", "501234567891", "PANTRY  ·  AISLE 4"),
            Record("TERRA & CO", "Dark Chocolate 72%", "Fair trade  ·  100 g bar", "4.25", "2", "99", "1.26", "$2.99 per 100 g", "SW-330145", "501234567892", "SWEETS  ·  AISLE 9"),
        };
        foreach (var key in records[0].Keys) b.Field(key, records[0][key]);

        // ---- header band: a sun mark (disc and ring) and the store name, the aisle on the right as a field
        b.Fill(m, m, b.W - 2 * m, 76, round: 2);
        b.Disc(m + 22, m + 20, 36, white: true);
        b.Circle(m + 14, m + 12, 52, 5, white: true);
        b.Text(m + 90, m + 16, "FRESHFIELD MARKET", Industrial, 10, bold: true, reverse: true);
        // The aisle is a field, which cannot be measured, so it is a block anchored to the right edge.
        b.Text(b.W - m - 24 - 300, m + 26, "{{Aisle}}", Ui, 6.5, bold: true, width: 300, align: TextAlignment.Right, reverse: true);

        // ---- product, left column
        b.Text(m + 12, 132, "{{Brand}}", Ui, 6.5, bold: true);
        var name = b.Text(m + 12, 176, "{{Name}}", Ui, 11.5, bold: true, width: 400, maxLines: 1);
        // Long names shrink to fit instead of wrapping, so the price block never moves.
        name.Block!.Fit = new TextFitOptions { Mode = TextFitMode.Shrink, MinSize = 8, MaxSize = 11.5 };
        b.Text(m + 12, 236, "{{Detail}}", Ui, 8);
        b.HRule(m + 12, 286, 410, 3);

        // ---- barcode, SKU and validity in the lower left
        var ean = b.Barcode(m + 12, 318, BarcodeSymbology.Ean13, "{{Ean}}", e => { e.ModuleWidth = 3; e.Height = 96; e.HumanReadable = true; });
        b.Text(m + 12, 478, "SKU  {{Sku}}", Ui, 6.5, bold: true);
        b.Text(m + 12, 520, "PRICE VALID 12 – 25 OCT 2026", Ui, 5.5);

        // ---- price block: the old price struck through, the dollars big, the cents raised
        int right = b.W - m - 24;
        b.Text(right - 330, 120, "WAS ${{WasPrice}}", Ui, 8, bold: true, width: 330, align: TextAlignment.Right, condition: onSale);
        // The strike-through is a rule drawn over the old price, because a label printer has no strike-through attribute.
        int strike = b.TextWidth("WAS $0.00", Ui, 8, true) + 14; // digits are tabular, so one measure fits every price
        b.HRule(right - strike + 7, 136, strike, 4, condition: onSale);
        b.Text(470, 184, "$", Industrial, 22, bold: true);
        b.Text(500, 184, "{{Whole}}", Industrial, 48, bold: true, width: 230, align: TextAlignment.Right);
        b.Text(742, 184, "{{Cents}}", Industrial, 24, bold: true);
        b.Text(right - 330, 356, "{{UnitPrice}}", Ui, 6.5, width: 330, align: TextAlignment.Right);

        // ---- SALE band, printed only for records with a WasPrice
        b.Fill(480, 412, b.W - m - 480, 132, round: 2, condition: onSale);
        b.Text(480, 434, "SALE", Industrial, 20, bold: true, width: b.W - m - 480, align: TextAlignment.Center, reverse: true, condition: onSale);
        b.Text(480, 506, "SAVE ${{Save}}", Ui, 7, bold: true, width: b.W - m - 480, align: TextAlignment.Center, reverse: true, condition: onSale);

        var variants = records.Select((r, i) => new ShowcaseVariant("-" + (i + 1), r, r["Name"])).ToArray();
        return new ShowcaseLabel("shelf-price-tags", b.Doc, variants, SheetLayout.Column);
    }

    /// <summary>One merge record of the shelf-tag example. The EAN column holds 12 digits: the printer, and Rotula's preview, add the check digit.</summary>
    private static Dictionary<string, string> Record(string brand, string name, string detail, string was, string whole, string cents, string save,
        string unit, string sku, string ean, string aisle) => new()
    {
        ["Brand"] = brand, ["Name"] = name, ["Detail"] = detail, ["WasPrice"] = was, ["Whole"] = whole, ["Cents"] = cents, ["Save"] = save,
        ["UnitPrice"] = unit, ["Sku"] = sku, ["Ean"] = ean, ["Aisle"] = aisle,
    };
}
