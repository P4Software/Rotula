using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Rendering.Input;
using Rotula.Rendering.View;

namespace Rotula.Printing;

/// <summary>
/// What the driver pre-flight found for one label design on one driver. Errors block the job; warnings let it print once the person has
/// acknowledged exactly this set of findings (see <see cref="Fingerprint"/>). The findings are <see cref="ValidationIssue"/>s whose message keys the
/// app translates; this library holds no user-facing text.
/// </summary>
/// <param name="Findings">Everything found, errors and warnings, in a stable order (paper and driver first, then the label's content).</param>
/// <param name="Media">The driver media the findings were worked out for, or null when no usable media was reported.</param>
/// <param name="LabelTurned">
/// True when the label does not fit the driver's paper as designed but fits it turned a quarter turn: the findings and <paramref name="Media"/>
/// are then for the turned label (the driver was asked for the turned size), and printing turns every picture the same way
/// (<see cref="DevicePicture.TurnedClockwise"/>). The pre-flight and the print call reach this through the same decision, so they cannot differ.
/// </param>
public sealed record DriverPreflightResult(IReadOnlyList<ValidationIssue> Findings, DriverMedia? Media, bool LabelTurned = false)
{
    /// <summary>The result for a job that needs no pre-flight (printer language mode): nothing found, nothing to acknowledge.</summary>
    public static DriverPreflightResult None { get; } = new(Array.Empty<ValidationIssue>(), null);

    /// <summary>True when at least one finding is an error: nothing may be sent, whatever was acknowledged.</summary>
    public bool HasErrors => Findings.Any(f => f.Severity == ValidationSeverity.Error);

    /// <summary>True when at least one finding is a warning: the job may print only after the person acknowledged these findings.</summary>
    public bool NeedsAcknowledgement => Findings.Any(f => f.Severity == ValidationSeverity.Warning);

    /// <summary>
    /// SHA-256, as lower-case hexadecimal, over one line per finding (<c>MessageKey|arg|arg</c>), the lines sorted ordinally and joined with line
    /// feeds. An acknowledgement stores this value: it stays the same for the same findings in any order and on any machine, and changes as soon
    /// as a finding is added, removed or gets other arguments, so a changed design is asked about again. Inside an argument a backslash, a bar
    /// and a line break are escaped, so one argument can never pass for two. Worked out on every read (it is cheap) so a copy made with
    /// <c>with</c> never carries a value that belongs to other findings.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var lines = Findings.Select(Line).ToList();
            lines.Sort(StringComparer.Ordinal);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
            return Convert.ToHexStringLower(hash);
        }
    }

    private static string Line(ValidationIssue finding) =>
        string.Join('|', new[] { Escape(finding.MessageKey) }.Concat(finding.Args.Select(Escape)));

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\n", "\\n").Replace("\r", "\\r");
}

