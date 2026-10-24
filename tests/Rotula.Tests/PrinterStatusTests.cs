using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>Realistic <c>~HS</c> replies, built field by field so each test says what it changes.</summary>
internal static class HsSamples
{
    public const char Stx = '\u0002';
    public const char Etx = '\u0003';

    /// <summary>String 1: aaa,b,c,dddd,eee,f,g,h,iii,j,k,l.</summary>
    public static string S1(int paperOut = 0, int pause = 0, int length = 786, int formats = 0, int bufferFull = 0, int diag = 0, int partial = 0,
        int corruptRam = 0, int under = 0, int over = 0) =>
        $"{Stx}030,{paperOut},{pause},{length:0000},{formats:000},{bufferFull},{diag},{partial},000,{corruptRam},{under},{over}{Etx}\r\n";

    /// <summary>String 2: mmm,n,o,p,q,r,s,t,uuuuuuuu,v,www.</summary>
    public static string S2(int headUp = 0, int ribbonOut = 0, int thermalTransfer = 0, char mode = '2', int labelWaiting = 0, long remaining = 0, int graphics = 0) =>
        $"{Stx}001,0,{headUp},{ribbonOut},{thermalTransfer},{mode},6,{labelWaiting},{remaining:00000000},1,{graphics:000}{Etx}\r\n";

    /// <summary>String 3: password and static RAM flag.</summary>
    public static string S3 => $"{Stx}1234,0{Etx}\r\n";

    /// <summary>A whole reply of an idle, ready printer (direct thermal, tear-off).</summary>
    public static string Ready => S1() + S2() + S3;
}

/// <summary>Decoding of the host status reply: every flag, and replies that are cut off, framed oddly or not a reply at all.</summary>
public class PrinterStatusParserTests
{
    [Fact]
    public void AReadyPrinter_IsAvailableCompleteIdleAndHasNoCondition()
    {
        var s = HostStatusParser.Parse(HsSamples.Ready);
        Assert.True(s.IsAvailable);
        Assert.Null(s.Reason);
        Assert.True(s.IsComplete);
        Assert.Empty(s.Conditions);
        Assert.False(s.IsBlocked);
        Assert.True(s.IsIdle);
        Assert.Equal(0, s.FormatsInBuffer);
        Assert.Equal(0, s.LabelsRemaining);
        Assert.Equal(786, s.LabelLengthDots);
        Assert.Equal(false, s.ThermalTransfer);
        Assert.Equal(PrinterPrintMode.TearOff, s.PrintMode);
        Assert.Equal(0, s.GraphicsStored);
    }

    [Fact]
    public void Bytes_DecodeLikeText()
    {
        var s = HostStatusParser.Parse(Encoding.ASCII.GetBytes(HsSamples.S1(paperOut: 1) + HsSamples.S2() + HsSamples.S3));
        Assert.Equal([PrinterCondition.PaperOut], s.Conditions);
    }

    [Fact]
    public void PaperOut_UsuallyComesWithPause_AndBothBlock_MostSeriousFirst()
    {
        var s = HostStatusParser.Parse(HsSamples.S1(paperOut: 1, pause: 1) + HsSamples.S2() + HsSamples.S3);
        Assert.Equal([PrinterCondition.PaperOut, PrinterCondition.Paused], s.Conditions);
        Assert.True(s.IsBlocked);
        Assert.Equal(s.Conditions, s.Blocking);
        Assert.Empty(s.Warnings);
    }

    [Theory]
    [InlineData("headUp", PrinterCondition.HeadOpen)]
    [InlineData("over", PrinterCondition.HeadTooHot)]
    [InlineData("diag", PrinterCondition.DiagnosticMode)]
    [InlineData("bufferFull", PrinterCondition.BufferFull)]
    [InlineData("pause", PrinterCondition.Paused)]
    [InlineData("paperOut", PrinterCondition.PaperOut)]
    public void EachBlockingFlag_IsDecoded(string flag, PrinterCondition expected)
    {
        string reply = flag switch
        {
            "headUp" => HsSamples.S1() + HsSamples.S2(headUp: 1),
            "over" => HsSamples.S1(over: 1) + HsSamples.S2(),
            "diag" => HsSamples.S1(diag: 1) + HsSamples.S2(),
            "bufferFull" => HsSamples.S1(bufferFull: 1) + HsSamples.S2(),
            "pause" => HsSamples.S1(pause: 1) + HsSamples.S2(),
            _ => HsSamples.S1(paperOut: 1) + HsSamples.S2(),
        };
        var s = HostStatusParser.Parse(reply + HsSamples.S3);
        Assert.Equal([expected], s.Conditions);
        Assert.True(s.IsBlocked);
        Assert.True(PrinterStatus.IsBlockingCondition(expected));
    }

