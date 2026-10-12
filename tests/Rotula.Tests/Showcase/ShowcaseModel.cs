using Rotula.Core.Model;
using ZXing;

namespace Rotula.Tests.Showcase;

/// <summary>A rectangle of the label in printer dots.</summary>
internal sealed record ScanRegion(int X, int Y, int Width, int Height);

/// <summary>A barcode that must be readable in a rendered picture: the symbology and the text a scanner has to return.</summary>
/// <param name="Format">The symbology ZXing should look for.</param>
/// <param name="Text">The decoded text, exactly as a scanner would report it.</param>
/// <param name="Region">Where the symbol is. Decoding only that area keeps neighbouring symbols from confusing the reader; null scans the whole label.</param>
internal sealed record ExpectedScan(BarcodeFormat Format, string Text, ScanRegion? Region = null);

/// <summary>One picture of a showcase label: an optional data record and the barcodes that picture must contain.</summary>
/// <param name="Suffix">Appended to the file name (empty for the single picture of most labels).</param>
/// <param name="Record">The merge record that fills the {{fields}}; null shows the sample values.</param>
/// <param name="Scans">Barcodes the picture is checked against.</param>
/// <param name="Caption">Text written under the label when several variants share one picture; empty for none.</param>
internal sealed record ShowcaseVariant(string Suffix, IReadOnlyDictionary<string, string>? Record, IReadOnlyList<ExpectedScan> Scans, string Caption = "");

/// <summary>How the variants of a label become pictures.</summary>
internal enum SheetLayout
{
    /// <summary>One picture per variant, named after the label plus the variant suffix.</summary>
    Separate,

    /// <summary>All variants stacked top to bottom in one picture named after the label.</summary>
    Column,

    /// <summary>All variants side by side in one picture named after the label.</summary>
    Row,
}

/// <summary>A finished showcase label: its file name, the document and the pictures to render from it.</summary>
/// <param name="Name">File name without extension, shared by the .rotula file and the PNGs.</param>
/// <param name="Document">The label as the designer would save it.</param>
/// <param name="Variants">The pictures to render.</param>
/// <param name="Layout">Whether the variants get their own pictures or share one.</param>
internal sealed record ShowcaseLabel(string Name, LabelDocument Document, IReadOnlyList<ShowcaseVariant> Variants, SheetLayout Layout = SheetLayout.Separate);
