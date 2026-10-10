using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>Fake Win32 layer: returns a canned answer or throws.</summary>
internal sealed class FakeCapsReader(RawPrinterCaps? answer, bool throws = false) : IPrinterCapsReader
{
    public RawPrinterCaps? Read(string printerName) => throws ? throw new InvalidOperationException("boom") : answer;
}

/// <summary>Spooler + discovery fake that reports a fixed capability.</summary>
internal sealed class InfoFakePrinter(PrinterCapabilities info) : IRawPrinter, IPrinterDiscovery
{
    public List<byte[]> Jobs { get; } = new();
    public Task<PrintResult> SendAsync(string printerName, string documentName, byte[] data, CancellationToken cancellationToken = default)
    {
        Jobs.Add(data);
        return Task.FromResult(PrintResult.Ok(data.Length));
    }
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PrinterInfo>>([]);
    public Task<PrinterCapabilities> GetPrinterInfoAsync(string printerName, CancellationToken ct = default) => Task.FromResult(info);
}

/// <summary>Name parsing, dpi mapping, interpretation and fallbacks of driver-based detection.</summary>
public class PrinterInfoTests
{
    [Theory]
    [InlineData("ZDesigner ZD621R-300dpi ZPL", 300)]
    [InlineData("ZDesigner ZD421-203dpi ZPL", 203)]
    [InlineData("Zebra ZT610 600 dpi", 600)]
    [InlineData("ZT610-300", 300)]
    [InlineData("ZT610-600", 600)]
    [InlineData("Zebra 12dpmm", 305)]
    [InlineData("Zebra 8 dpmm", 203)]
    [InlineData("zd621 300DPI", 300)]
    public void Name_tokens_are_parsed(string text, int expected) => Assert.Equal(expected, PrinterDpiMapper.ParseFromName(text));

    [Theory]
    [InlineData("Microsoft Print to PDF")]
    [InlineData("HP LaserJet 4000")]
    [InlineData("Zebra ZD620")]
    [InlineData("")]
    [InlineData(null)]
    public void Names_without_tokens_give_null(string? text) => Assert.Null(PrinterDpiMapper.ParseFromName(text));

    [Fact]
    public void Driver_name_is_searched_before_queue_name()
    {
        Assert.Equal(300, PrinterDpiMapper.ParseFromName("ZDesigner ZD621R-300dpi ZPL", "Queue 203dpi"));
        Assert.Equal(203, PrinterDpiMapper.ParseFromName(null, "Queue 203dpi"));
    }

    [Theory]
    [InlineData(203, PrinterDpi.Dpi203)]
    [InlineData(200, PrinterDpi.Dpi203)]
    [InlineData(300, PrinterDpi.Dpi300)]
    [InlineData(305, PrinterDpi.Dpi300)]
    [InlineData(600, PrinterDpi.Dpi600)]
    [InlineData(610, PrinterDpi.Dpi600)]
    public void Raw_dpi_within_tolerance_maps_to_supported(int raw, PrinterDpi expected) => Assert.Equal(expected, PrinterDpiMapper.Map(raw));

