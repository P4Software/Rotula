using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Rotula.Printing;

/// <summary>
/// The real <see cref="IDriverPrinter"/>: prints label pictures through any Windows printer driver with GDI, and asks the driver which page it
/// would use without printing anything.
/// <para>
/// <b>Paper.</b> Every probe and every job first asks the driver for a page of exactly the label's size: a user-defined paper size in portrait,
/// one copy, or, when the driver ignores user sizes, one of its own forms that has the label's size. The driver's answer is checked twice: the
/// paper size it wrote back into the settings, and the physical page an information context built from those settings really reports. When both
/// agree with the label (within 1 mm), the page <i>is</i> the label and the picture covers it from its corner. Otherwise the driver's own selected paper is kept (still forced to one copy) and the label is centred on its printable area;
/// <see cref="ProbeAsync"/> reports that as <see cref="DriverMedia.CustomSizeAccepted"/> = false, so the pre-flight can warn before printing.
/// Probe and print go through the same negotiation, and a job checks its device context against the probed media before it starts (another
/// resolution, or an accepted page that changed, is refused), so the resolution and page the picture was drawn for are the ones the job prints on.
/// </para>
/// <para>
/// <b>Failures.</b> A printer problem never throws: it comes back as a failed <see cref="PrintResult"/> together with the number of pages the driver
/// had already accepted (their <c>EndPage</c> succeeded), because such a page may have printed and its serial number must not be issued again.
/// Any job that did not finish is aborted with <c>AbortDoc</c>, and every printer handle, device context and settings buffer is released.
/// </para>
/// <para>
/// All calls are blocking Win32 calls (a network queue can take seconds), so the work runs on the thread pool. The device context is created and
/// used on that one thread for the whole job, as GDI requires.
/// </para>
/// </summary>
public sealed class GdiDriverPrinter : IDriverPrinter
{
    /// <summary>Driver name passed to CreateDC/CreateIC. Learn: GDI ignores it for printers; "WINSPOOL" is the conventional value.</summary>
    private const string SpoolerDriver = "WINSPOOL";

    /// <summary>ERROR_CANCELLED (winerror.h): the person closed a dialog the driver showed (for example the Save As of a PDF printer).</summary>
    private const int ErrorCancelled = 1223;

    private readonly string? _outputFilePath;

