using System.Net;
using System.Net.Sockets;
using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>Records what the service hands to the spooler; never touches a real printer.</summary>
internal sealed class FakeSpooler : IRawPrinter, IPrinterDiscovery
{
    public List<(string Printer, string Doc, byte[] Data)> Jobs { get; } = new();
    public PrintResult? Next { get; set; }

    public Task<PrintResult> SendAsync(string printerName, string documentName, byte[] data, CancellationToken cancellationToken = default)
    {
        Jobs.Add((printerName, documentName, data));
        return Task.FromResult(Next ?? PrintResult.Ok(data.Length));
    }

    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PrinterInfo>>([new PrinterInfo("Zebra ZD420", "USB001", "ZDesigner", true, false)]);
}

/// <summary>Spooler path, service facade, profile commands and error mapping.</summary>
public class PrintingServiceTests
{
    private static (PrintService Svc, FakeSpooler Fake) Make()
    {
        var fake = new FakeSpooler();
        return (new PrintService(fake, new TcpRawPrinter(), fake), fake);
    }

    private static LabelDocument Doc(ZplEncoding? enc = null)
    {
        var d = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30), Encoding = enc ?? ZplEncoding.Utf8 };
        d.Elements.Add(new TextElement { X = 10, Y = 10, Text = "Niño" });
        return d;
    }

    [Fact]
    public async Task Spooler_receives_the_generated_zpl_as_utf8()
    {
        var (svc, fake) = Make();
        var doc = Doc();
        var r = await svc.PrintAsync(PrinterTarget.Spooler("Zebra ZD420"), doc);
        Assert.True(r.Succeeded);
        var job = Assert.Single(fake.Jobs);
        Assert.Equal("Zebra ZD420", job.Printer);
        Assert.Equal(new UTF8Encoding(false).GetBytes(ZplGenerator.Generate(doc).Zpl), job.Data);
        Assert.Contains("^CI28", Encoding.UTF8.GetString(job.Data));
    }

    [Fact]
    public void Legacy_encoding_uses_the_matching_code_page_and_utf8_does_not()
    {
        Assert.True(PrintService.TryEncode("ñ", ZplEncoding.Cp1252, out var legacy));
        Assert.Equal(new byte[] { 0xF1 }, legacy);
        Assert.True(PrintService.TryEncode("ñ", ZplEncoding.Utf8, out var utf8));
        Assert.Equal(new byte[] { 0xC3, 0xB1 }, utf8);
        Assert.False(PrintService.TryEncode("中", ZplEncoding.Cp1252, out _));
    }

    [Fact]
    public async Task Unmappable_text_is_reported_not_replaced()
    {
        var (svc, fake) = Make();
        var r = await svc.PrintZplAsync(PrinterTarget.Spooler("p"), "^XA^FD中^FS^XZ", ZplEncoding.Cp1252);
        Assert.Equal(PrintErrorKind.EncodingFailed, r.Error!.Kind);
        Assert.Empty(fake.Jobs);
    }

    [Fact]
    public async Task Label_with_errors_is_not_sent()
    {
        var (svc, fake) = Make();
        var doc = Doc();
        doc.Print.Quantity = 0;
        var r = await svc.PrintAsync(PrinterTarget.Spooler("p"), doc);
        Assert.Equal("Print.Error.InvalidLabel", r.Error!.Key);
        Assert.Contains("Print.Quantity.Range", r.Error.Args);
        Assert.Empty(fake.Jobs);
    }

    [Fact]
    public async Task Spooler_failure_is_returned_as_a_value()
    {
        var (svc, fake) = Make();
        fake.Next = PrintResult.Fail(PrintErrorKind.Offline, "p");
        var r = await svc.PrintZplAsync(PrinterTarget.Spooler("p"), "^XA^XZ", ZplEncoding.Utf8);
        Assert.False(r.Succeeded);
        Assert.Equal("Print.Error.Offline", r.Error!.Key);
    }

    [Fact]
    public async Task Profile_setup_is_prepended_in_the_same_job()
    {
        var (svc, fake) = Make();
        var profile = new PrinterProfile { Darkness = 15, SpeedIps = 4, Width = Measure.Millimeters(100), MediaTracking = MediaTracking.Mark };
        await svc.PrintAsync(PrinterTarget.Spooler("p"), Doc(), null, profile);
        string text = Encoding.UTF8.GetString(Assert.Single(fake.Jobs).Data);
        Assert.StartsWith("~SD15\n^XA\n^PR4\n^MNM\n^PW799\n^XZ\n^XA", text);
    }

    [Fact]
    public void Setup_commands_are_exact_and_validated()
    {
        Assert.Equal("^XA\n^MNY\n^PW799\n^XZ\n", PrinterSetup.BuildSetupZpl(new PrinterProfile()));
        Assert.Throws<ArgumentException>(() => PrinterSetup.BuildSetupZpl(new PrinterProfile { Darkness = 31 }));
        Assert.Contains("Print.Profile.Speed.Range", new PrinterProfile { SpeedIps = 0 }.Validate());
    }

    [Fact]
    public async Task Invalid_profile_is_rejected_without_sending()
    {
        var (svc, fake) = Make();
        var r = await svc.ApplyProfileAsync(PrinterTarget.Spooler("p"), new PrinterProfile { Darkness = -1 });
        Assert.Equal(PrintErrorKind.InvalidProfile, r.Error!.Kind);
        Assert.Empty(fake.Jobs);
    }

    [Fact]
    public async Task Test_label_is_valid_zpl_sent_through_the_spooler()
    {
        var (svc, fake) = Make();
        var profile = new PrinterProfile { Dpi = PrinterDpi.Dpi300, Width = Measure.Millimeters(60), Height = Measure.Millimeters(40) };
        Assert.False(ZplGenerator.Generate(TestLabel.Build(profile)).HasErrors);
        var r = await svc.PrintTestLabelAsync(PrinterTarget.Spooler("p"), profile);
        Assert.True(r.Succeeded);
        string text = Encoding.UTF8.GetString(Assert.Single(fake.Jobs).Data);
        Assert.Contains("^PW709", text);
        Assert.Contains("^BQ", text);
    }

    [Fact]
    public async Task Discovery_goes_through_the_facade()
    {
        var (svc, _) = Make();
        var p = Assert.Single(await svc.GetPrintersAsync());
        Assert.Equal("Zebra ZD420", p.Name);
    }

    [Theory]
    [InlineData(5, PrintErrorKind.AccessDenied)]
    [InlineData(1801, PrintErrorKind.PrinterNotFound)]
    [InlineData(1722, PrintErrorKind.Offline)]
    [InlineData(9999, PrintErrorKind.Unknown)]
    public void Win32_errors_map_to_stable_kinds(int code, PrintErrorKind kind)
    {
        var e = PrinterErrors.FromWin32(code, "P");
        Assert.Equal(kind, e.Kind);
        Assert.Equal("Print.Error." + kind, e.Key);
        Assert.Equal("P", e.Args[0]);
    }

    [Fact]
    public void Offline_state_flags_are_recognised()
    {
        Assert.True(PrinterErrors.IsOffline(0x80, 0));
        Assert.True(PrinterErrors.IsOffline(0, 0x400));
        Assert.False(PrinterErrors.IsOffline(0, 0));
    }
}