    [Fact]
    public void RibbonOut_CountsOnlyInThermalTransferMode()
    {
        var transfer = HostStatusParser.Parse(HsSamples.S1() + HsSamples.S2(ribbonOut: 1, thermalTransfer: 1));
        Assert.Equal([PrinterCondition.RibbonOut], transfer.Conditions);
        Assert.Equal(true, transfer.ThermalTransfer);

        // Direct thermal needs no ribbon, so the sensor flag must not stop the job.
        var direct = HostStatusParser.Parse(HsSamples.S1() + HsSamples.S2(ribbonOut: 1, thermalTransfer: 0));
        Assert.Empty(direct.Conditions);
    }

    [Fact]
    public void NonBlockingFlags_AreWarnings()
    {
        var s = HostStatusParser.Parse(HsSamples.S1(under: 1, corruptRam: 1, partial: 1) + HsSamples.S2(labelWaiting: 1, mode: '1') + HsSamples.S3);
        Assert.Equal([PrinterCondition.HeadTooCold, PrinterCondition.CorruptRam, PrinterCondition.PartialFormat, PrinterCondition.LabelWaiting], s.Conditions);
        Assert.False(s.IsBlocked);
        Assert.Equal(s.Conditions, s.Warnings);
        Assert.Equal(PrinterPrintMode.PeelOff, s.PrintMode);
        Assert.Equal(true, s.PartialFormat);
        Assert.False(s.IsIdle);
    }

    [Fact]
    public void APrinterThatIsStillPrinting_IsNotIdle()
    {
        var s = HostStatusParser.Parse(HsSamples.S1(formats: 2) + HsSamples.S2(remaining: 37, graphics: 3) + HsSamples.S3);
        Assert.Equal(2, s.FormatsInBuffer);
        Assert.Equal(37, s.LabelsRemaining);
        Assert.Equal(3, s.GraphicsStored);
        Assert.False(s.IsIdle);
        Assert.False(HostStatusParser.Parse(HsSamples.S1() + HsSamples.S2(remaining: 1)).IsIdle);
        Assert.False(HostStatusParser.Parse(HsSamples.S1(formats: 1) + HsSamples.S2()).IsIdle);
    }

    [Theory]
    [InlineData('0', PrinterPrintMode.Rewind)]
    [InlineData('3', PrinterPrintMode.Cutter)]
    [InlineData('9', PrinterPrintMode.Rfid)]
    [InlineData('K', PrinterPrintMode.Kiosk)]
    [InlineData('s', PrinterPrintMode.Stream)]
    [InlineData('Z', PrinterPrintMode.Unknown)]
    public void PrintMode_IsDecoded_LettersIncluded(char code, PrinterPrintMode expected) =>
        Assert.Equal(expected, HostStatusParser.Parse(HsSamples.S1() + HsSamples.S2(mode: code)).PrintMode);

    [Fact]
    public void JunkAroundAndBetweenFrames_IsIgnored()
    {
        string reply = "garbage\r\n\u0006" + HsSamples.S1(paperOut: 1) + "xx\u0002not,a,status\u0003" + HsSamples.S2(headUp: 1) + "\0\0" + HsSamples.S3 + "trailing";
        var s = HostStatusParser.Parse(reply);
        Assert.True(s.IsComplete);
        Assert.Equal([PrinterCondition.HeadOpen, PrinterCondition.PaperOut], s.Conditions);
    }

    [Fact]
    public void StringsInAnotherOrder_StillDecode()
    {
        var s = HostStatusParser.Parse(HsSamples.S3 + HsSamples.S2(remaining: 5) + HsSamples.S1(formats: 1));
        Assert.True(s.IsComplete);
        Assert.Equal(1, s.FormatsInBuffer);
        Assert.Equal(5, s.LabelsRemaining);
    }

    [Fact]
    public void ACutOffReply_KeepsWhatIsWhole_AndIsNeverIdle()
    {
        // String 2 lost its end (no ETX): only string 1 counts, so head and batch are unknown.
        string reply = HsSamples.S1() + "\u0002001,0,1,0,0,2,6,0,000";
        var s = HostStatusParser.Parse(reply);
        Assert.True(s.IsAvailable);
        Assert.False(s.IsComplete);
        Assert.Null(s.LabelsRemaining);
        Assert.Null(s.ThermalTransfer);
        Assert.DoesNotContain(PrinterCondition.HeadOpen, s.Conditions);
        Assert.False(s.IsIdle);
    }

