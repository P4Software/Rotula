using System.Runtime.CompilerServices;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>A winspool stand-in that records the order of the calls and fails or cancels where a test says so.</summary>
internal sealed class RecordingSpoolerApi : ISpoolerApi
{
    public List<string> Calls { get; } = new();
    public int Written { get; private set; }
    public int WriteCalls { get; private set; }
    public bool Offline { get; set; }
    public bool FailStartPage { get; set; }
    public bool FailEndDoc { get; set; }

    /// <summary>The write call (1-based) that fails; 0 never.</summary>
    public int FailWriteAt { get; set; }

    /// <summary>Called after each successful write (cancel the token from here).</summary>
    public Action<int>? AfterWrite { get; set; }

    public bool Open(string printerName, out IntPtr handle) { Calls.Add("Open"); handle = new IntPtr(42); return true; }
    public bool IsOffline(IntPtr handle) => Offline;
    public bool StartDoc(IntPtr handle, string documentName) { Calls.Add("StartDoc"); return true; }
    public bool StartPage(IntPtr handle) { Calls.Add("StartPage"); return !FailStartPage; }

    public bool Write(IntPtr handle, byte[] data, int offset, int count, out int written)
    {
        WriteCalls++;
        Calls.Add("Write");
        if (FailWriteAt == WriteCalls) { written = 0; return false; }
        written = count;
        Written += count;
        AfterWrite?.Invoke(WriteCalls);
        return true;
    }

    public bool EndPage(IntPtr handle) { Calls.Add("EndPage"); return true; }
    public bool EndDoc(IntPtr handle) { Calls.Add("EndDoc"); return !FailEndDoc; }
    public bool Abort(IntPtr handle) { Calls.Add("Abort"); return true; }
    public void Close(IntPtr handle) => Calls.Add("Close");
    public int LastError() => 0;
}

/// <summary>A job that does not finish must be aborted, never ended: ending commits a truncated format to the printer.</summary>
public class WinspoolAbortTests
{
    // Bigger than one 64 KB write so the job needs several writes and can be cut in the middle.
    private static byte[] Big() => new byte[200 * 1024];

    [Fact]
    public void ACompleteJobIsEndedAndNeverAborted()
    {
        var api = new RecordingSpoolerApi();
        var r = WinspoolRawPrinter.SendCore(api, "Q", "doc", Big(), CancellationToken.None);

        Assert.True(r.Succeeded);
        Assert.Equal(200 * 1024, r.BytesSent);
        Assert.Contains("EndDoc", api.Calls);
        Assert.DoesNotContain("Abort", api.Calls);
        Assert.Equal("Close", api.Calls[^1]);
    }

    [Fact]
    public void ACancelInTheMiddleAbortsTheJobInsteadOfCommittingIt()
    {
        var api = new RecordingSpoolerApi();
        using var cts = new CancellationTokenSource();
        api.AfterWrite = n => { if (n == 1) cts.Cancel(); };

        var r = WinspoolRawPrinter.SendCore(api, "Q", "doc", Big(), cts.Token);

        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
        Assert.Equal(64 * 1024, r.BytesSent); // the bytes that were written stay counted: a direct (unspooled) printer may have printed
        Assert.Contains("Abort", api.Calls);
        Assert.DoesNotContain("EndDoc", api.Calls);
        Assert.DoesNotContain("EndPage", api.Calls);
        Assert.Equal("Close", api.Calls[^1]); // the handle is always closed, and last
    }

