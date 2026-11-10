using System.Runtime.InteropServices;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Rendering;
using Rotula.Rendering.Device;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Printing;

/// <summary>
/// Thrown by <see cref="DevicePictureRenderer.Render"/> when a label cannot be drawn faithfully for this record: barcode data that cannot be
/// encoded, or an image that cannot be read. The preview draws a red cross in such a place; on a real label that would be a silent substitution,
/// so the label must fail before any page of its job is sent. <see cref="Issues"/> carries what went wrong as translatable findings; the
/// exception message is plain English for developers and logs only.
/// </summary>
public sealed class DevicePictureException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="issues">What could not be drawn, as findings with message keys (at least one).</param>
    /// <param name="message">A plain English description for developers and logs.</param>
    public DevicePictureException(IReadOnlyList<ValidationIssue> issues, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues;
    }

    /// <summary>
    /// What could not be drawn: <see cref="DevicePictureRenderer.BarcodeNotDrawn"/> (symbology name, data), <see cref="DevicePictureRenderer.ImageNotDrawn"/>
    /// (no arguments) or <see cref="DevicePictureRenderer.ElementNotDrawn"/> (no arguments), all errors.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }
}

/// <summary>
/// Draws one label as the picture a Windows printer driver receives in picture mode: the same renderer as the preview, rescaled to the
/// driver's resolution with every barcode module on whole device dots, flattened to 8-bit grey.
/// <para>
/// Known limitation: bitmap images inside the label are drawn with the renderer's smooth (linear) sampling, because neither
/// <see cref="RenderOptions"/> nor <see cref="LabelRenderer"/> offers nearest-neighbour sampling. An image that is enlarged a lot therefore gets
/// grey edges between its dots, which the driver then halftones. Barcodes, boxes and lines are not affected (they are drawn without smoothing).
/// </para>
/// </summary>
public static class DevicePictureRenderer
{
    /// <summary>
    /// Largest picture drawn, in pixels (40 million: 40 MB of grey plus 160 MB of colour while it is drawn). A bigger label at a very high
    /// resolution is refused by the pre-flight with a finding instead of running the computer out of memory.
    /// </summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>Error key carried by <see cref="DevicePictureException"/>: a barcode's data cannot be encoded for this record (symbology name, data).</summary>
    public const string BarcodeNotDrawn = "Driver.Render.BarcodeNotDrawn";
    /// <summary>Error key carried by <see cref="DevicePictureException"/>: an image cannot be read or is too large to draw (no arguments).</summary>
    public const string ImageNotDrawn = "Driver.Render.ImageNotDrawn";
    /// <summary>Error key carried by <see cref="DevicePictureException"/>: any other element the renderer could not draw faithfully (no arguments).</summary>
    public const string ElementNotDrawn = "Driver.Render.ElementNotDrawn";

    /// <summary>
    /// The resolution the picture is drawn at for <paramref name="media"/>: the coarser of its two axes. The picture keeps square pixels and the
    /// device stretches it along the finer axis; with a whole ratio (600 x 300) every module still lands on whole device dots.
    /// </summary>
    /// <param name="media">What the driver reported.</param>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is null.</exception>
    public static int RenderDpi(DriverMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return Math.Min(media.DpiX, media.DpiY);
    }

    /// <summary>
    /// Draws <paramref name="document"/> with values that were already resolved for this label, at <paramref name="renderDpi"/>, on white paper.
    /// Objects that are not printed are left out, exactly as in every other print path; so is every part the driver cannot print (see
    /// <see cref="IsLeftOutOfPicture"/>). Print orientation (upside down) and mirror image are applied, because no printer applies them to a picture.
    /// </summary>
    /// <param name="document">The design.</param>
    /// <param name="resolvedValues">This label's resolved values (they are used as they are, never resolved again), or null for a label without variables.</param>
    /// <param name="renderDpi">Resolution of the picture in dots per inch, normally <see cref="RenderDpi"/> of the driver media.</param>
    /// <returns>The picture, one grey byte per pixel, 0 = black.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="renderDpi"/> is not positive.</exception>
    /// <exception cref="DevicePictureException">Something on the label could not be drawn faithfully for these values (bad barcode data, unreadable image).</exception>
    /// <exception cref="InvalidOperationException">The picture would be larger than <see cref="MaxPixels"/>.</exception>
    public static DevicePicture Render(LabelDocument document, IReadOnlyDictionary<string, string>? resolvedValues, int renderDpi)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(renderDpi);

        // Second guard behind the pre-flight's TooLarge finding: checked before anything is built, so a caller that skipped the pre-flight still
        // gets a clear refusal instead of an out-of-memory failure halfway through a job.
        var (width, height) = PictureSize(document, renderDpi);
        if ((long)width * height > MaxPixels)
            throw new InvalidOperationException(
                $"The label picture would be {width} x {height} pixels at {renderDpi} dpi, more than the {MaxPixels} pixels Rotula draws. Run the driver pre-flight first.");