    [Fact]
    public void OnlyString2_StillReportsTheHead()
    {
        var s = HostStatusParser.Parse(HsSamples.S2(headUp: 1));
        Assert.True(s.IsAvailable);
        Assert.False(s.IsComplete);
        Assert.Equal([PrinterCondition.HeadOpen], s.Conditions);
        Assert.Null(s.FormatsInBuffer);
        Assert.False(s.IsIdle);
    }

    [Fact]
    public void AnSTXInsideAnOpenFrame_RestartsTheFrame()
    {
        string reply = "\u0002030,0,0" + HsSamples.S1(paperOut: 1) + HsSamples.S2();
        Assert.Equal([PrinterCondition.PaperOut], HostStatusParser.Parse(reply).Conditions);
    }

    [Fact]
    public void ARepliesWithoutControlCharacters_AreReadLineByLine()
    {
        string stripped = HsSamples.Ready.Replace("\u0002", "").Replace("\u0003", "");
        var s = HostStatusParser.Parse(stripped);
        Assert.True(s.IsComplete);
        Assert.True(s.IsIdle);
    }

    [Fact]
    public void TwoReplies_TheLastOneWins()
    {
        string reply = HsSamples.S1(formats: 1) + HsSamples.S2(remaining: 4) + HsSamples.S3 + HsSamples.Ready;
        Assert.True(HostStatusParser.Parse(reply).IsIdle);
    }

    [Fact]
    public void SpacesAroundFields_AreAccepted()
    {
        var s = HostStatusParser.Parse("\u0002030, 1,0,0786,000,0,0,0,000,0,0,0\u0003");
        Assert.Equal([PrinterCondition.PaperOut], s.Conditions);
    }

    [Theory]
    [InlineData("hello printer")]
    [InlineData("\u0002ERROR\u0003")]
    [InlineData("\u0002030,X,0,0786,000,0,0,0,000,0,0,0\u0003")] // a flag that is not a number
    [InlineData("\u0002030,0,0,0786,000,0,0,0,000,0,0\u0003")]   // a field short
    [InlineData("\u0002001,0,0,0,0,22,6,0,00000000,1,000\u0003")] // print mode of two characters
    public void Garbage_IsUnreadable_NeverAFault(string reply)
    {
        var s = HostStatusParser.Parse(reply);
        Assert.False(s.IsAvailable);
        Assert.Equal(StatusUnavailableReason.Unreadable, s.Reason);
        Assert.Empty(s.Conditions);
        Assert.False(s.IsBlocked);
        Assert.False(s.IsIdle);
    }

    [Fact]
    public void NothingAtAll_IsNoReply()
    {
        Assert.Equal(StatusUnavailableReason.NoReply, HostStatusParser.Parse((string?)null).Reason);
        Assert.Equal(StatusUnavailableReason.NoReply, HostStatusParser.Parse("").Reason);
        Assert.Equal(StatusUnavailableReason.NoReply, HostStatusParser.Parse(ReadOnlySpan<byte>.Empty).Reason);
    }

    [Fact]
    public void CompleteFrames_CountsOnlyClosedFrames()
    {
        Assert.Equal(3, HostStatusParser.CompleteFrames(Encoding.ASCII.GetBytes(HsSamples.Ready)));
        Assert.Equal(1, HostStatusParser.CompleteFrames(Encoding.ASCII.GetBytes(HsSamples.S1() + "\u0002001,0")));
        Assert.Equal(0, HostStatusParser.CompleteFrames(Encoding.ASCII.GetBytes("\u0003\u0003")));
    }

    [Fact]
    public void TheErrorOfABlockedPrinter_NamesTheWorstConditionAndListsAll()
    {
        var s = HostStatusParser.Parse(HsSamples.S1(paperOut: 1, pause: 1, under: 1) + HsSamples.S2(headUp: 1));
        var e = PrintError.FromStatus(s, "Zebra ZD420")!;
        Assert.Equal(PrintErrorKind.HeadOpen, e.Kind);
        Assert.Equal("Print.Error.HeadOpen", e.Key);
        Assert.Equal(["Zebra ZD420", "HeadOpen", "PaperOut", "Paused", "HeadTooCold"], e.Args);
        Assert.Null(PrintError.FromStatus(HostStatusParser.Parse(HsSamples.S1(under: 1)), "p"));
        Assert.Null(PrintError.FromStatus(PrinterStatus.Unavailable(StatusUnavailableReason.NoReply), "p"));
    }