    /// <summary>Creates the printer.</summary>
    /// <param name="outputFilePath">
    /// Test seam: when set, every job is written to this file instead of the printer (it becomes <c>DOCINFO.lpszOutput</c>), which lets
    /// "Microsoft Print to PDF" produce a file without showing its Save As dialog. Production passes null, so the job goes to the printer.
    /// </param>
    public GdiDriverPrinter(string? outputFilePath = null) =>
        _outputFilePath = string.IsNullOrWhiteSpace(outputFilePath) ? null : outputFilePath;

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is printed and no job is created: the settings are negotiated as for a real job and measured on an information context.
    /// <see cref="DriverMedia.CustomSizeAccepted"/> is true when the page the job would print on has the label's size, whether the driver took
    /// the custom size or its selected paper already is that size. A probe whose token is cancelled ends as a cancelled task (awaiting it throws
    /// <see cref="OperationCanceledException"/>): null is kept for "the printer or its driver cannot be reached", and answering a cancel with
    /// null would make the pre-flight report an unreachable driver that is not.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A label size is not a positive finite number.</exception>
    public Task<DriverMedia?> ProbeAsync(string printerName, double labelWidthMm, double labelHeightMm, CancellationToken ct)
    {
        ValidateLabelSize(labelWidthMm, labelHeightMm);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(printerName)) return Task.FromResult<DriverMedia?>(null);
        if (ct.IsCancellationRequested) return Task.FromCanceled<DriverMedia?>(ct);
        return Task.Run(() =>
        {
            var media = ProbeCore(printerName, labelWidthMm, labelHeightMm);
            // The driver calls cannot be interrupted; a cancel that arrived meanwhile is honoured as soon as they return.
            ct.ThrowIfCancellationRequested();
            return media;
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One page per picture. Each picture is drawn at the label's physical size: on an axis where the device resolution is a whole multiple of
    /// the picture's resolution it is drawn 1:1 or stretched by that whole factor (see <see cref="GdiPageLayout"/>), so barcode modules stay on
    /// whole device dots; a picture clearly drawn for another resolution is stretched to the label's size in millimetres.
    /// </para>
    /// <para>
    /// The device context is compared with <paramref name="media"/> before anything else happens (<see cref="GdiPageLayout.DescribeChange"/>):
    /// another resolution, or, when the probe said the label size was accepted, a page that is no longer the probed one or no longer the label's
    /// size, ends the job as <see cref="PrintErrorKind.DriverRefused"/> with the printer name and a detail naming what changed, before any
    /// picture is requested and before StartDoc, so nothing reaches the queue and no page counts. This is what makes the pre-flight's
    /// "accepted" true at print time: CreateDC may fall back to the driver's defaults (Learn), or the defaults may have changed since the probe.
    /// </para>
    /// <para>
    /// <paramref name="pages"/> is enumerated exactly once and lazily: the first picture is drawn before the job is started (so a printer that
    /// cannot be reached or a first label that cannot be drawn leaves nothing in the queue), and the token is checked before each picture is
    /// requested and again before it is handed to the driver. An empty sequence succeeds with zero pages and creates no job.
    /// </para>
    /// <para>
    /// An exception thrown by <paramref name="pages"/> itself (for example a <see cref="DevicePictureException"/> for a label that cannot be
    /// drawn) is not a printer problem: the job is aborted, everything is released, and the exception reaches the caller unchanged. Every picture
    /// the sequence yielded before it threw had already been accepted by the driver, so the caller can count them as submitted. An
    /// <see cref="OperationCanceledException"/> thrown by the sequence while <paramref name="ct"/> is cancelled is reported as
    /// <see cref="PrintErrorKind.Cancelled"/> instead.
    /// </para>
    /// <para>
    /// Errors: the printer does not exist -> <see cref="PrintErrorKind.PrinterNotFound"/>; the user may not use it ->
    /// <see cref="PrintErrorKind.AccessDenied"/>; the driver will not open or accept the job or a page (CreateDC, StartDoc, StartPage,
    /// StretchDIBits, EndPage, EndDoc) -> <see cref="PrintErrorKind.DriverRefused"/> with the printer name and a detail naming the call and the
    /// Win32 error number; a cancel, or the person closing a dialog of the driver -> <see cref="PrintErrorKind.Cancelled"/>. Other OpenPrinter
    /// failures map exactly as for ZPL printing (<see cref="PrinterErrors.FromWin32"/>).
    /// </para>
    /// <para>
    /// <see cref="PrintResult.PagesSent"/> equals <see cref="DriverPrintOutcome.PagesSubmitted"/>, and <see cref="PrintResult.BytesSent"/> is the
    /// number of picture bytes in those pages (capped at <see cref="int.MaxValue"/>), so it is above zero exactly when a page may have printed.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> or <paramref name="pages"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A label size is not a positive finite number.</exception>
    public Task<DriverPrintOutcome> PrintAsync(string printerName, string documentName, double labelWidthMm, double labelHeightMm,
        DriverMedia media, IEnumerable<DevicePicture> pages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(pages);
        ValidateLabelSize(labelWidthMm, labelHeightMm);
        if (!OperatingSystem.IsWindows()) return Task.FromResult(Outcome(PrintError.Create(PrintErrorKind.Unsupported), 0, 0));
        if (string.IsNullOrWhiteSpace(printerName))
            return Task.FromResult(Outcome(PrintError.Create(PrintErrorKind.PrinterNotFound, printerName ?? string.Empty), 0, 0));
        // Answered here rather than by Task.Run(…, ct): a cancelled task would throw at the caller, and a cancel is a result like any other.
        if (ct.IsCancellationRequested) return Task.FromResult(Outcome(PrintError.Create(PrintErrorKind.Cancelled), 0, 0));
        string jobName = string.IsNullOrEmpty(documentName) ? "Rotula label" : documentName;
        return Task.Run(() => PrintCore(printerName, jobName, labelWidthMm, labelHeightMm, media, pages, ct), CancellationToken.None);
    }

    /// <summary>The probe on the calling thread: negotiate the page, then measure it on an information context. Null when anything fails.</summary>
    internal static DriverMedia? ProbeCore(string printerName, double labelWidthMm, double labelHeightMm)
    {
        var (devMode, error) = NegotiatePage(printerName, labelWidthMm, labelHeightMm);
        if (error is not null || devMode is null) return null;
        using (devMode)
        {
            if (!TryMeasurePage(printerName, devMode, out var device)) return null;
            return new DriverMedia(device.DpiX, device.DpiY, device.PhysicalWidth, device.PhysicalHeight, device.HorzRes, device.VertRes,
                device.OffsetX, device.OffsetY, GdiPageLayout.PageMatchesLabel(device, labelWidthMm, labelHeightMm));
        }
    }

    /// <summary>
    /// The job on the calling thread: negotiate the page, create the device context, check it against the probed media, submit the pages.
    /// </summary>
    private DriverPrintOutcome PrintCore(string printerName, string jobName, double labelWidthMm, double labelHeightMm, DriverMedia media,
        IEnumerable<DevicePicture> pages, CancellationToken ct)
    {
        var (devMode, negotiationError) = NegotiatePage(printerName, labelWidthMm, labelHeightMm);
        if (negotiationError is not null || devMode is null)
            return Outcome(negotiationError ?? PrintError.Create(PrintErrorKind.DriverRefused, printerName, "DocumentProperties"), 0, 0);

        using (devMode)
        using (var dc = GdiNative.CreateDC(SpoolerDriver, printerName, null, devMode.Pointer))
        {
            if (dc.IsInvalid) return Outcome(GdiFailure(printerName, "CreateDC", Marshal.GetLastPInvokeError()), 0, 0);

            // The device context's own answers decide the geometry: Learn says CreateDC falls back to a default DEVMODE when the driver cannot use
            // the one it was given, so the negotiated values are not trusted blindly.
            var device = GdiDeviceGeometry.Read(dc);
            if (!device.IsUsable) return Outcome(PrintError.Create(PrintErrorKind.DriverRefused, printerName, Detail("GetDeviceCaps", 0)), 0, 0);

            // The pre-flight judged, and the pictures were drawn for, the probed media. A device context on another resolution or another page
            // would print a label the person never saw judged (centred, clipped, or stretched off whole dots), so it is refused here, before
            // StartDoc and before a picture is requested: nothing reaches the queue, no page counts, and the using blocks delete the DC.
            if (GdiPageLayout.DescribeChange(device, media, labelWidthMm, labelHeightMm) is { } change)
                return Outcome(PrintError.Create(PrintErrorKind.DriverRefused, printerName, change), 0, 0);

            // COLORONCOLOR, not HALFTONE: the picture is already black and white at the device resolution and is only ever drawn 1:1 or enlarged by
            // a whole factor, where COLORONCOLOR repeats pixels exactly. HALFTONE averages neighbouring pixels into grey, which the driver then
            // dithers into ragged bar edges (and it needs SetBrushOrgEx). BLACKONWHITE only differs when shrinking, which never happens here.
            // A driver may ignore the mode and scale on its own; that is a quality hint, not a failure, so the result is not checked.
            _ = GdiNative.SetStretchBltMode(dc, GdiNative.COLORONCOLOR);

            // A new device context uses MM_TEXT: one logical unit is one device dot, so the placement below is in device dots.
            return SubmitPages(dc, device, printerName, jobName, labelWidthMm, labelHeightMm, pages, ct);
        }
    }

    /// <summary>
    /// The job proper: StartDoc before the first page, then StartPage / StretchDIBits / EndPage for each picture, then EndDoc. Any other way out
    /// (failure, cancel, exception) aborts the job in the <c>finally</c>; the caller deletes the device context afterwards.
    /// </summary>
    private DriverPrintOutcome SubmitPages(SafeDcHandle dc, GdiDeviceGeometry device, string printerName, string jobName,
        double labelWidthMm, double labelHeightMm, IEnumerable<DevicePicture> pages, CancellationToken ct)
    {
        int submitted = 0;
        long bytes = 0;
        bool started = false, ended = false;
        byte[] bitmapInfo = GdiBitmapInfo.CreateGray8();
        byte[]? paddedRows = null; // reused across pages of the same size, so a 500-label run allocates it once

        // Disposed after the finally below, which runs the producer's own finally blocks (it may hold resources for the run).
        using var source = pages.GetEnumerator();
        try
        {
            while (true)
            {
                // Before asking for the next picture: drawing it is the expensive part, and it is not needed any more.
                if (ct.IsCancellationRequested) return Outcome(PrintError.Create(PrintErrorKind.Cancelled), submitted, bytes);
                if (!source.MoveNext()) break;
                var picture = source.Current;
                ValidatePicture(picture);
                // And before handing it over: a cancel that arrived while it was being drawn stops it from reaching the driver.
                if (ct.IsCancellationRequested) return Outcome(PrintError.Create(PrintErrorKind.Cancelled), submitted, bytes);

                if (!started)
                {
                    var doc = new GdiNative.DOCINFOW
                    {
                        cbSize = Marshal.SizeOf<GdiNative.DOCINFOW>(),
                        lpszDocName = jobName,
                        lpszOutput = _outputFilePath,
                    };
                    if (GdiNative.StartDoc(dc, ref doc) <= 0)
                        return Outcome(GdiFailure(printerName, "StartDoc", Marshal.GetLastPInvokeError()), submitted, bytes);
                    started = true;
                }

                if (GdiNative.StartPage(dc) <= 0)
                    return Outcome(GdiFailure(printerName, "StartPage", Marshal.GetLastPInvokeError()), submitted, bytes);

                var place = GdiPageLayout.Place(device, labelWidthMm, labelHeightMm, picture.WidthPx, picture.HeightPx);
                byte[] rows = GdiBitmapInfo.DibRows(picture, ref paddedRows);
                GdiBitmapInfo.SetSize(bitmapInfo, picture.WidthPx, picture.HeightPx);
                int lines = GdiNative.StretchDIBits(dc, place.X, place.Y, place.Width, place.Height, 0, 0, picture.WidthPx, picture.HeightPx,
                    rows, bitmapInfo, GdiNative.DIB_RGB_COLORS, GdiNative.SRCCOPY);
                if (lines == 0 || lines == GdiNative.GDI_ERROR)
                    return Outcome(GdiFailure(printerName, "StretchDIBits", Marshal.GetLastPInvokeError()), submitted, bytes);

                if (GdiNative.EndPage(dc) <= 0)
                    return Outcome(GdiFailure(printerName, "EndPage", Marshal.GetLastPInvokeError()), submitted, bytes);
                // Counted only now: from here on the page may print even if the job is aborted later.
                submitted++;
                bytes += (long)picture.WidthPx * picture.HeightPx;
            }

            // Nothing to print: no job was started, so nothing appears in the queue.
            if (!started) return Outcome(null, 0, 0);

            if (GdiNative.EndDoc(dc) <= 0)
            {
                // Whether the spooler prints the pages is unknown, so they stay counted as submitted (the safe side for serial numbers).
                return Outcome(GdiFailure(printerName, "EndDoc", Marshal.GetLastPInvokeError()), submitted, bytes);
            }
            ended = true;
            return Outcome(null, submitted, bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The page source honoured the same token (for example while drawing): a cancel, reported as a value like the checks above.
            return Outcome(PrintError.Create(PrintErrorKind.Cancelled), submitted, bytes);
        }
        finally
        {
            // Every exit that is not a finished document discards it. The return value is not checked: there is nothing more to do if even
            // AbortDoc fails, and the result already says the job did not finish.
            if (started && !ended) _ = GdiNative.AbortDoc(dc);
        }
    }

    /// <summary>
    /// Builds the settings a probe or a job uses: the driver's current DEVMODE with a page of the label's size, or, when the driver does not
    /// produce such a page, the driver's own paper. Whatever is returned carries one copy (<see cref="WithoutExtraCopies"/>): Rotula prints
    /// copies as separate pages, and a driver default of several copies would multiply every label behind the serial-number count. The caller
    /// owns (and disposes) the returned settings.
    /// </summary>
    /// <returns>The settings, or the error when the printer cannot be opened or the driver will not report its settings.</returns>
    internal static (GdiDevMode? DevMode, PrintError? Error) NegotiatePage(string printerName, double labelWidthMm, double labelHeightMm)
    {
        if (!GdiNative.OpenPrinter(printerName, out var printer, IntPtr.Zero))
        {
            int code = Marshal.GetLastPInvokeError();
            printer.Dispose();
            // Same mapping as ZPL printing: not found, access denied, offline, or unknown with the code.
            return (null, PrinterErrors.FromWin32(code, printerName));
        }

        using (printer)
        {
            // Learn's sequence: ask for the size (the driver's private data follows the public DEVMODE), allocate it, read the current settings.
            int size = GdiNative.DocumentProperties(IntPtr.Zero, printer, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0) return (null, GdiFailure(printerName, "DocumentProperties", Marshal.GetLastPInvokeError()));

            GdiDevMode? current = GdiDevMode.Allocate(size);
            try
            {
                if (GdiNative.DocumentProperties(IntPtr.Zero, printer, printerName, current.Pointer, IntPtr.Zero, GdiNative.DM_OUT_BUFFER) < 0)
                    return (null, GdiFailure(printerName, "DocumentProperties", Marshal.GetLastPInvokeError()));

                var chosen = TryLabelSizedPage(printer, printerName, current, labelWidthMm, labelHeightMm)
                    ?? TryWithSettings(printer, printerName, current, d => d.TrySetSingleCopy());
                if (chosen is null)
                {
                    // The driver would not validate even "one copy": its own settings are used rather than not printing at all, but never with
                    // its copy count (WithoutExtraCopies below writes it directly).
                    chosen = current;
                    current = null;
                }
                return (WithoutExtraCopies(chosen), null);
            }
            finally
            {
                current?.Dispose();
            }
        }
    }

    /// <summary>
    /// The last guard of the negotiation, applied to every settings it returns: when they hold a copy count other than 1, it is set to 1
    /// (dmCopies = 1, DM_COPIES) directly, without asking the driver again. That matters for the last resort, the driver's settings as they
    /// are, which the driver refused to change through DocumentProperties and which may carry its default of several copies; on the validated
    /// settings it only acts if the driver changed the copy count back. Writing the field unvalidated is safe for placement: CreateDC validates
    /// the DEVMODE itself (Learn: an unusable one is replaced by the driver's default) and the geometry is always read from the device context.
    /// Settings whose public part ends before dmCopies have no copy count at all and are returned as they are.
    /// </summary>
    /// <param name="settings">The settings to return; changed in place.</param>
    /// <returns><paramref name="settings"/>, now with at most one copy.</returns>
    internal static GdiDevMode WithoutExtraCopies(GdiDevMode settings)
    {
        if (settings.Copies is { } copies && copies != 1) _ = settings.TrySetSingleCopy();
        return settings;
    }

    /// <summary>
    /// Settings whose page has the label's size, or null when the driver offers none. First a user-defined paper of the label's size
    /// (DMPAPER_USER, what label printer drivers normally support); then, if the driver refuses that, one of the driver's own forms whose size is
    /// the label's. The second step exists because drivers with a fixed form list ignore a user size without failing: "Microsoft Print to PDF"
    /// answers a DMPAPER_USER request for 210 x 297 mm with Letter, yet prints a true A4 page when its A4 form is selected by index.
    /// </summary>
    private static GdiDevMode? TryLabelSizedPage(SafePrinterHandle printer, string printerName, GdiDevMode current, double labelWidthMm, double labelHeightMm)
    {
        int width = TenthsOfMm(labelWidthMm), length = TenthsOfMm(labelHeightMm);
        var userSize = KeepIfLabelSized(TryWithSettings(printer, printerName, current, d => d.TryRequestPaper(width, length)),
            printerName, width, length, labelWidthMm, labelHeightMm);
        if (userSize is not null) return userSize;

        if (FindLabelSizedForm(printerName, current, width, length) is not { } form) return null;
        return KeepIfLabelSized(TryWithSettings(printer, printerName, current, d => d.TryRequestForm(form)),
            printerName, width, length, labelWidthMm, labelHeightMm);
    }

    /// <summary>
    /// Keeps validated settings only when the driver really produces a label-sized page with them, and disposes them otherwise. Two checks: the
    /// paper size the driver wrote back must match (a different explicit size is a refusal; no explicit size means the driver describes the
    /// paper by its form), and the physical page an information context reports must match too, because a driver can return success from
    /// DocumentProperties and still print on its own paper.
    /// </summary>
    private static GdiDevMode? KeepIfLabelSized(GdiDevMode? candidate, string printerName, int widthTenthsMm, int lengthTenthsMm,
        double labelWidthMm, double labelHeightMm)
    {
        if (candidate is null) return null;
        bool accepted = (!candidate.ReportsPaperSize || candidate.PaperIs(widthTenthsMm, lengthTenthsMm))
            && TryMeasurePage(printerName, candidate, out var device)
            && GdiPageLayout.PageMatchesLabel(device, labelWidthMm, labelHeightMm);
        if (accepted) return candidate;
        candidate.Dispose();
        return null;
    }

    /// <summary>
    /// The index of the driver's own form whose portrait size is the label's within 1 mm (the closest one when several are), or null. Reads the
    /// form list with DeviceCapabilities DC_PAPERS and DC_PAPERSIZE, exactly as printer detection does (<see cref="Win32PrinterCapsReader"/>):
    /// sizes are POINTs in tenths of a millimetre "as if the paper were in DMORIENT_PORTRAIT" (Learn), which is the orientation requested.
    /// DMPAPER_USER itself is skipped: it is the user-size request that was just refused. The port is passed as null: Windows looks the printer
    /// up by name, and this was checked against "Microsoft Print to PDF".
    /// </summary>
    private static short? FindLabelSizedForm(string printerName, GdiDevMode current, int widthTenthsMm, int lengthTenthsMm)
    {
        const ushort DC_PAPERS = 2, DC_PAPERSIZE = 3;
        const int MaxForms = 4096; // same sanity cap as printer detection: a larger count is a broken driver answer
        int count = NativeMethods.DeviceCapabilities(printerName, null, DC_PAPERS, IntPtr.Zero, current.Pointer);
        if (count <= 0 || count > MaxForms) return null;

        IntPtr papers = Marshal.AllocHGlobal(count * 2), sizes = Marshal.AllocHGlobal(count * 8);
        try
        {
            if (NativeMethods.DeviceCapabilities(printerName, null, DC_PAPERS, papers, current.Pointer) != count) return null;
            if (NativeMethods.DeviceCapabilities(printerName, null, DC_PAPERSIZE, sizes, current.Pointer) != count) return null;
            short? best = null;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int index = (ushort)Marshal.ReadInt16(papers, i * 2);
                if (index <= 0 || index > short.MaxValue || index == GdiNative.DMPAPER_USER) continue;
                int dx = Math.Abs(Marshal.ReadInt32(sizes, i * 8) - widthTenthsMm), dy = Math.Abs(Marshal.ReadInt32(sizes, i * 8 + 4) - lengthTenthsMm);
                if (dx > GdiDevMode.PaperToleranceTenths || dy > GdiDevMode.PaperToleranceTenths || dx + dy >= bestDistance) continue;
                best = (short)index;
                bestDistance = dx + dy;
            }
            return best;
        }
        finally
        {
            Marshal.FreeHGlobal(papers);
            Marshal.FreeHGlobal(sizes);
        }
    }

    /// <summary>
    /// Copies <paramref name="current"/>, applies <paramref name="change"/> and has the driver validate the result
    /// (DocumentProperties with DM_IN_BUFFER | DM_OUT_BUFFER on the same buffer). Null when the change cannot be made or the driver rejects it.
    /// </summary>
    private static GdiDevMode? TryWithSettings(SafePrinterHandle printer, string printerName, GdiDevMode current, Func<GdiDevMode, bool> change)
    {
        GdiDevMode? copy = current.Clone();
        try
        {
            if (!change(copy)) return null;
            if (GdiNative.DocumentProperties(IntPtr.Zero, printer, printerName, copy.Pointer, copy.Pointer,
                    GdiNative.DM_IN_BUFFER | GdiNative.DM_OUT_BUFFER) < 0)
                return null;
            var validated = copy;
            copy = null;
            return validated;
        }
        finally
        {
            copy?.Dispose();
        }
    }

    /// <summary>Measures the page <paramref name="devMode"/> produces on an information context (nothing is printed). False when it cannot be measured.</summary>
    private static bool TryMeasurePage(string printerName, GdiDevMode devMode, out GdiDeviceGeometry device)
    {
        using var ic = GdiNative.CreateIC(SpoolerDriver, printerName, null, devMode.Pointer);
        device = ic.IsInvalid ? default : GdiDeviceGeometry.Read(ic);
        return !ic.IsInvalid && device.IsUsable;
    }

    /// <summary>
    /// Maps the Win32 error of a failed driver call: access denied and a vanished printer keep their own kinds, a dialog the person closed is a
    /// cancel, anything else is <see cref="PrintErrorKind.DriverRefused"/> (printer, detail).
    /// </summary>
    internal static PrintError GdiFailure(string printerName, string call, int code) => code switch
    {
        PrinterErrors.ErrorAccessDenied => PrintError.Create(PrintErrorKind.AccessDenied, printerName),
        ErrorCancelled => PrintError.Create(PrintErrorKind.Cancelled),
        PrinterErrors.ErrorInvalidPrinterName or PrinterErrors.ErrorPrinterNotFound => PrintError.Create(PrintErrorKind.PrinterNotFound, printerName),
        _ => PrintError.Create(PrintErrorKind.DriverRefused, printerName, Detail(call, code)),
    };

    /// <summary>
    /// The technical detail of a refusal: the call and the Win32 error number, plus Windows' own description of it (Windows words it in the
    /// user's language). GDI does not always set an error; then the number is 0 and no description is added, because Windows would call 0
    /// "The operation completed successfully".
    /// </summary>
    internal static string Detail(string call, int code)
    {
        string number = code.ToString(CultureInfo.InvariantCulture);
        return code == 0 ? $"{call}, Win32 error {number}" : $"{call}, Win32 error {number}: {new Win32Exception(code).Message}";
    }

    /// <summary>The outcome of a job: success when <paramref name="error"/> is null, with the pages and picture bytes the driver accepted.</summary>
    private static DriverPrintOutcome Outcome(PrintError? error, int pages, long bytes)
    {
        int sent = (int)Math.Min(bytes, int.MaxValue);
        var result = error is null ? PrintResult.Ok(sent) : PrintResult.Fail(error, sent);
        return new DriverPrintOutcome(result.WithPages(pages), pages);
    }

    /// <summary>Millimetres to the DEVMODE's tenths of a millimetre, saturated so a huge value cannot wrap (TryRequestPaper rejects it).</summary>
    private static int TenthsOfMm(double mm) => (int)Math.Round(Math.Min(mm * 10.0, int.MaxValue), MidpointRounding.AwayFromZero);

    private static void ValidateLabelSize(double widthMm, double heightMm)
    {
        if (!double.IsFinite(widthMm) || widthMm <= 0) throw new ArgumentOutOfRangeException(nameof(widthMm), widthMm, "The label width must be a positive number of millimetres.");
        if (!double.IsFinite(heightMm) || heightMm <= 0) throw new ArgumentOutOfRangeException(nameof(heightMm), heightMm, "The label height must be a positive number of millimetres.");
    }

    /// <summary>A malformed picture is a programming error of the page source, not a printer problem: it throws (after the job is aborted).</summary>
    private static void ValidatePicture(DevicePicture? picture)
    {
        if (picture is null) throw new ArgumentException("The page source yielded a null picture.", "pages");
        if (picture.WidthPx <= 0 || picture.HeightPx <= 0 || picture.Gray8 is null || (long)picture.WidthPx * picture.HeightPx > picture.Gray8.Length)
            throw new ArgumentException(
                $"The picture {picture.WidthPx} x {picture.HeightPx} does not match its {picture.Gray8?.Length ?? 0} grey bytes.", "pages");
    }
}

/// <summary>What a printer device or information context reports about its page, in device dots.</summary>
/// <param name="DpiX">LOGPIXELSX: horizontal dots per inch.</param>
/// <param name="DpiY">LOGPIXELSY: vertical dots per inch.</param>
/// <param name="HorzRes">HORZRES: width of the printable area.</param>
/// <param name="VertRes">VERTRES: height of the printable area.</param>
/// <param name="PhysicalWidth">PHYSICALWIDTH: width of the physical page (never smaller than the printable area).</param>
/// <param name="PhysicalHeight">PHYSICALHEIGHT: height of the physical page.</param>
/// <param name="OffsetX">PHYSICALOFFSETX: from the left edge of the physical page to the printable area.</param>
/// <param name="OffsetY">PHYSICALOFFSETY: from the top edge of the physical page to the printable area.</param>
internal readonly record struct GdiDeviceGeometry(int DpiX, int DpiY, int HorzRes, int VertRes, int PhysicalWidth, int PhysicalHeight, int OffsetX, int OffsetY)
{
    /// <summary>True when the context reported a resolution and a printable area; without them nothing can be placed.</summary>
    public bool IsUsable => DpiX > 0 && DpiY > 0 && HorzRes > 0 && VertRes > 0;

    /// <summary>Reads every value from a device or information context.</summary>
    public static GdiDeviceGeometry Read(SafeDcHandle dc) => new(
        GdiNative.GetDeviceCaps(dc, GdiNative.LOGPIXELSX), GdiNative.GetDeviceCaps(dc, GdiNative.LOGPIXELSY),
        GdiNative.GetDeviceCaps(dc, GdiNative.HORZRES), GdiNative.GetDeviceCaps(dc, GdiNative.VERTRES),
        GdiNative.GetDeviceCaps(dc, GdiNative.PHYSICALWIDTH), GdiNative.GetDeviceCaps(dc, GdiNative.PHYSICALHEIGHT),
        GdiNative.GetDeviceCaps(dc, GdiNative.PHYSICALOFFSETX), GdiNative.GetDeviceCaps(dc, GdiNative.PHYSICALOFFSETY));
}

/// <summary>Where a picture goes on the page, in GDI device coordinates (origin at the printable area's top-left corner).</summary>
/// <param name="X">Left edge; negative when the label starts in the unprintable margin.</param>
/// <param name="Y">Top edge; negative when the label starts in the unprintable margin.</param>
/// <param name="Width">Width of the destination rectangle in device dots.</param>
/// <param name="Height">Height of the destination rectangle in device dots.</param>
internal readonly record struct GdiPagePlacement(int X, int Y, int Width, int Height);

/// <summary>
/// The page geometry of picture printing, kept free of Win32 so it can be tested without a printer.
/// <para>
/// <b>Origin.</b> On a printer device context, GDI coordinate (0,0) is the top-left corner of the printable area, not of the paper: HORZRES/VERTRES
/// measure the printable area, PHYSICALOFFSETX/Y measure how far it starts from the paper's edges (Learn, GetDeviceCaps), and .NET's
/// <c>PrintDocument.OriginAtMargins = false</c>, which passes the device context through untranslated, is documented as "the graphics origin is
/// at the top-left corner of the printable page". To put the label's corner on the paper's corner the picture is therefore drawn at minus the
/// offset; the part under the unprintable margin is clipped by the device, and everything else lands where it was designed.
/// </para>
/// </summary>
internal static class GdiPageLayout
{
    /// <summary>
    /// How far the page may differ from the label and still be "the label's size". Drivers round paper sizes to whole dots or whole millimetres;
    /// the same 1 mm the pre-flight allows for a label against the printable area.
    /// </summary>
    internal const double PageToleranceMm = 1.0;

    /// <summary>
    /// How many picture pixels the picture may differ from the label's size in millimetres and still be treated as drawn for this device. The
    /// picture's size is the label rounded twice (to design dots, then to device dots), at most about 1.25 pixels off; beyond 2 pixels the
    /// picture was drawn for another resolution.
    /// </summary>
    internal const int SnapTolerancePixels = 2;

    /// <summary>True when the device's physical page has the label's size on both axes, within <see cref="PageToleranceMm"/>.</summary>
    internal static bool PageMatchesLabel(GdiDeviceGeometry device, double labelWidthMm, double labelHeightMm)
    {
        if (device.DpiX <= 0 || device.DpiY <= 0) return false;
        double wantWidth = labelWidthMm / 25.4 * device.DpiX, wantHeight = labelHeightMm / 25.4 * device.DpiY;
        double toleranceX = PageToleranceMm / 25.4 * device.DpiX, toleranceY = PageToleranceMm / 25.4 * device.DpiY;
        return Math.Abs(device.PhysicalWidth - wantWidth) <= toleranceX && Math.Abs(device.PhysicalHeight - wantHeight) <= toleranceY;
    }

    /// <summary>
    /// What changed between the probe the pre-flight judged (<paramref name="probed"/>) and the device context a job is about to print on, or
    /// null when they agree. The answer is the technical detail of a <see cref="PrintErrorKind.DriverRefused"/> (English, invariant culture,
    /// as every refusal detail of this class), so it names the values in plain words.
    /// <list type="bullet">
    /// <item><b>Resolution</b>, always: both axes must be exactly the probed ones. The picture is drawn at the coarser probed axis
    /// (<see cref="DevicePictureRenderer.RenderDpi"/>), and <see cref="DestinationLength"/> keeps whole dots only when the device resolution is the
    /// one it was drawn for; on any other it stretches the picture to millimetres and bars stop landing on whole dots.</item>
    /// <item><b>Page</b>, only when the probe said the label size was accepted: the device's physical page must be the probed page within
    /// <see cref="PageToleranceMm"/> and still be the label's size (<see cref="PageMatchesLabel"/>, the same rule <see cref="Place"/> uses), so the
    /// label is drawn from the paper's corner as judged. Both are needed: a page within 1 mm of the probe can be up to 2 mm off the label, and
    /// <see cref="Place"/> would then centre it without a word. When the probe said the size was refused, the label is centred on whatever paper
    /// the driver uses and the pre-flight already warned about that, so the page is not compared.</item>
    /// </list>
    /// </summary>
    /// <param name="device">What the job's device context reports.</param>
    /// <param name="probed">The media the pre-flight judged.</param>
    /// <param name="labelWidthMm">Label width in millimetres.</param>
    /// <param name="labelHeightMm">Label height in millimetres.</param>
    internal static string? DescribeChange(GdiDeviceGeometry device, DriverMedia probed, double labelWidthMm, double labelHeightMm)
    {
        if (device.DpiX != probed.DpiX || device.DpiY != probed.DpiY)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"the resolution changed after the check: {probed.DpiX} x {probed.DpiY} dpi then, {device.DpiX} x {device.DpiY} dpi now");
        }
        if (!probed.CustomSizeAccepted) return null;

        // Same resolution from here on, so the probed and the device dots convert to millimetres alike.
        double toleranceX = PageToleranceMm / 25.4 * device.DpiX, toleranceY = PageToleranceMm / 25.4 * device.DpiY;
        bool samePage = Math.Abs(device.PhysicalWidth - probed.PhysicalWidthDots) <= toleranceX
            && Math.Abs(device.PhysicalHeight - probed.PhysicalHeightDots) <= toleranceY;
        if (samePage && PageMatchesLabel(device, labelWidthMm, labelHeightMm)) return null;

        string now = PageMm(device.PhysicalWidth, device.PhysicalHeight, device.DpiX, device.DpiY);
        return samePage
            ? string.Create(CultureInfo.InvariantCulture,
                $"the page is no longer the label's size: label {Mm(labelWidthMm)} x {Mm(labelHeightMm)} mm, page {now} mm")
            : string.Create(CultureInfo.InvariantCulture,
                $"the page changed after the check: {PageMm(probed.PhysicalWidthDots, probed.PhysicalHeightDots, probed.DpiX, probed.DpiY)} mm then, {now} mm now");
    }

