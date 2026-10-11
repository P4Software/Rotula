using System.Collections.Concurrent;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Rendering;

/// <summary>What <see cref="TypefaceSource.Resolve"/> found for a font request.</summary>
/// <param name="Typeface">The face to draw with. Shared for the life of the process: do not dispose it.</param>
/// <param name="ActualFamily">The family of that face.</param>
/// <param name="Substituted">True when the computer has no font with the requested family and another face stands in.</param>
/// <param name="SkewX">Slant to add to the font when italic was asked for and the face has no italic of its own (the bundled stand-ins).</param>
internal sealed record ResolvedTypeface(SKTypeface Typeface, string ActualFamily, bool Substituted, float SkewX);

/// <summary>
/// Finds the typeface for a font family name. The two bundled Liberation faces are always available (they are what the preview uses for printer
/// fonts and what the tests rely on, so no test needs an installed font); other names are asked from the operating system. The
/// operating system silently gives a default face for a name it does not know, so the answer is compared with the request and a substitution is reported.
/// </summary>
internal static class TypefaceSource
{
    private const float SyntheticItalic = -0.25f;
    private static readonly ConcurrentDictionary<string, ResolvedTypeface> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a family, weight and slant. Thread safe.</summary>
    public static ResolvedTypeface Resolve(string family, bool bold, bool italic)
    {
        string key = family + "|" + bold + "|" + italic;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        if (Cache.Count > 256) Cache.Clear();
        return Cache[key] = Find(family, bold, italic);
    }

    private static ResolvedTypeface Find(string family, bool bold, bool italic)
    {
        if (string.Equals(family, "Liberation Sans", StringComparison.OrdinalIgnoreCase))
            return new ResolvedTypeface(RenderFonts.Get(RenderFontFamily.Sans, bold), "Liberation Sans", false, italic ? SyntheticItalic : 0f);
        if (string.Equals(family, "Liberation Mono", StringComparison.OrdinalIgnoreCase))
            return new ResolvedTypeface(RenderFonts.Get(RenderFontFamily.Mono, bold), "Liberation Mono", false, italic ? SyntheticItalic : 0f);

        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var face = SKTypeface.FromFamilyName(family, style);
        if (face is null)
            return new ResolvedTypeface(RenderFonts.Get(RenderFontFamily.Sans, bold), "Liberation Sans", true, italic ? SyntheticItalic : 0f);
        bool same = string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase);
        return new ResolvedTypeface(face, face.FamilyName, !same, 0f);
    }
}