    [Theory]
    [InlineData(PrinterCondition.HeadOpen, PrintErrorKind.HeadOpen)]
    [InlineData(PrinterCondition.PaperOut, PrintErrorKind.PaperOut)]
    [InlineData(PrinterCondition.RibbonOut, PrintErrorKind.RibbonOut)]
    [InlineData(PrinterCondition.HeadTooHot, PrintErrorKind.HeadTooHot)]
    [InlineData(PrinterCondition.DiagnosticMode, PrintErrorKind.DiagnosticMode)]
    [InlineData(PrinterCondition.BufferFull, PrintErrorKind.BufferFull)]
    [InlineData(PrinterCondition.Paused, PrintErrorKind.Paused)]
    public void EveryBlockingCondition_HasItsOwnErrorKind(PrinterCondition condition, PrintErrorKind kind)
    {
        string reply = condition switch
        {
            PrinterCondition.HeadOpen => HsSamples.S1() + HsSamples.S2(headUp: 1),
            PrinterCondition.PaperOut => HsSamples.S1(paperOut: 1) + HsSamples.S2(),
            PrinterCondition.RibbonOut => HsSamples.S1() + HsSamples.S2(ribbonOut: 1, thermalTransfer: 1),
            PrinterCondition.HeadTooHot => HsSamples.S1(over: 1) + HsSamples.S2(),
            PrinterCondition.DiagnosticMode => HsSamples.S1(diag: 1) + HsSamples.S2(),
            PrinterCondition.BufferFull => HsSamples.S1(bufferFull: 1) + HsSamples.S2(),
            _ => HsSamples.S1(pause: 1) + HsSamples.S2(),
        };
        Assert.Equal(kind, PrintError.FromStatus(HostStatusParser.Parse(reply), "p")!.Kind);
    }
}

/// <summary>A status reader that answers from a script and records how often it was asked.</summary>
internal sealed class ScriptedStatusReader : IPrinterStatusReader
{
    private readonly Func<int, PrinterStatus> _script;

    public ScriptedStatusReader(Func<int, PrinterStatus> script) => _script = script;

    public ScriptedStatusReader(params string[] replies) : this(n => HostStatusParser.Parse(replies[Math.Min(n, replies.Length) - 1])) { }

    public int Calls { get; private set; }

    /// <summary>Runs on each call, for example to cancel the caller in the middle of a verification.</summary>
    public Action<int>? OnCall { get; set; }

    public Task<PrinterStatus> ReadStatusAsync(PrinterTarget target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        OnCall?.Invoke(Calls);
        return Task.FromResult(_script(Calls));
    }
}

/// <summary>A spooler and network fake that records every job as text.</summary>
internal sealed class StatusTestPrinter : IRawPrinter, INetworkPrinter, IPrinterDiscovery
{
    public List<string> Sent { get; } = new();

    public Task<PrintResult> SendAsync(string printerName, string documentName, byte[] data, CancellationToken cancellationToken = default)
    {
        Sent.Add(Encoding.UTF8.GetString(data));
        return Task.FromResult(PrintResult.Ok(data.Length));
    }

    public Task<PrintResult> SendAsync(string host, int port, byte[] data, NetworkPrinterOptions options, CancellationToken cancellationToken = default)
    {
        Sent.Add(Encoding.UTF8.GetString(data));
        return Task.FromResult(PrintResult.Ok(data.Length));
    }

    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PrinterInfo>>(Array.Empty<PrinterInfo>());
}