    /// <summary>A page size in dots as <c>W x H</c> millimetres with at most one decimal, invariant culture.</summary>
    private static string PageMm(int widthDots, int heightDots, int dpiX, int dpiY) =>
        Mm(widthDots * 25.4 / dpiX) + " x " + Mm(heightDots * 25.4 / dpiY);

    /// <summary>Millimetres with at most one decimal, invariant culture.</summary>
    private static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// The destination rectangle of a picture: the label's size on the device, its corner on the paper's corner when the page is the label,
    /// otherwise centred in the printable area (a label larger than that area overflows evenly on both sides; the pre-flight reports it).
    /// </summary>
    internal static GdiPagePlacement Place(GdiDeviceGeometry device, double labelWidthMm, double labelHeightMm, int pictureWidthPx, int pictureHeightPx)
    {
        // The picture is drawn at the coarser of the two resolutions (DevicePictureRenderer.RenderDpi), so that is the resolution to compare with.
        int renderDpi = Math.Min(device.DpiX, device.DpiY);
        int width = DestinationLength(pictureWidthPx, labelWidthMm, device.DpiX, renderDpi);
        int height = DestinationLength(pictureHeightPx, labelHeightMm, device.DpiY, renderDpi);
        if (PageMatchesLabel(device, labelWidthMm, labelHeightMm)) return new GdiPagePlacement(-device.OffsetX, -device.OffsetY, width, height);
        return new GdiPagePlacement((device.HorzRes - width) / 2, (device.VertRes - height) / 2, width, height);
    }

