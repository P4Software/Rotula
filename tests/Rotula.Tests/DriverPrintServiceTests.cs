using System.Text;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>One job the fake driver received: where it went, the media it was told the pre-flight judged, and the pictures it accepted as pages, in order.</summary>
internal sealed record FakeDriverJob(string Printer, string Document, double WidthMm, double HeightMm, DriverMedia Media)
{
    /// <summary>The pages the driver accepted (a picture is added only once it is accepted, as GDI counts a page after EndPage).</summary>
    public List<DevicePicture> Pages { get; } = new();
}

/// <summary>
/// A Windows driver that records what the print service hands it and never prints. It keeps the contract of <see cref="GdiDriverPrinter"/>:
/// the page sequence is enumerated once and lazily, the token is checked before each picture is asked for, a page is counted only after it
/// is accepted, a cancelled probe ends as a cancelled task, and a failure is a value carrying the pages accepted so far.
/// </summary>
internal sealed class FakeDriverPrinter : IDriverPrinter
{
    private int _accepted;

    /// <summary>What <see cref="ProbeAsync"/> reports; null means the printer or its driver cannot be reached.</summary>
    public DriverMedia? Media { get; set; }

    /// <summary>
    /// When set, the media depends on the size asked for (a real driver answers per page size): called with the label's width and height in
    /// millimetres, it replaces <see cref="Media"/>. Used to model sideways stock, where the driver gives a page of one shape whatever is asked.
    /// </summary>
    public Func<double, double, DriverMedia?>? MediaFor { get; set; }

    /// <summary>Every probe: printer name and label size in millimetres.</summary>
    public List<(string Printer, double WidthMm, double HeightMm)> Probes { get; } = new();

    /// <summary>Every print job, in order.</summary>
    public List<FakeDriverJob> Jobs { get; } = new();

    /// <summary>Every accepted page of every job, in order.</summary>
    public List<DevicePicture> AllPages => Jobs.SelectMany(j => j.Pages).ToList();

    /// <summary>
    /// When set, the driver accepts this many pages in total (over all jobs) and refuses the next one with <see cref="Failure"/>, after it was
    /// handed over (like a StartPage that fails); nothing after it reaches the driver.
    /// </summary>
    public int? FailAfterPages { get; set; }

    /// <summary>The error of a refused page.</summary>
    public PrintError Failure { get; set; } = PrintError.Create(PrintErrorKind.DriverRefused, "Label Printer", "StartPage (Win32 error 1)");

    /// <summary>When set, the driver accepts this many pages in total, takes the next picture and then throws (a broken driver binding).</summary>
    public int? ThrowAfterPages { get; set; }

    /// <summary>Called after each accepted page with the total accepted so far; a test cancels its token here.</summary>
    public Action<int>? AfterPage { get; set; }

    /// <summary>When set, every job returns this result at once without taking a page (for example a printer that is gone).</summary>
    public PrintResult? Next { get; set; }

    /// <summary>Called after a job finished successfully, with the number of jobs so far; a test cancels its token here (between documents).</summary>
    public Action<int>? AfterJob { get; set; }

    /// <summary>
    /// When true, a cancel is not reported as a value: the driver takes the next picture and then throws <see cref="OperationCanceledException"/>
    /// (a binding that honours the token by throwing), so the service gets no page count back.
    /// </summary>
    public bool ThrowWhenCancelled { get; set; }

    public Task<DriverMedia?> ProbeAsync(string printerName, double labelWidthMm, double labelHeightMm, CancellationToken ct)
    {
        Probes.Add((printerName, labelWidthMm, labelHeightMm));
        // Like the real printer: a cancelled probe is a cancelled task, never "no media" (that would read as an unreachable driver).
        if (ct.IsCancellationRequested) return Task.FromCanceled<DriverMedia?>(ct);
        return Task.FromResult(MediaFor is { } byLabel ? byLabel(labelWidthMm, labelHeightMm) : Media);
    }

    public Task<DriverPrintOutcome> PrintAsync(string printerName, string documentName, double labelWidthMm, double labelHeightMm,
        DriverMedia media, IEnumerable<DevicePicture> pages, CancellationToken ct)
    {
        // Like the real printer: the media is required, because the job compares the driver's page with it before it starts.
        ArgumentNullException.ThrowIfNull(media);
        var job = new FakeDriverJob(printerName, documentName, labelWidthMm, labelHeightMm, media);
        Jobs.Add(job);
        if (Next is { } next) return Task.FromResult(new DriverPrintOutcome(next, 0));

        using var source = pages.GetEnumerator();
        while (true)
        {
            if (ct.IsCancellationRequested && !ThrowWhenCancelled) return Outcome(PrintError.Create(PrintErrorKind.Cancelled), job);
            if (!source.MoveNext()) break;
            if (ThrowWhenCancelled && ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (ThrowAfterPages is { } broken && _accepted >= broken) throw new InvalidOperationException("The fake driver broke down.");
            if (FailAfterPages is { } limit && _accepted >= limit) return Outcome(Failure, job);
            job.Pages.Add(source.Current);
            _accepted++;
            AfterPage?.Invoke(_accepted);
        }
        var finished = Outcome(null, job);
        AfterJob?.Invoke(Jobs.Count);
        return finished;
    }

    private static Task<DriverPrintOutcome> Outcome(PrintError? error, FakeDriverJob job)
    {
        int bytes = (int)Math.Min(job.Pages.Sum(p => (long)p.Gray8.Length), int.MaxValue);
        var result = error is null ? PrintResult.Ok(bytes) : PrintResult.Fail(error, bytes);
        return Task.FromResult(new DriverPrintOutcome(result.WithPages(job.Pages.Count), job.Pages.Count));
    }
}

/// <summary>
/// Picture mode in <see cref="PrintService"/>: the pre-flight and the acknowledgement gate, one rendered picture per distinct label, pages in
/// driver documents of <see cref="PrintVariableOptions.LabelsPerJob"/>, and serial numbers confirmed only for pages the driver may have printed.
/// </summary>
public class DriverPrintServiceTests
{
    private const string PrinterName = "Label Printer";
    private static readonly PrinterTarget DriverTarget = PrinterTarget.Spooler(PrinterName).WithMode(PrinterOutputMode.WindowsDriver);

    /// <summary>A memory counter store that also counts reservations, so a test can prove nothing was reserved.</summary>
    private sealed class SpyCounterStore : ICounterStore
    {
        private readonly MemoryCounterStore _inner = new();
        public int Reservations { get; private set; }
        public CounterRecord? Get(string key) => _inner.Get(key);
        public CounterReservation Reserve(string key, string fingerprint, long count)
        {
            Reservations++;
            return _inner.Reserve(key, fingerprint, count);
        }
        public bool Complete(CounterReservation reservation, long used, string? lastValue) => _inner.Complete(reservation, used, lastValue);
        public bool Reclaim(string key, string pendingId) => _inner.Reclaim(key, pendingId);
        public void Reset(string key, string fingerprint, long nextIndex = 0) => _inner.Reset(key, fingerprint, nextIndex);
        public void AcceptSettings(string key, string fingerprint) => _inner.AcceptSettings(key, fingerprint);
    }

