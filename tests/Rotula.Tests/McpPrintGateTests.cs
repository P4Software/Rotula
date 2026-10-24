using ModelContextProtocol;
using Rotula.Mcp.Security;
using Rotula.Mcp.Settings;
using Rotula.Mcp.Tools;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>The print state machine: refused, dry run, token reuse, expiry, copies cap. Nothing here touches a real printer.</summary>
public class McpPrintGateTests
{
    private static McpSettings Allowed(int copies = 10) =>
        new() { AllowPrinting = true, AllowedPrinters = ["Zebra ZD420"], MaxCopies = copies, ConfirmationSeconds = 60 };

    [Fact]
    public void PrintingIsOffByDefault()
    {
        var gate = new PrintGate(new McpSettings());
        var r = gate.Evaluate("Zebra ZD420", 1, "fp", null);
        Assert.Equal(GateDecision.Refused, r.Decision);
        Assert.Contains("switched off", r.Message);
        Assert.Null(r.Token);
    }

    [Fact]
    public void AnEmptyAllowListRefusesEvenWhenPrintingIsOn()
    {
        var gate = new PrintGate(new McpSettings { AllowPrinting = true });
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420", 1, "fp", null).Decision);
    }

    [Fact]
    public void APrinterNotOnTheListIsRefused()
    {
        var gate = new PrintGate(Allowed());
        var r = gate.Evaluate("Office Laser", 1, "fp", null);
        Assert.Equal(GateDecision.Refused, r.Decision);
        Assert.Contains("allowed list", r.Message);
    }

    [Fact]
    public void ThePrinterNameMatchIgnoresCaseButNotSpelling()
    {
        var gate = new PrintGate(Allowed());
        Assert.Equal(GateDecision.DryRun, gate.Evaluate("zebra zd420", 1, "fp", null).Decision);
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD42", 1, "fp", null).Decision);
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420*", 1, "fp", null).Decision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void CopiesOutsideTheCapAreRefused(int copies)
    {
        var gate = new PrintGate(Allowed(10));
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420", copies, "fp", null).Decision);
    }

    [Fact]
    public void TheFirstCallIsAlwaysADryRunWithAToken()
    {
        var gate = new PrintGate(Allowed());
        var r = gate.Evaluate("Zebra ZD420", 3, "fp", null);
        Assert.Equal(GateDecision.DryRun, r.Decision);
        Assert.False(string.IsNullOrEmpty(r.Token));
        Assert.NotNull(r.ExpiresAt);
    }

    [Fact]
    public void ATokenConfirmsExactlyOnce()
    {
        var gate = new PrintGate(Allowed());
        string token = gate.Evaluate("Zebra ZD420", 2, "fp", null).Token!;
        Assert.Equal(GateDecision.Confirmed, gate.Evaluate("Zebra ZD420", 2, "fp", token).Decision);
        var again = gate.Evaluate("Zebra ZD420", 2, "fp", token);
        Assert.Equal(GateDecision.Refused, again.Decision);
        Assert.Contains("not valid", again.Message);
    }

    [Fact]
    public void ATokenForAnotherRequestIsRefusedAndConsumed()
    {
        var gate = new PrintGate(Allowed());
        string token = gate.Evaluate("Zebra ZD420", 2, "fp-A", null).Token!;
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420", 2, "fp-B", token).Decision);
        // The mismatch burned it: the original request cannot be confirmed any more either.
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420", 2, "fp-A", token).Decision);
    }

    [Fact]
    public void ChangingCopiesOrPrinterInvalidatesTheToken()
    {
        var gate = new PrintGate(new McpSettings { AllowPrinting = true, AllowedPrinters = ["A", "B"], MaxCopies = 10 });
        string t1 = gate.Evaluate("A", 1, PrintGate.Fingerprint("A", 1, "zpl"), null).Token!;
        Assert.Equal(GateDecision.Refused, gate.Evaluate("A", 2, PrintGate.Fingerprint("A", 2, "zpl"), t1).Decision);
        string t2 = gate.Evaluate("A", 1, PrintGate.Fingerprint("A", 1, "zpl"), null).Token!;
        Assert.Equal(GateDecision.Refused, gate.Evaluate("B", 1, PrintGate.Fingerprint("B", 1, "zpl"), t2).Decision);
    }

    [Fact]
    public void ATokenExpires()
    {
        var clock = McpTestEnv.Clock(out var advance);
        var gate = new PrintGate(Allowed(), clock);
        string token = gate.Evaluate("Zebra ZD420", 1, "fp", null).Token!;
        advance(TimeSpan.FromSeconds(61));
        var r = gate.Evaluate("Zebra ZD420", 1, "fp", token);
        Assert.Equal(GateDecision.Refused, r.Decision);
        Assert.Contains("expired", r.Message);
    }

    [Fact]
    public void ATokenStillWorksJustBeforeItExpires()
    {
        var clock = McpTestEnv.Clock(out var advance);
        var gate = new PrintGate(Allowed(), clock);
        string token = gate.Evaluate("Zebra ZD420", 1, "fp", null).Token!;
        advance(TimeSpan.FromSeconds(59));
        Assert.Equal(GateDecision.Confirmed, gate.Evaluate("Zebra ZD420", 1, "fp", token).Decision);
    }

    [Fact]
    public void ANewDryRunReplacesTheOldToken()
    {
        var gate = new PrintGate(Allowed());
        string first = gate.Evaluate("Zebra ZD420", 1, "fp", null).Token!;
        string second = gate.Evaluate("Zebra ZD420", 1, "fp", null).Token!;
        Assert.NotEqual(first, second);
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420", 1, "fp", first).Decision);
    }

    [Fact]
    public void GuessedOrMalformedTokensNeverConfirm()
    {
        var gate = new PrintGate(Allowed());
        gate.Evaluate("Zebra ZD420", 1, "fp", null);
        foreach (string guess in new[] { "0", "000000000000000000000000", "' OR 1=1", new string('a', 10_000), "TOKEN" })
            Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420", 1, "fp", guess).Decision);
    }

    [Fact]
    public void ARefusedPolicyDropsAnOutstandingToken()
    {
        var gate = new PrintGate(Allowed());
        string token = gate.Evaluate("Zebra ZD420", 1, "fp", null).Token!;
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420", 99, "fp", null).Decision);
        Assert.Equal(GateDecision.Refused, gate.Evaluate("Zebra ZD420", 1, "fp", token).Decision);
    }
}

/// <summary>The print tool end to end over a fake printer.</summary>
public class McpPrintToolTests : IDisposable
{
    private readonly McpTestEnv _env;