    [Fact]
    public void AWriteFailureAbortsTheJob()
    {
        var api = new RecordingSpoolerApi { FailWriteAt = 2 };

        var r = WinspoolRawPrinter.SendCore(api, "Q", "doc", Big(), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Equal(64 * 1024, r.BytesSent);
        Assert.Contains("Abort", api.Calls);
        Assert.DoesNotContain("EndDoc", api.Calls);
        Assert.Equal("Close", api.Calls[^1]);
    }

    [Fact]
    public void AFailedStartPageAbortsTheJob()
    {
        var api = new RecordingSpoolerApi { FailStartPage = true };

        var r = WinspoolRawPrinter.SendCore(api, "Q", "doc", Big(), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Equal(0, r.BytesSent);
        Assert.Contains("Abort", api.Calls);
        Assert.DoesNotContain("EndDoc", api.Calls);
    }

    [Fact]
    public void AnEndDocThatFailsIsReportedAsAFailureWithTheBytesWritten()
    {
        var api = new RecordingSpoolerApi { FailEndDoc = true };

        var r = WinspoolRawPrinter.SendCore(api, "Q", "doc", Big(), CancellationToken.None);

        Assert.False(r.Succeeded);
        // Bytes > 0 makes the print service count serial numbers as used, because the document may still print.
        Assert.True(LabelPrintRun.MayHavePrinted(r));
    }

    [Fact]
    public void AnOfflineQueueNeverStartsADocument()
    {
        var api = new RecordingSpoolerApi { Offline = true };

        var r = WinspoolRawPrinter.SendCore(api, "Q", "doc", Big(), CancellationToken.None);

        Assert.Equal(PrintErrorKind.Offline, r.Error!.Kind);
        Assert.DoesNotContain("StartDoc", api.Calls);
        Assert.DoesNotContain("Abort", api.Calls);
        Assert.Equal("Close", api.Calls[^1]);
    }
}

/// <summary>The byte count of a failed TCP send must include the chunk that was in flight.</summary>
public class TcpAccountingTests
{
    /// <summary>A stream whose writes block until cancelled, as a printer that stopped reading does.</summary>
    private sealed class HangingStream : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ACancelDuringTheFirstChunkCountsThatChunkAsReached()
    {
        var reached = new StrongBox<int>(0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TcpRawPrinter.WriteAllAsync(new HangingStream(), new byte[40_000], 16 * 1024, reached, cts.Token));

        // Before the fix this stayed 0, so the print service handed the serial numbers out again although part of a label may have printed.
        Assert.Equal(16 * 1024, reached.Value);
    }

    [Fact]
    public async Task ACancelBeforeAnyWriteReportsNothingSent()
    {
        var reached = new StrongBox<int>(0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TcpRawPrinter.WriteAllAsync(new HangingStream(), new byte[40_000], 16 * 1024, reached, cts.Token));

        Assert.Equal(0, reached.Value);
    }

    [Fact]
    public async Task AStalledRealSocketReportsAtLeastTheFirstChunkAndKeepsTheNumbersUsed()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync(); // accepts but never reads
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var options = new NetworkPrinterOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

        var r = await new TcpRawPrinter().SendAsync("127.0.0.1", port, new byte[256 * 1024 * 1024], options, cts.Token);

        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
        Assert.True(r.BytesSent >= 16 * 1024);
        Assert.True(LabelPrintRun.MayHavePrinted(r));
        (await accepted).Dispose();
    }
}

/// <summary>A failure while a print run starts is a result for the operator, and a reservation made on the way is given back.</summary>
public class PrintBeginFailureTests
{
    private static readonly PrinterTarget Target = PrinterTarget.Spooler("Test Printer");

    private sealed class BrokenStore : ICounterStore
    {
        public CounterRecord? Get(string key) => throw new IOException("the counter file is locked by another program");
        public CounterReservation Reserve(string key, string fingerprint, long count) => throw new IOException("locked");
        public bool Complete(CounterReservation reservation, long used, string? lastValue) => throw new IOException("locked");
        public bool Reclaim(string key, string pendingId) => throw new IOException("locked");
        public void Reset(string key, string fingerprint, long nextIndex = 0) => throw new IOException("locked");
        public void AcceptSettings(string key, string fingerprint) => throw new IOException("locked");
    }

    private sealed class ThrowingRasterizer : ITextRasterizer
    {
        public TextMeasurement Measure(TextRasterRequest request) => throw new InvalidOperationException("font engine failed");
        public TextRasterResult Rasterize(TextRasterRequest request) => throw new InvalidOperationException("font engine failed");
        public IReadOnlyList<string> GetFontFamilies() => [];
    }

    private static LabelDocument CounterLabel(bool windowsFont = false)
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
        doc.Elements.Add(new TextElement
        {
            X = 10, Y = 10, Text = "{{serial}}",
            Font = windowsFont ? FontChoice.ForWindows("Arial", 12) : new FontChoice(),
        });
        return doc;
    }

    [Fact]
    public async Task ACounterStoreThatThrowsBecomesAFailedResultNotAnException()
    {
        var spooler = new ScriptedSpooler();
        var svc = new PrintService(spooler, new TcpRawPrinter(), spooler);

        var r = await svc.PrintAsync(Target, CounterLabel(), null, null, new PrintVariableOptions { CounterStore = new BrokenStore() });

        Assert.False(r.Succeeded);
        // The job turns the store's failure into an issue (and gives back what it reserved), so the operator gets a labelled reason.
        Assert.Equal(PrintErrorKind.InvalidLabel, r.Error!.Kind);
        Assert.Contains(VariableMessageKeys.CounterStoreFailed, r.Error.Args);
        Assert.Contains(r.Notices, n => n.MessageKey == VariableMessageKeys.CounterStoreFailed && n.Args.Any(a => a.Contains("locked")));
        Assert.Empty(spooler.Sent);
    }

    [Fact]
    public async Task AFailureAfterTheNumbersWereReservedGivesThemBack()
    {
        var spooler = new ScriptedSpooler();
        var svc = new PrintService(spooler, new TcpRawPrinter(), spooler);
        var store = new MemoryCounterStore();
        var doc = CounterLabel(windowsFont: true);

        var r = await svc.PrintAsync(Target, doc, null, null,
            new PrintVariableOptions { CounterStore = store, TextRasterizer = new ThrowingRasterizer() });

        Assert.False(r.Succeeded);
        Assert.Empty(spooler.Sent);
        // Nothing reached the printer, so the reserved number must not stay pending.
        var record = store.Get(CounterKey.For(doc.Id!, "serial"));
        Assert.True(record is null || record.Pending.Count == 0);
    }
}
