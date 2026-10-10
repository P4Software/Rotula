using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using SkiaSharp;

namespace Rotula.Rendering.Input;

/// <summary>
/// Maps the Rotula.Core document model onto the renderer's own <see cref="RenderLabel"/> input:
/// variables are substituted, defaults (^CF, ^FW) are applied and margins (^LH) are added to every position,
/// so the preview lays elements out exactly where <see cref="ZplGenerator"/> tells the printer to put them.
/// <para>
/// Not previewed: whole-label inversion (^PO) is ignored, and bitmap font ids other than '0' are all drawn with
/// the monospaced stand-in. Text preview is approximate in general (see <see cref="LabelRenderer"/>).
/// </para>
/// </summary>
public static class LabelDocumentAdapter
{
    /// <summary>Converts a label document into render input.</summary>
    /// <param name="doc">The design.</param>
    /// <param name="data">Variable values; missing names fall back to the declared sample values.</param>
    public static RenderLabel ToRenderLabel(LabelDocument doc, IReadOnlyDictionary<string, string>? data = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        int dpi = (int)doc.Dpi;
        int homeX = doc.Margins.Left.ToDots(doc.Dpi);
        int homeY = doc.Margins.Top.ToDots(doc.Dpi);

        // Resolver warnings (unknown variables) are reported by the ZPL validation, not by the preview.
        var ignored = new List<ValidationIssue>();
        var elements = new List<RenderElement>(doc.Elements.Count);
        foreach (var e in doc.Elements)
        {
            var mapped = Map(doc, e, data, ignored);
            if (mapped is null) continue;
            elements.Add(mapped with { X = e.X + homeX, Y = e.Y + homeY });
        }

        return new RenderLabel(doc.WidthDots, doc.HeightDots, dpi, elements);
    }