/// <summary>The print service's check before a job and verification after it, against fakes (nothing is printed).</summary>
public class PrinterStatusServiceTests
{
    private static readonly PrinterStatusOptions Fast = PrinterStatusOptions.Default with
    {
        QueryTimeout = TimeSpan.FromMilliseconds(200),
        VerifyTimeout = TimeSpan.FromMilliseconds(300),
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    private static readonly PrinterTarget Tcp = PrinterTarget.TcpIp("192.168.1.50").WithStatus(Fast);

    private static (PrintService Svc, StatusTestPrinter Printer) Make(IPrinterStatusReader reader)
    {
        var printer = new StatusTestPrinter();
        return (new PrintService(printer, printer, printer, reader), printer);
    }

    private static LabelDocument Doc()
    {
        var d = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        d.Elements.Add(new TextElement { X = 10, Y = 10, Text = "Hola" });
        return d;
    }

    private static LabelDocument CounterLabel(int copies)
    {
        var doc = Doc();
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
        doc.Elements.Add(new TextElement { X = 10, Y = 40, Text = "{{serial}}" });
        doc.Print.Quantity = copies;
        return doc;
    }

    private static string[] Numbers(IEnumerable<string> jobs) => jobs.SelectMany(j => Regex.Matches(j, @"SN\d{4}").Select(m => m.Value)).ToArray();

    [Fact]
    public async Task WithoutStatusOptions_ThePrinterIsNeverAsked()
    {
        var reader = new ScriptedStatusReader(HsSamples.S1(paperOut: 1));
        var (svc, printer) = Make(reader);
        var r = await svc.PrintAsync(PrinterTarget.TcpIp("192.168.1.50"), Doc());
        Assert.True(r.Succeeded);
        Assert.Equal(0, reader.Calls);
        Assert.Single(printer.Sent);
        Assert.Same(PrinterStatusOptions.Off, PrinterTarget.Spooler("p").Status);
    }

    [Theory]
    [InlineData(PrintErrorKind.PaperOut)]
    [InlineData(PrintErrorKind.HeadOpen)]
    [InlineData(PrintErrorKind.Paused)]
    public async Task ABlockingFault_SendsNothing_AndSaysWhy(PrintErrorKind kind)
    {
        string reply = kind switch
        {
            PrintErrorKind.PaperOut => HsSamples.S1(paperOut: 1, pause: 1) + HsSamples.S2(),
            PrintErrorKind.HeadOpen => HsSamples.S1() + HsSamples.S2(headUp: 1),
            _ => HsSamples.S1(pause: 1) + HsSamples.S2(),
        };
        var (svc, printer) = Make(new ScriptedStatusReader(reply));
        var r = await svc.PrintAsync(Tcp, Doc());
        Assert.False(r.Succeeded);
        Assert.Equal(kind, r.Error!.Kind);
        Assert.Equal("Print.Error." + kind, r.Error.Key);
        Assert.Equal("192.168.1.50:9100", r.Error.Args[0]);
        Assert.Equal(0, r.BytesSent);
        Assert.Empty(printer.Sent);
        Assert.False(LabelPrintRun.MayHavePrinted(r));
    }

    [Fact]
    public async Task AnUnknownStatus_DoesNotBlock_ButSaysTheCheckWasSkipped()
    {
        var reader = new ScriptedStatusReader(_ => PrinterStatus.Unavailable(StatusUnavailableReason.NoReply));
        var (svc, printer) = Make(reader);
        var r = await svc.PrintAsync(Tcp.WithStatus(Fast with { VerifyAfterPrinting = false }), Doc());
        Assert.True(r.Succeeded);
        Assert.Single(printer.Sent);
        var n = Assert.Single(r.Notices);
        Assert.Equal(PrinterStatusKeys.NotChecked, n.MessageKey);
        Assert.Equal(["NoReply"], n.Args);
    }

    [Fact]
    public async Task AReaderThatThrows_IsTreatedAsNoStatus()
    {
        var (svc, printer) = Make(new ScriptedStatusReader(_ => throw new InvalidOperationException("broken")));
        var r = await svc.PrintAsync(Tcp.WithStatus(Fast with { VerifyAfterPrinting = false }), Doc());
        Assert.True(r.Succeeded);
        Assert.Single(printer.Sent);
        Assert.Equal(PrinterStatusKeys.NotChecked, Assert.Single(r.Notices).MessageKey);
    }

    [Fact]
    public async Task AWarningCondition_IsSentWithANotice()
    {
        var (svc, printer) = Make(new ScriptedStatusReader(HsSamples.S1(under: 1) + HsSamples.S2()));
        var r = await svc.PrintAsync(Tcp.WithStatus(Fast with { VerifyAfterPrinting = false }), Doc());
        Assert.True(r.Succeeded);
        Assert.Single(printer.Sent);
        var n = Assert.Single(r.Notices);
        Assert.Equal(PrinterStatusKeys.Warning, n.MessageKey);
        Assert.Equal(["HeadTooCold"], n.Args);
    }

    [Fact]
    public async Task Verification_WaitsUntilThePrinterFinished()
    {
        // Ready before, then still printing twice, then idle.
        var reader = new ScriptedStatusReader(HsSamples.Ready, HsSamples.S1(formats: 1) + HsSamples.S2(remaining: 3), HsSamples.S1() + HsSamples.S2(remaining: 1), HsSamples.Ready);
        var (svc, printer) = Make(reader);
        // A long verification limit: this test counts polls, and a busy CI runner can take longer than Fast's 300 ms for three of them.
        var r = await svc.PrintAsync(Tcp.WithStatus(Fast with { VerifyTimeout = TimeSpan.FromSeconds(30) }), Doc());
        Assert.True(r.Succeeded);
        Assert.Single(printer.Sent);
        Assert.Equal(4, reader.Calls);
        Assert.Equal(PrinterStatusKeys.Verified, Assert.Single(r.Notices).MessageKey);
    }

    [Fact]
    public async Task AFaultAfterTheJob_IsANotice_TheResultStaysSent()
    {
        var reader = new ScriptedStatusReader(HsSamples.Ready, HsSamples.S1(paperOut: 1, pause: 1, formats: 1) + HsSamples.S2(remaining: 2));
        var (svc, _) = Make(reader);
        var r = await svc.PrintAsync(Tcp, Doc());
        Assert.True(r.Succeeded);
        var n = Assert.Single(r.Notices);
        Assert.Equal(PrinterStatusKeys.FaultAfterJob, n.MessageKey);
        Assert.Equal(["PaperOut", "Paused"], n.Args);
    }

    [Fact]
    public async Task APrinterThatNeverFinishes_IsNotVerified_WithinTheTimeout()
    {
        var reader = new ScriptedStatusReader(HsSamples.Ready, HsSamples.S1(formats: 1) + HsSamples.S2(remaining: 9));
        var (svc, _) = Make(reader);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await svc.PrintAsync(Tcp, Doc());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(r.Succeeded);
        var n = Assert.Single(r.Notices);
        Assert.Equal(PrinterStatusKeys.NotVerified, n.MessageKey);
        Assert.Equal([PrinterStatusKeys.StillPrinting], n.Args);
    }

    [Fact]
    public async Task ATransportThatCannotRead_StopsVerifyingAtOnce()
    {
        var reader = new ScriptedStatusReader(_ => PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported));
        var (svc, _) = Make(reader);
        var r = await svc.PrintAsync(PrinterTarget.Spooler("USB printer").WithStatus(Fast with { VerifyTimeout = TimeSpan.FromMinutes(5) }), Doc());
        Assert.True(r.Succeeded);
        Assert.Equal(2, reader.Calls); // one before, one after
        Assert.Collection(r.Notices,
            n => Assert.Equal(PrinterStatusKeys.NotChecked, n.MessageKey),
            n => { Assert.Equal(PrinterStatusKeys.NotVerified, n.MessageKey); Assert.Equal(["NotSupported"], n.Args); });
    }

