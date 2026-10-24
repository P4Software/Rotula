using System.Text.Json;
using Rotula.Core.Model;
using Rotula.Mcp.Hosting;
using Rotula.Mcp.Settings;
using Rotula.Mcp.Tools;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>Records every job; never touches a real printer. Reports a fixed printer list, resolution and status.</summary>
internal sealed class McpFakePrinter : IRawPrinter, INetworkPrinter, IPrinterDiscovery, IPrinterStatusReader
{
    /// <summary>The answer to a status query, and the printers that were asked.</summary>
    public PrinterStatus Status { get; set; } = PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported);
    public List<string> StatusQueries { get; } = new();

    public Task<PrinterStatus> ReadStatusAsync(PrinterTarget target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        StatusQueries.Add(target.PrinterName);
        return Task.FromResult(Status);
    }

    public List<(string Printer, byte[] Data)> Jobs { get; } = new();
    public int NetworkJobs { get; private set; }
    public PrinterDpi? Dpi { get; set; } = PrinterDpi.Dpi203;
    public string[] Names { get; set; } = ["Zebra ZD420", "Office Laser"];

    public Task<PrintResult> SendAsync(string printerName, string documentName, byte[] data, CancellationToken cancellationToken = default)
    {
        Jobs.Add((printerName, data));
        return Task.FromResult(PrintResult.Ok(data.Length));
    }

    public Task<PrintResult> SendAsync(string host, int port, byte[] data, NetworkPrinterOptions options, CancellationToken cancellationToken = default)
    {
        NetworkJobs++;
        return Task.FromResult(PrintResult.Ok(data.Length));
    }

    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PrinterInfo>>(Names.Select((n, i) => new PrinterInfo(n, "USB00" + i, "Driver", i == 0, false)).ToList());

    public Task<PrinterCapabilities> GetPrinterInfoAsync(string printerName, CancellationToken ct = default) =>
        Task.FromResult(new PrinterCapabilities(printerName, "Driver", (int?)Dpi, (int?)Dpi, Dpi, Measure.Millimeters(100), Measure.Millimeters(150),
            Dpi is null ? PrinterInfoSource.Unknown : PrinterInfoSource.DeviceCaps));
}

/// <summary>A throw-away folder, settings and context. Disposing removes the folder.</summary>
internal sealed class McpTestEnv : IDisposable
{
    public McpTestEnv(Func<McpSettings, McpSettings>? configure = null, Func<DateTimeOffset>? clock = null, Func<Rotula.Data.IConnectionStore>? connections = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "rotula-mcp-tests", Guid.NewGuid().ToString("N"));
        Labels = Path.Combine(Root, "Rotula Labels");
        Directory.CreateDirectory(Labels);
        var settings = new McpSettings { AllowedFolders = [Labels] };
        if (configure is not null) settings = configure(settings);
        Settings = settings;
        Printer = new McpFakePrinter();
        Context = new McpContext(settings, new PrintService(Printer, Printer, Printer, Printer), connections, clock);
        Label = new LabelTools(Context);
        Elements = new ElementTools(Context);
        Preview = new PreviewTools(Context);
        Printing = new PrinterTools(Context);
        Data = new DataTools(Context);
    }

    public string Root { get; }
    public string Labels { get; }
    public McpSettings Settings { get; }
    public McpFakePrinter Printer { get; }
    public McpContext Context { get; }
    public LabelTools Label { get; }
    public ElementTools Elements { get; }
    public PreviewTools Preview { get; }
    public PrinterTools Printing { get; }
    public DataTools Data { get; }

    /// <summary>A clock tests can move forward.</summary>
    public static Func<DateTimeOffset> Clock(out Action<TimeSpan> advance)
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        advance = t => now += t;
        return () => now;
    }

    public static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