    public McpPrintToolTests()
    {
        _env = new McpTestEnv(s => s with { AllowPrinting = true, AllowedPrinters = ["Zebra ZD420"], MaxCopies = 5 });
        _env.Label.CreateLabel(100, 50);
        _env.Elements.AddText("Hello", 5, 5);
    }

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task TheFirstCallNeverPrints_AndShowsTheJob()
    {
        var r = await _env.Printing.PrintLabel("Zebra ZD420", 3);
        Assert.Equal("dry-run", r.Status);
        Assert.Contains("NOTHING WAS PRINTED", r.Message);
        Assert.Empty(_env.Printer.Jobs);
        Assert.Equal("Zebra ZD420", r.Printer);
        Assert.Equal(3, r.Copies);
        Assert.Contains("100 x 50 mm", r.LabelSize);
        Assert.Contains("^XA", r.ZplPreview);
        Assert.False(string.IsNullOrEmpty(r.ConfirmationToken));
    }

    [Fact]
    public async Task ConfirmingSendsExactlyOneJob_WithTheCopiesAsPq()
    {
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 3);
        var sent = await _env.Printing.PrintLabel("Zebra ZD420", 3, dry.ConfirmationToken);
        Assert.Equal("sent", sent.Status);
        var job = Assert.Single(_env.Printer.Jobs);
        Assert.Equal("Zebra ZD420", job.Printer);
        Assert.Contains("^PQ3,", System.Text.Encoding.UTF8.GetString(job.Data));
        Assert.Equal(0, _env.Printer.NetworkJobs);
    }

