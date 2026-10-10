using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Printing;

/// <summary>The single entry point the UI uses to find printers and print. Every call is async and never throws for printing failures.</summary>
public interface IPrintService
{
    /// <summary>Lists installed Windows printers.</summary>
    Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default);

    /// <summary>Generates ZPL for <paramref name="document"/> and prints it. Labels with validation errors are not sent.</summary>
    /// <param name="target">Destination printer.</param>
    /// <param name="document">The label.</param>
    /// <param name="data">Variable values merged into the label.</param>
    /// <param name="profile">Optional profile whose set-up commands are sent first, in the same job.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PrintResult> PrintAsync(PrinterTarget target, LabelDocument document, IReadOnlyDictionary<string, string>? data = null,
        PrinterProfile? profile = null, CancellationToken cancellationToken = default);

    /// <summary>Prints ready-made ZPL, encoding it with <paramref name="encoding"/>.</summary>
    Task<PrintResult> PrintZplAsync(PrinterTarget target, string zpl, ZplEncoding encoding, CancellationToken cancellationToken = default);

    /// <summary>Sends only the profile set-up commands (darkness, speed, media, width).</summary>
    Task<PrintResult> ApplyProfileAsync(PrinterTarget target, PrinterProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Builds a small test label for the profile and prints it.</summary>
    Task<PrintResult> PrintTestLabelAsync(PrinterTarget target, PrinterProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IPrintService"/>: ZPL from Core, bytes from <see cref="ZplEncoding"/>, transport by target.</summary>
public sealed class PrintService : IPrintService
{
    private readonly IRawPrinter _spooler;
    private readonly INetworkPrinter _network;
    private readonly IPrinterDiscovery _discovery;

    /// <summary>Creates the service with the real winspool and TCP implementations.</summary>
    public PrintService() : this(new WinspoolRawPrinter(), new TcpRawPrinter(), new WinspoolRawPrinter()) { }

    /// <summary>Creates the service with explicit transports (tests pass fakes).</summary>
    public PrintService(IRawPrinter spooler, INetworkPrinter network, IPrinterDiscovery discovery)
    {
        _spooler = spooler ?? throw new ArgumentNullException(nameof(spooler));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        _discovery.GetPrintersAsync(cancellationToken);

    /// <inheritdoc />
    public Task<PrintResult> PrintAsync(PrinterTarget target, LabelDocument document, IReadOnlyDictionary<string, string>? data = null,
        PrinterProfile? profile = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var generated = ZplGenerator.Generate(document, data);
        if (generated.HasErrors)
            return Task.FromResult(PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel,
                generated.Issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey).ToArray())));

        string zpl = generated.Zpl;
        if (profile is not null)
        {
            var bad = profile.Validate();
            if (bad.Count > 0) return Task.FromResult(PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidProfile, bad.ToArray())));
            zpl = PrinterSetup.BuildSetupZpl(profile) + zpl;
        }
        return PrintZplAsync(target, zpl, document.Encoding, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrintResult> PrintZplAsync(PrinterTarget target, string zpl, ZplEncoding encoding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(zpl);
        ArgumentNullException.ThrowIfNull(encoding);
        if (!TryEncode(zpl, encoding, out var bytes))
            return Task.FromResult(PrintResult.Fail(PrintErrorKind.EncodingFailed, encoding.ToString()));
        return SendAsync(target, bytes, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrintResult> ApplyProfileAsync(PrinterTarget target, PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var bad = profile.Validate();
        if (bad.Count > 0) return Task.FromResult(PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidProfile, bad.ToArray())));
        // Set-up text is pure ASCII, so any encoding yields the same bytes.
        return PrintZplAsync(target, PrinterSetup.BuildSetupZpl(profile), ZplEncoding.Utf8, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrintResult> PrintTestLabelAsync(PrinterTarget target, PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return PrintAsync(target, TestLabel.Build(profile), null, profile, cancellationToken);
    }

    /// <summary>
    /// Encodes ZPL with the document's code page: UTF-8 for ^CI28, the matching legacy page otherwise.
    /// Strict (exception) fallback so an unmappable character is reported instead of silently becoming "?".
    /// </summary>
    internal static bool TryEncode(string zpl, ZplEncoding encoding, out byte[] bytes)
    {
        try
        {
            // DotNetCodePage is read after the ZplEncoding static constructor ran, which registers the legacy code pages.
            Encoding enc = encoding.DotNetCodePage is { } page
                ? Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : new UTF8Encoding(false);
            bytes = enc.GetBytes(zpl);
            return true;
        }
        catch (Exception ex) when (ex is EncoderFallbackException or ArgumentException or NotSupportedException)
        {
            bytes = [];
            return false;
        }
    }

    private Task<PrintResult> SendAsync(PrinterTarget target, byte[] bytes, CancellationToken ct) => target.Transport switch
    {
        PrinterTransport.Spooler => _spooler.SendAsync(target.PrinterName, "Rotula label", bytes, ct),
        PrinterTransport.Tcp => _network.SendAsync(target.Host, target.Port, bytes, target.Network, ct),
        _ => Task.FromResult(PrintResult.Fail(PrintErrorKind.Unknown, target.Transport.ToString())),
    };
}

/// <summary>Builds the small label used by "print test label".</summary>
public static class TestLabel
{
    /// <summary>Creates a label sized and configured from the profile: a frame, a title with accents and a QR code.</summary>
    public static LabelDocument Build(PrinterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var doc = new LabelDocument
        {
            Width = profile.Width,
            Height = profile.Height,
            Dpi = profile.Dpi,
            MediaTracking = profile.MediaTracking,
            Encoding = profile.Encoding,
        };
        int w = doc.WidthDots, h = doc.HeightDots;
        int margin = Math.Max(4, w / 40);
        doc.Elements.Add(new BoxElement { X = 0, Y = 0, WidthDots = Math.Max(1, w), HeightDots = Math.Max(1, h), ThicknessDots = 3 });
        // Accents in the sample text prove that the code page and ^CI setting match the printer.
        doc.Elements.Add(new TextElement { X = margin + 6, Y = margin + 6, Text = "Rotula - prueba / test", FontHeight = Math.Max(20, h / 8), FontWidth = Math.Max(20, h / 8) });
        doc.Elements.Add(new TextElement { X = margin + 6, Y = margin + 6 + Math.Max(24, h / 7), Text = "áéíóú ñ ¿?", FontHeight = Math.Max(20, h / 8), FontWidth = Math.Max(20, h / 8) });
        doc.Elements.Add(new BarcodeElement
        {
            X = margin + 6, Y = h / 2, Symbology = BarcodeSymbology.QrCode, Data = "ROTULA-TEST", ModuleSize = Math.Max(2, h / 60),
        });
        return doc;
    }
}