    [Fact]
    public async Task StoppingTheVerification_KeepsTheResultSent()
    {
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedStatusReader(HsSamples.Ready, HsSamples.S1(formats: 1) + HsSamples.S2(remaining: 9)) { OnCall = n => { if (n == 2) cts.Cancel(); } };
        var (svc, printer) = Make(reader);
        var r = await svc.PrintAsync(Tcp.WithStatus(Fast with { VerifyTimeout = TimeSpan.FromMinutes(5) }), Doc(), null, null, cts.Token);
        Assert.True(r.Succeeded);
        Assert.Single(printer.Sent);
        var n = Assert.Single(r.Notices);
        Assert.Equal(PrinterStatusKeys.NotVerified, n.MessageKey);
        Assert.Equal([PrinterStatusKeys.Stopped], n.Args);
    }

    [Fact]
    public async Task CancellingDuringTheCheck_SendsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (svc, printer) = Make(new ScriptedStatusReader(HsSamples.Ready));
        var r = await svc.PrintZplAsync(Tcp, "^XA^XZ", ZplEncoding.Utf8, cts.Token);
        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
        Assert.Empty(printer.Sent);
    }

    [Fact]
    public async Task ABlockedPrinter_GivesTheReservedSerialNumbersBack()
    {
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 1);
        var reader = new ScriptedStatusReader(HsSamples.S1(paperOut: 1) + HsSamples.S2(), HsSamples.Ready);
        var (svc, printer) = Make(reader);
        var options = new PrintVariableOptions { CounterStore = store };
        var target = Tcp.WithStatus(Fast with { VerifyAfterPrinting = false });

        Assert.Equal(PrintErrorKind.PaperOut, (await svc.PrintAsync(target, doc, null, null, options)).Error!.Kind);
        Assert.True((await svc.PrintAsync(target, doc, null, null, options)).Succeeded);

        // The held-back label printed nothing, so SN0001 was never used and goes on the next label.
        Assert.Equal(["SN0001"], Numbers(printer.Sent));
    }

    [Fact]
    public async Task AFaultInTheMiddleOfARun_StopsBeforeTheNextBlock_AndKeepsOnlyWhatWasSent()
    {
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 5);
        // Blocks of two labels: the first is checked and sent, then the printer runs out of labels.
        var reader = new ScriptedStatusReader(HsSamples.Ready, HsSamples.S1(paperOut: 1) + HsSamples.S2());
        var (svc, printer) = Make(reader);
        var options = new PrintVariableOptions { CounterStore = store, LabelsPerJob = 2 };

        var r = await svc.PrintAsync(Tcp, doc, null, null, options);
        Assert.Equal(PrintErrorKind.PaperOut, r.Error!.Kind);
        Assert.Equal(["SN0001", "SN0002"], Numbers(printer.Sent));
        Assert.Equal("SN0002", CounterAdmin.Peek(doc, "serial", store).LastValue);
    }

    [Fact]
    public async Task AFaultAfterTheJob_StillCountsTheLabelsAsPrinted()
    {
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 1);
        var reader = new ScriptedStatusReader(HsSamples.Ready, HsSamples.S1(paperOut: 1) + HsSamples.S2(), HsSamples.Ready, HsSamples.Ready);
        var (svc, printer) = Make(reader);
        var options = new PrintVariableOptions { CounterStore = store };

        var first = await svc.PrintAsync(Tcp, doc, null, null, options);
        Assert.Equal(PrinterStatusKeys.FaultAfterJob, Assert.Single(first.Notices).MessageKey);
        await svc.PrintAsync(Tcp, doc, null, null, options);

        // The label may have come out before the fault: its number is never printed again.
        Assert.Equal(["SN0001", "SN0002"], Numbers(printer.Sent));
    }

    [Fact]
    public async Task ReadyMadeZpl_IsCheckedAndVerifiedToo()
    {
        var blocked = Make(new ScriptedStatusReader(HsSamples.S1() + HsSamples.S2(ribbonOut: 1, thermalTransfer: 1)));
        var r = await blocked.Svc.PrintZplAsync(Tcp, "^XA^XZ", ZplEncoding.Utf8, Array.Empty<string>());
        Assert.Equal(PrintErrorKind.RibbonOut, r.Error!.Kind);
        Assert.Empty(blocked.Printer.Sent);

        var fine = Make(new ScriptedStatusReader(HsSamples.Ready));
        var ok = await fine.Svc.PrintZplAsync(Tcp, "^XA^XZ", ZplEncoding.Utf8);
        Assert.True(ok.Succeeded);
        Assert.Equal(PrinterStatusKeys.Verified, Assert.Single(ok.Notices).MessageKey);
    }

    [Fact]
    public async Task ApplyingTheProfile_DoesNotAskForTheStatus()
    {
        var reader = new ScriptedStatusReader(HsSamples.S1(paperOut: 1));
        var (svc, printer) = Make(reader);
        var r = await svc.ApplyProfileAsync(Tcp, new PrinterProfile());
        Assert.True(r.Succeeded);
        Assert.Equal(0, reader.Calls);
        Assert.Single(printer.Sent);
    }

    [Fact]
    public async Task GetStatus_GoesThroughTheReader()
    {
        var (svc, _) = Make(new ScriptedStatusReader(HsSamples.S1(paperOut: 1) + HsSamples.S2()));
        var s = await svc.GetStatusAsync(PrinterTarget.TcpIp("10.0.0.5"));
        Assert.Equal([PrinterCondition.PaperOut], s.Conditions);
    }

    [Fact]
    public async Task VerifyPrinted_CanBeCalledOnceAfterSeveralJobs()
    {
        var reader = new ScriptedStatusReader(HsSamples.S1(formats: 3) + HsSamples.S2(remaining: 2), HsSamples.Ready);
        var (svc, _) = Make(reader);
        var notes = await svc.VerifyPrintedAsync(Tcp.WithStatus(Fast with { VerifyTimeout = TimeSpan.FromSeconds(30) }));
        Assert.Equal(PrinterStatusKeys.Verified, Assert.Single(notes).MessageKey);
    }

    [Fact]
    public void Targets_CarryTheirStatusOptions_AndAName()
    {
        var t = PrinterTarget.TcpIp("printer.local", 6101).WithStatus(PrinterStatusOptions.Default);
        Assert.True(t.Status.CheckBeforePrinting);
        Assert.True(t.Status.Enabled);
        Assert.Equal("printer.local:6101", t.DisplayName);
        Assert.Equal("Zebra ZD420", PrinterTarget.Spooler("Zebra ZD420").DisplayName);
        Assert.False(PrinterStatusOptions.Off.Enabled);
        Assert.Same(PrinterStatusOptions.Off, t.WithStatus(null!).Status);
    }
}