    [Fact]
    public async Task ReusingTheTokenSendsNothingMore()
    {
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);
        await _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken);
        await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken));
        Assert.Single(_env.Printer.Jobs);
    }

    [Fact]
    public async Task EditingTheLabelAfterTheDryRunInvalidatesTheToken()
    {
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);
        _env.Elements.AddText("Sneaky extra text", 5, 20);
        await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken));
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task ChangingTheCopiesAfterTheDryRunInvalidatesTheToken()
    {
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);
        await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 4, dry.ConfirmationToken));
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task CopiesAboveTheCapAreRefusedWithoutAToken()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 6));
        Assert.Contains("At most 5", ex.Message);
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task APrinterThatIsInstalledButNotAllowedIsRefused()
    {
        await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Office Laser", 1));
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task APrinterThatIsAllowedButNotInstalledIsRefused()
    {
        _env.Printer.Names = ["Something else"];
        var ex = await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1));
        Assert.Contains("no printer called", ex.Message);
    }

    [Fact]
    public async Task TheDryRunReportsTheResolutionConversion()
    {
        _env.Printer.Dpi = PrinterDpiOf(300);
        var r = await _env.Printing.PrintLabel("Zebra ZD420", 1);
        Assert.Equal(300, r.PrinterDpi);
        Assert.Contains("203 DPI", r.DpiConversion);
        Assert.Contains("300 DPI", r.DpiConversion);
        Assert.Contains("^PW1181", string.Join("\n", r.ZplPreview)); // 100 mm at 300 DPI
        // The working label itself is not converted by printing.
        Assert.Equal(203, _env.Label.GetLabel().Label.Dpi);
    }

    [Fact]
    public async Task WhenTheDriverKnowsNoResolutionThatIsSaid()
    {
        _env.Printer.Dpi = null;
        var r = await _env.Printing.PrintLabel("Zebra ZD420", 1);
        Assert.Contains("did not report", r.DpiConversion);
    }

    [Fact]
    public async Task ALabelWithErrorsIsNotOfferedForPrinting()
    {
        _env.Elements.AddBarcode("Ean13", "12345", 5, 20); // too short
        var ex = await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1));
        Assert.Contains("Nothing was sent", ex.Message);
    }

    [Fact]
    public async Task TheDryRunListsImportedRawZplThatWillBeSentUnchanged()
    {
        _env.Label.ImportZpl("^XA^FO10,10^A0N,30,30^FDHi^FS^JMA^MMT^XZ");
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);
        Assert.NotEmpty(dry.RawZplBlocks);
        Assert.Contains(dry.RawZplBlocks, b => b.Contains("^JMA"));
        Assert.Contains("sent to the printer unchanged", dry.Message);
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task WithPrintingOffEveryCallIsRefused_AndTheDriverIsNeverAsked()
    {
        using var off = new McpTestEnv();
        off.Label.CreateLabel(50, 30);
        var ex = await Assert.ThrowsAsync<McpException>(() => off.Printing.PrintLabel("Zebra ZD420", 1));
        Assert.Contains("switched off", ex.Message);
        Assert.Empty(off.Printer.Jobs);
    }

    [Fact]
    public async Task ListingPrintersIsReadOnly()
    {
        var list = await _env.Printing.ListPrinters();
        Assert.Equal(2, list.Printers.Count);
        Assert.True(list.Printers.Single(p => p.Name == "Zebra ZD420").AllowedForPrinting);
        Assert.False(list.Printers.Single(p => p.Name == "Office Laser").AllowedForPrinting);
        var info = await _env.Printing.GetPrinterInfo("Zebra ZD420");
        Assert.Equal(203, info.SupportedDpi);
        await Assert.ThrowsAsync<McpException>(() => _env.Printing.GetPrinterInfo("Not Installed"));
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task PrinterStatus_IsDecodedInPlainWords_AndPrintsNothing()
    {
        _env.Printer.Status = HostStatusParser.Parse(HsSamples.S1(paperOut: 1, pause: 1) + HsSamples.S2(remaining: 3));
        var r = await _env.Printing.GetPrinterStatus("zebra zd420");
        Assert.Equal("Zebra ZD420", r.Name);
        Assert.True(r.StatusAvailable);
        Assert.False(r.Ready);
        Assert.Equal(["out of labels", "paused"], r.Blocking);
        Assert.Equal(3, r.LabelsRemaining);
        Assert.Contains("cannot print right now", r.Message);
        Assert.Equal(["Zebra ZD420"], _env.Printer.StatusQueries);
        Assert.Empty(_env.Printer.Jobs);

        _env.Printer.Status = HostStatusParser.Parse(HsSamples.Ready);
        var ready = await _env.Printing.GetPrinterStatus("Zebra ZD420");
        Assert.True(ready.Ready);
        Assert.Contains("is ready to print", ready.Message);
    }

    [Fact]
    public async Task PrinterStatus_Unavailable_SaysSoWithoutFailing()
    {
        var r = await _env.Printing.GetPrinterStatus("Zebra ZD420");
        Assert.False(r.StatusAvailable);
        Assert.Null(r.Ready);
        Assert.Contains("does not answer status requests", r.Message);
    }

    [Fact]
    public async Task PrinterStatus_NeedsThePrintingPermission()
    {
        await Assert.ThrowsAsync<McpException>(() => _env.Printing.GetPrinterStatus("Office Laser"));
        using var off = new McpTestEnv();
        var ex = await Assert.ThrowsAsync<McpException>(() => off.Printing.GetPrinterStatus("Zebra ZD420"));
        Assert.Contains("switched off", ex.Message);
        Assert.Empty(off.Printer.StatusQueries);
        Assert.Empty(_env.Printer.StatusQueries);
    }

    private static Rotula.Core.Model.PrinterDpi PrinterDpiOf(int dpi) => (Rotula.Core.Model.PrinterDpi)dpi;
}