    /// <summary>
    /// The length of the destination rectangle on one axis, in device dots.
    /// <para>
    /// Deliberately not always the label's millimetres converted to dots: the picture's own size can differ from that by a dot (it is rounded
    /// twice), and stretching, say, 1181 pixels onto 1182 dots makes the device repeat one column somewhere, possibly inside a barcode, so that one
    /// module comes out a dot wider. When the device resolution is a whole multiple of the picture's (1:1, or 600 on a 300 dpi picture) and the
    /// picture is within <see cref="SnapTolerancePixels"/> of the label's size, it is drawn at exactly that whole factor: the physical size is off by
    /// at most a couple of dots and every module keeps its width. A picture clearly drawn for another resolution, or an axis whose ratio is not
    /// whole (already a pre-flight warning), is stretched to the label's size in millimetres.
    /// </para>
    /// </summary>
    internal static int DestinationLength(int picturePixels, double labelMm, int deviceDpi, int renderDpi)
    {
        int fromMillimetres = Math.Max(1, (int)Math.Round(labelMm / 25.4 * deviceDpi, MidpointRounding.AwayFromZero));
        if (renderDpi > 0 && deviceDpi % renderDpi == 0)
        {
            int factor = deviceDpi / renderDpi;
            long whole = (long)picturePixels * factor;
            if (Math.Abs(whole - fromMillimetres) <= (long)SnapTolerancePixels * factor) return (int)whole;
        }
        return fromMillimetres;
    }
}

