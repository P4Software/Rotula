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
        if (!same && TryFindStyleVariant(family, bold, italic) is { } variant) return new ResolvedTypeface(variant, variant.FamilyName, false, 0f);
        if (!same && TryLoadInstalledFile(family, bold, italic) is { } installed) return new ResolvedTypeface(installed, installed.FamilyName, false, 0f);
        return new ResolvedTypeface(face, face.FamilyName, !same, 0f);
    }

    /// <summary>
    /// Weight and width words that Windows puts at the end of a family name for a face that is really a style of a bigger family. The font
    /// manager only lists the parent family ("Arial", "Segoe UI", "Bahnschrift"), so a request for "Arial Black" or "Segoe UI Semibold" found nothing
    /// and fell back to another font. Longer words come first so "SemiBold" is not read as "Bold".
    /// </summary>
    private static readonly (string Word, int Weight, int Width)[] StyleWords =
    {
        ("ExtraCondensed", 0, (int)SKFontStyleWidth.ExtraCondensed), ("SemiCondensed", 0, (int)SKFontStyleWidth.SemiCondensed),
        ("Condensed", 0, (int)SKFontStyleWidth.Condensed), ("Narrow", 0, (int)SKFontStyleWidth.Condensed),
        ("ExtraLight", 200, 0), ("SemiLight", 350, 0), ("SemiBold", 600, 0), ("DemiBold", 600, 0), ("ExtraBold", 800, 0),
        ("Thin", 100, 0), ("Light", 300, 0), ("Medium", 500, 0), ("Bold", 700, 0), ("Black", 900, 0), ("Heavy", 900, 0),
    };

    /// <summary>
    /// Reads trailing style words off <paramref name="family"/> ("Segoe UI Semibold" is "Segoe UI" at weight 600) and asks for the parent family at
    /// that weight and width. Returns null when no word applies or the parent family is not installed either, so the caller reports a substitution.
    /// </summary>
    private static SKTypeface? TryFindStyleVariant(string family, bool bold, bool italic)
    {
        string baseName = family.Trim();
        int weight = 0, width = (int)SKFontStyleWidth.Normal;
        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (var (word, w, wd) in StyleWords)
            {
                if (baseName.Length <= word.Length + 1 || !baseName.EndsWith(" " + word, StringComparison.OrdinalIgnoreCase)) continue;
                baseName = baseName[..^(word.Length + 1)].TrimEnd();
                if (w != 0 && weight == 0) weight = w;
                if (wd != 0) width = wd;
                stripped = true;
                break;
            }
        }
        if (weight == 0 && width == (int)SKFontStyleWidth.Normal) return null;

        // An explicit "Bold" on top of an already heavy variant must not lighten it; only raise the weight.
        int finalWeight = weight == 0 ? (bold ? 700 : 400) : bold ? Math.Max(weight, 700) : weight;
        var style = new SKFontStyle(finalWeight, width, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var face = SKTypeface.FromFamilyName(baseName, style);
        // Accept only the parent family itself: anything else is the operating system's default, i.e. still a substitution.
        return face is not null && string.Equals(face.FamilyName, baseName, StringComparison.OrdinalIgnoreCase) ? face : null;
    }

    /// <summary>
    /// Last resort for a family the font manager does not list but Windows has installed (Arial Narrow, Franklin Gothic Medium and other families
    /// that only exist under their own name): reads the list of installed font files from the registry and loads the matching file directly.
    /// Returns null when the family is not installed, so the caller still reports a substitution.
    /// </summary>
    private static SKTypeface? TryLoadInstalledFile(string family, bool bold, bool italic)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            string wanted = family.Trim() + (bold ? " Bold" : string.Empty) + (italic ? " Italic" : string.Empty);
            string regular = family.Trim() + (italic ? " Italic" : string.Empty);
            // Machine fonts first, then the ones a user installed just for themselves (those are stored with absolute paths).
            foreach (var (hive, keyPath) in new[]
                     {
                         (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"),
                         (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"),
                     })
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key is null) continue;
                // Prefer the exact weight and slant; fall back to the plain face of the family, which is better than another family.
                foreach (string target in bold ? new[] { wanted, regular } : new[] { wanted })
                    foreach (string valueName in key.GetValueNames())
                    {
                        if (!RegistryNameMatches(valueName, target)) continue;
                        if (key.GetValue(valueName) is not string file || file.Length == 0) continue;
                        string path = Path.IsPathRooted(file) ? file : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
                        if (!File.Exists(path)) continue;
                        var face = SKTypeface.FromFile(path);
                        if (face is not null) return face;
                    }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No access to the registry or the file: the caller reports the substitution as before.
        }
        return null;
    }

    /// <summary>
    /// True when a registry value name such as "Arial Narrow Bold (TrueType)" names <paramref name="target"/>. A font collection lists several faces
    /// in one name ("Face A &amp; Face B (TrueType)"), so each part is compared on its own.
    /// </summary>
    private static bool RegistryNameMatches(string valueName, string target)
    {
        int paren = valueName.LastIndexOf(" (", StringComparison.Ordinal);
        string names = paren > 0 ? valueName[..paren] : valueName;
        foreach (string part in names.Split(" & ", StringSplitOptions.TrimEntries))
            if (string.Equals(part, target, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
