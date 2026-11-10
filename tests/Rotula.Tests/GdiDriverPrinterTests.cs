using System.Runtime.InteropServices;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// A [Fact] that runs only when the Windows printer "Microsoft Print to PDF" is installed. xunit 2.9 has no dynamic skip, so the attribute sets
/// <see cref="FactAttribute.Skip"/> in its constructor: a machine without the printer reports these tests as skipped, never as silently passed.
/// The printer is looked up through the same discovery the app uses (<see cref="WinspoolRawPrinter"/> as <see cref="IPrinterDiscovery"/>).
/// </summary>
internal sealed class RequiresPdfPrinterFactAttribute : FactAttribute
{
    /// <summary>The Windows queue name of the PDF printer every Windows 10/11 installation ships with (it can be removed).</summary>
    public const string PrinterName = "Microsoft Print to PDF";

    private static readonly Lazy<bool> Installed = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return false;
        IPrinterDiscovery discovery = new WinspoolRawPrinter();
        var printers = discovery.GetPrintersAsync().GetAwaiter().GetResult();
        return printers.Any(p => string.Equals(p.Name, PrinterName, StringComparison.OrdinalIgnoreCase));
    });

    public RequiresPdfPrinterFactAttribute()
    {
        if (!Installed.Value) Skip = "Microsoft Print to PDF is not installed";
    }
}

/// <summary>
/// The GDI driver printer: probing the driver without printing, printing pictures as pages, and what it reports when the printer is missing or
/// the job is cancelled. These tests never print to a physical printer: they use only "Microsoft Print to PDF" with an output file in the temp
/// folder (passed as <c>DOCINFO.lpszOutput</c>, so no Save As dialog appears), or a printer name that does not exist.
/// </summary>
public class GdiDriverPrinterTests
{
    private const string Pdf = RequiresPdfPrinterFactAttribute.PrinterName;

