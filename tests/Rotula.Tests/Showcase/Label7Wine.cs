using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests.Showcase;

internal static partial class ShowcaseLabels
{
    // ======================================================================================================
    // 7. Wine bottle label (3.5 x 4.5 in, 300 dpi): serif Windows fonts, rotated side text, ornaments from
    //    circles and lines
    // ======================================================================================================

    private static ShowcaseLabel WineLabel()
    {
        var b = new LabelBuilder(3.5, 4.5, 300, 7);
        const string Serif = "Georgia", Roman = "Palatino Linotype";
        int cx = b.W / 2;

        // ---- double rule frame with a disc in each corner
        b.Frame(26, 26, b.W - 52, b.H - 52, 7);
        b.Frame(46, 46, b.W - 92, b.H - 92, 3);
        foreach (int x in new[] { 26, b.W - 26 - 20 })
            foreach (int y in new[] { 26, b.H - 26 - 20 })
                b.Disc(x, y, 20);

        // ---- a small ornament used twice: a rule, a ring around a dot, a rule
        void Ornament(int y, int half = 190)
        {
            b.HRule(cx - half, y, half - 26, 3);
            b.HRule(cx + 26, y, half - 26, 3);
            b.Disc(cx - 8, y - 6, 16);
            b.Circle(cx - 18, y - 16, 36, 3);
        }

        string overline = "E S T A T E   B O T T L E D";
        b.TextCentered(cx, 96, overline, Serif, 7, bold: true);
        Ornament(160);

        // ---- crest: two rings and a monogram, with three "wings" on each side
        int crestD = 270, cyTop = 205;
        b.Circle(cx - crestD / 2, cyTop, crestD, 9);
        b.Circle(cx - crestD / 2 + 24, cyTop + 24, crestD - 48, 3);
        b.TextCentered(cx, cyTop + 64, "V", Serif, 64, bold: true, italic: true);
        // Wings: three rules of decreasing length on each side of the crest, their inner ends level with the ring.
        for (int k = 0; k < 3; k++)
        {
            int len = 130 - k * 36, y = cyTop + crestD / 2 - 40 + k * 34;
            b.HRule(cx - crestD / 2 - 24 - len, y, len, 5);
            b.HRule(cx + crestD / 2 + 24, y, len, 5);
        }

        // ---- the house name, sized to fit the room between the side texts
        int room = b.W - 2 * 150;
        b.TextCentered(cx, 540, "D O M A I N E", Serif, 10, italic: true);
        double namePt = b.FitPoints("VALMORAL", Roman, 40, room, bold: true);
        b.TextCentered(cx, 610, "VALMORAL", Roman, namePt, bold: true);
        b.HRule(cx - 150, 730, 300, 4);
        b.HRule(cx - 150, 742, 300, 2);

        // ---- the wine
        b.TextCentered(cx, 790, "Réserve Syrah", Serif, b.FitPoints("Réserve Syrah", Serif, 27, room, italic: true), italic: true);
        b.HRule(cx - 250, 962, 150, 3);
        b.TextCentered(cx, 925, "2021", Roman, 24, bold: true);
        b.HRule(cx + 100, 962, 150, 3);
        b.TextCentered(cx, 1035, "Vallée du Lys", Serif, 12);
        b.TextCentered(cx, 1095, "Appellation Contrôlée", Serif, 8.5, italic: true);
        Ornament(1170, 150);

        // ---- foot
        b.TextCentered(cx, 1208, "RED WINE   ·   750 ml   ·   13.5% vol.", Serif, 8, bold: true);
        b.TextCentered(cx, 1262, "Contains sulphites. Please drink responsibly.", Ui, 6);

        // ---- side texts turned 90 degrees, centred on the label height. A rotated text is placed by the top-left of its turned box.
        string left = "PRODUIT DE FRANCE", right = "MIS EN BOUTEILLE AU DOMAINE";
        int midY = b.H / 2;
        b.Text(76, midY - b.TextWidth(left, Serif, 7.5, true) / 2, left, Serif, 7.5, bold: true, rotation: FieldRotation.Rotated270);
        b.Text(b.W - 76 - b.Pt(7.5) * 5 / 4, midY - b.TextWidth(right, Serif, 7.5, true) / 2, right, Serif, 7.5, bold: true, rotation: FieldRotation.Rotated90);

        return new ShowcaseLabel("wine-bottle-label", b.Doc, new[] { Single() });
    }
}