/// <summary>
/// Checks a label design against what a Windows printer driver reported, before anything is printed in picture mode. It looks at the design
/// once for the whole job (a 500-record batch is checked once) and never draws or allocates the picture.
/// <para>Findings (message key, severity, arguments in order):</para>
/// <list type="bullet">
/// <item><see cref="DriverUnavailable"/>, error: no usable media was reported (the printer or its driver cannot be reached, or it reports no
/// resolution or no printable area, or it accepted the label size but reports no physical page that holds its printable area). Reported alone.</item>
/// <item><see cref="TooLarge"/>, error: the picture would have more than <see cref="DevicePictureRenderer.MaxPixels"/> pixels (pixels, limit).</item>
/// <item><see cref="LabelLargerThanPaper"/>, error: the label is more than 1 mm wider or taller than the paper the driver will really use: the
/// physical page when the label size was accepted, the printable area of the driver's own paper when it was refused (label mm, printable mm).</item>
/// <item><see cref="MarginBand"/>, warning: the label size was accepted and the label fits the physical page, but it is more than 1 mm wider or
/// taller than the printable area, so anything of it in the strip along the edges the printer cannot print on may be cut off (label mm,
/// printable mm, widest unprintable edge mm). It comes for every such label, whatever its content: the check knows the page, not where the
/// design puts its ink.</item>
/// <item><see cref="LabelTurned"/>, warning: only from <see cref="Analyze(LabelDocument, DriverMedia?, bool)"/> with a turned label (label size as
/// designed, paper it lies on). The paper findings after it are then the turned label's.</item>
/// <item><see cref="PaperMismatch"/>, warning: the driver refused the label size as a custom paper size, so the label is centred on its own
/// paper (label mm, printable mm).</item>
/// <item><see cref="AxisRatio"/>, warning: the horizontal and vertical resolutions are not a whole ratio of each other (dpi x, dpi y).</item>
/// <item><see cref="RawBlocks"/>, warning: parts of the label that are printer commands or features the driver cannot print (count): every raw
/// ZPL block kept from an imported file, every element the renderer would only show as a stand-in (a barcode only the printer can draw, a
/// recall of a graphic stored on the printer, an image too large to draw) and every UL or CSA certification mark. <see cref="DevicePictureRenderer"/>
/// leaves exactly these out of the picture (one shared rule).</item>
/// <item><see cref="ModuleOneDot"/>, error: a barcode module is a single device dot (barcode name).</item>
/// <item><see cref="ModuleBelowMinimum"/>, warning: a barcode module is narrower than its symbology's minimum (barcode name, module mils,
/// minimum mils).</item>
/// <item><see cref="PrinterCounter"/>, error: a text or barcode whose data the printer fills in itself: it numbers it (<c>^SN</c>, <c>^SF</c>), dates
/// it (<c>^FC</c>) or expands embedded field references in it (<c>^FE</c>) (field name). The setting alone counts. Not counted with
/// <see cref="RawBlocks"/>.</item>
/// </list>
/// <see cref="Cancelled"/> is not produced here: the print service reports it alone when the driver could not be asked because the job was cancelled.
/// <see cref="TooManyLabels"/> (an error) is not produced here either: it judges the size of the whole job, not the design (see <see cref="CheckJobSize"/>).
/// Sizes are written as <c>WxH</c> in millimetres with at most one decimal (<c>100x50</c>, <c>99.5x49.9</c>), mils and the widest edge always
/// with one decimal (<c>3.3</c>, <c>1.5</c>), all in the invariant culture so the fingerprint never depends on the machine's language.
/// </summary>
public static class DriverPreflight
{
    /// <summary>Error key: the driver reported no usable media.</summary>
    public const string DriverUnavailable = "Driver.Preflight.DriverUnavailable";
    /// <summary>Warning key: the label holds raw ZPL blocks or other parts (printer-drawn barcodes, stored-graphic recalls, oversize images, UL/CSA marks) that picture mode cannot draw.</summary>
    public const string RawBlocks = "Driver.Preflight.RawBlocks";
    /// <summary>Error key: a barcode module is one device dot.</summary>
    public const string ModuleOneDot = "Driver.Preflight.ModuleOneDot";
    /// <summary>Warning key: a barcode module is below the symbology's minimum width.</summary>
    public const string ModuleBelowMinimum = "Driver.Preflight.ModuleBelowMinimum";
    /// <summary>Error key: the label does not fit the paper the driver will use (its page when the size was accepted, its printable area otherwise).</summary>
    public const string LabelLargerThanPaper = "Driver.Preflight.LabelLargerThanPaper";
    /// <summary>Warning key: the driver refused the label size as custom paper.</summary>
    public const string PaperMismatch = "Driver.Preflight.PaperMismatch";
    /// <summary>
    /// Warning key: the label fits the page the driver accepted, but it is more than 1 mm larger than the area the printer can print on, so
    /// anything of the label in the strip along the edges may be cut off. Arguments: label size, printable area, widest unprintable edge in mm.
    /// </summary>
    public const string MarginBand = "Driver.Preflight.MarginBand";
    /// <summary>Warning key: the two resolutions are not a whole ratio.</summary>
    public const string AxisRatio = "Driver.Preflight.AxisRatio";
    /// <summary>Error key: the picture would be too large to draw.</summary>
    public const string TooLarge = "Driver.Preflight.TooLarge";
    /// <summary>
    /// Error key: a text or barcode is numbered, dated or completed by the printer itself (^SN, ^SF, ^FC or ^FE embedded references), which a
    /// picture cannot do.
    /// </summary>
    public const string PrinterCounter = "Driver.Preflight.PrinterCounter";
    /// <summary>Error key: the pre-flight was cancelled before the driver answered, so nothing was judged and nothing may be printed.</summary>
    public const string Cancelled = "Driver.Preflight.Cancelled";
    /// <summary>
    /// Error key: the whole job would print more pictures than <see cref="MaxLabelsPerJob"/>. Arguments: the number of labels asked for, the limit
    /// (both whole numbers in the invariant culture). Reported alone, before the driver is asked anything.
    /// </summary>
    public const string TooManyLabels = "Driver.Preflight.TooManyLabels";
    /// <summary>
    /// Warning key: the label does not fit the driver's paper as designed but fits it turned a quarter turn clockwise, so Rotula turns the picture.
    /// Arguments: the label size as designed and the paper it lies on (both <c>WxH</c> in millimetres, the paper the other way round). It is a
    /// warning, so it needs the usual single acknowledgement: the person should see once that the printout is turned (and, if the printer feeds
    /// the other way, upside down), and the tick is remembered for exactly these findings like every other.
    /// </summary>
    public const string LabelTurned = "Driver.Preflight.LabelTurned";