/// <summary>
/// The BITMAPINFO and pixel rows <see cref="GdiNative.StretchDIBits"/> reads for a <see cref="DevicePicture"/>: an 8-bit top-down DIB whose
/// 256-entry palette maps index i to grey i, so the picture's bytes are used as palette indexes unchanged (0 black, 255 white).
/// </summary>
internal static class GdiBitmapInfo
{
    /// <summary>Size of BITMAPINFOHEADER.</summary>
    internal const int HeaderSize = 40;

    /// <summary>Palette entries of an 8-bit DIB.</summary>
    internal const int PaletteEntries = 256;

    /// <summary>A BITMAPINFO (header plus grey palette) for an 8-bit picture; the size is filled in per page with <see cref="SetSize"/>.</summary>
    internal static byte[] CreateGray8()
    {
        var info = new byte[HeaderSize + PaletteEntries * 4];
        var header = new GdiNative.BITMAPINFOHEADER
        {
            biSize = HeaderSize,
            biPlanes = 1,
            biBitCount = 8,
            biCompression = GdiNative.BI_RGB,
            biSizeImage = 0, // allowed for BI_RGB (Learn); GDI works it out from the size
            biClrUsed = PaletteEntries,
        };
        MemoryMarshal.Write(info.AsSpan(0, HeaderSize), in header);
        for (int i = 0; i < PaletteEntries; i++)
        {
            // RGBQUAD order is blue, green, red, reserved.
            int at = HeaderSize + i * 4;
            info[at] = info[at + 1] = info[at + 2] = (byte)i;
            info[at + 3] = 0;
        }
        return info;
    }

