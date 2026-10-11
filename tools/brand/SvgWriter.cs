using System.Text;

namespace Rotula.Brand;

/// <summary>Writes the vector logos. The paths come from <see cref="Layout"/> and <see cref="Wordmark"/>, the same data the raster side draws.</summary>
internal static class SvgWriter
{
    private const string Ns = "http://www.w3.org/2000/svg";

    /// <summary>The mark alone: a 256 x 256 SVG.</summary>
    public static string Mark()
    {
        var l = Layout.ForTile(Layout.Master);
        return $"<svg xmlns=\"{Ns}\" viewBox=\"0 0 256 256\" width=\"256\" height=\"256\" role=\"img\" aria-label=\"Rotula\">" +
               $"<title>Rotula</title>{Defs(l)}{MarkBody(l)}</svg>\n";
    }

    /// <summary>The mark followed by the wordmark, for banners. <paramref name="darkSurface"/> picks light lettering for dark backgrounds.</summary>
    public static string Lockup(bool darkSurface)
    {
        var l = Layout.ForTile(Layout.Master);
        const float gap = 40, scale = 1.3f;
        float textW = Wordmark.Width * scale, textH = Wordmark.Height * scale;
        float textX = 256 + gap, textY = (256 - textH) / 2;
        float width = textX + textW + 12;
        string ink = darkSurface ? Palette.BarLight : Palette.Charcoal;
        string accent = darkSurface ? Palette.AccentOnDark : Palette.Accent;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"{Ns}\" viewBox=\"0 0 {Layout.N(width)} 256\" width=\"{Layout.N(width)}\" height=\"256\" role=\"img\" aria-label=\"Rotula\">");
        sb.Append("<title>Rotula</title>").Append(Defs(l)).Append(MarkBody(l));
        sb.Append($"<g transform=\"translate({Layout.N(textX)} {Layout.N(textY)}) scale({Layout.N(scale)})\" fill=\"none\" stroke=\"{ink}\" stroke-width=\"{Layout.N(Wordmark.Stroke)}\">");
        foreach (var p in Wordmark.Pieces)
        {
            if (p.Filled) sb.Append($"<path d=\"{p.Data}\" fill=\"{ink}\" stroke=\"none\"/>");
            else if (p.Accent) sb.Append($"<path d=\"{p.Data}\" stroke=\"{accent}\"/>");
            else sb.Append($"<path d=\"{p.Data}\"/>");
        }
        sb.Append("</g></svg>\n");
        return sb.ToString();
    }

    private static string Defs(Layout l)
    {
        string right = Layout.N(l.X + l.W), top = Layout.N(l.Y), fold = Layout.N(l.Fold);
        return "<defs>" +
               $"<linearGradient id=\"t\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"{Palette.TileTop}\"/><stop offset=\"1\" stop-color=\"{Palette.TileBottom}\"/></linearGradient>" +
               $"<linearGradient id=\"f\" gradientUnits=\"userSpaceOnUse\" x1=\"{Layout.N(l.X + l.W - l.Fold)}\" y1=\"{Layout.N(l.Y + l.Fold)}\" x2=\"{right}\" y2=\"{top}\"><stop offset=\"0\" stop-color=\"{Palette.FlapBottom}\"/><stop offset=\"1\" stop-color=\"{Palette.FlapTop}\"/></linearGradient>" +
               $"<clipPath id=\"c\"><path d=\"{l.BodyPath()}\"/></clipPath></defs>";
    }

    private static string MarkBody(Layout l) =>
        $"<path d=\"{l.BodyPath()}\" fill=\"url(#t)\"/>" +
        $"<path d=\"{l.BodyPath()}\" clip-path=\"url(#c)\" fill=\"none\" stroke=\"#fff\" stroke-opacity=\".18\" stroke-width=\"{Layout.N(l.Edge * 2)}\"/>" +
        $"<path d=\"{l.FlapPath()}\" fill=\"url(#f)\"/>" +
        $"<path d=\"{l.BarsPath(false)}\" fill=\"{Palette.BarLight}\"/>" +
        $"<path d=\"{l.BarsPath(true)}\" fill=\"{Palette.AccentOnDark}\"/>";
}