    /// <summary>A status reader that only counts how often it was asked.</summary>
    private sealed class CountingStatusReader : IPrinterStatusReader
    {
        public int Calls { get; private set; }
        public Task<PrinterStatus> ReadStatusAsync(PrinterTarget target, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported));
        }
    }

    /// <summary>The service with fakes on every side; every picture is still drawn by the real renderer, and the values it was drawn with are recorded.</summary>
    private sealed class Rig
    {
        public required PrintService Service { get; init; }
        public required FakeSpooler Spooler { get; init; }
        public required FakeDriverPrinter Driver { get; init; }
        public required CountingStatusReader Status { get; init; }
        public List<IReadOnlyDictionary<string, string>?> Renders { get; } = new();
        public string?[] Serials => Renders.Select(v => v?["serial"]).ToArray();
    }

    private static Rig Make(DriverMedia? media = null, bool noMedia = false)
    {
        var spooler = new FakeSpooler();
        var driver = new FakeDriverPrinter { Media = noMedia ? null : media ?? MediaFor(50, 30, 203, 203) };
        var status = new CountingStatusReader();
        var rig = new Rig { Service = new PrintService(spooler, new TcpRawPrinter(), spooler, status, driver), Spooler = spooler, Driver = driver, Status = status };
        rig.Service.PictureRenderer = (doc, values, dpi) =>
        {
            rig.Renders.Add(values);
            return DevicePictureRenderer.Render(doc, values, dpi);
        };
        return rig;
    }

    /// <summary>Media whose page and printable area are exactly the label size at the given resolution, custom paper size accepted.</summary>
    private static DriverMedia MediaFor(double widthMm, double heightMm, int dpiX, int dpiY)
    {
        int width = (int)Math.Round(widthMm * dpiX / 25.4, MidpointRounding.AwayFromZero);
        int height = (int)Math.Round(heightMm * dpiY / 25.4, MidpointRounding.AwayFromZero);
        return new DriverMedia(dpiX, dpiY, width, height, width, height, 0, 0, true);
    }

    private static LabelDocument PlainLabel(int copies = 1)
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30), Encoding = ZplEncoding.Utf8 };
        doc.EnsureId();
        doc.Elements.Add(new TextElement { X = 10, Y = 10, Text = "Niño" });
        doc.Print.Quantity = copies;
        return doc;
    }

    private static LabelDocument CounterLabel(int copies = 1)
    {
        var doc = PlainLabel(copies);
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
        doc.Elements.Add(new TextElement { X = 10, Y = 60, Text = "{{serial}}" });
        return doc;
    }

    private static RawZplElement Raw(int x) => new() { Zpl = $"^FO{x},10^GB50,50,2^FS", IsVisual = true, X = x, Y = 10 };

    /// <summary>A Code 128 with 2-dot modules designed at 203 dpi: drawn at 100 dpi each module is a single device dot (an error finding).</summary>
    private static (LabelDocument Doc, DriverMedia Media) OneDotLabel()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(25), Dpi = PrinterDpi.Dpi203 };
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
        doc.Elements.Add(new BarcodeElement { Name = "SKU", Symbology = BarcodeSymbology.Code128, Data = "ROTULA-12345", ModuleWidth = 2, Height = 80, X = 20, Y = 20 });
        return (doc, MediaFor(50, 25, 100, 100));
    }

    private static PrintVariableOptions Options(ICounterStore? store, int perJob = 50) => new() { CounterStore = store, LabelsPerJob = perJob };

    private static int DistinctInstances(IEnumerable<DevicePicture> pages) => pages.Distinct(ReferenceEqualityComparer.Instance).Count();

    // ------------------------------------------------------------------------------------ printer language stays byte for byte

    [Fact]
    public async Task ZplModeSendsExactlyTheSameBytesAsBefore()
    {
        var rig = Make();
        var doc = PlainLabel();
        var profile = new PrinterProfile();
        // The stored expectation: exactly what the ZPL path always sent, set-up commands first, built from the generator directly.
        byte[] expected = new UTF8Encoding(false).GetBytes(PrinterSetup.BuildSetupZpl(profile) + ZplGenerator.Generate(doc).Zpl);

        var r = await rig.Service.PrintAsync(PrinterTarget.Spooler("Zebra ZD420"), doc, null, profile);

        Assert.True(r.Succeeded);
        Assert.Equal(expected, Assert.Single(rig.Spooler.Jobs).Data);
        Assert.Equal(0, r.PagesSent);
        Assert.Empty(rig.Driver.Probes);
        Assert.Empty(rig.Driver.Jobs);
        Assert.Empty(rig.Renders);
    }

    [Fact]
    public async Task RawZplNeverReachesAQueueInWindowsDriverMode()
    {
        // The second guard: every caller in the app already keeps ZPL away from a picture-mode queue, but the library refuses it too, so a future
        // caller that forgets cannot print ZPL as text on an office printer. A value, never an exception.
        var rig = Make();

        var plain = await rig.Service.PrintZplAsync(DriverTarget, "^XA^FO10,10^FDHello^FS^XZ", ZplEncoding.Utf8);
        var exact = await rig.Service.PrintZplAsync(DriverTarget, "^XA^XZ", ZplEncoding.Utf8, Array.Empty<string>());
        var profile = await rig.Service.ApplyProfileAsync(DriverTarget, new PrinterProfile { Darkness = 15 });

        Assert.All(new[] { plain, exact, profile }, r => Assert.Equal(PrintErrorKind.Unsupported, r.Error!.Kind));
        Assert.All(new[] { plain, exact, profile }, r => Assert.Equal(0, r.BytesSent));
        Assert.Empty(rig.Spooler.Jobs);
        Assert.Empty(rig.Driver.Jobs);
        Assert.Equal(0, rig.Status.Calls);

        // The same queue in printer-language mode, and a TCP printer (always printer language), still get their ZPL.
        Assert.True((await rig.Service.PrintZplAsync(PrinterTarget.Spooler(PrinterName), "^XA^XZ", ZplEncoding.Utf8)).Succeeded);
        Assert.True((await rig.Service.ApplyProfileAsync(PrinterTarget.Spooler(PrinterName), new PrinterProfile())).Succeeded);
        Assert.Equal(2, rig.Spooler.Jobs.Count);
    }

    [Fact]
    public async Task NoStatusQueryReachesAQueueInWindowsDriverMode()
    {
        // ~HS is a ZPL command: a picture-mode queue would print it as text. Status and verification answer "not supported" without asking.
        var rig = Make();
        var fast = DriverTarget.WithStatus(PrinterStatusOptions.Default with { PollInterval = TimeSpan.FromMilliseconds(1) });

        var status = await rig.Service.GetStatusAsync(DriverTarget);
        var verified = await rig.Service.VerifyPrintedAsync(fast);

        Assert.False(status.IsAvailable);
        Assert.Equal(StatusUnavailableReason.NotSupported, status.Reason);
        var note = Assert.Single(verified);
        Assert.Equal(PrinterStatusKeys.NotVerified, note.MessageKey);
        Assert.Equal(0, rig.Status.Calls);
        Assert.Empty(rig.Spooler.Jobs);

        // A printer-language target is still asked.
        await rig.Service.GetStatusAsync(PrinterTarget.Spooler(PrinterName));
        Assert.Equal(1, rig.Status.Calls);
    }

    [Fact]
    public async Task PreflightIsNoneForPrinterLanguageAndTcpTargetsWithoutAskingTheDriver()
    {
        var rig = Make();
        var doc = PlainLabel();
        doc.Elements.Add(Raw(300));

        var zpl = await rig.Service.PreflightAsync(PrinterTarget.Spooler(PrinterName), doc);
        // A TCP target has no driver: WithMode keeps it in printer language whatever is asked.
        var tcp = await rig.Service.PreflightAsync(PrinterTarget.TcpIp("10.0.0.5").WithMode(PrinterOutputMode.WindowsDriver), doc);

        Assert.Same(DriverPreflightResult.None, zpl);
        Assert.Same(DriverPreflightResult.None, tcp);
        Assert.Empty(rig.Driver.Probes);
    }

    // ------------------------------------------------------------------------------------ pre-flight and acknowledgement

    [Fact]
    public async Task PreflightProbesTheDriverWithTheLabelSize()
    {
        var rig = Make();
        var doc = PlainLabel();
        doc.Elements.Add(Raw(300));

        var result = await rig.Service.PreflightAsync(DriverTarget, doc);

        var probe = Assert.Single(rig.Driver.Probes);
        Assert.Equal(PrinterName, probe.Printer);
        Assert.Equal(50, probe.WidthMm, 6);
        Assert.Equal(30, probe.HeightMm, 6);
        Assert.Equal(DriverPreflight.Analyze(doc, rig.Driver.Media).Fingerprint, result.Fingerprint);
        Assert.Equal(DriverPreflight.RawBlocks, Assert.Single(result.Findings).MessageKey);
    }

    [Fact]
    public async Task DriverModeNeedsAcknowledgement()
    {
        var rig = Make();
        var store = new SpyCounterStore();
        var doc = CounterLabel(copies: 3);
        doc.Elements.Add(Raw(300));

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store));

        Assert.Equal(PrintErrorKind.NotAcknowledged, r.Error!.Kind);
        Assert.Contains(r.Notices, n => n.MessageKey == DriverPreflight.RawBlocks);
        Assert.Empty(rig.Driver.Jobs);
        Assert.Empty(rig.Spooler.Jobs);
        // Nothing reserved: the counter store was never asked for a number and still knows nothing of this counter.
        Assert.Equal(0, store.Reservations);
        Assert.False(CounterAdmin.Peek(doc, "serial", store).Exists);
    }

    [Fact]
    public async Task AcknowledgedFingerprintAllowsPrinting()
    {
        var rig = Make();
        var doc = PlainLabel(copies: 3);
        doc.Elements.Add(Raw(300));

        string fingerprint = (await rig.Service.PreflightAsync(DriverTarget, doc)).Fingerprint;
        var r = await rig.Service.PrintAsync(DriverTarget.WithAcknowledgement(fingerprint), doc);

        Assert.True(r.Succeeded, r.Error?.Key);
        Assert.Equal(3, r.PagesSent);
        Assert.Equal(3, rig.Driver.AllPages.Count);
        Assert.Empty(rig.Spooler.Jobs);
        var job = Assert.Single(rig.Driver.Jobs);
        Assert.Equal(PrinterName, job.Printer);
        Assert.Equal(50, job.WidthMm, 6);
        Assert.Equal(30, job.HeightMm, 6);
    }

    [Fact]
    public async Task EveryDocumentIsPrintedAgainstTheMediaThePreflightJudged()
    {
        // The driver compares its page with the probed media before each document starts, so the service must hand over exactly the media
        // the findings were worked out for: here 7 plain labels in documents of 3, then a resolved batch of 2 labels.
        var rig = Make();
        var r = await rig.Service.PrintAsync(DriverTarget, PlainLabel(copies: 7), null, null, Options(null, perJob: 3));
        var batch = await rig.Service.PrintResolvedAsync(DriverTarget, PlainLabel(), new[] { new ResolvedLabel(null, 1), new ResolvedLabel(null, 1) });

        Assert.True(r.Succeeded, r.Error?.Key);
        Assert.True(batch.Succeeded, batch.Error?.Key);
        Assert.Equal(4, rig.Driver.Jobs.Count);
        Assert.All(rig.Driver.Jobs, job => Assert.Same(rig.Driver.Media, job.Media));
    }

    [Fact]
    public async Task ADriverThatRefusesTheChangedPageSendsNothingAndGivesTheNumbersBack()
    {
        // The real driver printer refuses a document whose device page no longer matches the probe (DriverRefused, no page accepted). The
        // service passes that refusal on untouched, and every serial number goes back because no page may have printed.
        var rig = Make();
        var store = new SpyCounterStore();
        var doc = CounterLabel(copies: 2);
        var refusal = PrintError.Create(PrintErrorKind.DriverRefused, PrinterName, "the resolution changed after the check: 203 x 203 dpi then, 300 x 300 dpi now");
        rig.Driver.Next = PrintResult.Fail(refusal);

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store));

        Assert.Same(refusal, r.Error);
        Assert.Equal(0, r.PagesSent);
        Assert.Equal(0, CounterAdmin.Peek(doc, "serial", store).NextIndex);
    }

    [Fact]
    public async Task ChangedDesignInvalidatesTheAcknowledgement()
    {
        var rig = Make();
        var doc = PlainLabel();
        doc.Elements.Add(Raw(300));
        var target = DriverTarget.WithAcknowledgement((await rig.Service.PreflightAsync(DriverTarget, doc)).Fingerprint);

        // A second raw block changes the findings (2 parts instead of 1), so the old acknowledgement no longer covers the design.
        doc.Elements.Add(Raw(200));
        var r = await rig.Service.PrintAsync(target, doc);

        Assert.Equal(PrintErrorKind.NotAcknowledged, r.Error!.Kind);
        Assert.Empty(rig.Driver.Jobs);
    }

    [Fact]
    public async Task ErrorFindingBlocksEvenWhenAcknowledged()
    {
        var (doc, media) = OneDotLabel();
        var rig = Make(media);
        var store = new SpyCounterStore();
        var preflight = await rig.Service.PreflightAsync(DriverTarget, doc);
        Assert.True(preflight.HasErrors);

        var r = await rig.Service.PrintAsync(DriverTarget.WithAcknowledgement(preflight.Fingerprint), doc, null, null, Options(store));

        Assert.Equal(PrintErrorKind.InvalidLabel, r.Error!.Kind);
        Assert.Contains("Driver.Preflight.ModuleOneDot", r.Error.Args);
        Assert.Contains(r.Notices, n => n.MessageKey == DriverPreflight.ModuleOneDot && n.Args.SequenceEqual(new[] { "SKU" }));
        Assert.Empty(rig.Driver.Jobs);
        Assert.Equal(0, store.Reservations);
    }

    [Fact]
    public async Task PrinterGoneIsPrinterNotFound()
    {
        // Pre-flight: the driver cannot be reached, so the label cannot be judged and nothing is sent.
        var gone = Make(noMedia: true);
        var r = await gone.Service.PrintAsync(DriverTarget, PlainLabel());
        Assert.Equal(PrintErrorKind.InvalidLabel, r.Error!.Kind);
        Assert.Contains(DriverPreflight.DriverUnavailable, r.Error.Args);
        Assert.Empty(gone.Driver.Jobs);

        // The pre-flight passed but the printer disappeared before the job: the driver's own error comes back untouched.
        var rig = Make();
        var error = PrintError.Create(PrintErrorKind.PrinterNotFound, PrinterName);
        rig.Driver.Next = PrintResult.Fail(error);
        var lost = await rig.Service.PrintAsync(DriverTarget, PlainLabel());
        Assert.Same(error, lost.Error);
        Assert.Equal(0, lost.PagesSent);
    }

    [Fact]
    public async Task ACancelledProbeIsCancelledAndNeverAnUnreachableDriver()
    {
        var rig = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var printed = await rig.Service.PrintAsync(DriverTarget, PlainLabel(), null, null, null, cts.Token);
        var preflight = await rig.Service.PreflightAsync(DriverTarget, PlainLabel(), cts.Token);

        Assert.Equal(PrintErrorKind.Cancelled, printed.Error!.Kind);
        Assert.Empty(rig.Driver.Jobs);
        // A pre-flight result cannot be "cancelled", so it carries one error finding of its own: it stops the dialog, and it does not claim the
        // driver is unreachable.
        var finding = Assert.Single(preflight.Findings);
        Assert.Equal("Driver.Preflight.Cancelled", finding.MessageKey);
        Assert.Equal(DriverPreflight.Cancelled, finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Empty(finding.Args);
        Assert.True(preflight.HasErrors);
    }

    [Fact]
    public async Task AFieldNumberedByThePrinterIsRefusedBeforeAnythingIsReserved()
    {
        var rig = Make();
        var store = new SpyCounterStore();
        // Rotula's own counter (host side) would work, but the ^SN field is numbered by the printer: drawn once as a picture, all five copies
        // would carry 0001.
        var doc = CounterLabel(copies: 5);
        doc.Elements.Add(new TextElement { Name = "Box", X = 10, Y = 110, Text = "0001", Serial = new PrinterSerialOptions() });
        // Even an acknowledgement of exactly these findings does not let it through: it is an error.
        string fingerprint = (await rig.Service.PreflightAsync(DriverTarget, doc)).Fingerprint;

        var r = await rig.Service.PrintAsync(DriverTarget.WithAcknowledgement(fingerprint), doc, null, null, Options(store));

        Assert.Equal(PrintErrorKind.InvalidLabel, r.Error!.Kind);
        Assert.Contains("Driver.Preflight.PrinterCounter", r.Error.Args);
        Assert.Contains(r.Notices, n => n.MessageKey == DriverPreflight.PrinterCounter && n.Args.SequenceEqual(new[] { "Box" }));
        // Refused before LabelPrintRun.Begin: no number reserved, nothing drawn, no driver job.
        Assert.Equal(0, store.Reservations);
        Assert.False(CounterAdmin.Peek(doc, "serial", store).Exists);
        Assert.Empty(rig.Renders);
        Assert.Empty(rig.Driver.Jobs);
    }

    [Fact]
    public async Task DriverModeIgnoresTheProfileAndAsksNoStatus()
    {
        var rig = Make();
        var profile = new PrinterProfile { Darkness = 15, SpeedIps = 4 };

        var r = await rig.Service.PrintAsync(DriverTarget.WithStatus(PrinterStatusOptions.Default), PlainLabel(), null, profile);

        Assert.True(r.Succeeded, r.Error?.Key);
        Assert.Equal(1, r.PagesSent);
        Assert.Equal(0, rig.Status.Calls);
        Assert.Empty(rig.Spooler.Jobs);
    }

    // ------------------------------------------------------------------------------------ pictures and pages

    [Fact]
    public async Task CopiesRenderOnce()
    {
        var rig = Make();

        var r = await rig.Service.PrintAsync(DriverTarget, PlainLabel(copies: 500));

        Assert.True(r.Succeeded, r.Error?.Key);
        Assert.Equal(500, r.PagesSent);
        // One render for the whole run, even across driver documents: the copies are the same label.
        Assert.Single(rig.Renders);
        Assert.Equal(500, rig.Driver.AllPages.Count);
        Assert.Equal(1, DistinctInstances(rig.Driver.AllPages));
        // Documents of LabelsPerJob (50 by default) pages each.
        Assert.Equal(10, rig.Driver.Jobs.Count);
        Assert.All(rig.Driver.Jobs, j => Assert.Equal(50, j.Pages.Count));
    }

    [Fact]
    public async Task ACountingRunIsSplitIntoDocumentsOfLabelsPerJobInOrder()
    {
        var rig = Make();
        var store = new MemoryCounterStore();

        var r = await rig.Service.PrintAsync(DriverTarget, CounterLabel(copies: 10), null, null, Options(store, perJob: 4));

        Assert.True(r.Succeeded, r.Error?.Key);
        Assert.Equal(10, r.PagesSent);
        Assert.Equal(new[] { 4, 4, 2 }, rig.Driver.Jobs.Select(j => j.Pages.Count));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"SN{i:0000}"), rig.Serials);
        Assert.Equal(10, DistinctInstances(rig.Driver.AllPages));
    }

    [Fact]
    public async Task LargePicturesEndADriverDocumentEarly()
    {
        var rig = Make();
        var counting = CounterLabel(copies: 6);
        var (w, h) = DevicePictureRenderer.PictureSize(counting, 203);
        // Room for two distinct pictures per document: six different labels need three documents.
        rig.Service.ChunkPictureBudgetBytes = 2L * w * h;

        var r = await rig.Service.PrintAsync(DriverTarget, counting, null, null, Options(new MemoryCounterStore()));

        Assert.True(r.Succeeded, r.Error?.Key);
        Assert.Equal(new[] { 2, 2, 2 }, rig.Driver.Jobs.Select(j => j.Pages.Count));

        // Copies of one label share one picture, so they never hit the limit.
        var plain = Make();
        plain.Service.ChunkPictureBudgetBytes = 2L * w * h;
        Assert.True((await plain.Service.PrintAsync(DriverTarget, PlainLabel(copies: 6))).Succeeded);
        Assert.Equal(6, Assert.Single(plain.Driver.Jobs).Pages.Count);
    }

    // ------------------------------------------------------------------------------------ serial numbers

    [Fact]
    public async Task CounterRunConfirmsOnlySubmittedPages()
    {
        var rig = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 10);
        // The fifth page is refused when it is handed over; nothing after it reaches the driver.
        rig.Driver.FailAfterPages = 4;

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store));

        Assert.Equal(PrintErrorKind.DriverRefused, r.Error!.Kind);
        Assert.Equal(4, r.PagesSent);
        var status = CounterAdmin.Peek(doc, "serial", store);
        Assert.Equal(4, status.NextIndex);          // exactly 4 used ...
        Assert.Equal("SN0004", status.LastValue);
        Assert.Empty(status.Unconfirmed);            // ... and the other 6 given back, nothing left pending

        rig.Driver.FailAfterPages = null;
        rig.Renders.Clear();
        doc.Print.Quantity = 1;
        Assert.True((await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store))).Succeeded);
        Assert.Equal(new[] { "SN0005" }, rig.Serials);
    }

    [Fact]
    public async Task CancelCountsSubmittedPagesAsUsed()
    {
        var rig = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 10);
        using var cts = new CancellationTokenSource();
        rig.Driver.AfterPage = accepted => { if (accepted == 3) cts.Cancel(); };

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store), cts.Token);

        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
        Assert.Equal(3, r.PagesSent);
        var status = CounterAdmin.Peek(doc, "serial", store);
        Assert.Equal(3, status.NextIndex);
        Assert.Equal("SN0003", status.LastValue);
        Assert.Empty(status.Unconfirmed);
    }

    [Fact]
    public async Task ALabelThatCannotBeDrawnStopsBeforeItsDocumentIsSent()
    {
        var rig = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 10);
        var notDrawn = new ValidationIssue(ValidationSeverity.Error, DevicePictureRenderer.BarcodeNotDrawn, null, "Code 128", "bad");
        // The sixth label (second document of four) cannot be drawn faithfully for its values.
        rig.Service.PictureRenderer = (d, values, dpi) =>
        {
            rig.Renders.Add(values);
            return values?["serial"] == "SN0006" ? throw new DevicePictureException(new[] { notDrawn }, "test") : DevicePictureRenderer.Render(d, values, dpi);
        };

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store, perJob: 4));

        Assert.Equal(PrintErrorKind.InvalidLabel, r.Error!.Kind);
        Assert.Contains(DevicePictureRenderer.BarcodeNotDrawn, r.Error.Args);
        Assert.Contains(notDrawn, r.Notices);
        Assert.Equal(4, r.PagesSent);
        // Only the first document reached the driver; no page of the second one did.
        Assert.Equal(4, Assert.Single(rig.Driver.Jobs).Pages.Count);
        var status = CounterAdmin.Peek(doc, "serial", store);
        Assert.Equal(4, status.NextIndex);
        Assert.Empty(status.Unconfirmed);
    }

    [Fact]
    public async Task ADriverThatThrowsIsAFailedResultAndEveryPictureHandedOverStaysUsed()
    {
        var rig = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 6);
        // Three pages accepted, the fourth picture taken, then the driver binding breaks without reporting a count.
        rig.Driver.ThrowAfterPages = 3;

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store));

        Assert.False(r.Succeeded);
        Assert.Equal(PrintErrorKind.Unknown, r.Error!.Kind);
        Assert.Contains(nameof(InvalidOperationException), r.Error.Args);
        // Four pictures were handed to the driver, so four numbers may be on paper: the safe side for serial numbers.
        Assert.Equal(4, r.PagesSent);
        var status = CounterAdmin.Peek(doc, "serial", store);
        Assert.Equal(4, status.NextIndex);
        Assert.Empty(status.Unconfirmed);
    }

    [Fact]
    public async Task AFailureInALaterDocumentKeepsTheEarlierDocumentsAndTheAcceptedPages()
    {
        var rig = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 10);
        // Documents of 4: the first is accepted in full, the second takes one page and refuses the next, the third is never started.
        rig.Driver.FailAfterPages = 5;

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store, perJob: 4));

        Assert.Equal(PrintErrorKind.DriverRefused, r.Error!.Kind);
        Assert.Equal(new[] { 4, 1 }, rig.Driver.Jobs.Select(j => j.Pages.Count));
        // 4 (whole first document) + 1 (accepted of the failing one) = 5 used; SN0006 to SN0010 go back.
        Assert.Equal(5, r.PagesSent);
        var status = CounterAdmin.Peek(doc, "serial", store);
        Assert.Equal(5, status.NextIndex);
        Assert.Equal("SN0005", status.LastValue);
        Assert.Empty(status.Unconfirmed);

        rig.Driver.FailAfterPages = null;
        rig.Renders.Clear();
        doc.Print.Quantity = 1;
        Assert.True((await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store))).Succeeded);
        Assert.Equal(new[] { "SN0006" }, rig.Serials);
    }

    [Fact]
    public async Task ACancelBetweenDocumentsStopsBeforeTheNextOneIsDrawn()
    {
        var rig = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 10);
        using var cts = new CancellationTokenSource();
        // The cancel arrives after the driver finished the first document and before the service draws the second.
        rig.Driver.AfterJob = jobs => { if (jobs == 1) cts.Cancel(); };

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store, perJob: 4), cts.Token);

        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
        Assert.Equal(4, r.PagesSent);
        Assert.Equal(4, Assert.Single(rig.Driver.Jobs).Pages.Count);
        // Nothing of the second document was drawn, let alone sent.
        Assert.Equal(4, rig.Renders.Count);
        var status = CounterAdmin.Peek(doc, "serial", store);
        Assert.Equal(4, status.NextIndex);
        Assert.Equal("SN0004", status.LastValue);
        Assert.Empty(status.Unconfirmed);
    }

    [Fact]
    public async Task ADriverThatThrowsOnCancelCountsEveryPictureHandedOver()
    {
        var rig = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 10);
        using var cts = new CancellationTokenSource();
        // Three pages accepted, then the cancel; the driver takes the fourth picture and throws OperationCanceledException without a count.
        rig.Driver.ThrowWhenCancelled = true;
        rig.Driver.AfterPage = accepted => { if (accepted == 3) cts.Cancel(); };

        var r = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store), cts.Token);

        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
        // No count came back, so every picture handed over counts: 4, one more than the driver accepted (the safe side).
        Assert.Equal(4, r.PagesSent);
        var status = CounterAdmin.Peek(doc, "serial", store);
        Assert.Equal(4, status.NextIndex);
        Assert.Equal("SN0004", status.LastValue);
        Assert.Empty(status.Unconfirmed);
    }

    // ------------------------------------------------------------------------------------ test label and resolved labels

    [Fact]
    public async Task TheTestLabelGoesThroughTheDriverBranch()
    {
        var rig = Make();
        var profile = new PrinterProfile { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30), UseDetectedSettings = false };
        string fingerprint = (await rig.Service.PreflightAsync(DriverTarget, TestLabel.Build(profile))).Fingerprint;

        var r = await rig.Service.PrintTestLabelAsync(DriverTarget.WithAcknowledgement(fingerprint), profile);

        Assert.True(r.Succeeded, r.Error?.Key);
        Assert.Equal(1, r.PagesSent);
        Assert.Empty(rig.Spooler.Jobs);
    }

    [Fact]
    public async Task PrintResolvedAsyncPrintsEachResolvedLabel()
    {
        var rig = Make();
        var doc = PlainLabel();
        doc.Elements.Add(new TextElement { X = 10, Y = 60, Text = "{{name}}" });
        var labels = new[]
        {
            new ResolvedLabel(new Dictionary<string, string> { ["name"] = "ALPHA" }, 2),
            new ResolvedLabel(new Dictionary<string, string> { ["name"] = "OMEGA" }, 3),
        };

        var r = await rig.Service.PrintResolvedAsync(DriverTarget, doc, labels);

        Assert.True(r.Succeeded, r.Error?.Key);
        Assert.Equal(5, r.PagesSent);
        var pages = rig.Driver.AllPages;
        Assert.Equal(5, pages.Count);
        // One render per label, resubmitted for its copies, and the two labels really differ on paper.
        Assert.Equal(new[] { "ALPHA", "OMEGA" }, rig.Renders.Select(v => v!["name"]));
        Assert.Same(pages[0], pages[1]);
        Assert.Same(pages[2], pages[4]);
        Assert.NotSame(pages[0], pages[2]);
        Assert.False(pages[0].Gray8.AsSpan().SequenceEqual(pages[2].Gray8));
    }

    [Fact]
    public async Task PrintResolvedAsyncChecksItsInputAndTheSameGate()
    {
        var rig = Make();
        var doc = PlainLabel();

        // Zero copies would print nothing and report success: refused before the driver is touched.
        var zero = await rig.Service.PrintResolvedAsync(DriverTarget, doc, new[] { new ResolvedLabel(null, 1), new ResolvedLabel(null, 0) });
        Assert.Equal(PrintErrorKind.InvalidLabel, zero.Error!.Kind);
        Assert.Empty(rig.Driver.Probes);

        // Printer-language targets print batches as ZPL; this entry point is for picture mode only.
        var zpl = await rig.Service.PrintResolvedAsync(PrinterTarget.Spooler(PrinterName), doc, new[] { new ResolvedLabel(null, 1) });
        Assert.Equal(PrintErrorKind.Unsupported, zpl.Error!.Kind);

        // Nothing to print: success with no page and no driver call.
        var empty = await rig.Service.PrintResolvedAsync(DriverTarget, doc, Array.Empty<ResolvedLabel>());
        Assert.True(empty.Succeeded);
        Assert.Equal(0, empty.PagesSent);

        // The same acknowledgement gate as a single print.
        doc.Elements.Add(Raw(300));
        var unacknowledged = await rig.Service.PrintResolvedAsync(DriverTarget, doc, new[] { new ResolvedLabel(null, 2) });
        Assert.Equal(PrintErrorKind.NotAcknowledged, unacknowledged.Error!.Kind);
        Assert.Empty(rig.Driver.Jobs);
    }

    // ------------------------------------------------------------------------------------ the limit on labels in one picture job

    /// <summary>The most labels one picture job may print.</summary>
    private const int Cap = (int)DriverPreflight.MaxLabelsPerJob;

    [Fact]
    public void TheLimit_IsTheOneTheZplPathAlreadyHasForSeparateLabels() =>
        Assert.Equal(PrintVariableOptions.MaxIndividualLabels, DriverPreflight.MaxLabelsPerJob);

    [Fact]
    public async Task ThePreflight_OfAJobAtTheLimit_FindsNothingAboutItsSize()
    {
        var rig = Make();
        var result = await rig.Service.PreflightAsync(DriverTarget, PlainLabel(), Cap);
        Assert.DoesNotContain(result.Findings, f => f.MessageKey == DriverPreflight.TooManyLabels);
        Assert.False(result.HasErrors);
        // A job within the limit is pre-flighted as ever: the driver is asked.
        Assert.Single(rig.Driver.Probes);
    }

    [Fact]
    public async Task ThePreflight_OfAJobOneLabelOverTheLimit_IsAnErrorAlone_AndTheDriverIsNotAsked()
    {
        var rig = Make();
        var result = await rig.Service.PreflightAsync(DriverTarget, PlainLabel(), Cap + 1L);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(DriverPreflight.TooManyLabels, finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.True(finding.Args.SequenceEqual(new[] { "100001", "100000" }));
        Assert.True(result.HasErrors);
        Assert.False(result.NeedsAcknowledgement);
        Assert.Null(result.Media);
        Assert.Empty(rig.Driver.Probes);
    }

    [Fact]
    public async Task ThePreflight_OfAPrinterLanguageOrNetworkTarget_NeverJudgesTheSize()
    {
        var rig = Make();
        // The ZPL path has its own limit (Variable.Copies.TooMany); nothing about it may change, and the driver is never asked.
        var zpl = await rig.Service.PreflightAsync(PrinterTarget.Spooler(PrinterName), PlainLabel(), long.MaxValue);
        var tcp = await rig.Service.PreflightAsync(PrinterTarget.TcpIp("10.0.0.5").WithMode(PrinterOutputMode.WindowsDriver), PlainLabel(), long.MaxValue);
        Assert.Empty(zpl.Findings);
        Assert.Empty(tcp.Findings);
        Assert.Empty(rig.Driver.Probes);
    }

    [Fact]
    public async Task ThePreflight_WithoutACount_IsExactlyWhatItWas()
    {
        var rig = Make();
        var plain = await rig.Service.PreflightAsync(DriverTarget, PlainLabel());
        var counted = await rig.Service.PreflightAsync(DriverTarget, PlainLabel(), 1);
        Assert.Equal(plain.Fingerprint, counted.Fingerprint);
        Assert.Equal(plain.Findings.Count, counted.Findings.Count);
    }

    [Fact]
    public async Task APicturePrint_AtTheLimit_PrintsEveryCopyAsAPage()
    {
        var rig = Make();
        // 100,000 labels in two records: the copies of every record add up, and the total is exactly the limit.
        var result = await rig.Service.PrintResolvedAsync(DriverTarget, PlainLabel(), new[] { new ResolvedLabel(null, 60_000), new ResolvedLabel(null, 40_000) });
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(Cap, result.PagesSent);
        Assert.Equal(Cap, rig.Driver.AllPages.Count);
    }

    [Theory]
    [InlineData(1, 100_001)]      // one record asking for one copy too many
    [InlineData(2, 50_001)]       // copies x records is what counts, not the copies of one record
    [InlineData(1_001, 100)]      // many records with a few copies each
    public async Task APicturePrint_OverTheLimit_IsRefusedWithTheSameFinding_AndNothingIsSent(int records, int copies)
    {
        var rig = Make();
        var labels = Enumerable.Range(0, records).Select(_ => new ResolvedLabel(null, copies)).ToList();
        long total = (long)records * copies;
        // A caller that skipped the pre-flight, and one that holds an acknowledgement: neither gets past the size.
        var result = await rig.Service.PrintResolvedAsync(DriverTarget.WithAcknowledgement("anything"), PlainLabel(), labels);

        Assert.False(result.Succeeded);
        Assert.Equal(PrintErrorKind.InvalidLabel, result.Error!.Kind);
        var notice = Assert.Single(result.Notices);
        Assert.Equal(DriverPreflight.TooManyLabels, notice.MessageKey);
        Assert.Equal(ValidationSeverity.Error, notice.Severity);
        Assert.True(notice.Args.SequenceEqual(new[] { total.ToString(System.Globalization.CultureInfo.InvariantCulture), "100000" }));
        Assert.Equal(0, result.PagesSent);
        Assert.Empty(rig.Driver.Probes);
        Assert.Empty(rig.Driver.Jobs);
        Assert.Empty(rig.Renders);
        Assert.Empty(rig.Spooler.Jobs);
        Assert.Equal(0, rig.Status.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASinglePicturePrint_OverTheLimit_IsRefusedBeforeASerialNumberIsReserved(bool withCounter)
    {
        var rig = Make();
        var store = new SpyCounterStore();
        var doc = withCounter ? CounterLabel(Cap + 1) : PlainLabel(Cap + 1);
        var result = await rig.Service.PrintAsync(DriverTarget, doc, null, null, Options(store));

        Assert.False(result.Succeeded);
        Assert.Equal(PrintErrorKind.InvalidLabel, result.Error!.Kind);
        Assert.Equal(DriverPreflight.TooManyLabels, Assert.Single(result.Notices).MessageKey);
        Assert.Equal(0, store.Reservations);
        Assert.Empty(rig.Driver.Probes);
        Assert.Empty(rig.Driver.Jobs);
        Assert.Empty(rig.Renders);
    }

    // ------------------------------------------------------------------------------------ sideways stock is turned to fit

    /// <summary>
    /// A roll printer that only ever gives a page of one shape: asked for exactly <paramref name="stockWidthMm"/> x <paramref name="stockHeightMm"/> it
    /// takes the size, asked for anything else it keeps its own paper (the stock), whose printable area is the whole page.
    /// </summary>
    private static Func<double, double, DriverMedia?> Stock(double stockWidthMm, double stockHeightMm, int dpi = 300) => (w, h) =>
    {
        bool asked = Math.Abs(w - stockWidthMm) <= 1 && Math.Abs(h - stockHeightMm) <= 1;
        return MediaFor(stockWidthMm, stockHeightMm, dpi, dpi) with { CustomSizeAccepted = asked };
    };

    /// <summary>A label of the given size (millimetres at 203 dpi) with a text and a Code 128 barcode.</summary>
    private static LabelDocument Sized(double widthMm, double heightMm)
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(widthMm), Height = Measure.Millimeters(heightMm), Encoding = ZplEncoding.Utf8 };
        doc.EnsureId();
        doc.Elements.Add(new TextElement { X = 10, Y = 10, Text = "Niño" });
        doc.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "ROTULA-12345", ModuleWidth = 2, Height = 80, X = 20, Y = 60 });
        return doc;
    }

    private static readonly ResolvedLabel[] ThreeCopies = [new ResolvedLabel(null, 3)];

    [Fact]
    public void TurnedClockwise_MovesEveryPixel_AndFourTurnsAreTheIdentity()
    {
        var picture = new DevicePicture(3, 2, new byte[] { 1, 2, 3, 4, 5, 6 });
        var turned = picture.TurnedClockwise();
        // The old left column, bottom to top, becomes the top row: 4 1 / 5 2 / 6 3.
        Assert.Equal((2, 3), (turned.WidthPx, turned.HeightPx));
        Assert.Equal(new byte[] { 4, 1, 5, 2, 6, 3 }, turned.Gray8);
        Assert.Equal(picture.Gray8, turned.TurnedClockwise().TurnedClockwise().TurnedClockwise().Gray8);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, picture.Gray8);   // the original is untouched
    }

    [Fact]
    public async Task SidewaysStock_TurnsTheLabel_AndTheJobPrintsTheTurnedPicture()
    {
        // Designed 150 x 100 (landscape) on stock the driver gives as 100 x 150 only.
        var rig = Make();
        rig.Driver.MediaFor = Stock(100, 150);
        var doc = Sized(150, 100);

        var preflight = await rig.Service.PreflightAsync(DriverTarget, doc);
        Assert.True(preflight.LabelTurned);
        Assert.False(preflight.HasErrors, string.Join(", ", preflight.Findings.Select(f => f.MessageKey)));
        // A warning, so the usual single acknowledgement: it names the label as designed and the paper it lies on.
        var turnedFinding = Assert.Single(preflight.Findings);
        Assert.Equal(DriverPreflight.LabelTurned, turnedFinding.MessageKey);
        Assert.Equal(ValidationSeverity.Warning, turnedFinding.Severity);
        Assert.True(turnedFinding.Args.SequenceEqual(new[] { "150x100", "100x150" }));
        Assert.True(preflight.NeedsAcknowledgement);
        Assert.True(preflight.Media!.CustomSizeAccepted);   // the driver was asked for, and took, the turned size

        // Not acknowledged: nothing is sent.
        var refused = await rig.Service.PrintResolvedAsync(DriverTarget, doc, ThreeCopies);
        Assert.Equal(PrintErrorKind.NotAcknowledged, refused.Error!.Kind);
        Assert.Empty(rig.Driver.Jobs);

        // Acknowledged with the pre-flight's own fingerprint: the job is for the turned label.
        var result = await rig.Service.PrintResolvedAsync(DriverTarget.WithAcknowledgement(preflight.Fingerprint), doc, ThreeCopies);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(3, result.PagesSent);
        var job = Assert.Single(rig.Driver.Jobs);
        Assert.Equal((100.0, 150.0), (Math.Round(job.WidthMm, 1), Math.Round(job.HeightMm, 1)));
        // The media the job was started with is the one the pre-flight judged (the media for the turned size).
        Assert.Equal(preflight.Media, job.Media);
        var (designedW, designedH) = DevicePictureRenderer.PictureSize(doc, 300);
        Assert.All(job.Pages, p => Assert.Equal((designedH, designedW), (p.WidthPx, p.HeightPx)));
        Assert.True(designedW > designedH);   // drawn landscape, printed portrait
    }

    [Fact]
    public async Task ASinglePrint_OnSidewaysStock_AgreesWithThePreflight()
    {
        var rig = Make();
        rig.Driver.MediaFor = Stock(100, 150);
        var doc = Sized(150, 100);
        doc.Print.Quantity = 2;

        var preflight = await rig.Service.PreflightAsync(DriverTarget, doc);
        var unticked = await rig.Service.PrintAsync(DriverTarget, doc);
        Assert.Equal(PrintErrorKind.NotAcknowledged, unticked.Error!.Kind);
        Assert.Empty(rig.Driver.Jobs);

        var result = await rig.Service.PrintAsync(DriverTarget.WithAcknowledgement(preflight.Fingerprint), doc);
        Assert.True(result.Succeeded, result.Error?.ToString());
        var job = Assert.Single(rig.Driver.Jobs);
        Assert.Equal(2, job.Pages.Count);
        Assert.Equal((100.0, 150.0), (Math.Round(job.WidthMm, 1), Math.Round(job.HeightMm, 1)));
        Assert.All(job.Pages, p => Assert.True(p.WidthPx < p.HeightPx));
    }

    [Fact]
    public async Task ALabelThatFitsAsDesigned_IsNeverTurned()
    {
        // Exactly the stock: accepted, one question to the driver, nothing found.
        var exact = Make();
        exact.Driver.MediaFor = Stock(100, 150);
        var doc = Sized(100, 150);
        var first = await exact.Service.PreflightAsync(DriverTarget, doc);
        Assert.False(first.LabelTurned);
        Assert.Empty(first.Findings);
        Assert.Single(exact.Driver.Probes);
        Assert.True((await exact.Service.PrintResolvedAsync(DriverTarget, doc, ThreeCopies)).Succeeded);
        var page = Assert.Single(exact.Driver.Jobs).Pages[0];
        Assert.True(page.WidthPx < page.HeightPx);

        // Smaller than paper of its own (a refused size, label centred, the usual warning): it fits, so it stays as designed even though it
        // would also fit turned.
        var roomy = Make();
        roomy.Driver.MediaFor = Stock(210, 297);
        var landscape = await roomy.Service.PreflightAsync(DriverTarget, Sized(150, 100));
        Assert.False(landscape.LabelTurned);
        Assert.DoesNotContain(landscape.Findings, f => f.MessageKey == DriverPreflight.LabelTurned);
        Assert.Contains(landscape.Findings, f => f.MessageKey == DriverPreflight.PaperMismatch);
        Assert.Single(roomy.Driver.Probes);
    }

    [Fact]
    public async Task WhenNeitherWayFits_TheOrdinaryRefusalStands()
    {
        var rig = Make();
        rig.Driver.MediaFor = Stock(100, 150);
        var doc = Sized(200, 120);
        var preflight = await rig.Service.PreflightAsync(DriverTarget, doc);

        Assert.False(preflight.LabelTurned);
        Assert.True(preflight.HasErrors);
        var finding = Assert.Single(preflight.Findings, f => f.MessageKey == DriverPreflight.LabelLargerThanPaper);
        Assert.Equal("200x120", finding.Args[0]);   // the label as designed, not turned
        Assert.DoesNotContain(preflight.Findings, f => f.MessageKey == DriverPreflight.LabelTurned);
        Assert.Equal(2, rig.Driver.Probes.Count);   // it was looked at turned, and did not fit either

        var result = await rig.Service.PrintResolvedAsync(DriverTarget.WithAcknowledgement(preflight.Fingerprint), doc, ThreeCopies);
        Assert.Equal(PrintErrorKind.InvalidLabel, result.Error!.Kind);
        Assert.Empty(rig.Driver.Jobs);
    }

    [Fact]
    public async Task ASquareLabel_HasNothingToTurn_AndTheDriverIsAskedOnce()
    {
        var rig = Make();
        rig.Driver.MediaFor = Stock(60, 60);
        var preflight = await rig.Service.PreflightAsync(DriverTarget, Sized(100, 100));
        Assert.Contains(preflight.Findings, f => f.MessageKey == DriverPreflight.LabelLargerThanPaper);
        Assert.False(preflight.LabelTurned);
        Assert.Single(rig.Driver.Probes);
    }

    [Fact]
    public async Task IfTheDriverCannotAnswerForTheTurnedSize_TheOrdinaryRefusalStands_NotDriverUnavailable()
    {
        var rig = Make();
        var stock = Stock(100, 150);
        rig.Driver.MediaFor = (w, h) => w > h ? stock(w, h) : null;   // answers for the landscape request only
        var preflight = await rig.Service.PreflightAsync(DriverTarget, Sized(150, 100));
        Assert.False(preflight.LabelTurned);
        Assert.Contains(preflight.Findings, f => f.MessageKey == DriverPreflight.LabelLargerThanPaper);
        Assert.DoesNotContain(preflight.Findings, f => f.MessageKey == DriverPreflight.DriverUnavailable);
    }

    [Fact]
    public async Task TurningMovesPixels_SoEveryBarcodeModuleKeepsItsExactSize()
    {
        // The real renderer's picture for a label with a Code 128: every pixel row's runs of black must be, unchanged, the runs of the matching
        // column of the turned picture.
        var doc = Sized(150, 100);
        var picture = DevicePictureRenderer.Render(doc, null, 300);
        var turned = picture.TurnedClockwise();
        Assert.Equal((picture.HeightPx, picture.WidthPx), (turned.WidthPx, turned.HeightPx));

        static List<(int Start, int Length)> Runs(IEnumerable<byte> line)
        {
            var runs = new List<(int, int)>();
            int start = -1, i = 0;
            foreach (byte value in line)
            {
                if (value < 128 && start < 0) start = i;
                if (value >= 128 && start >= 0) { runs.Add((start, i - start)); start = -1; }
                i++;
            }
            if (start >= 0) runs.Add((start, i - start));
            return runs;
        }

        int moduleRuns = 0;
        for (int y = 0; y < picture.HeightPx; y += 7)
        {
            var row = Runs(picture.Gray8.Skip(y * picture.WidthPx).Take(picture.WidthPx));
            int column = turned.WidthPx - 1 - y;
            var down = Runs(Enumerable.Range(0, turned.HeightPx).Select(r => turned.Gray8[r * turned.WidthPx + column]));
            Assert.Equal(row, down);
            moduleRuns += row.Count;
        }
        Assert.True(moduleRuns > 50, "the label has a barcode, so there are many runs to compare");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task APrinterLanguagePrint_IsNeverTurned_NorEvenAsked()
    {
        var onStock = Make();
        onStock.Driver.MediaFor = Stock(100, 150);
        var other = Make();
        other.Driver.MediaFor = Stock(150, 100);
        var zpl = PrinterTarget.Spooler(PrinterName);
        var doc = Sized(150, 100);

        Assert.Empty((await onStock.Service.PreflightAsync(zpl, doc)).Findings);
        Assert.False((await onStock.Service.PreflightAsync(zpl, doc)).LabelTurned);
        Assert.True((await onStock.Service.PrintAsync(zpl, doc)).Succeeded);
        Assert.True((await other.Service.PrintAsync(zpl, doc)).Succeeded);

        Assert.Empty(onStock.Driver.Probes);
        Assert.Empty(onStock.Driver.Jobs);
        // The ZPL is the same whatever paper a driver would report: it does not depend on any of this.
        Assert.Equal(Assert.Single(other.Spooler.Jobs).Data, Assert.Single(onStock.Spooler.Jobs).Data);
    }
}