    private static RenderElement? Map(LabelDocument doc, LabelElement e, IReadOnlyDictionary<string, string>? data, List<ValidationIssue> issues)
    {
        var rotation = ToRotation(e.Rotation ?? doc.DefaultRotation);
        bool typeset = e.OriginKind == FieldOriginKind.Typeset;

        switch (e)
        {
            case TextElement t:
            {
                var def = doc.DefaultFont ?? new FontSpec('0', 30, 30);
                var text = VariableResolver.Resolve(t.GetTemplate(), data, doc.Variables, t.Id, issues);
                // Outside a ^FB block the printer prints one line, so line breaks are treated as spaces.
                if (t.Block is null)
                    text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                return new RenderText
                {
                    Text = text,
                    FontHeightDots = t.FontHeight ?? def.Height,
                    Font = (t.FontId ?? def.FontId) == '0' ? RenderFontFamily.Sans : RenderFontFamily.Mono,
                    WidthDots = t.Block?.WidthDots ?? 0,
                    MaxLines = t.Block?.MaxLines ?? 0,
                    LineSpacingDots = t.Block?.LineSpacing ?? 0,
                    Alignment = t.Block?.Alignment switch
                    {
                        TextAlignment.Center => RenderTextAlignment.Center,
                        TextAlignment.Right => RenderTextAlignment.Right,
                        TextAlignment.Justified => RenderTextAlignment.Justified,
                        _ => RenderTextAlignment.Left,
                    },
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = t.ReverseField,
                };
            }

            case BarcodeElement b:
            {
                bool linear = Barcodes.BarcodeEncoder.IsLinear(b.Symbology);
                var data1 = VariableResolver.Resolve(b.Data, data, doc.Variables, b.Id, issues);
                return new RenderBarcode
                {
                    Symbology = b.Symbology,
                    Data = data1,
                    // ZPL: ^BY sets the module for linear codes and PDF417; ^BQ / ^BX carry their own module size.
                    ModuleDots = b.Symbology is BarcodeSymbology.QrCode or BarcodeSymbology.DataMatrix ? b.ModuleSize : b.ModuleWidth,
                    HeightDots = b.Height,
                    ShowText = linear && b.HumanReadable,
                    TextAbove = b.HumanReadablePosition == HumanReadablePosition.Above,
                    CheckDigit = b.CheckDigit,
                    QrErrorCorrection = b.QrErrorCorrection,
                    Pdf417SecurityLevel = b.Pdf417SecurityLevel,
                    Pdf417Truncate = b.Pdf417Truncate,
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = b.ReverseField,
                };
            }

            case BoxElement box:
            {
                int min = Math.Min(box.WidthDots, box.HeightDots);
                return new RenderBox
                {
                    WidthDots = box.WidthDots,
                    HeightDots = box.HeightDots,
                    ThicknessDots = box.ThicknessDots,
                    // ZPL rounding degree 0-8 maps to a corner radius of up to half the shorter side.
                    CornerRadiusDots = box.CornerRounding * min / 16.0,
                    White = box.Color == GraphicColor.White,
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = box.ReverseField,
                };
            }

            case LineElement line:
            {
                // ZPL draws a line as a box whose short side equals the thickness, so a solid box is exact.
                bool horizontal = line.Direction == LineDirection.Horizontal;
                int w = horizontal ? line.LengthDots : line.ThicknessDots;
                int h = horizontal ? line.ThicknessDots : line.LengthDots;
                return new RenderBox
                {
                    WidthDots = w,
                    HeightDots = h,
                    ThicknessDots = Math.Max(w, h),
                    White = line.Color == GraphicColor.White,
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = line.ReverseField,
                };
            }

            case CircleElement c:
                return new RenderCircle
                {
                    DiameterDots = c.DiameterDots,
                    ThicknessDots = c.ThicknessDots,
                    White = c.Color == GraphicColor.White,
                    // The image/circle origin is the bounding box corner; rotation of a circle changes nothing visible.
                    Typeset = typeset,
                    Reverse = c.ReverseField,
                };

            case ImageElement img:
            {
                if (img.Source is null) return null;
                // Use the exact dots the printer will receive, so dithering is previewed faithfully.
                var mono = ImageProcessor.ToMonochrome(img);
                return new RenderImage
                {
                    EncodedBytes = EncodeMonochrome(mono),
                    WidthDots = mono.Width,
                    HeightDots = mono.Height,
                    // ^GF ignores field rotation (it is applied to the dots already), so none is passed on.
                    Typeset = typeset,
                    Reverse = img.ReverseField,
                };
            }

            case RecallPlaceholderElement recall:
                // The stored object is unknown, so the designer's own size is used. The caption says what is recalled.
                return new RenderPlaceholder
                {
                    WidthDots = Math.Max(1, recall.WidthDots),
                    HeightDots = Math.Max(1, recall.HeightDots),
                    Caption = string.IsNullOrWhiteSpace(recall.Reference)
                        ? "Stored on printer (^" + recall.Command + ")"
                        : "Stored on printer: " + recall.Reference,
                    Typeset = typeset,
                };

            // Only raw blocks that draw something get a marker; configuration commands have no place on the label.
            // The real size of the content is unknown, so a small fixed marker is drawn at the field position.
            case RawZplElement { IsVisual: true }:
                return new RenderPlaceholder
                {
                    WidthDots = RawMarkerWidthDots,
                    HeightDots = RawMarkerHeightDots,
                    Caption = "Raw ZPL",
                    Typeset = typeset,
                };

            default:
                return null;
        }
    }

    private const int RawMarkerWidthDots = 100;
    private const int RawMarkerHeightDots = 40;

    private static RenderRotation ToRotation(FieldRotation r) => r switch
    {
        FieldRotation.Rotated90 => RenderRotation.Rotate90,
        FieldRotation.Inverted180 => RenderRotation.Rotate180,
        FieldRotation.Rotated270 => RenderRotation.Rotate270,
        _ => RenderRotation.None,
    };

    /// <summary>Encodes packed 1-bit dots (1 = black) as a grey PNG.</summary>
    private static byte[] EncodeMonochrome(MonochromeBitmap mono)
    {
        using var bmp = new SKBitmap(new SKImageInfo(mono.Width, mono.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = new byte[mono.Width * mono.Height];
        for (int y = 0; y < mono.Height; y++)
            for (int x = 0; x < mono.Width; x++)
                pixels[y * mono.Width + x] = mono.IsBlack(x, y) ? (byte)0 : (byte)255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