        // The parts the driver cannot print are left out after the layout is worked out on the whole design, so nothing anchored to them moves.
        var designed = LabelDocumentAdapter.ToRenderLabelResolved(document, resolvedValues, e => IsLeftOutOfPicture(document, e));
        // In printer-language mode the printer itself turns (^POI) or mirrors (^PMY) the label; in picture mode nothing does, so the picture is
        // drawn the way the printer would put it on the media. The scaler keeps both flags.
        var asPrinted = LabelDocumentAdapter.AsPrinted(designed, document);
        return Draw(RenderLabelScaler.ToResolution(asPrinted, renderDpi));
    }

    /// <summary>
    /// Draws a render label that is already at device resolution into the grey picture, and refuses it when the renderer had to put a stand-in
    /// (a red cross) anywhere: with the parts the driver cannot print already left out, every remaining renderer issue is a real failure.
    /// </summary>
    /// <param name="label">The label at device resolution.</param>
    /// <exception cref="DevicePictureException">The renderer reported at least one element it could not draw.</exception>
    internal static DevicePicture Draw(RenderLabel label)
    {
        // Scale 1: one pixel per device dot. A Windows-font text keeps the exact (unrounded) em size the scaler gave it; nothing here rounds it.
        using var rendered = LabelRenderer.RenderBitmap(label, new RenderOptions { Scale = 1.0, Background = SKColors.White });
        if (rendered.Issues.Count > 0)
            throw new DevicePictureException(rendered.Issues.Select(ToFinding).ToList(),
                "The label picture could not be drawn faithfully, so it must not be printed: " + string.Join(" ", rendered.Issues.Select(i => i.Message)));
        return ToGray8(rendered.Bitmap);
    }

    /// <summary>
    /// True for every element the Windows-driver picture leaves out because only the printer can produce it. The pre-flight counts exactly these
    /// elements (as <see cref="DriverPreflight.RawBlocks"/>), so what is reported and what is left out can never differ:
    /// <list type="bullet">
    /// <item>every raw ZPL block, visual or not (it is a printer command);</item>
    /// <item>every element the preview shows as a hatched stand-in (<see cref="LabelDocumentAdapter.IsDrawnAsPlaceholder"/>: a barcode only the
    /// printer can draw, a recall of a graphic stored on the printer, an image too large to draw);</item>
    /// <item>the UL and CSA certification marks (<c>^GS</c> D and E): the printer prints the real marks, the renderer only has look-alike letters.
    /// The preview keeps showing the letters; only the driver picture leaves them out.</item>
    /// </list>
    /// Whether the element is printed at all (do not print, print conditions) is not looked at here.
    /// </summary>
    /// <param name="document">The design the element belongs to.</param>
    /// <param name="element">The element.</param>
    internal static bool IsLeftOutOfPicture(LabelDocument document, LabelElement element) =>
        element is RawZplElement
        || element is SymbolElement { Symbol: GraphicSymbol.UnderwritersLaboratories or GraphicSymbol.CanadianStandards }
        || LabelDocumentAdapter.IsDrawnAsPlaceholder(document, element);

    /// <summary>
    /// Width and height in pixels of the picture <see cref="Render"/> draws for <paramref name="document"/> at <paramref name="renderDpi"/>, worked
    /// out from the label size alone without allocating anything. It makes the same two roundings as the real pipeline (the label size to whole
    /// dots at the design resolution, then <see cref="RenderLabelScaler"/>'s rescale), so the pre-flight's size check judges the picture that is really drawn.
    /// </summary>
    /// <param name="document">The design.</param>
    /// <param name="renderDpi">Resolution of the picture in dots per inch.</param>
    internal static (int Width, int Height) PictureSize(LabelDocument document, int renderDpi)
    {
        ArgumentNullException.ThrowIfNull(document);
        int designDpi = (int)document.Dpi;
        return (DeviceDots(document.WidthDots, designDpi, renderDpi), DeviceDots(document.HeightDots, designDpi, renderDpi));
    }

    /// <summary>
    /// A length of <paramref name="dots"/> design dots in device dots at <paramref name="renderDpi"/>, at least one. The rounding is
    /// <see cref="RenderLabelScaler.ToDeviceDots"/> itself, with the same ratio the scaler uses, so the pre-flight judges exactly the module and
    /// picture size that are drawn.
    /// </summary>
    /// <param name="dots">Length in dots at the design resolution.</param>
    /// <param name="designDpi">The design resolution (the document's).</param>
    /// <param name="renderDpi">The picture resolution.</param>
    internal static int DeviceDots(int dots, int designDpi, int renderDpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(designDpi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(renderDpi);
        return RenderLabelScaler.ToDeviceDots(dots, (double)renderDpi / designDpi, 1);
    }

    /// <summary>Turns a renderer issue (plain English, for the preview) into a translatable finding with stable arguments.</summary>
    private static ValidationIssue ToFinding(RenderIssue issue) => issue.Element switch
    {
        RenderBarcode barcode => new ValidationIssue(ValidationSeverity.Error, BarcodeNotDrawn, null,
            Enum.IsDefined(barcode.Symbology) ? BarcodeCatalog.Get(barcode.Symbology).DisplayName : barcode.Symbology.ToString(), barcode.Data),
        RenderImage => new ValidationIssue(ValidationSeverity.Error, ImageNotDrawn, null),
        _ => new ValidationIssue(ValidationSeverity.Error, ElementNotDrawn, null),
    };

    /// <summary>
    /// Copies the rendered colour bitmap into a tightly packed grey buffer. Skia converts the pixel format while it copies (luminance), so black
    /// stays exactly 0 and the white paper exactly 255.
    /// </summary>
    private static DevicePicture ToGray8(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var gray = new byte[width * height];
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        // Pinned so Skia can write straight into the managed array; row bytes = width means no padding, as DevicePicture promises.
        var handle = GCHandle.Alloc(gray, GCHandleType.Pinned);
        try
        {
            using var pixmap = bitmap.PeekPixels()
                ?? throw new InvalidOperationException("The rendered label picture could not be read.");
            if (!pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), width, 0, 0))
                throw new InvalidOperationException("The rendered label picture could not be converted to grey.");
        }
        finally
        {
            handle.Free();
        }
        return new DevicePicture(width, height, gray);
    }
}