    /// <summary>
    /// Most labels (so most pictures, one per copy) one picture-mode job may print. It is the limit the printer-language path already puts on
    /// separately numbered labels (<see cref="PrintVariableOptions.MaxIndividualLabels"/>), reused on purpose: a person learns one number for
    /// "too many labels in one go", and it is a sensible one for pictures too. In picture mode every copy is a page of its own in the Windows
    /// queue (a driver document holds <see cref="PrintVariableOptions.LabelsPerJob"/> pages, 50 by default), so 100,000 labels are 2,000
    /// documents and, at the driver's own page size, gigabytes of spool; the 99,999,999 copies the Copies box accepts would be about two million
    /// documents and would fill the disk long before the first label came out. Nobody prints a real run that large in one click: bigger runs
    /// are printed in batches.
    /// </summary>
    public const long MaxLabelsPerJob = PrintVariableOptions.MaxIndividualLabels;

    /// <summary>
    /// Judges only the size of a job: the error finding <see cref="TooManyLabels"/> when <paramref name="labelCount"/> pictures are more than
    /// <see cref="MaxLabelsPerJob"/>, otherwise null. It needs no driver, so it is the same for the pre-flight and for the print call that
    /// guards a caller who skipped the pre-flight. <paramref name="labelCount"/> is the total of the whole job: every record times its copies.
    /// </summary>
    /// <param name="labelCount">How many pictures (labels with their copies) the whole job prints.</param>
    public static ValidationIssue? CheckJobSize(long labelCount) =>
        labelCount <= MaxLabelsPerJob
            ? null
            : new ValidationIssue(ValidationSeverity.Error, TooManyLabels, null,
                labelCount.ToString(CultureInfo.InvariantCulture), MaxLabelsPerJob.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Paper sizes are reported in whole dots and often rounded to whole millimetres by drivers, so a label up to this much larger than the
    /// printable area is treated as fitting; beyond it the label would visibly be cut off.
    /// </summary>
    private const double PaperToleranceMm = 1.0;

    /// <summary>How far from a whole number the ratio of the two resolutions may be before modules stop landing on whole device dots.</summary>
    private const double AxisRatioTolerance = 0.01;

    /// <summary>
    /// Runs every check of the pre-flight for <paramref name="document"/> printed on <paramref name="media"/>. Objects marked "do not print" are
    /// skipped (they never reach the printer); objects with a print condition are checked, because some record of the job may print them.
    /// </summary>
    /// <param name="document">The design.</param>
    /// <param name="media">What the driver reported, or null when it could not be asked.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static DriverPreflightResult Analyze(LabelDocument document, DriverMedia? media) => Analyze(document, media, labelTurned: false);

    /// <summary>
    /// Like <see cref="Analyze(LabelDocument, DriverMedia?)"/>, for a label that is printed turned a quarter turn (<paramref name="labelTurned"/>):
    /// the paper checks judge the turned label (width and height swapped) against <paramref name="media"/>, which must be what the driver
    /// reported for the turned size, and the warning <see cref="LabelTurned"/> says what is done. Everything else (picture size in pixels, bars,
    /// raw blocks, printer-side fields) is the same as turning moves pixels without changing any of them.
    /// </summary>
    /// <param name="document">The design, as designed (not turned).</param>
    /// <param name="media">What the driver reported, or null when it could not be asked.</param>
    /// <param name="labelTurned">True when the picture will be turned to fit the paper.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static DriverPreflightResult Analyze(LabelDocument document, DriverMedia? media, bool labelTurned)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Without a resolution and a printable area nothing else can be judged (module sizes, picture size, fit), and the job cannot print
        // anyway, so this stands alone. A driver reporting a zero printable area has reported nothing usable: "larger than 0x0 mm paper" would mislead.
        // When the label size was accepted, the fit is judged against the physical page, which must at least hold the printable area drawn on it;
        // a driver claiming otherwise (no physical page, or one smaller than its own printable area) has not reported a page either.
        if (media is null || media.DpiX <= 0 || media.DpiY <= 0 || media.PrintableWidthDots <= 0 || media.PrintableHeightDots <= 0
            || (media.CustomSizeAccepted && (media.PhysicalWidthDots < media.PrintableWidthDots || media.PhysicalHeightDots < media.PrintableHeightDots)))
            return new DriverPreflightResult(new[] { new ValidationIssue(ValidationSeverity.Error, DriverUnavailable) }, null);

        int renderDpi = DevicePictureRenderer.RenderDpi(media);
        int designDpi = (int)document.Dpi;
        var findings = new List<ValidationIssue>();

        CheckPictureSize(document, renderDpi, findings);
        CheckPaper(document, media, labelTurned, findings);
        CheckAxisRatio(media, findings);
        CheckRawBlocks(document, findings);
        CheckBarcodeModules(document, designDpi, renderDpi, findings);
        CheckPrinterSideFields(document, findings);

        return new DriverPreflightResult(findings, media, labelTurned);
    }