    /// <summary>Writes the picture's size into <paramref name="info"/>. The height is negative: DevicePicture rows run from the top down.</summary>
    internal static void SetSize(byte[] info, int widthPx, int heightPx)
    {
        var header = MemoryMarshal.Read<GdiNative.BITMAPINFOHEADER>(info);
        header.biWidth = widthPx;
        header.biHeight = -heightPx;
        MemoryMarshal.Write(info.AsSpan(0, HeaderSize), in header);
    }

    /// <summary>
    /// The picture's rows as a DIB needs them: each row padded to a multiple of 4 bytes (Learn: the stride is the width rounded up to a DWORD).
    /// A width that already is a multiple of 4 is passed through without copying; otherwise the rows are copied into <paramref name="scratch"/>,
    /// which is reused for the next page of the same size. Its padding bytes are never written, so they stay zero.
    /// </summary>
    internal static byte[] DibRows(DevicePicture picture, ref byte[]? scratch)
    {
        int width = picture.WidthPx, height = picture.HeightPx;
        long stride = ((long)width + 3) & ~3L;
        if (stride == width) return picture.Gray8;
        long size = stride * height;
        if (scratch is null || scratch.LongLength != size) scratch = new byte[size];
        for (int y = 0; y < height; y++) Buffer.BlockCopy(picture.Gray8, y * width, scratch, (int)(y * stride), width);
        return scratch;
    }
}

