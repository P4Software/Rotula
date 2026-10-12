using Rotula.Core.Elements;
using Rotula.Core.Gs1;
using Rotula.Core.Model;

namespace Rotula.Tests.Showcase;

internal static partial class ShowcaseLabels
{
    // ======================================================================================================
    // 3. Food product label (4 x 6 in, 300 dpi): nutrition panel, ingredients, allergen box, EAN-13
    // ======================================================================================================

    private static ShowcaseLabel FoodLabel()
    {
        var b = new LabelBuilder(4, 6, 300, 3);
        int m = 60, inner = b.W - 2 * m;
        string ean = Gs1Keys.AppendCheckDigit("590123412345");
        const string Serif = "Georgia", Body = "Palatino Linotype", Heavy = "Arial Black";

        // ---- brand: a beehive (dome ellipse, three bands, entrance) inside a ring, with the name set in Georgia
        b.Circle(m, m + 4, 150, 9);
        b.Ellipse(m + 33, m + 30, 84, 100, 8);
        for (int k = 0; k < 3; k++) b.HRule(m + 39 + (k == 0 ? 8 : 0), m + 58 + k * 22, 72 - (k == 0 ? 16 : 0), 6);
        b.Fill(m + 63, m + 108, 24, 22, round: 8);
        b.Text(m + 190, m + 6, "Hearth & Hive", Serif, 25, bold: true, italic: true);
        b.Text(m + 192, m + 104, "SLOW-BAKED IN SMALL BATCHES", Ui, 7.5, bold: true);
        b.HRule(m + 192, m + 148, inner - 192, 4);

        // ---- product band, reverse print
        b.Fill(m, 250, inner, 120, round: 2);
        b.TextCentered(m + inner / 2, 250 + 30, "WILD BERRY & ALMOND GRANOLA", Body, 12.5, bold: true, reverse: true);

        // ---- nutrition panel, drawn like the regulated table: a frame, heavy rules between sections and hairlines between rows
        int px = m, py = 410, pw = 590, pad = 22;
        int rx = px + pw - pad; // right edge for the values
        b.Frame(px, py, pw, 1330, 6);
        int y = py + 18;
        b.Text(px + pad, y, "Nutrition Facts", Heavy, 15, bold: true);
        y += 86;
        b.Text(px + pad, y, "8 servings per container", Ui, 8.5);
        y += 46;
        b.Text(px + pad, y, "Serving size", Ui, 9.5, bold: true);
        b.TextRight(rx, y, "1/2 cup (55g)", Ui, 9.5, bold: true);
        y += 50;
        b.HRule(px + pad, y, pw - 2 * pad, 18);
        y += 36;
        b.Text(px + pad, y, "Amount per serving", Ui, 7.5, bold: true);
        y += 40;
        b.Text(px + pad, y + 4, "Calories", Ui, 15, bold: true);
        b.TextRight(rx, y - 6, "230", Heavy, 22, bold: true);
        y += 82;
        b.HRule(px + pad, y, pw - 2 * pad, 10);
        y += 18;
        b.TextRight(rx, y, "% Daily Value*", Ui, 7.5, bold: true);
        y += 42;
        b.HRule(px + pad, y, pw - 2 * pad, 3);

        // The rows: name, amount, daily value; sub-rows are indented and not bold.
        (string Name, string Amount, string Dv, bool Sub)[] rows =
        {
            ("Total Fat", "8g", "10%", false),
            ("Saturated Fat", "1g", "5%", true),
            ("Trans Fat", "0g", "", true),
            ("Cholesterol", "0mg", "0%", false),
            ("Sodium", "15mg", "1%", false),
            ("Total Carbohydrate", "37g", "13%", false),
            ("Dietary Fiber", "5g", "18%", true),
            ("Total Sugars", "11g", "", true),
            ("Includes 9g Added Sugars", "", "18%", true),
            ("Protein", "6g", "", false),
        };
        const int rowH = 56;
        foreach (var r in rows)
        {
            y += 12;
            int ix = px + pad + (r.Sub ? 28 : 0);
            // The amount is set in regular weight right after the bold name, like the printed regulation layout.
            b.Text(ix, y, r.Name, Ui, 8.5, bold: !r.Sub);
            if (r.Amount.Length > 0 && !r.Name.StartsWith("Includes", StringComparison.Ordinal))
                b.Text(ix + b.TextWidth(r.Name + " ", Ui, 8.5, !r.Sub), y, r.Amount, Ui, 8.5);
            if (r.Dv.Length > 0) b.TextRight(rx, y, r.Dv, Ui, 8.5, bold: true);
            y += rowH - 12;
            b.HRule(px + pad, y, pw - 2 * pad, 2);
        }
        y += 8;
        b.HRule(px + pad, y, pw - 2 * pad, 12);
        y += 22;
        (string Name, string Amount, string Dv)[] vitamins =
        {
            ("Vitamin D", "0mcg", "0%"), ("Calcium", "40mg", "4%"), ("Iron", "2mg", "10%"), ("Potassium", "190mg", "4%"),
        };
        foreach (var v in vitamins)
        {
            b.Text(px + pad, y, v.Name + " " + v.Amount, Ui, 8.5);
            b.TextRight(rx, y, v.Dv, Ui, 8.5);
            y += 52;
            b.HRule(px + pad, y - 14, pw - 2 * pad, 2);
        }
        b.Text(px + pad, y + 4, "* The % Daily Value (DV) tells you how much a\nnutrient in a serving contributes to a daily diet.\n2,000 calories a day is used for general advice.",
            Ui, 6, width: pw - 2 * pad, maxLines: 3, lineSpacing: 2);

        // ---- right column: ingredients, allergens, badges, weight and barcode
        int cx = px + pw + 40, cw = m + inner - cx;
        b.Text(cx, 410, "INGREDIENTS", Ui, 7.5, bold: true);
        b.Text(cx, 462, "Whole grain rolled oats, almonds, raw honey, sunflower oil, dried blueberries, dried cranberries, pumpkin seeds, cinnamon, sea salt.",
            Body, 9, width: cw, maxLines: 9, lineSpacing: 6);

        int ay = 410 + 400;
        b.Frame(cx, ay, cw, 334, 6, round: 3);
        b.Fill(cx, ay, cw, 62, round: 3);
        b.Fill(cx, ay + 30, cw, 32); // squares off the lower corners of the title tab so only the top corners stay round
        b.Text(cx + 22, ay + 17, "CONTAINS ALLERGENS", Ui, 7.5, bold: true, reverse: true);
        // Wheat ear icon from a stem and three pairs of grains.
        int wx = cx + 50, wy = ay + 100;
        b.VRule(wx + 20, wy, 120, 5);
        for (int k = 0; k < 3; k++)
        {
            b.Ellipse(wx, wy + k * 32, 22, 38, 11);
            b.Ellipse(wx + 26, wy + k * 32, 22, 38, 11);
        }
        b.Ellipse(wx + 14, wy - 30, 14, 36, 14);
        b.Text(cx + 130, ay + 94, "Oats (gluten)\nAlmonds\nSesame", Ui, 9.5, bold: true, width: cw - 150, maxLines: 3, lineSpacing: 6);
        b.Text(cx + 22, ay + 276, "May contain traces of peanuts.", Ui, 7, width: cw - 44, maxLines: 1);

        // Badges: rings with a mark inside, drawn from the same primitives.
        int by = ay + 334 + 36, d = 112;
        b.Circle(cx, by, d, 6);
        b.TextCentered(cx + d / 2, by + 32, "V", Ui, 17, bold: true);
        b.TextCentered(cx + d / 2, by + d + 14, "VEGAN", Ui, 6.5, bold: true);
        int bx2 = cx + d + 40;
        b.Circle(bx2, by, d, 6);
        b.Circle(bx2 + 30, by + 30, 52, 6);
        b.Diagonal(bx2 + 18, by + 18, d - 36, d - 36, 6, leanRight: false);
        b.TextCentered(bx2 + d / 2, by + d + 14, "NO PALM OIL", Ui, 6.5, bold: true);

        // Weight block above the barcode.
        int sy = by + d + 14 + 20 + 50;
        b.HRule(cx, sy, cw, 6);
        b.Text(cx, sy + 30, "NET WT", Ui, 7.5, bold: true);
        b.Text(cx, sy + 72, "12 OZ", Heavy, 19, bold: true);
        b.Text(cx + b.TextWidth("12 OZ", Heavy, 19, true) + 22, sy + 94, "(340 g)", Ui, 11, bold: true);
        var bc = b.BarcodeFit(cx, sy + 190, BarcodeSymbology.Ean13, ean, cw, e => { e.Height = b.H - m - (sy + 190) - 62; e.HumanReadable = true; }, center: true);

        return new ShowcaseLabel("food-nutrition-label", b.Doc, new[] { Single() });
    }
}
