namespace Rotula.Printing;

/// <summary>
/// What a Windows printer driver reports about the page it will print a label on, read without printing anything (see
/// <see cref="IDriverPrinter.ProbeAsync"/>). The pre-flight compares it with the label, the picture renderer draws at its resolution, and the
/// print job checks that the driver still uses this page before it starts (<see cref="IDriverPrinter.PrintAsync"/>).
/// All dot values are device dots: horizontal ones at <paramref name="DpiX"/>, vertical ones at <paramref name="DpiY"/>.
/// <para>
/// The physical page and the printable area are both carried because they answer different questions. When the driver accepted the label
/// size, the label is drawn from the paper's corner, so whether it fits is a question about the physical page; the printable area inside it
/// only tells which strip along the edges the printer cannot reach (die-cut label drivers often report about 1.5 mm there).
/// </para>
/// </summary>
/// <param name="DpiX">Horizontal resolution of the driver in dots per inch, exactly as it reports it (any value, not only 203/300/600).</param>
/// <param name="DpiY">Vertical resolution in dots per inch. It can differ from <paramref name="DpiX"/> (for example 600 x 300).</param>
/// <param name="PhysicalWidthDots">Width of the whole sheet of paper (GDI PHYSICALWIDTH), in horizontal device dots. Never smaller than the printable width on a sane driver.</param>
/// <param name="PhysicalHeightDots">Height of the whole sheet of paper (GDI PHYSICALHEIGHT), in vertical device dots.</param>
/// <param name="PrintableWidthDots">Width of the area the driver can print on, in horizontal device dots.</param>
/// <param name="PrintableHeightDots">Height of the area the driver can print on, in vertical device dots.</param>
/// <param name="OffsetXDots">Unprintable margin on the left of the paper, in horizontal device dots.</param>
/// <param name="OffsetYDots">Unprintable margin at the top of the paper, in vertical device dots.</param>
/// <param name="CustomSizeAccepted">
/// True when the driver accepted a paper size equal to the label size. False means it kept its own selected paper, so the label is centred on
/// paper of another size.
/// </param>
public sealed record DriverMedia(int DpiX, int DpiY, int PhysicalWidthDots, int PhysicalHeightDots, int PrintableWidthDots, int PrintableHeightDots,
    int OffsetXDots, int OffsetYDots, bool CustomSizeAccepted);

/// <summary>
/// One label drawn as a picture for a printer driver: 8 bits of grey per pixel, row after row from the top, with no padding between rows.
/// A value of 0 is black and 255 is white (paper).
/// </summary>
/// <param name="WidthPx">Width in pixels.</param>
/// <param name="HeightPx">Height in pixels.</param>
/// <param name="Gray8">The pixels, <paramref name="WidthPx"/> times <paramref name="HeightPx"/> bytes.</param>
public sealed record DevicePicture(int WidthPx, int HeightPx, byte[] Gray8)
{
    /// <summary>
    /// The same picture turned a quarter turn clockwise: the old top row becomes the right-hand column, so the old top-left pixel ends at the
    /// top right. A new picture of <see cref="HeightPx"/> x <see cref="WidthPx"/> pixels. It moves pixels and changes none, which is the reason
    /// to turn the finished picture instead of drawing the turned label: every barcode module keeps its exact size in device dots (a module
    /// that is 3 pixels across is 3 pixels tall afterwards), and nothing is resampled or smoothed.
    /// </summary>
    /// <exception cref="ArgumentException">The picture's buffer is shorter than its size says.</exception>
    public DevicePicture TurnedClockwise()
    {
        int width = WidthPx, height = HeightPx;
        if (width <= 0 || height <= 0 || Gray8 is null || (long)width * height > Gray8.Length)
            throw new ArgumentException($"The picture {width} x {height} does not match its {Gray8?.Length ?? 0} grey bytes.");
        var turned = new byte[width * height];
        // Source (x, y) goes to column height-1-y, row x of a picture that is `height` wide: walking the source rows in order keeps the reads
        // sequential, and the writes jump by one turned row each, which is acceptable for a one-off picture per distinct label.
        for (int y = 0; y < height; y++)
        {
            int from = y * width, column = height - 1 - y;
            for (int x = 0; x < width; x++) turned[x * height + column] = Gray8[from + x];
        }
        return new DevicePicture(height, width, turned);
    }
}