    /// <summary>A picture with a black frame and a black bar, so a driver that drops the bits would produce a visibly empty page.</summary>
    private static DevicePicture Picture(int width, int height)
    {
        var gray = new byte[width * height];
        Array.Fill(gray, (byte)255);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool frame = x < 4 || y < 4 || x >= width - 4 || y >= height - 4;
            bool bar = x >= width / 4 && x < width / 4 + 12;
            if (frame || bar) gray[y * width + x] = 0;
        }
        return new DevicePicture(width, height, gray);
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), "rotula-gdi-test-" + Guid.NewGuid().ToString("N") + ".pdf");

    /// <summary>
    /// Probes the PDF printer for a label of this size, as the pre-flight does before every print: a job is always printed against the media
    /// its findings were worked out for.
    /// </summary>
    private static async Task<DriverMedia> ProbePdfAsync(double widthMm, double heightMm)
    {
        var media = await new GdiDriverPrinter().ProbeAsync(Pdf, widthMm, heightMm, CancellationToken.None);
        Assert.NotNull(media);
        return media;
    }

    /// <summary>Media for a printer that does not exist: the job fails at OpenPrinter before the media is ever compared.</summary>
    private static readonly DriverMedia AnyMedia = new(300, 300, 591, 295, 591, 295, 0, 0, true);

    /// <summary>
    /// The spooler writes the PDF after <c>EndDoc</c> returns, so the file appears a moment later. Waits until its first bytes can be read
    /// (shared, because the spooler may still hold it open) or the time runs out.
    /// </summary>
    private static byte[]? WaitForFileStart(string path, int count, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length >= count)
                    {
                        var head = new byte[count];
                        stream.ReadExactly(head);
                        return head;
                    }
                }
            }
            catch (IOException)
            {
                // Still being created by the spooler: try again shortly.
            }
            Thread.Sleep(200);
        }
        return null;
    }

    /// <summary>Deletes a test output file, retrying for a short while because an aborted job can leave the spooler holding it.</summary>
    private static void DeleteWithRetries(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }

    /// <summary>A lazy page source that records how it was consumed, to prove the printer enumerates exactly once and stops when told.</summary>
    private sealed class CountingPages(IReadOnlyList<DevicePicture> pictures, Action<int>? beforeYield = null) : IEnumerable<DevicePicture>
    {
        public int Enumerations { get; private set; }
        public int Pulled { get; private set; }
        public bool Disposed { get; private set; }

        public IEnumerator<DevicePicture> GetEnumerator()
        {
            Enumerations++;
            return Produce();
        }

        private IEnumerator<DevicePicture> Produce()
        {
            try
            {
                for (int i = 0; i < pictures.Count; i++)
                {
                    beforeYield?.Invoke(i);
                    Pulled++;
                    yield return pictures[i];
                }
            }
            finally
            {
                Disposed = true;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [RequiresPdfPrinterFact]
    public async Task ProbeReportsRealResolution()
    {
        var media = await new GdiDriverPrinter().ProbeAsync(Pdf, 100, 50, CancellationToken.None);

        Assert.NotNull(media);
        Assert.True(media.DpiX > 0, "DpiX " + media.DpiX);
        Assert.True(media.DpiY > 0, "DpiY " + media.DpiY);
        Assert.True(media.PrintableWidthDots > 0, "PrintableWidthDots " + media.PrintableWidthDots);
        Assert.True(media.PrintableHeightDots > 0, "PrintableHeightDots " + media.PrintableHeightDots);
        Assert.True(media.OffsetXDots >= 0 && media.OffsetYDots >= 0);
        // The physical page is reported too (the pre-flight judges an accepted label against it), and it holds the printable area.
        Assert.True(media.PhysicalWidthDots >= media.PrintableWidthDots, $"PhysicalWidthDots {media.PhysicalWidthDots} < {media.PrintableWidthDots}");
        Assert.True(media.PhysicalHeightDots >= media.PrintableHeightDots, $"PhysicalHeightDots {media.PhysicalHeightDots} < {media.PrintableHeightDots}");
    }

    [RequiresPdfPrinterFact]
    public async Task ALabelTheSizeOfADriverFormGetsALabelSizedPage()
    {
        // "Microsoft Print to PDF" ignores a user-defined paper size but has an A4 form. On a Letter-default machine this goes through the whole
        // negotiation (user size refused, A4 form found and validated); on an A4-default machine the driver's own page already is the label.
        var media = await new GdiDriverPrinter().ProbeAsync(Pdf, 210, 297, CancellationToken.None);

        Assert.NotNull(media);
        Assert.True(media.CustomSizeAccepted);
        // The page really is 210 x 297 mm at the reported resolution (within the 1 mm tolerance), and the PDF printer has no margins.
        Assert.InRange(media.PrintableWidthDots + media.OffsetXDots * 2, 209 / 25.4 * media.DpiX, 211 / 25.4 * media.DpiX);
        Assert.InRange(media.PrintableHeightDots + media.OffsetYDots * 2, 296 / 25.4 * media.DpiY, 298 / 25.4 * media.DpiY);
        Assert.InRange(media.PhysicalWidthDots, 209 / 25.4 * media.DpiX, 211 / 25.4 * media.DpiX);
        Assert.InRange(media.PhysicalHeightDots, 296 / 25.4 * media.DpiY, 298 / 25.4 * media.DpiY);
    }

    [RequiresPdfPrinterFact]
    public async Task ALabelSizeTheDriverCannotMakeIsReportedAsRefused()
    {
        // No PDF printer form is 100 x 50 mm and it ignores user sizes (it returns success and silently keeps its own paper, which only the
        // physical-page check catches): the label will be centred on the driver's paper, and the probe says so. This relies on that driver
        // behaviour as of Windows 11; if a later version accepts user sizes, pick a size it still refuses.
        var media = await new GdiDriverPrinter().ProbeAsync(Pdf, 100, 50, CancellationToken.None);

        Assert.NotNull(media);
        Assert.False(media.CustomSizeAccepted);
    }

    [RequiresPdfPrinterFact]
    public async Task PrintsOnePageToAPdfFile()
    {
        string output = TempPdfPath();
        try
        {
            // Odd widths: the picture rows are not a multiple of 4 bytes, so the printer must pad each row for the DIB.
            var pages = new CountingPages([Picture(401, 203), Picture(401, 203)]);
            var media = await ProbePdfAsync(50, 25);

            var outcome = await new GdiDriverPrinter(output).PrintAsync(Pdf, "Rotula test", 50, 25, media, pages, CancellationToken.None);

            Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Kind + " " + string.Join(" | ", outcome.Result.Error?.Args ?? []));
            Assert.Equal(2, outcome.PagesSubmitted);
            Assert.Equal(2, outcome.Result.PagesSent);
            Assert.True(outcome.Result.BytesSent > 0);
            // Lazy and exactly once: one enumeration, two pictures pulled, the producer's finally ran.
            Assert.Equal(1, pages.Enumerations);
            Assert.Equal(2, pages.Pulled);
            Assert.True(pages.Disposed);

            var head = WaitForFileStart(output, 4, TimeSpan.FromSeconds(60));
            Assert.NotNull(head);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(head));
        }
        finally
        {
            DeleteWithRetries(output);
        }
    }

    [Fact]
    public async Task UnknownPrinterIsPrinterNotFound()
    {
        var pages = new CountingPages([Picture(40, 20)]);

        var outcome = await new GdiDriverPrinter().PrintAsync("No such printer 123", "Rotula test", 50, 25, AnyMedia, pages, CancellationToken.None);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(PrintErrorKind.PrinterNotFound, outcome.Result.Error!.Kind);
        Assert.Equal("No such printer 123", outcome.Result.Error.Args[0]);
        Assert.Equal(0, outcome.PagesSubmitted);
        Assert.Equal(0, outcome.Result.BytesSent);
        // Nothing was drawn for a printer that does not exist.
        Assert.Equal(0, pages.Enumerations);
    }

    [Fact]
    public async Task UnknownPrinterProbeIsNull()
    {
        Assert.Null(await new GdiDriverPrinter().ProbeAsync("No such printer 123", 100, 50, CancellationToken.None));
    }

    [RequiresPdfPrinterFact]
    public async Task CancelBeforeFirstPageAborts()
    {
        string output = TempPdfPath();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var pages = new CountingPages([Picture(40, 20)]);
            var media = await ProbePdfAsync(50, 25);

            // A cancelled token never throws: the outcome says Cancelled, as every printer problem comes back as a value.
            var outcome = await new GdiDriverPrinter(output).PrintAsync(Pdf, "Rotula test", 50, 25, media, pages, cts.Token);

            Assert.False(outcome.Result.Succeeded);
            Assert.Equal(PrintErrorKind.Cancelled, outcome.Result.Error!.Kind);
            Assert.Equal(0, outcome.PagesSubmitted);
            Assert.Equal(0, pages.Pulled);
        }
        finally
        {
            DeleteWithRetries(output);
        }
    }

    [RequiresPdfPrinterFact]
    public async Task CancelDuringTheJobCountsTheAcceptedPages()
    {
        string output = TempPdfPath();
        try
        {
            using var cts = new CancellationTokenSource();
            // The person cancels while the second picture is being drawn: the first page was accepted by the driver and stays counted, the
            // second is never submitted and the third is never drawn.
            var pages = new CountingPages([Picture(40, 20), Picture(40, 20), Picture(40, 20)], i => { if (i == 1) cts.Cancel(); });
            var media = await ProbePdfAsync(50, 25);

            var outcome = await new GdiDriverPrinter(output).PrintAsync(Pdf, "Rotula test", 50, 25, media, pages, cts.Token);

            Assert.Equal(PrintErrorKind.Cancelled, outcome.Result.Error!.Kind);
            Assert.Equal(1, outcome.PagesSubmitted);
            Assert.Equal(1, outcome.Result.PagesSent);
            // A page that may have printed keeps BytesSent above zero, which is how the serial-number logic knows something went out.
            Assert.True(outcome.Result.BytesSent > 0);
            Assert.Equal(2, pages.Pulled);
            Assert.True(pages.Disposed);
        }
        finally
        {
            DeleteWithRetries(output);
        }
    }

    [RequiresPdfPrinterFact]
    public async Task AFailingPageSourceAbortsTheJobAndPropagates()
    {
        string output = TempPdfPath();
        try
        {
            // A picture that cannot be drawn (Task 4's DevicePictureException) is thrown from inside the lazy page source. It is not a printer
            // problem, so it reaches the caller unchanged, after the job was aborted and the device context deleted.
            var pages = new CountingPages([Picture(40, 20), Picture(40, 20)],
                i => { if (i == 1) throw new DevicePictureException([], "test: the second label cannot be drawn"); });
            var media = await ProbePdfAsync(50, 25);

            await Assert.ThrowsAsync<DevicePictureException>(() =>
                new GdiDriverPrinter(output).PrintAsync(Pdf, "Rotula test", 50, 25, media, pages, CancellationToken.None));
            Assert.True(pages.Disposed);
        }
        finally
        {
            DeleteWithRetries(output);
        }
    }

    [RequiresPdfPrinterFact]
    public async Task AnAcceptedPagePrintsAgainstTheMediaItsProbeReported()
    {
        string output = TempPdfPath();
        try
        {
            // The success case of the probe-against-print check: the PDF printer accepts an A4 label as its page, and the job printed with exactly
            // the media that probe reported goes through (its device context still has that resolution and that page).
            var media = await ProbePdfAsync(210, 297);
            Assert.True(media.CustomSizeAccepted);
            var pages = new CountingPages([Picture(40, 20)]);

            var outcome = await new GdiDriverPrinter(output).PrintAsync(Pdf, "Rotula test", 210, 297, media, pages, CancellationToken.None);

            Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Kind + " " + string.Join(" | ", outcome.Result.Error?.Args ?? []));
            Assert.Equal(1, outcome.PagesSubmitted);
            Assert.Equal(1, pages.Pulled);
            var head = WaitForFileStart(output, 4, TimeSpan.FromSeconds(60));
            Assert.NotNull(head);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(head));
        }
        finally
        {
            DeleteWithRetries(output);
        }
    }

    [RequiresPdfPrinterFact]
    public async Task AJobWhoseResolutionDiffersFromTheProbeIsRefusedBeforeAnythingIsSent()
    {
        string output = TempPdfPath();
        try
        {
            // The pre-flight judged (and the picture was drawn for) another resolution than the one the device context now reports, as when
            // the driver's defaults changed between the check and the print. Nothing may be drawn, started or sent.
            var probed = await ProbePdfAsync(50, 25);
            var stale = probed with { DpiX = probed.DpiX * 2, DpiY = probed.DpiY * 2 };
            var pages = new CountingPages([Picture(40, 20)]);

            var outcome = await new GdiDriverPrinter(output).PrintAsync(Pdf, "Rotula test", 50, 25, stale, pages, CancellationToken.None);

            Assert.Equal(PrintErrorKind.DriverRefused, outcome.Result.Error!.Kind);
            Assert.Equal(Pdf, outcome.Result.Error.Args[0]);
            Assert.Contains("resolution", outcome.Result.Error.Args[1]);
            Assert.Equal(0, outcome.PagesSubmitted);
            Assert.Equal(0, outcome.Result.PagesSent);
            Assert.Equal(0, outcome.Result.BytesSent);
            // Refused before StartDoc: the page source was never asked for a picture, so no job reached the spooler.
            Assert.Equal(0, pages.Enumerations);
            Assert.False(File.Exists(output));
        }
        finally
        {
            DeleteWithRetries(output);
        }
    }

    [RequiresPdfPrinterFact]
    public async Task AnAcceptedPageThatChangedSinceTheProbeIsRefused()
    {
        string output = TempPdfPath();
        try
        {
            // The probe said the driver accepted an A4 label as its page (the PDF printer has an A4 form). Had the device context fallen back to
            // another paper, the label would be centred or cut off without a word: the job refuses instead. Simulated by a probe that recorded a
            // page 20 mm narrower than the one the device context really has.
            var probed = await ProbePdfAsync(210, 297);
            Assert.True(probed.CustomSizeAccepted);
            var stale = probed with { PhysicalWidthDots = probed.PhysicalWidthDots - (int)Math.Round(20 / 25.4 * probed.DpiX) };
            var pages = new CountingPages([Picture(40, 20)]);

            var outcome = await new GdiDriverPrinter(output).PrintAsync(Pdf, "Rotula test", 210, 297, stale, pages, CancellationToken.None);

            Assert.Equal(PrintErrorKind.DriverRefused, outcome.Result.Error!.Kind);
            Assert.Contains("page", outcome.Result.Error.Args[1]);
            Assert.Equal(0, outcome.PagesSubmitted);
            Assert.Equal(0, pages.Enumerations);
        }
        finally
        {
            DeleteWithRetries(output);
        }
    }

    [Fact]
    public async Task TheMediaIsRequired()
    {
        // A job without the media the pre-flight judged could not check the driver's page: a programming error, not a printer problem.
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new GdiDriverPrinter().PrintAsync("No such printer 123", "Rotula test", 50, 25, null!, new CountingPages([Picture(40, 20)]), CancellationToken.None));
    }

    // ---- Page geometry (pure, no printer) ----

    /// <summary>A 300 dpi label printer whose page is exactly 100 x 50 mm, with a small unprintable margin.</summary>
    private static GdiDeviceGeometry LabelPage300 => new(
        DpiX: 300, DpiY: 300, HorzRes: 1157, VertRes: 571, PhysicalWidth: 1181, PhysicalHeight: 591, OffsetX: 12, OffsetY: 10);

    /// <summary>A 600 dpi office printer on Letter paper (8.5 x 11 in) with quarter-inch margins.</summary>
    private static GdiDeviceGeometry Letter600 => new(
        DpiX: 600, DpiY: 600, HorzRes: 4800, VertRes: 6300, PhysicalWidth: 5100, PhysicalHeight: 6600, OffsetX: 150, OffsetY: 150);

    [Fact]
    public void ALabelSizedPageIsDrawnFromThePaperCorner()
    {
        // 100 x 50 mm at 300 dpi is 1181 x 591 dots. GDI coordinates start at the printable area, so the paper corner is at minus the offset.
        var place = GdiPageLayout.Place(LabelPage300, 100, 50, 1181, 591);

        Assert.True(GdiPageLayout.PageMatchesLabel(LabelPage300, 100, 50));
        Assert.Equal(new GdiPagePlacement(-12, -10, 1181, 591), place);
    }

    [Fact]
    public void ARefusedSizeIsCentredInThePrintableArea()
    {
        var place = GdiPageLayout.Place(Letter600, 100, 50, 2362, 1181);

        Assert.False(GdiPageLayout.PageMatchesLabel(Letter600, 100, 50));
        Assert.Equal(2362, place.Width);
        Assert.Equal(1181, place.Height);
        Assert.Equal((4800 - 2362) / 2, place.X);
        Assert.Equal((6300 - 1181) / 2, place.Y);
    }

    [Fact]
    public void APictureOneDotOffTheMillimetreSizeIsDrawnOneToOne()
    {
        // The picture size comes from the label rounded to design dots and then to device dots, so it can differ from the millimetre size by a
        // dot. Stretching 1181 pixels to 1182 dots would double one column somewhere, possibly inside a barcode; the picture is drawn 1:1 instead.
        var place = GdiPageLayout.Place(LabelPage300, 100, 50, 1180, 592);

        Assert.Equal(1180, place.Width);
        Assert.Equal(592, place.Height);
    }

    [Fact]
    public void AWholeAxisRatioStretchesByAWholeFactor()
    {
        // 600 x 300 dpi: the picture is drawn at 300 dpi (the coarser axis), so every pixel becomes exactly two dots across and one dot down.
        var geometry = new GdiDeviceGeometry(600, 300, 2362, 591, 2362, 591, 0, 0);

        var place = GdiPageLayout.Place(geometry, 100, 50, 1181, 591);

        Assert.Equal(new GdiPagePlacement(0, 0, 2362, 591), place);
    }

    // ---- Sideways stock: the job is for the turned label

    /// <summary>A 300 dpi roll printer whose page is 50 x 100 mm in portrait (591 x 1181 dots), printable to its edges.</summary>
    private static GdiDeviceGeometry Portrait300 => new(300, 300, 591, 1181, 591, 1181, 0, 0);

    [Fact]
    public void ATurnedLabelIsPlacedAndCheckedAtItsTurnedSize()
    {
        // Designed 100 x 50 mm (landscape), turned to 50 x 100 to lie on this page: the driver is asked for, the page is compared with, and the
        // picture is placed at the turned size, so the turned picture (591 x 1181) covers the page from its corner.
        var probed = ProbedFrom(Portrait300, true);
        Assert.Equal(new GdiPagePlacement(0, 0, 591, 1181), GdiPageLayout.Place(Portrait300, 50, 100, 591, 1181));
        Assert.Null(GdiPageLayout.DescribeChange(Portrait300, probed, 50, 100));
        // Compared with the designed size it would read as "the page is no longer the label's size": the job must be given the turned size.
        Assert.NotNull(GdiPageLayout.DescribeChange(Portrait300, probed, 100, 50));
    }
    // ---- Probe and print agree (pure, no printer)

    /// <summary>The media a probe reports for this device page.</summary>
    private static DriverMedia ProbedFrom(GdiDeviceGeometry g, bool customSizeAccepted) =>
        new(g.DpiX, g.DpiY, g.PhysicalWidth, g.PhysicalHeight, g.HorzRes, g.VertRes, g.OffsetX, g.OffsetY, customSizeAccepted);

    [Fact]
    public void TheDevicePageThePreflightJudgedIsNoChange()
    {
        Assert.Null(GdiPageLayout.DescribeChange(LabelPage300, ProbedFrom(LabelPage300, true), 100, 50));
        Assert.Null(GdiPageLayout.DescribeChange(Letter600, ProbedFrom(Letter600, false), 100, 50));
    }

    [Theory]
    [InlineData(600, 300)]
    [InlineData(300, 600)]
    [InlineData(600, 600)]
    public void AnotherResolutionIsAChangeOnEitherAxis(int dpiX, int dpiY)
    {
        // The picture was drawn at the coarser probed axis; at any other device resolution its bars would no longer land on whole dots, or the
        // destination would be stretched to millimetres. Refused also when the probe said the size was refused: the resolution matters either way.
        var device = LabelPage300 with { DpiX = dpiX, DpiY = dpiY };

        foreach (bool accepted in new[] { true, false })
        {
            string? change = GdiPageLayout.DescribeChange(device, ProbedFrom(LabelPage300, accepted), 100, 50);
            Assert.NotNull(change);
            Assert.Contains("resolution", change);
            Assert.Contains("300 x 300 dpi", change);
            Assert.Contains($"{dpiX} x {dpiY} dpi", change);
        }
    }

    [Fact]
    public void AnotherPageWhileTheProbeSaidAcceptedIsAChange()
    {
        // The probe said the page is the label; the device context fell back to Letter at the same resolution. Place would centre the label.
        var letter300 = new GdiDeviceGeometry(300, 300, 2400, 3150, 2550, 3300, 75, 75);

        string? change = GdiPageLayout.DescribeChange(letter300, ProbedFrom(LabelPage300, true), 100, 50);

        Assert.NotNull(change);
        Assert.Contains("page", change);
        Assert.Contains("100 x 50 mm", change);
        Assert.Contains("215.9 x 279.4 mm", change);
    }

    [Fact]
    public void AnotherPageWhileTheProbeSaidRefusedIsNoChange()
    {
        // A refused size is centred on whatever paper the driver uses, and the pre-flight already warned about that; only the resolution counts.
        var a4At600 = new GdiDeviceGeometry(600, 600, 4760, 6814, 4960, 7014, 100, 100);

        Assert.Null(GdiPageLayout.DescribeChange(a4At600, ProbedFrom(Letter600, false), 100, 50));
    }

    [Fact]
    public void APageWithinTheToleranceIsNoChange()
    {
        // 5 dots at 300 dpi is 0.4 mm: the rounding of a driver's paper sizes, not another paper.
        var device = LabelPage300 with { PhysicalWidth = LabelPage300.PhysicalWidth + 5, PhysicalHeight = LabelPage300.PhysicalHeight - 5 };

        Assert.Null(GdiPageLayout.DescribeChange(device, ProbedFrom(LabelPage300, true), 100, 50));
    }

    [Fact]
    public void APageThatDriftedOffTheLabelSizeIsAChangeEvenWithinTheToleranceOfTheProbe()
    {
        // Probed page 1192 dots (100.9 mm) for a 100 mm label: accepted. The device page is 1203 dots (101.9 mm): within 1 mm of the probe, but
        // 1.9 mm off the label, so Place would no longer draw it from the paper's corner. Two tolerances must not add up to a silent centring.
        var probed = LabelPage300 with { PhysicalWidth = 1192 };
        var device = LabelPage300 with { PhysicalWidth = 1203 };
        Assert.True(GdiPageLayout.PageMatchesLabel(probed, 100, 50));
        Assert.False(GdiPageLayout.PageMatchesLabel(device, 100, 50));

        string? change = GdiPageLayout.DescribeChange(device, ProbedFrom(probed, true), 100, 50);

        Assert.NotNull(change);
        Assert.Contains("page", change);
    }

    [Fact]
    public void APictureForAnotherResolutionIsStretchedToTheLabelSize()
    {
        // A picture clearly not drawn for this device (half the dots) is still printed at the label's physical size: the device stretches it.
        var place = GdiPageLayout.Place(LabelPage300, 100, 50, 590, 295);

        Assert.Equal(1181, place.Width);
        Assert.Equal(591, place.Height);
    }

    // ---- Native layouts (no printer) ----

    [Fact]
    public void NativeStructuresHaveTheWindowsLayout()
    {
        // BITMAPINFOHEADER is 40 bytes on every architecture; DOCINFOW is an int, three pointers and a DWORD with natural alignment.
        Assert.Equal(40, Marshal.SizeOf<GdiNative.BITMAPINFOHEADER>());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 20, Marshal.SizeOf<GdiNative.DOCINFOW>());

        var info = GdiBitmapInfo.CreateGray8();
        Assert.Equal(40 + 256 * 4, info.Length);
        GdiBitmapInfo.SetSize(info, 401, 203);
        var header = MemoryMarshal.Read<GdiNative.BITMAPINFOHEADER>(info);
        Assert.Equal(40u, header.biSize);
        Assert.Equal(401, header.biWidth);
        Assert.Equal(-203, header.biHeight); // negative: top-down rows, as DevicePicture stores them
        Assert.Equal((ushort)8, header.biBitCount);
        Assert.Equal(256u, header.biClrUsed);
        // Palette entry i is the grey i (blue, green, red, reserved): 0 black, 255 white.
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, info[40..44]);
        Assert.Equal(new byte[] { 128, 128, 128, 0 }, info[(40 + 128 * 4)..(40 + 128 * 4 + 4)]);
        Assert.Equal(new byte[] { 255, 255, 255, 0 }, info[^4..]);
    }

    [Fact]
    public void RowsArePaddedToFourBytesForTheDib()
    {
        var picture = new DevicePicture(3, 2, [1, 2, 3, 4, 5, 6]);
        byte[]? scratch = null;

        var rows = GdiBitmapInfo.DibRows(picture, ref scratch);

        Assert.Equal(new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 }, rows);
        // A width that is already a multiple of four is passed through without a copy.
        var aligned = new DevicePicture(4, 1, [9, 8, 7, 6]);
        Assert.Same(aligned.Gray8, GdiBitmapInfo.DibRows(aligned, ref scratch));
    }

    /// <summary>
    /// A DEVMODEW as "Microsoft Print to PDF" returns it (dumped on a real machine): public part of 220 bytes, Letter (form 1, form name
    /// "Letter"), here with 3 copies and landscape so every field the request must change starts out different.
    /// </summary>
    private static GdiDevMode LetterDevMode()
    {
        var devMode = GdiDevMode.Allocate(220 + 16);
        devMode.WriteUInt16(GdiDevMode.OffSize, 220);
        devMode.WriteUInt32(GdiDevMode.OffFields,
            GdiNative.DM_PAPERSIZE | GdiNative.DM_PAPERWIDTH | GdiNative.DM_PAPERLENGTH | GdiNative.DM_COPIES | GdiNative.DM_ORIENTATION | GdiNative.DM_FORMNAME);
        devMode.WriteInt16(GdiDevMode.OffPaperSize, 1);
        devMode.WriteInt16(GdiDevMode.OffPaperWidth, 2159);
        devMode.WriteInt16(GdiDevMode.OffPaperLength, 2794);
        devMode.WriteInt16(GdiDevMode.OffCopies, 3);
        devMode.WriteInt16(GdiDevMode.OffOrientation, 2);
        devMode.WriteInt16(GdiDevMode.OffFormName, (short)'L');
        return devMode;
    }

    [Fact]
    public void ACustomPaperRequestSetsTheUserSizeOneCopyAndPortrait()
    {
        using var devMode = LetterDevMode();

        Assert.True(devMode.TryRequestPaper(1000, 500));

        uint fields = devMode.ReadUInt32(GdiDevMode.OffFields);
        // The stale form name is dropped (flag and text), so a driver that resolves paper by name cannot keep Letter.
        Assert.Equal(GdiNative.DM_ORIENTATION | GdiNative.DM_PAPERSIZE | GdiNative.DM_PAPERLENGTH | GdiNative.DM_PAPERWIDTH | GdiNative.DM_COPIES, fields);
        Assert.Equal(0, devMode.ReadInt16(GdiDevMode.OffFormName));
        Assert.Equal(GdiNative.DMPAPER_USER, devMode.ReadInt16(GdiDevMode.OffPaperSize));
        Assert.Equal(1000, devMode.ReadInt16(GdiDevMode.OffPaperWidth));
        Assert.Equal(500, devMode.ReadInt16(GdiDevMode.OffPaperLength));
        Assert.Equal(1, devMode.ReadInt16(GdiDevMode.OffCopies));
        Assert.Equal(GdiNative.DMORIENT_PORTRAIT, devMode.ReadInt16(GdiDevMode.OffOrientation));
        Assert.True(devMode.PaperIs(1000, 500));
        Assert.False(devMode.PaperIs(1016, 500));
    }

    [Fact]
    public void AFormRequestSelectsTheFormByIndexOneCopyAndPortrait()
    {
        using var devMode = LetterDevMode();

        Assert.True(devMode.TryRequestForm(9)); // DMPAPER_A4

        uint fields = devMode.ReadUInt32(GdiDevMode.OffFields);
        // Width and length would override dmPaperSize (Learn), and they still hold Letter's values: their flags must go.
        Assert.Equal(GdiNative.DM_ORIENTATION | GdiNative.DM_PAPERSIZE | GdiNative.DM_COPIES, fields);
        Assert.False(devMode.ReportsPaperSize);
        Assert.Equal(9, devMode.ReadInt16(GdiDevMode.OffPaperSize));
        Assert.Equal(0, devMode.ReadInt16(GdiDevMode.OffFormName));
        Assert.Equal(1, devMode.ReadInt16(GdiDevMode.OffCopies));
        Assert.Equal(GdiNative.DMORIENT_PORTRAIT, devMode.ReadInt16(GdiDevMode.OffOrientation));
        Assert.False(devMode.TryRequestForm(0));
    }

    [Fact]
    public void TheLastResortSettingsNeverPrintMoreThanOneCopy()
    {
        // The driver refused every change, so its own settings are used as they are: here a default of 5 copies on Letter. The paper stays the
        // driver's, but the copy count is written directly, so one page is still one label for the serial-number count.
        using var devMode = LetterDevMode();
        devMode.WriteInt16(GdiDevMode.OffCopies, 5);
        uint fieldsBefore = devMode.ReadUInt32(GdiDevMode.OffFields);

        var result = GdiDriverPrinter.WithoutExtraCopies(devMode);

        Assert.Same(devMode, result);
        Assert.Equal((short)1, result.Copies);
        Assert.Equal(fieldsBefore | GdiNative.DM_COPIES, result.ReadUInt32(GdiDevMode.OffFields));
        // Nothing else is touched: still the driver's Letter paper and form name.
        Assert.Equal(1, result.ReadInt16(GdiDevMode.OffPaperSize));
        Assert.Equal(2159, result.ReadInt16(GdiDevMode.OffPaperWidth));
        Assert.Equal(2794, result.ReadInt16(GdiDevMode.OffPaperLength));
        Assert.Equal((short)'L', result.ReadInt16(GdiDevMode.OffFormName));
    }

    [Fact]
    public void ACopyCountIsForcedEvenWhenItsFlagIsMissing()
    {
        // A stray count with DM_COPIES cleared is forced as well (the flag is set with it), and settings that already say one copy are left
        // exactly as the driver validated them.
        using var stray = LetterDevMode();
        stray.WriteInt16(GdiDevMode.OffCopies, 2);
        stray.WriteUInt32(GdiDevMode.OffFields, stray.ReadUInt32(GdiDevMode.OffFields) & ~GdiNative.DM_COPIES);
        Assert.Equal((short)1, GdiDriverPrinter.WithoutExtraCopies(stray).Copies);
        Assert.NotEqual(0u, stray.ReadUInt32(GdiDevMode.OffFields) & GdiNative.DM_COPIES);

        using var single = LetterDevMode();
        single.WriteInt16(GdiDevMode.OffCopies, 1);
        single.WriteUInt32(GdiDevMode.OffFields, single.ReadUInt32(GdiDevMode.OffFields) & ~GdiNative.DM_COPIES);
        uint fields = single.ReadUInt32(GdiDevMode.OffFields);
        GdiDriverPrinter.WithoutExtraCopies(single);
        Assert.Equal(fields, single.ReadUInt32(GdiDevMode.OffFields));

        // A public part that ends before dmCopies has no copy count to force.
        using var tiny = GdiDevMode.Allocate(80);
        tiny.WriteUInt16(GdiDevMode.OffSize, 72);
        Assert.Null(GdiDriverPrinter.WithoutExtraCopies(tiny).Copies);
    }

    [Fact]
    public void APaperSizeOutsideTheDevModeRangeIsNotRequested()
    {
        using var devMode = GdiDevMode.Allocate(220);
        devMode.WriteUInt16(GdiDevMode.OffSize, 220);

        // dmPaperWidth is a short in tenths of a millimetre: more than 3276.7 mm cannot be asked for.
        Assert.False(devMode.TryRequestPaper(40000, 500));
        // A driver returning a public part too short to hold the paper fields cannot be edited either.
        using var tiny = GdiDevMode.Allocate(80);
        tiny.WriteUInt16(GdiDevMode.OffSize, 72);
        Assert.False(tiny.TryRequestPaper(1000, 500));
    }
}