    private static void CheckPrinterSideFields(LabelDocument document, List<ValidationIssue> findings)
    {
        // A field with Serial (^SN / ^SF), Clock (^FC) or EmbedCharacter (^FE) is written for the printer to fill in: it changes the number on
        // every label, puts its own date into the clock codes, or replaces references such as #2# with another field's data. A picture is drawn
        // once from values Rotula worked out and repeated for the copies, so every copy would carry the start value, the bare clock codes or the
        // bare reference, with nothing to tell the person. That is a wrong label, not a lost part, so it is an error (no acknowledgement lets it
        // through) and it is not counted with RawBlocks. The setting alone counts, whether or not the data holds a reference today: the rule
        // stays simple, and the data can change per record. Rotula's own counter and date variables are worked out on the computer and print
        // correctly in picture mode.
        foreach (var field in document.Elements.OfType<FieldDataElement>())
        {
            if (field.NotPrintable) continue;
            if (field.Serial is null && field.Clock is null && field.EmbedCharacter is null) continue;
            // A field left out of the picture entirely (a barcode only the printer can draw) prints no wrong value; it is already counted there.
            if (DevicePictureRenderer.IsLeftOutOfPicture(document, field)) continue;
            findings.Add(new ValidationIssue(ValidationSeverity.Error, PrinterCounter, field.Id, NameOf(field)));
        }
    }