/// <summary>The real status reader against an in-process listener (no printer, no network beyond loopback).</summary>
public class PrinterStatusTcpTests
{
    private static async Task<(TcpListener Listener, int Port)> Listen()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        await Task.Yield();
        return (l, ((IPEndPoint)l.LocalEndpoint).Port);
    }

    private static async Task<string> ReadQuery(NetworkStream s)
    {
        var buf = new byte[3];
        int got = 0;
        while (got < 3)
        {
            int n = await s.ReadAsync(buf.AsMemory(got));
            if (n == 0) break;
            got += n;
        }
        return Encoding.ASCII.GetString(buf, 0, got);
    }

    [Fact]
    public async Task SendsTildeHS_AndDecodesTheReply()
    {
        var (listener, port) = await Listen();
        using var _ = listener;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            string query = await ReadQuery(s);
            // Answer in two pieces, as a real printer may.
            byte[] reply = Encoding.ASCII.GetBytes(HsSamples.S1(formats: 1) + HsSamples.S2(remaining: 4) + HsSamples.S3);
            await s.WriteAsync(reply.AsMemory(0, 20));
            await Task.Delay(30);
            await s.WriteAsync(reply.AsMemory(20));
            // Keep the connection open: the reader must stop on the third frame, not wait for the close.
            await Task.Delay(2000);
            return query;
        });

        var status = await new PrinterStatusReader().ReadStatusAsync(PrinterTarget.TcpIp("127.0.0.1", port), TimeSpan.FromSeconds(1.5));
        Assert.True(status.IsComplete);
        Assert.Equal(1, status.FormatsInBuffer);
        Assert.Equal(4, status.LabelsRemaining);
        Assert.Equal("~HS", await server.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ASilentPrinter_IsNoReply_WithinTheTimeout()
    {
        var (listener, port) = await Listen();
        using var _ = listener;
        var accepted = listener.AcceptTcpClientAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var status = await new PrinterStatusReader().ReadStatusAsync(PrinterTarget.TcpIp("127.0.0.1", port), TimeSpan.FromMilliseconds(300));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal(StatusUnavailableReason.NoReply, status.Reason);
        (await accepted).Dispose();
    }

    [Fact]
    public async Task APartialReplyBeforeTheTimeout_IsStillDecoded()
    {
        var (listener, port) = await Listen();
        using var _ = listener;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            await ReadQuery(s);
            await s.WriteAsync(Encoding.ASCII.GetBytes(HsSamples.S1(paperOut: 1)));
            await Task.Delay(2000);
        });
        var status = await new PrinterStatusReader().ReadStatusAsync(PrinterTarget.TcpIp("127.0.0.1", port), TimeSpan.FromMilliseconds(400));
        Assert.True(status.IsAvailable);
        Assert.False(status.IsComplete);
        Assert.Equal([PrinterCondition.PaperOut], status.Conditions);
    }

    [Fact]
    public async Task APrinterThatClosesWithoutAnswering_IsNoReply()
    {
        var (listener, port) = await Listen();
        using var _ = listener;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            await ReadQuery(c.GetStream());
        });
        var status = await new PrinterStatusReader().ReadStatusAsync(PrinterTarget.TcpIp("127.0.0.1", port), TimeSpan.FromSeconds(2));
        Assert.False(status.IsAvailable);
        Assert.True(status.Reason is StatusUnavailableReason.NoReply or StatusUnavailableReason.Unreachable);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NothingListening_IsUnreachable()
    {
        var (listener, port) = await Listen();
        listener.Stop();
        var status = await new PrinterStatusReader().ReadStatusAsync(PrinterTarget.TcpIp("127.0.0.1", port), TimeSpan.FromSeconds(2));
        Assert.Equal(StatusUnavailableReason.Unreachable, status.Reason);
    }

    [Fact]
    public async Task ABadAddress_IsUnreachable_WithoutConnecting()
    {
        var status = await new PrinterStatusReader().ReadStatusAsync(PrinterTarget.TcpIp(" ", 9100), TimeSpan.FromSeconds(1));
        Assert.Equal(StatusUnavailableReason.Unreachable, status.Reason);
    }

    [Fact]
    public async Task Cancelling_Throws()
    {
        var (listener, port) = await Listen();
        using var _ = listener;
        var accepted = listener.AcceptTcpClientAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PrinterStatusReader().ReadStatusAsync(PrinterTarget.TcpIp("127.0.0.1", port), TimeSpan.FromSeconds(10), cts.Token));
        (await accepted).Dispose();
    }

    [Fact]
    public async Task AQueueThatDoesNotExist_FailsCleanly_AndQuickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var status = await new PrinterStatusReader().ReadStatusAsync(PrinterTarget.Spooler("Rotula test queue " + Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(2));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(status.IsAvailable);
        Assert.True(status.Reason is StatusUnavailableReason.NotSupported or StatusUnavailableReason.Unreachable, status.Reason.ToString());
    }
}