/// <summary>TCP path against an in-process listener.</summary>
public class PrintingTcpTests
{
    private static readonly NetworkPrinterOptions Fast = new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task Bytes_received_equal_the_zpl_bytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            using var ms = new MemoryStream();
            await c.GetStream().CopyToAsync(ms);
            return ms.ToArray();
        });

        var svc = new PrintService();
        string zpl = ZplGenerator.Generate(new LabelDocument { Elements = { } }).Zpl;
        var r = await svc.PrintZplAsync(PrinterTarget.TcpIp("127.0.0.1", port, NetworkPrinterOptions.Default), zpl, ZplEncoding.Utf8);

        Assert.True(r.Succeeded);
        var expected = Encoding.UTF8.GetBytes(zpl);
        Assert.Equal(expected.Length, r.BytesSent);
        Assert.Equal(expected, await received.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Connection_refused_is_reported()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop(); // nothing listens any more

        var r = await new TcpRawPrinter().SendAsync("127.0.0.1", port, [1], Fast);
        Assert.Equal(PrintErrorKind.ConnectionRefused, r.Error!.Kind);
    }

    [Fact]
    public async Task Unresolvable_host_is_reported_as_unreachable()
    {
        // .invalid is reserved (RFC 2606) and never resolves.
        var r = await new TcpRawPrinter().SendAsync("printer.invalid", 9100, [1], Fast);
        Assert.Equal(PrintErrorKind.HostUnreachable, r.Error!.Kind);
    }

    [Fact]
    public async Task Stalled_printer_times_out_while_sending()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Accept but never read: the socket buffers fill up and the write blocks.
        var accepted = listener.AcceptTcpClientAsync();

        var r = await new TcpRawPrinter().SendAsync("127.0.0.1", port, new byte[256 * 1024 * 1024], Fast);
        Assert.Equal(PrintErrorKind.Timeout, r.Error!.Kind);
        Assert.True(r.BytesSent < 256 * 1024 * 1024);
        (await accepted).Dispose();
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_as_cancelled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var slow = new NetworkPrinterOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        var r = await new TcpRawPrinter().SendAsync("127.0.0.1", port, new byte[256 * 1024 * 1024], slow, cts.Token);
        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
        (await accepted).Dispose();
    }

    [Fact]
    public async Task Already_cancelled_token_never_connects()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await new TcpRawPrinter().SendAsync("127.0.0.1", 9, [1], Fast, cts.Token);
        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
    }
}

