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
        ArgumentNullException.ThrowIfNull(target);
        // Raw blocks imported from bytes hold bytes above 0x7F as the Latin-1 character with the same value, so they
        // must go out as those exact bytes; the rest of the label follows the document encoding.
        var rawBlocks = document.Elements.OfType<RawZplElement>().Select(r => r.Zpl).Where(NeedsByteExactEncoding).Distinct().ToArray();
        if (!TryEncode(zpl, document.Encoding, rawBlocks, out var bytes))
            return Task.FromResult(PrintResult.Fail(PrintErrorKind.EncodingFailed, document.Encoding.ToString()));
        return SendAsync(target, bytes, cancellationToken);
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
    internal static bool TryEncode(string zpl, ZplEncoding encoding, out byte[] bytes) => TryEncode(zpl, encoding, [], out bytes);

    /// <summary>True for a raw ZPL block whose characters are all Latin-1 and at least one is above 0x7F (a byte kept as a char).</summary>
    internal static bool NeedsByteExactEncoding(string rawZpl)
    {
        bool high = false;
        foreach (char c in rawZpl)
        {
            if (c > 0xFF) return false; // a real Unicode character: not a stored byte, so the document encoding applies
            if (c > 0x7F) high = true;
        }
        return high;
    }

    /// <summary>
    /// Like the plain overload, but every occurrence of a block in <paramref name="byteExactBlocks"/> is written as
    /// Latin-1 (one byte per char) instead of the document encoding. Why: re-encoding such a char as UTF-8 would turn
    /// one original byte into two and corrupt the command (for example a ^GF payload or a binary ^FD).
    /// Matching by text is safe here because the generator emits raw blocks verbatim; a block that cannot be found
    /// simply follows the document encoding.
    /// </summary>
    internal static bool TryEncode(string zpl, ZplEncoding encoding, IReadOnlyCollection<string> byteExactBlocks, out byte[] bytes)
    {
        if (byteExactBlocks.Count == 0) return TryEncodeSegment(zpl, encoding, out bytes);

        var ranges = new List<(int Start, int End)>();
        foreach (var block in byteExactBlocks)
        {
            for (int i = zpl.IndexOf(block, StringComparison.Ordinal); i >= 0; i = zpl.IndexOf(block, i + block.Length, StringComparison.Ordinal))
                ranges.Add((i, i + block.Length));
        }
        ranges.Sort();

        using var ms = new MemoryStream();
        int pos = 0;
        foreach (var (start, end) in ranges)
        {
            if (end <= pos) continue; // nested in an earlier range
            int s = Math.Max(start, pos); // overlapping ranges are merged
            if (s > pos)
            {
                if (!TryEncodeSegment(zpl[pos..s], encoding, out var head)) { bytes = []; return false; }
                ms.Write(head);
            }
            ms.Write(Encoding.Latin1.GetBytes(zpl[s..end]));
            pos = end;
        }
        if (pos < zpl.Length)
        {
            if (!TryEncodeSegment(zpl[pos..], encoding, out var tail)) { bytes = []; return false; }
            ms.Write(tail);
        }
        bytes = ms.ToArray();
        return true;
    }

    private static bool TryEncodeSegment(string zpl, ZplEncoding encoding, out byte[] bytes)
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