    private static void CheckPictureSize(LabelDocument document, int renderDpi, List<ValidationIssue> findings)
    {
        // From the label size and the resolution only; the picture itself is never built here.
        var (width, height) = DevicePictureRenderer.PictureSize(document, renderDpi);
        long pixels = (long)width * height;
        if (pixels > DevicePictureRenderer.MaxPixels)
            findings.Add(new ValidationIssue(ValidationSeverity.Error, TooLarge, null,
                pixels.ToString(CultureInfo.InvariantCulture), DevicePictureRenderer.MaxPixels.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Judges the label against the paper the driver will really use. Which rectangle that is depends on how the label is placed
    /// (<c>GdiPageLayout.Place</c>):
    /// <list type="bullet">
    /// <item>Custom size accepted: the page is the label and the picture is drawn from the paper's corner, so the label must fit the
    /// <b>physical page</b>. Only a label larger than that is <see cref="LabelLargerThanPaper"/>. A label that fits the page but is more than
    /// 1 mm larger than the printable area on either axis covers part of the strip the printer cannot reach: <see cref="MarginBand"/>, a warning,
    /// whatever the label's content (the check knows the page, not where the design puts its ink), with the widest edge as a third value. Judging
    /// this case against the printable area, as before, refused every full-size label on die-cut drivers (DYMO, Brother) that report a small
    /// unprintable margin, with advice ("choose a larger paper size") that cannot help.</item>
    /// <item>Custom size refused: the label is centred in the printable area of the driver's own paper, so it must fit that area
    /// (<see cref="LabelLargerThanPaper"/>), and <see cref="PaperMismatch"/> is always reported.</item>
    /// </list>
    /// Every paper finding carries the label size and the printable area (both <c>WxH</c> in millimetres), so the sentence "larger than the area
    /// this printer can print on" stays true in both cases and the arguments of the fingerprint do not depend on which case applied.
    /// </summary>
    private static void CheckPaper(LabelDocument document, DriverMedia media, bool labelTurned, List<ValidationIssue> findings)
    {
        int designDpi = (int)document.Dpi;
        // The label's own size as designed (not the rounded dot size), so the message shows the figures the person typed.
        double designedW = UnitConverter.Convert(document.Width.Value, document.Width.Unit, LengthUnit.Millimeters, designDpi);
        double designedH = UnitConverter.Convert(document.Height.Value, document.Height.Unit, LengthUnit.Millimeters, designDpi);
        // A turned label lies on the paper the other way round: it is the turned size that must fit, and that the driver was asked for.
        double labelW = labelTurned ? designedH : designedW;
        double labelH = labelTurned ? designedW : designedH;
        // Each axis at its own resolution: the printable area and the page are measured in device dots of that axis.
        double printableW = UnitConverter.DotsToMm(media.PrintableWidthDots, media.DpiX);
        double printableH = UnitConverter.DotsToMm(media.PrintableHeightDots, media.DpiY);
        string label = Size(labelW, labelH), printable = Size(printableW, printableH);
        bool beyondPrintable = labelW > printableW + PaperToleranceMm || labelH > printableH + PaperToleranceMm;

        if (labelTurned)
        {
            // Said first among the paper findings: it explains the sizes of the others, which are the turned label's. The paper named is the
            // page the label lies on (the whole page when the driver took the size, else the printable area of its own paper).
            string paper = media.CustomSizeAccepted
                ? Size(UnitConverter.DotsToMm(media.PhysicalWidthDots, media.DpiX), UnitConverter.DotsToMm(media.PhysicalHeightDots, media.DpiY))
                : printable;
            findings.Add(new ValidationIssue(ValidationSeverity.Warning, LabelTurned, null, Size(designedW, designedH), paper));
        }

        if (!media.CustomSizeAccepted)
        {
            if (beyondPrintable) findings.Add(new ValidationIssue(ValidationSeverity.Error, LabelLargerThanPaper, null, label, printable));
            // A refused custom size is reported even when the label fits: it is then centred on paper of another size, which the person should know.
            findings.Add(new ValidationIssue(ValidationSeverity.Warning, PaperMismatch, null, label, printable));
            return;
        }

        double pageW = UnitConverter.DotsToMm(media.PhysicalWidthDots, media.DpiX);
        double pageH = UnitConverter.DotsToMm(media.PhysicalHeightDots, media.DpiY);
        if (labelW > pageW + PaperToleranceMm || labelH > pageH + PaperToleranceMm)
            findings.Add(new ValidationIssue(ValidationSeverity.Error, LabelLargerThanPaper, null, label, printable));
        // Only when the label fits its page: a label that does not fit is already an error, and the band would only repeat it.
        else if (beyondPrintable)
            findings.Add(new ValidationIssue(ValidationSeverity.Warning, MarginBand, null, label, printable,
                WidestUnprintableEdgeMm(media).ToString("0.0", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The widest of the four strips along the edges of the physical page the driver cannot print on, in millimetres: left and top are the
    /// offsets of the printable area, right and bottom are what the page has beyond it. The widest one, not an average, because a driver can keep
    /// its whole margin on one side (0 mm on the left and 3 mm on the right is a 3 mm edge, not 1.5 mm), and the person needs the figure that
    /// clears every edge. A negative strip (a driver whose offsets do not add up) counts as none. Worked out here rather than by the app, so the
    /// finding carries every value its text shows and any formatter can word it.
    /// </summary>
    private static double WidestUnprintableEdgeMm(DriverMedia media)
    {
        int right = media.PhysicalWidthDots - media.PrintableWidthDots - media.OffsetXDots;
        int bottom = media.PhysicalHeightDots - media.PrintableHeightDots - media.OffsetYDots;
        double horizontal = UnitConverter.DotsToMm(Math.Max(Math.Max(media.OffsetXDots, right), 0), media.DpiX);
        double vertical = UnitConverter.DotsToMm(Math.Max(Math.Max(media.OffsetYDots, bottom), 0), media.DpiY);
        return Math.Max(horizontal, vertical);
    }

    private static void CheckAxisRatio(DriverMedia media, List<ValidationIssue> findings)
    {
        // The picture is drawn at the coarser resolution and stretched by the device; only a whole stretch keeps every module on whole dots.
        double ratio = (double)Math.Max(media.DpiX, media.DpiY) / Math.Min(media.DpiX, media.DpiY);
        if (Math.Abs(ratio - Math.Round(ratio)) > AxisRatioTolerance)
            findings.Add(new ValidationIssue(ValidationSeverity.Warning, AxisRatio, null,
                media.DpiX.ToString(CultureInfo.InvariantCulture), media.DpiY.ToString(CultureInfo.InvariantCulture)));
    }

    private static void CheckRawBlocks(LabelDocument document, List<ValidationIssue> findings)
    {
        // Every part the picture leaves out counts: raw blocks (a configuration command is just as lost as a drawing one when no ZPL is sent),
        // elements the preview only shows as a stand-in, and the UL and CSA marks. It is the renderer's own rule, so what is reported and what is
        // left out of the picture can never differ.
        int count = document.Elements.Count(e => !e.NotPrintable && DevicePictureRenderer.IsLeftOutOfPicture(document, e));
        if (count > 0)
            findings.Add(new ValidationIssue(ValidationSeverity.Warning, RawBlocks, null, count.ToString(CultureInfo.InvariantCulture)));
    }

    private static void CheckBarcodeModules(LabelDocument document, int designDpi, int renderDpi, List<ValidationIssue> findings)
    {
        foreach (var barcode in document.Elements.OfType<BarcodeElement>())
        {
            if (barcode.NotPrintable) continue;
            // A barcode only the printer can draw is not in the picture at all (it is counted with the raw blocks), so its module is not judged.
            if (DevicePictureRenderer.IsLeftOutOfPicture(document, barcode)) continue;
            // MaxiCode (and any symbology without an adjustable module) has a fixed size: there is no module to judge.
            if (BarcodeSizing.ModuleDotsOf(barcode) is not { } moduleDots) continue;

            // The module the picture really gets (the scaler's own rounding rule), judged at the resolution the picture is drawn at.
            int deviceModule = DevicePictureRenderer.DeviceDots(moduleDots, designDpi, renderDpi);
            // The minimum depends on the content for QR Code: GS1's 0.375 mm applies to a GS1 Digital Link only, any other QR takes the matrix
            // fallback (ISO/IEC 18004 states none). The verdict and the minimum shown in the warning use the same content, so they never disagree.
            bool gs1DigitalLink = BarcodeSizing.HasGs1DigitalLinkContent(barcode);
            switch (BarcodeSizing.ModuleSafetyOf(barcode.Symbology, deviceModule, renderDpi, gs1DigitalLink))
            {
                case ModuleSafety.SingleDot:
                    findings.Add(new ValidationIssue(ValidationSeverity.Error, ModuleOneDot, barcode.Id, NameOf(barcode)));
                    break;
                case ModuleSafety.BelowMinimum:
                    findings.Add(new ValidationIssue(ValidationSeverity.Warning, ModuleBelowMinimum, barcode.Id, NameOf(barcode),
                        BarcodeSizing.MilsOf(deviceModule, renderDpi).ToString("0.0", CultureInfo.InvariantCulture),
                        BarcodeSizing.MinimumModuleMils(barcode.Symbology, gs1DigitalLink).ToString("0.0", CultureInfo.InvariantCulture)));
                    break;
            }
        }
    }

    /// <summary>
    /// The name a person recognises a field by: its own name, or else what it shows: a barcode's symbology, a text's first line (its template, so
    /// a data field reads <c>{{name}}</c>). Never the internal id: it means nothing to a reader, and a ZPL import makes new ids every time, which
    /// would change the fingerprint and ask the same question again. The id still travels as the finding's element id, so the app can point at
    /// the object.
    /// </summary>
    private static string NameOf(FieldDataElement field)
    {
        if (!string.IsNullOrWhiteSpace(field.Name)) return field.Name.Trim();
        return field switch
        {
            BarcodeElement barcode => BarcodeCatalog.Get(barcode.Symbology).DisplayName,
            TextElement text => FirstLine(text.GetTemplate()),
            _ => string.Empty,
        };
    }

    /// <summary>The first line of a text, trimmed: a wrapped block or text box can hold several lines, and one is enough to recognise it.</summary>
    private static string FirstLine(string text)
    {
        int end = text.AsSpan().IndexOfAny('\r', '\n');
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static string Size(double widthMm, double heightMm) =>
        widthMm.ToString("0.#", CultureInfo.InvariantCulture) + "x" + heightMm.ToString("0.#", CultureInfo.InvariantCulture);
}
