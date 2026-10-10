using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.App.Localization;

namespace Rotula.App.Editing;

/// <summary>
/// Turns the English <see cref="BarcodeSymbologyInfo.PreviewNote"/> of the catalogue into text in the current language.
/// Why keyed by symbology: the catalogue text is English prose, so the UI looks its own translation up by symbology
/// instead of comparing strings, and a test makes sure every catalogue note has one.
/// </summary>
public static class BarcodePreviewNotes
{
    /// <summary>
    /// The resource key of the note that explains what the preview of <paramref name="symbology"/> does differently,
    /// or null when the preview is exact. A symbology without a preview drawing always uses the shared placeholder note.
    /// </summary>
    public static string? KeyFor(BarcodeSymbology symbology)
    {
        var info = BarcodeCatalog.Get(symbology);
        if (info.PreviewNote is null) return null;
        if (!info.HasPreviewEncoder) return "Preview.Note.Placeholder";
        return symbology switch
        {
            BarcodeSymbology.Code39 or BarcodeSymbology.Logmars or BarcodeSymbology.Codabar => "Preview.Note.Ratio2",
            BarcodeSymbology.Code11 or BarcodeSymbology.Industrial2Of5 or BarcodeSymbology.Standard2Of5
                or BarcodeSymbology.Msi or BarcodeSymbology.Plessey => "Preview.Note.Ratio3",
            BarcodeSymbology.Postnet or BarcodeSymbology.Planet => "Preview.Note.Postal",
            BarcodeSymbology.DataBar => "Preview.Note.DataBar",
            BarcodeSymbology.Aztec => "Preview.Note.Aztec",
            _ => "Preview.Note.Placeholder",
        };
    }

    /// <summary>
    /// The note for one element in the current language, or null when its preview is exact. An option the preview cannot
    /// reproduce (an Aztec Rune, for example) gives a more specific note than the symbology as a whole.
    /// </summary>
    public static string? For(BarcodeElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Enum.IsDefined(element.Symbology)) return null;
        string? key = KeyFor(element.Symbology);
        if (key is null) return null;
        if (element.Symbology == BarcodeSymbology.Aztec && BarcodeCatalog.GetPreviewLimitation(element) is not null)
            key = "Preview.Note.AztecSpecial";
        return Loc.Get(key);
    }
}