/// <summary>Read-only smoke tests of the real winspool bindings (no job is ever submitted).</summary>
public class PrintingWinspoolSmokeTests
{
    [Fact]
    public async Task Enumerating_installed_printers_does_not_throw()
    {
        var list = await new WinspoolRawPrinter().GetPrintersAsync();
        Assert.NotNull(list);
    }

    [Fact]
    public async Task Unknown_printer_name_is_reported_as_not_found()
    {
        // OpenPrinter fails before any document is started, so nothing can be printed.
        var r = await new WinspoolRawPrinter().SendAsync("Rotula-no-such-printer-" + Guid.NewGuid().ToString("N"), "t", [1]);
        Assert.Equal(PrintErrorKind.PrinterNotFound, r.Error!.Kind);
    }
}

/// <summary>Raw ZPL blocks imported from bytes must reach the printer byte for byte.</summary>
public class PrintingRawBytesTests
{
    [Fact]
    public async Task Raw_block_with_high_bytes_is_sent_as_latin1_while_the_rest_stays_utf8()
    {
        var fake = new FakeSpooler();
        var svc = new PrintService(fake, new TcpRawPrinter(), fake);
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30), Encoding = ZplEncoding.Utf8 };
        doc.Elements.Add(new TextElement { X = 10, Y = 10, Text = "Niño" });
        doc.Elements.Add(new RawZplElement { Zpl = "^FO5,5^FDéÿ^FS", IsVisual = true });

        var r = await svc.PrintAsync(PrinterTarget.Spooler("Zebra ZD420"), doc);

        Assert.True(r.Succeeded);
        var bytes = Assert.Single(fake.Jobs).Data;
        // Raw block: one byte per char (E9 FF), not the UTF-8 pairs C3 A9 C3 BF.
        Assert.Equal(1, Count(bytes, [0x5E, 0x46, 0x44, 0xE9, 0xFF, 0x5E, 0x46, 0x53]));
        // The generator hex-escapes real text, so nothing else may carry a byte above 0x7F (no UTF-8 lead bytes).
        Assert.Equal(2, bytes.Count(b => b > 0x7F));
    }

    [Fact]
    public void Cp1252_document_sends_raw_0x80_to_0x9F_bytes_unchanged()
    {
        // Latin-1 char U+0085 is byte 0x85; cp1252 would map it differently, so it needs the byte-exact path.
        const string zpl = "^XA^FDabc\u0085\u0099^FS^XZ";
        Assert.True(PrintService.TryEncode(zpl, ZplEncoding.Cp1252, ["abc\u0085\u0099"], out var bytes));
        Assert.Equal([0x61, 0x62, 0x63, 0x85, 0x99], bytes[6..11]);
    }

    [Fact]
    public void Without_raw_blocks_encoding_is_unchanged()
    {
        Assert.True(PrintService.TryEncode("ñ", ZplEncoding.Utf8, [], out var bytes));
        Assert.Equal([0xC3, 0xB1], bytes);
    }

    [Theory]
    [InlineData("^FO1,1^FS", false)]
    [InlineData("^FDé", true)]
    [InlineData("^FD€é", false)] // a real Unicode char: not a stored byte
    public void Only_latin1_blocks_with_high_chars_need_byte_exact_encoding(string raw, bool expected) =>
        Assert.Equal(expected, PrintService.NeedsByteExactEncoding(raw));

    private static int Count(byte[] hay, byte[] needle)
    {
        int n = 0;
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) n++;
        return n;
    }
}