    [Theory]
    [InlineData(96)]
    [InlineData(150)]
    [InlineData(400)]
    [InlineData(1200)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Raw_dpi_outside_tolerance_does_not_map(int raw) => Assert.Null(PrinterDpiMapper.Map(raw));

    [Fact]
    public void Map_of_null_is_null() => Assert.Null(PrinterDpiMapper.Map(null));

    [Fact]
    public void Device_caps_win_over_name()
    {
        var raw = new RawPrinterCaps("ZDesigner ZD421-203dpi ZPL", null, null, 300, 300, null, null, null);
        var info = PrinterInfoDetector.Interpret("Q", raw);
        Assert.Equal(PrinterDpi.Dpi300, info.Dpi);
        Assert.Equal(PrinterInfoSource.DeviceCaps, info.DpiSource);
        Assert.Equal(300, info.HorizontalDpi);
    }

    [Fact]
    public void Dev_mode_is_used_when_info_context_is_not_usable()
    {
        var raw = new RawPrinterCaps("Drv", 600, 600, 96, 96, null, null, null);
        var info = PrinterInfoDetector.Interpret("Q", raw);
        Assert.Equal(PrinterDpi.Dpi600, info.Dpi);
        Assert.Equal(PrinterInfoSource.DevMode, info.DpiSource);
    }

    [Fact]
    public void Unmappable_driver_value_falls_back_to_name()
    {
        var raw = new RawPrinterCaps("ZDesigner ZD621R-300dpi ZPL", null, null, 96, 96, null, null, null);
        var info = PrinterInfoDetector.Interpret("Q", raw);
        Assert.Equal(PrinterDpi.Dpi300, info.Dpi);
        Assert.Equal(PrinterInfoSource.DriverName, info.DpiSource);
    }

    [Fact]
    public void Unmappable_value_without_name_hint_keeps_raw_and_leaves_dpi_null()
    {
        var raw = new RawPrinterCaps("Generic", null, null, 1200, 1200, null, null, null);
        var info = PrinterInfoDetector.Interpret("Q", raw);
        Assert.Null(info.Dpi);
        Assert.Equal(1200, info.HorizontalDpi);
        Assert.Equal(PrinterInfoSource.DeviceCaps, info.DpiSource);
    }

    [Fact]
    public void Nothing_known_is_unknown()
    {
        var info = PrinterInfoDetector.Interpret("Plain queue", null);
        Assert.Equal(PrinterInfoSource.Unknown, info.DpiSource);
        Assert.Null(info.Dpi);
        Assert.Null(info.HorizontalDpi);
        Assert.Null(info.PaperWidth);
    }

    [Fact]
    public void Paper_size_converts_tenths_of_mm()
    {
        var raw = new RawPrinterCaps("D", null, null, 203, 203, null, 1016, 1524);
        var info = PrinterInfoDetector.Interpret("Q", raw);
        Assert.Equal(Measure.Millimeters(101.6), info.PaperWidth);
        Assert.Equal(Measure.Millimeters(152.4), info.PaperHeight);
    }

    [Fact]
    public void Reader_exception_gives_unknown_or_name_based_info_and_never_throws()
    {
        var info = PrinterInfoDetector.Detect("Queue", new FakeCapsReader(null, throws: true));
        Assert.Equal(PrinterInfoSource.Unknown, info.DpiSource);
        var named = PrinterInfoDetector.Detect("Queue 300dpi", new FakeCapsReader(null, throws: true));
        Assert.Equal(PrinterDpi.Dpi300, named.Dpi);
        Assert.Equal(PrinterInfoSource.DriverName, named.DpiSource);
    }

    [Fact]
    public async Task Winspool_uses_injected_reader_off_the_caller()
    {
        var printer = new WinspoolRawPrinter(new FakeCapsReader(new RawPrinterCaps("D", null, null, 300, 300, null, null, null)));
        var info = await printer.GetPrinterInfoAsync("Q");
        Assert.Equal(PrinterDpi.Dpi300, info.Dpi);
    }

    [Fact]
    public async Task Default_discovery_member_returns_unknown()
    {
        IPrinterDiscovery d = new FakeSpooler();
        var info = await d.GetPrinterInfoAsync("Zebra");
        Assert.Equal(PrinterInfoSource.Unknown, info.DpiSource);
        Assert.Equal("Zebra", info.Name);
    }

    [Fact]
    public async Task Tcp_target_returns_unknown_without_touching_discovery()
    {
        var fake = new InfoFakePrinter(new PrinterCapabilities("x", "d", 300, 300, PrinterDpi.Dpi300, null, null, PrinterInfoSource.DeviceCaps));
        var svc = new PrintService(fake, new TcpRawPrinter(), fake);
        var info = await svc.GetPrinterInfoAsync(PrinterTarget.TcpIp("192.0.2.1"));
        Assert.Equal(PrinterInfoSource.Unknown, info.DpiSource);
    }

    [Fact]
    public async Task Test_label_uses_detected_dpi_and_paper_when_opted_in()
    {
        var caps = new PrinterCapabilities("Q", "D", 300, 300, PrinterDpi.Dpi300, Measure.Millimeters(101.6), Measure.Millimeters(76.2), PrinterInfoSource.DeviceCaps);
        var fake = new InfoFakePrinter(caps);
        var svc = new PrintService(fake, new TcpRawPrinter(), fake);
        var profile = new PrinterProfile { Dpi = PrinterDpi.Dpi203, UseDetectedSettings = true };
        var r = await svc.PrintTestLabelAsync(PrinterTarget.Spooler("Q"), profile);
        Assert.True(r.Succeeded);
        string zpl = Encoding.UTF8.GetString(Assert.Single(fake.Jobs));
        Assert.Contains("^PW1200", zpl); // 101.6 mm at 300 dpi
        Assert.Contains("^LL900", zpl);  // 76.2 mm at 300 dpi
    }

    [Fact]
    public async Task Test_label_keeps_profile_dpi_when_not_opted_in()
    {
        var caps = new PrinterCapabilities("Q", "D", 600, 600, PrinterDpi.Dpi600, null, null, PrinterInfoSource.DeviceCaps);
        var fake = new InfoFakePrinter(caps);
        var svc = new PrintService(fake, new TcpRawPrinter(), fake);
        await svc.PrintTestLabelAsync(PrinterTarget.Spooler("Q"), new PrinterProfile { Dpi = PrinterDpi.Dpi203, Width = Measure.Millimeters(100) });
        Assert.Contains("^PW799", Encoding.UTF8.GetString(Assert.Single(fake.Jobs)));
    }

    [Fact]
    public void FromInfo_ignores_unknown_values()
    {
        var baseProfile = new PrinterProfile { Dpi = PrinterDpi.Dpi600, Name = "x" };
        Assert.Equal(baseProfile, PrinterProfile.FromInfo(PrinterCapabilities.Unknown("Q"), baseProfile));
        Assert.Equal(PrinterDpi.Dpi203, PrinterProfile.FromInfo(new PrinterCapabilities("Q", null, 203, 203, PrinterDpi.Dpi203, null, null, PrinterInfoSource.DevMode), baseProfile).Dpi);
    }

    /// <summary>Read-only smoke test against the real spooler: enumerates and queries every installed queue, asserts only "no exception".</summary>
    [Fact]
    public async Task Real_api_smoke_reads_every_installed_printer_without_throwing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var printer = new WinspoolRawPrinter();
        var list = await printer.GetPrintersAsync();
        var lines = new List<string>();
        foreach (var p in list)
        {
            var info = await printer.GetPrinterInfoAsync(p.Name);
            Assert.NotNull(info);
            lines.Add($"{info.Name} | {info.DriverName} | {info.HorizontalDpi}x{info.VerticalDpi} -> {info.Dpi} | {info.DpiSource} | {info.PaperWidth} x {info.PaperHeight}");
        }
        var path = Environment.GetEnvironmentVariable("ROTULA_PRINTERINFO_DUMP");
        if (!string.IsNullOrEmpty(path)) File.WriteAllLines(path, lines);
    }
}

