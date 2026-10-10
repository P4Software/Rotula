using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Mcp.Hosting;
using Rotula.Mcp.Security;
using Rotula.Mcp.Session;
using Rotula.Printing;

namespace Rotula.Mcp.Tools;

/// <summary>Printer discovery (read-only) and the guarded print tool.</summary>
[McpServerToolType]
public sealed class PrinterTools
{
    private const int PreviewLines = 12;

    private readonly McpContext _ctx;

    /// <summary>Creates the tool set over the shared context.</summary>
    public PrinterTools(McpContext ctx) => _ctx = ctx;

    /// <summary>Lists Windows printers.</summary>
    [McpServerTool(Name = "list_printers", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists the printers installed in Windows (read-only; nothing is sent to them). Shows which of them printing is allowed for in rotula-mcp.settings.json.")]
    public async Task<PrinterListResult> ListPrinters(CancellationToken cancellationToken = default)
    {
        var printers = await SafeListAsync(cancellationToken).ConfigureAwait(false);
        return new PrinterListResult(
            printers.Select(p => new PrinterEntry(p.Name, p.PortName, p.DriverName, p.IsDefault, p.IsOffline,
                _ctx.Settings.AllowPrinting && _ctx.Settings.AllowedPrinters.Contains(p.Name, StringComparer.OrdinalIgnoreCase))).ToList(),
            _ctx.Settings.AllowPrinting);
    }

    /// <summary>Reads resolution and paper size from the driver.</summary>
    [McpServerTool(Name = "get_printer_info", ReadOnly = true, UseStructuredContent = true)]
    [Description("Reads a printer's resolution (DPI) and paper size from its Windows driver. Read-only: no job is created and nothing is printed. Drivers do not always know the real printhead resolution, so treat the numbers as a hint and confirm with the printer's own label.")]
    public async Task<PrinterInfoResult> GetPrinterInfo(
        [Description("Exact Windows printer name from list_printers.")] string printer,
        CancellationToken cancellationToken = default)
    {
        string name = await RequireInstalledAsync(printer, cancellationToken).ConfigureAwait(false);
        var caps = await _ctx.Print.GetPrinterInfoAsync(PrinterTarget.Spooler(name), cancellationToken).ConfigureAwait(false);
        string note = caps.Dpi is null && caps.HorizontalDpi is null
            ? "The driver did not report a resolution. Ask the person at the printer, or read it from the printer's configuration label."
            : "Detected from the Windows driver.";
        return new PrinterInfoResult(caps.Name, caps.DriverName, caps.HorizontalDpi, caps.Dpi is { } d ? (int)d : null,
            caps.PaperWidth is { } w ? Math.Round(Units.Express(w, "mm", PrinterDpi.Dpi203), 1) : null,
            caps.PaperHeight is { } h ? Math.Round(Units.Express(h, "mm", PrinterDpi.Dpi203), 1) : null,
            caps.DpiSource.ToString(), note);
    }

    /// <summary>Prints the label after a dry run and a confirmation.</summary>
    [McpServerTool(Name = "print_label", Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Prints the current label on a Windows printer, in two steps. Printing is switched off unless the person who installed the server allows it and lists the printer. The FIRST call never prints: it returns exactly what would be sent (printer, copies, label size, any resolution conversion, the start of the ZPL) and a one-time confirmationToken. Show that to the user and, only if they agree, call print_label again with the same printer and copies and that token to send exactly one job. The token expires quickly and stops working if the label, printer or copies change.")]
    public async Task<PrintResultInfo> PrintLabel(
        [Description("Exact Windows printer name from list_printers; it must be on the allowed list.")] string printer,
        [Description("Number of labels. Capped by maxCopies in the settings (default 10).")] int copies = 1,
        [Description("The confirmationToken from the dry run. Leave empty for the first call.")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        // Rules that need no label come first so a disabled server answers instantly and never touches a printer.
        var policy = _ctx.Gate.CheckPolicy(printer, copies);
        if (policy is not null) throw new McpException(policy.Message!);

        string name = await RequireInstalledAsync(printer, cancellationToken).ConfigureAwait(false);
        var copy = _ctx.Session.GetCopy();
        var doc = copy.Document;

        // Match the printer's resolution the same way the app does, so the printed size stays right.
        var caps = await _ctx.Print.GetPrinterInfoAsync(PrinterTarget.Spooler(name), cancellationToken).ConfigureAwait(false);
        string? conversion = null;
        if (caps.Dpi is { } printerDpi && LabelDpiConverter.NeedsConversion(doc, printerDpi))
        {
            var converted = LabelDpiConverter.Convert(doc, printerDpi);
            conversion = $"The label was designed for {(int)doc.Dpi} DPI and the printer reports {(int)printerDpi} DPI, so it is rescaled to keep the printed size" +
                         (converted.Issues.Count == 0 ? "." : ": " + string.Join(" ", converted.Issues.Select(Messages.Describe)));
            doc = converted.Document;
        }
        else if (caps.Dpi is null)
        {
            conversion = $"The driver did not report a resolution, so no conversion is applied; the label stays at {(int)doc.Dpi} DPI.";
        }

        // Copies go on a copy of the label (^PQ); the working label is never altered by printing.
        doc.Print.Quantity = copies;
        var generated = ZplGenerator.Generate(doc);
        var issues = generated.Issues.Select(McpContext.ToInfo).ToList();
        if (generated.HasErrors)
        {
            _ctx.Gate.Forget();
            throw new McpException("The label has errors and cannot be printed: " +
                                   string.Join(" ", issues.Where(i => i.Severity == "error").Take(6).Select(i => i.Message)) + " Nothing was sent.");
        }

        string fingerprint = PrintGate.Fingerprint(name, copies, generated.Zpl);
        var decision = _ctx.Gate.Evaluate(name, copies, fingerprint, confirmationToken);
        string size = $"{Units.Express(doc.Width, "mm", doc.Dpi):0.##} x {Units.Express(doc.Height, "mm", doc.Dpi):0.##} mm ({doc.WidthDots} x {doc.HeightDots} dots at {(int)doc.Dpi} DPI)";
        var preview = generated.Zpl.Split('\n').Take(PreviewLines).ToList();

        // Imported ZPL that Rotula does not understand is sent to the printer exactly as it was written. The dry run lists every such
        // block, because the ZPL preview above shows only the first lines and the person confirming should see them all.
        var rawBlocks = doc.Elements
            .Select(e => e switch
            {
                RawZplElement r => r.Zpl,
                RecallPlaceholderElement p => p.Zpl,
                _ => null,
            })
            .Where(z => !string.IsNullOrWhiteSpace(z))
            .Select(z => Summarise(z!))
            .ToList();
        string rawNote = rawBlocks.Count == 0
            ? ""
            : $" This label also contains {rawBlocks.Count} block(s) of imported raw ZPL that are sent to the printer unchanged and were not checked by Rotula: {string.Join("; ", rawBlocks.Take(10))}{(rawBlocks.Count > 10 ? "; ..." : "")}.";

        switch (decision.Decision)
        {
            case GateDecision.Refused:
                throw new McpException(decision.Message!);

            case GateDecision.DryRun:
                return new PrintResultInfo("dry-run",
                    $"NOTHING WAS PRINTED. This is a preview of the job. If the user confirms, call print_label again with printer \"{name}\", copies {copies} and confirmationToken \"{decision.Token}\" before it expires." + rawNote,
                    name, copies, size, caps.Dpi is { } d ? (int)d : null, conversion, preview, decision.Token, decision.ExpiresAt?.ToUniversalTime(), issues, rawBlocks);

            default:
                // Exactly one job. The result is a value, never an exception; a failure is reported and nothing is retried.
                var result = await _ctx.Print.PrintAsync(PrinterTarget.Spooler(name), doc, null, null, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                    throw new McpException("The printer did not accept the job: " + Messages.Describe(result.Error!));
                return new PrintResultInfo("sent", $"Sent one print job of {copies} label(s) to \"{name}\" ({result.BytesSent:N0} bytes).",
                    name, copies, size, caps.Dpi is { } d2 ? (int)d2 : null, conversion, preview, null, null, issues, rawBlocks);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>A short, single-line form of a raw ZPL block for the confirmation message.</summary>
    private static string Summarise(string zpl)
    {
        string oneLine = string.Join(" ", zpl.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return oneLine.Length > 60 ? oneLine[..60] + "..." : oneLine;
    }

    private async Task<IReadOnlyList<PrinterInfo>> SafeListAsync(CancellationToken ct)
    {
        try { return await _ctx.Print.GetPrintersAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpException($"The printer list could not be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>Returns the installed printer's exact name, or refuses. Only names Windows reports are ever used.</summary>
    private async Task<string> RequireInstalledAsync(string? printer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(printer)) throw new McpException("A printer name is required. Call list_printers.");
        var all = await SafeListAsync(ct).ConfigureAwait(false);
        var found = all.FirstOrDefault(p => string.Equals(p.Name, printer.Trim(), StringComparison.OrdinalIgnoreCase));
        return found?.Name ?? throw new McpException($"Windows has no printer called \"{printer}\". Call list_printers for the installed names.");
    }
}