/// <summary>
/// One label of a batch whose variable values were already worked out (data merge, counters, dates), so printing draws exactly these values
/// and never resolves them again (a second resolution would hand out a different serial number).
/// </summary>
/// <param name="Values">The values of this label, or null for a label without variables.</param>
/// <param name="Copies">How many identical copies of this label to print.</param>
public sealed record ResolvedLabel(IReadOnlyDictionary<string, string>? Values, int Copies);

/// <summary>
/// The result of one driver print job plus how many pages the driver really took. The page count is what keeps serial numbers honest
/// after a failure or a cancel: a page the driver accepted may have printed, so its number is never issued again.
/// </summary>
/// <param name="Result">Success, or the failure with its stable error kind.</param>
/// <param name="PagesSubmitted">Pages the driver accepted before the job ended (all of them on success).</param>
public sealed record DriverPrintOutcome(PrintResult Result, int PagesSubmitted);

/// <summary>
/// The seam between the print service and the Windows print system for picture printing. The real implementation talks to GDI; tests use a fake.
/// Printer problems come back as <see cref="PrintResult"/> values, never as exceptions.
/// </summary>
public interface IDriverPrinter
{
    /// <summary>
    /// Asks the driver, without printing, which resolution and printable area it would use for a page of the label's size, and whether it
    /// accepts that size as a custom paper size.
    /// </summary>
    /// <param name="printerName">The Windows printer name.</param>
    /// <param name="labelWidthMm">Label width in millimetres.</param>
    /// <param name="labelHeightMm">Label height in millimetres.</param>
    /// <param name="ct">Cancels the probe.</param>
    /// <returns>The media, or null when the printer or its driver cannot be reached.</returns>
    Task<DriverMedia?> ProbeAsync(string printerName, double labelWidthMm, double labelHeightMm, CancellationToken ct);

    /// <summary>
    /// Prints <paramref name="pages"/> as one print job, one picture per page, each drawn at the label's physical size.
    /// <para>
    /// Before the job starts, the implementation compares the page the driver will really print on with <paramref name="media"/>, the media the
    /// pre-flight judged and the pictures were drawn for. When the resolution differs, or the probe said the label size was accepted and the
    /// page is no longer that size, the job is refused with <see cref="PrintErrorKind.DriverRefused"/> and no page: the findings the person
    /// accepted, and the picture itself, belong to the probed page, and printing on another one would centre, clip or stretch the label without
    /// a word.
    /// </para>
    /// </summary>
    /// <param name="printerName">The Windows printer name.</param>
    /// <param name="documentName">Name of the job in the Windows print queue.</param>
    /// <param name="labelWidthMm">
    /// Label width in millimetres (the size each picture is printed at). For a label turned to fit sideways stock this is the turned label's
    /// width (the designed height): the same size the media was probed for, which is what the check before the job compares with.
    /// </param>
    /// <param name="labelHeightMm">Label height in millimetres (the turned label's height when it is turned).</param>
    /// <param name="media">The media <see cref="ProbeAsync"/> reported and the pre-flight judged; required.</param>
    /// <param name="pages">
    /// The pictures, produced lazily so a long run never holds every picture in memory at once. The implementation enumerates it exactly once.
    /// </param>
    /// <param name="ct">Cancels the job between pages; pages already accepted by the driver stay counted.</param>
    Task<DriverPrintOutcome> PrintAsync(string printerName, string documentName, double labelWidthMm, double labelHeightMm, DriverMedia media,
        IEnumerable<DevicePicture> pages, CancellationToken ct);
}