/// <summary>
/// An unmanaged copy of a driver's DEVMODEW: the public part followed by the driver's private data, allocated at the size DocumentProperties
/// reports (Learn: never <c>sizeof(DEVMODE)</c>, the private part varies per driver). Fields are read and written at their documented offsets,
/// and only when they lie inside both the allocation and the public part the driver declared (<c>dmSize</c>). Always disposed in a <c>finally</c>.
/// </summary>
internal sealed class GdiDevMode : IDisposable
{
    // DEVMODEW offsets (Learn, DEVMODEW): WCHAR dmDeviceName[32] (64 bytes), dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra (WORDs),
    // dmFields (DWORD), then the printer struct of the union: dmOrientation, dmPaperSize, dmPaperLength, dmPaperWidth, dmScale, dmCopies (shorts).
    /// <summary>dmSize: size of the public part.</summary>
    internal const int OffSize = 68;
    /// <summary>dmFields: which members are valid.</summary>
    internal const int OffFields = 72;
    /// <summary>dmOrientation.</summary>
    internal const int OffOrientation = 76;
    /// <summary>dmPaperSize.</summary>
    internal const int OffPaperSize = 78;
    /// <summary>dmPaperLength, tenths of a millimetre.</summary>
    internal const int OffPaperLength = 80;
    /// <summary>dmPaperWidth, tenths of a millimetre.</summary>
    internal const int OffPaperWidth = 82;
    /// <summary>dmCopies.</summary>
    internal const int OffCopies = 86;
    /// <summary>dmFormName, WCHAR[32], after dmColor, dmDuplex, dmYResolution, dmTTOption and dmCollate.</summary>
    internal const int OffFormName = 102;

    /// <summary>A paper size within 1 mm (10 tenths) of the label counts as the label's size, for a driver's answer and for a form alike.</summary>
    internal const int PaperToleranceTenths = 10;

    private IntPtr _buffer;

    private GdiDevMode(int length)
    {
        Length = length;
        _buffer = Marshal.AllocHGlobal(length);
        // Zeroed: a driver that fills fewer bytes than it reported must not leave garbage where fields are read.
        Marshal.Copy(new byte[length], 0, _buffer, length);
    }

    /// <summary>Size of the allocation in bytes.</summary>
    internal int Length { get; }

    /// <summary>The buffer, for DocumentProperties, CreateDC and CreateIC.</summary>
    internal IntPtr Pointer => _buffer != IntPtr.Zero ? _buffer : throw new ObjectDisposedException(nameof(GdiDevMode));