/// <summary>The test label must be valid and fit at every label size and resolution.</summary>
public class PrinterInfoTestLabelTests
{
    public static IEnumerable<object[]> Sizes()
    {
        foreach (var dpi in new[] { PrinterDpi.Dpi203, PrinterDpi.Dpi300, PrinterDpi.Dpi600 })
            yield return new object[] { Measure.Inches(4), Measure.Inches(3), dpi };
        yield return new object[] { Measure.Inches(4), Measure.Inches(6), PrinterDpi.Dpi203 };
        yield return new object[] { Measure.Inches(4), Measure.Inches(6), PrinterDpi.Dpi300 };
        yield return new object[] { Measure.Inches(2), Measure.Inches(1), PrinterDpi.Dpi203 };
        yield return new object[] { Measure.Inches(2), Measure.Inches(1), PrinterDpi.Dpi300 };
        yield return new object[] { Measure.Millimeters(100), Measure.Millimeters(50), PrinterDpi.Dpi203 };
        yield return new object[] { Measure.Millimeters(100), Measure.Millimeters(50), PrinterDpi.Dpi600 };
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Test_label_generates_without_errors_and_stays_inside(Measure width, Measure height, PrinterDpi dpi)
    {
        var profile = new PrinterProfile { Width = width, Height = height, Dpi = dpi };
        var doc = TestLabel.Build(profile);
        var result = ZplGenerator.Generate(doc);
        Assert.False(result.HasErrors, string.Join(",", result.Issues.Select(i => i.MessageKey)));
        Assert.DoesNotContain(result.Issues, i => i.MessageKey == "Element.OutsideLabel");

        foreach (var e in doc.Elements)
        {
            int extentW, extentH;
            switch (e)
            {
                case TextElement t:
                    // ~22 characters at about 0.6 em for the longest line.
                    extentW = (int)((t.FontWidth ?? 0) * 0.6 * t.Text.Length);
                    extentH = t.FontHeight ?? 0;
                    break;
                case BarcodeElement b:
                    Assert.InRange(b.ModuleSize, 1, 10);
                    extentW = extentH = 29 * b.ModuleSize; // widest QR for this payload incl. quiet zone
                    break;
                default: continue;
            }
            Assert.True(e.X >= 0 && e.Y >= 0);
            Assert.True(e.X + extentW <= doc.WidthDots, $"{e.GetType().Name} too wide");
            Assert.True(e.Y + extentH <= doc.HeightDots, $"{e.GetType().Name} too tall");
        }
    }

    [Fact]
    public void Text_is_readable_on_4x3_inches()
    {
        foreach (var (dpi, minDots) in new[] { (PrinterDpi.Dpi203, 40), (PrinterDpi.Dpi300, 60), (PrinterDpi.Dpi600, 120) })
        {
            var doc = TestLabel.Build(new PrinterProfile { Width = Measure.Inches(4), Height = Measure.Inches(3), Dpi = dpi });
            Assert.All(doc.Elements.OfType<TextElement>(), t => Assert.True(t.FontHeight >= minDots, $"{dpi}: {t.FontHeight}"));
        }
    }
}