    /// <summary>A zeroed buffer of <paramref name="length"/> bytes.</summary>
    internal static GdiDevMode Allocate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return new GdiDevMode(length);
    }

    /// <summary>An independent copy, so a rejected change never spoils the driver's current settings.</summary>
    internal GdiDevMode Clone()
    {
        var bytes = new byte[Length];
        Marshal.Copy(Pointer, bytes, 0, Length);
        var copy = new GdiDevMode(Length);
        Marshal.Copy(bytes, 0, copy._buffer, Length);
        return copy;
    }

    /// <summary>
    /// Asks for a user-defined paper of <paramref name="widthTenthsMm"/> x <paramref name="lengthTenthsMm"/> in portrait, one copy: dmPaperSize
    /// DMPAPER_USER, dmPaperWidth/Length in tenths of a millimetre, and the matching dmFields flags. Portrait because the picture is already
    /// turned the way it prints; a landscape default would swap the page's sides. False, with nothing changed, when the size does not fit the
    /// DEVMODE's short fields (more than 3276.7 mm) or the driver's public part is too short to hold them.
    /// </summary>
    internal bool TryRequestPaper(int widthTenthsMm, int lengthTenthsMm)
    {
        if (widthTenthsMm is < 1 or > short.MaxValue || lengthTenthsMm is < 1 or > short.MaxValue) return false;
        if (!Holds(OffCopies, 2)) return false;
        WriteInt16(OffOrientation, GdiNative.DMORIENT_PORTRAIT);
        WriteInt16(OffPaperSize, GdiNative.DMPAPER_USER);
        WriteInt16(OffPaperLength, (short)lengthTenthsMm);
        WriteInt16(OffPaperWidth, (short)widthTenthsMm);
        WriteInt16(OffCopies, 1);
        WriteUInt32(OffFields, (ReadUInt32(OffFields)
            | GdiNative.DM_ORIENTATION | GdiNative.DM_PAPERSIZE | GdiNative.DM_PAPERLENGTH | GdiNative.DM_PAPERWIDTH | GdiNative.DM_COPIES)
            & ~GdiNative.DM_FORMNAME);
        ClearFormName();
        return true;
    }

    /// <summary>
    /// Asks for the driver's own form <paramref name="paperIndex"/> (a DMPAPER value from its DC_PAPERS list) in portrait, one copy. The width and
    /// length flags are cleared because Learn says those members override dmPaperSize, and the current settings may still carry the old paper's
    /// values. False, with nothing changed, when the index is not positive or the public part is too short.
    /// </summary>
    internal bool TryRequestForm(short paperIndex)
    {
        if (paperIndex <= 0 || !Holds(OffCopies, 2)) return false;
        WriteInt16(OffOrientation, GdiNative.DMORIENT_PORTRAIT);
        WriteInt16(OffPaperSize, paperIndex);
        WriteInt16(OffCopies, 1);
        WriteUInt32(OffFields, (ReadUInt32(OffFields) | GdiNative.DM_ORIENTATION | GdiNative.DM_PAPERSIZE | GdiNative.DM_COPIES)
            & ~(GdiNative.DM_PAPERWIDTH | GdiNative.DM_PAPERLENGTH | GdiNative.DM_FORMNAME));
        ClearFormName();
        return true;
    }

    /// <summary>Sets one copy (dmCopies = 1, DM_COPIES) and leaves the paper alone. False when the public part is too short.</summary>
    internal bool TrySetSingleCopy()
    {
        if (!Holds(OffCopies, 2)) return false;
        WriteInt16(OffCopies, 1);
        WriteUInt32(OffFields, ReadUInt32(OffFields) | GdiNative.DM_COPIES);
        return true;
    }

    /// <summary>
    /// dmCopies, or null when the driver's public part ends before it. Read whatever dmFields says: a stray count is still forced to 1 by
    /// <see cref="GdiDriverPrinter.WithoutExtraCopies"/>, and writing 1 costs nothing.
    /// </summary>
    internal short? Copies => Holds(OffCopies, 2) ? ReadInt16(OffCopies) : null;

    /// <summary>True when the settings carry an explicit paper width and length (both flagged in dmFields and inside the public part).</summary>
    internal bool ReportsPaperSize =>
        Holds(OffPaperWidth, 2)
        && (ReadUInt32(OffFields) & (GdiNative.DM_PAPERWIDTH | GdiNative.DM_PAPERLENGTH)) == (GdiNative.DM_PAPERWIDTH | GdiNative.DM_PAPERLENGTH);

    /// <summary>True when the explicit paper size equals the given one within 1 mm.</summary>
    internal bool PaperIs(int widthTenthsMm, int lengthTenthsMm) =>
        ReportsPaperSize
        && Math.Abs(ReadInt16(OffPaperWidth) - widthTenthsMm) <= PaperToleranceTenths
        && Math.Abs(ReadInt16(OffPaperLength) - lengthTenthsMm) <= PaperToleranceTenths;

    /// <summary>Reads a WORD.</summary>
    internal ushort ReadUInt16(int offset) => (ushort)Marshal.ReadInt16(At(offset, 2));
    /// <summary>Reads a short.</summary>
    internal short ReadInt16(int offset) => Marshal.ReadInt16(At(offset, 2));
    /// <summary>Reads a DWORD.</summary>
    internal uint ReadUInt32(int offset) => (uint)Marshal.ReadInt32(At(offset, 4));
    /// <summary>Writes a WORD.</summary>
    internal void WriteUInt16(int offset, ushort value) => Marshal.WriteInt16(At(offset, 2), unchecked((short)value));
    /// <summary>Writes a short.</summary>
    internal void WriteInt16(int offset, short value) => Marshal.WriteInt16(At(offset, 2), value);
    /// <summary>Writes a DWORD.</summary>
    internal void WriteUInt32(int offset, uint value) => Marshal.WriteInt32(At(offset, 4), unchecked((int)value));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_buffer == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }

    /// <summary>
    /// Empties dmFormName (its DM_FORMNAME flag is cleared by the callers). The current settings name their paper ("Letter"), and a driver that
    /// resolves the paper by form name before dmPaperSize would otherwise let that stale name win over the request.
    /// </summary>
    private void ClearFormName()
    {
        if (Holds(OffFormName, 2)) WriteInt16(OffFormName, 0);
    }

    /// <summary>True when a field lies inside the allocation and inside the public part the driver declared in dmSize.</summary>
    private bool Holds(int offset, int length) =>
        OffSize + 2 <= Length && offset + length <= Length && offset + length <= ReadUInt16(OffSize);

    /// <summary>The address of a field, refusing any access outside the allocation (a programming error, never a driver answer).</summary>
    private IntPtr At(int offset, int length)
    {
        if (offset < 0 || offset + length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The field lies outside the DEVMODE buffer.");
        return Pointer + offset;
    }
}
