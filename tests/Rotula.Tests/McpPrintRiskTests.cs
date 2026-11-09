using ModelContextProtocol;
using Rotula.Core.Elements;
using Rotula.Mcp.Security;

namespace Rotula.Tests;

/// <summary>
/// The scanner that finds printer-changing commands in the ZPL a job will send. It must see every command whatever the padding around
/// it, so it reads the whole text and nothing is skipped on the strength of what the text claims about itself.
/// </summary>
public class McpPrinterCommandScannerTests
{
    private static string[] Names(string zpl) => PrinterCommandScanner.Scan(zpl).Select(c => c.Name).ToArray();

    [Theory]
    [InlineData("^XA~JA^XZ", "~JA")]
    [InlineData("^XA~JB^XZ", "~JB")]
    [InlineData("^XA~JC^XZ", "~JC")]
    [InlineData("^XA^IDE:*.*^XZ", "^ID")]
    [InlineData("^XA~WC^XZ", "~WC")]
    [InlineData("^XA~DYE:LOGO,P,P,5,,ABCDE^XZ", "~DY")]
    [InlineData("^XA^DFR:FMT.ZPL^FS^XZ", "^DF")]
    [InlineData("^XA^JUF^XZ", "^JU")]
    [InlineData("^XA^JJ1,1^XZ", "^JJ")]
    [InlineData("^XA~RO^XZ", "~RO")]
    [InlineData("^XA^MPD^XZ", "^MP")]
    [InlineData("^XA~SD30^XZ", "~SD")]
    [InlineData("^XA^MMC^XZ", "^MM")]
    [InlineData("^XA^SZ1^XZ", "^SZ")]
    [InlineData("^XA~PS^XZ", "~PS")]
    [InlineData("^XA~JL^XZ", "~JL")]
    [InlineData("^XA^CWA,E:ARIAL.TTF^XZ", "^CW")]
    [InlineData("^XA~DGE:LOGO.GRF,4,2,ABCDABCD^XZ", "~DG")]
    public void EachListedCommandIsFoundByName(string zpl, string expected) => Assert.Contains(expected, Names(zpl));

    [Fact]
    public void LowerCaseCommandsAreFoundBecausePrintersAcceptThem() => Assert.Equal(["~JA", "^ID"], Names("^xa~ja^ide:*.*^xz"));

    [Fact]
    public void ACommandAfterHundredsOfCharactersOfPaddingIsStillFound()
    {
        string padded = "^XA^FO10,10^FD" + new string('x', 5000) + "^FS" + new string(' ', 3000) + "~JA^XZ";
        Assert.Equal(["~JA"], Names(padded));
    }

    [Fact]
    public void TheNumberOfOccurrencesAndAnExampleAreReported()
    {
        var found = Assert.Single(PrinterCommandScanner.Scan("^XA^IDE:A.ZPL^IDE:B.ZPL^IDR:*.*^XZ"));
        Assert.Equal("^ID", found.Name);
        Assert.Equal(3, found.Count);
        Assert.Equal("^IDE:A.ZPL", found.Example);
    }

    [Fact]
    public void ADownloadToRamIsNotFlaggedButAnyFlashDeviceIs()
    {
        Assert.Empty(Names("^XA~DGR:LOGO.GRF,4,2,ABCDABCD^XZ"));
        Assert.Empty(Names("^XA~DGLOGO.GRF,4,2,ABCDABCD^XZ")); // no device letter means RAM
        Assert.Equal(["~DG"], Names("^XA~DGB:LOGO.GRF,4,2,ABCDABCD^XZ"));
        Assert.Equal(["~DG"], Names("^XA~dga:LOGO.GRF,4,2,ABCDABCD^XZ"));
    }

    [Theory]
    [InlineData("^XA^CC+^FO10,10^FDx^FS+IDE:*.*^XZ")]
    [InlineData("^XA~CT+^XZ")]
    [InlineData("^XA^CD;^XZ")]
    public void ChangingTheCommandCharactersIsFlaggedBecauseTheRestCannotBeRead(string zpl) =>
        Assert.Contains(PrinterCommandScanner.Scan(zpl), c => c.Name is "^CC" or "~CT" or "^CD");

    [Fact]
    public void OrdinaryLabelCommandsAreNotFlagged() =>
        Assert.Empty(Names("^XA^MNY^MTT^CI28^PW800^LL400^PON^LH0,0^MD10^PR4^FO10,10^A0N,30,30^FDHello^FS^BCN,100,Y,N,N^FD123^FS^PQ2,0,1,Y^XZ"));

    [Fact]
    public void TextWithoutAControlCharacterIsNotFlagged() => Assert.Empty(Names("JA ID WC ~ ^ plain text"));

    [Fact]
    public void ATildeOrCaretAtTheVeryEndDoesNotBreakTheScan() => Assert.Empty(Names("^XA^FDx^FS^XZ~"));
}

/// <summary>print_label shows every raw block and every printer-changing command, and refuses to send those without an explicit acknowledgement.</summary>
public class McpPrintRiskTests : IDisposable
{
    private readonly McpTestEnv _env;

    public McpPrintRiskTests()
    {
        _env = new McpTestEnv(s => s with { AllowPrinting = true, AllowedPrinters = ["Zebra ZD420"], MaxCopies = 5 });
        _env.Label.CreateLabel(100, 50);
        _env.Elements.AddText("Hello", 5, 5);
    }

    public void Dispose() => _env.Dispose();

    private void AddRaw(string zpl, bool visual = false) =>
        _env.Context.Session.Mutate("test", d => { d.Elements.Add(new RawZplElement { Zpl = zpl, IsVisual = visual }); return 0; });

    /// <summary>What a hostile file does: long harmless-looking padding first, the dangerous command after it.</summary>
    private static string Buried(string command) => "^FX " + new string('a', 200) + " " + command;

    [Fact]
    public async Task ADangerousCommandBuriedAfterPaddingIsNamedInTheDryRun()
    {
        AddRaw(Buried("~JA"));
        AddRaw(Buried("^IDE:*.*"));

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        Assert.Equal("dry-run", dry.Status);
        Assert.True(dry.RequiresAcknowledgement);
        Assert.Equal(2, dry.RiskyCommands.Count);
        Assert.Contains(dry.RiskyCommands, c => c.Name == "~JA");
        Assert.Contains(dry.RiskyCommands, c => c.Name == "^ID" && c.Example!.StartsWith("^IDE:*.*"));
        Assert.Contains("~JA", dry.Message);
        Assert.Contains("^ID", dry.Message);
        Assert.Contains("acknowledgedCommands", dry.Message);
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task ADangerousCommandBuriedAfterManyRawBlocksIsStillFound()
    {
        for (int i = 0; i < 25; i++) AddRaw("^FX filler " + i);
        AddRaw("~JB");

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        Assert.Contains(dry.RiskyCommands, c => c.Name == "~JB");
        Assert.True(dry.RequiresAcknowledgement);
    }

    [Fact]
    public async Task ConfirmingWithoutTheAcknowledgementSendsNothing_AndUsesUpTheCode()
    {
        AddRaw(Buried("~JA"));
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        var ex = await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken));
        Assert.Contains("~JA", ex.Message);
        Assert.Contains("acknowledgedCommands", ex.Message);
        Assert.Empty(_env.Printer.Jobs);

        // The code was single use: even the right acknowledgement cannot revive it, a new preview is needed.
        await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken, null, ["~JA"]));
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task AnIncompleteAcknowledgementIsRefused()
    {
        AddRaw("~JA");
        AddRaw("^IDE:*.*");
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        var ex = await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken, null, ["~JA"]));

        Assert.Contains("^ID", ex.Message);
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task AFlagWithoutTheCommandNamesIsNotAnAcknowledgement()
    {
        AddRaw("~JA");
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken, null, ["yes"]));
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task AcknowledgingEveryListedCommandSendsExactlyOneJob()
    {
        AddRaw(Buried("~JA"));
        AddRaw(Buried("^IDE:*.*"));
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        var sent = await _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken, null, dry.RiskyCommands.Select(c => c.Name.ToLowerInvariant()).ToList());

        Assert.Equal("sent", sent.Status);
        Assert.Single(_env.Printer.Jobs);
    }

    [Fact]
    public async Task EditingTheLabelAfterTheDryRunStillInvalidatesTheCode()
    {
        AddRaw("~JA");
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);
        AddRaw("^IDE:*.*"); // a second dangerous command appears after the user saw the list

        await Assert.ThrowsAsync<McpException>(() => _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken, null, ["~JA", "^ID"]));
        Assert.Empty(_env.Printer.Jobs);
    }

    [Fact]
    public async Task AnOrdinaryLabelNeedsNoAcknowledgement()
    {
        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        Assert.False(dry.RequiresAcknowledgement);
        Assert.Empty(dry.RiskyCommands);
        Assert.Equal("sent", (await _env.Printing.PrintLabel("Zebra ZD420", 1, dry.ConfirmationToken)).Status);
    }

    [Fact]
    public async Task TheFullRawTextIsShownNotJustItsStart()
    {
        string block = Buried("~JA");
        AddRaw(block);

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        Assert.Contains(dry.RawZplBlocks, b => b.EndsWith("~JA", StringComparison.Ordinal) && b.Length >= block.Length);
    }

    [Fact]
    public async Task AnEnormousBlockIsTruncatedWithAStatement_AndStillScanned()
    {
        AddRaw("^FX " + new string('z', 100_000) + " ~JA");

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        var shown = Assert.Single(dry.RawZplBlocks);
        Assert.Contains("not shown", shown);
        Assert.True(shown.Length < 10_000);
        Assert.Contains(dry.RiskyCommands, c => c.Name == "~JA");
    }

    [Fact]
    public async Task EveryRawBlockIsListedUpToALargeLimit_AndTheRestIsCounted()
    {
        for (int i = 0; i < 130; i++) AddRaw("^FX note " + i);

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        Assert.True(dry.RawZplBlocks.Count > 100);
        Assert.Contains("not listed", dry.RawZplBlocks[^1]);
    }

    [Fact]
    public async Task ImportedZplWithADangerousCommandIsCaughtToo()
    {
        _env.Label.ImportZpl("^XA^FO10,10^A0N,30,30^FDHi^FS~JA^XZ");

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        Assert.Contains(dry.RiskyCommands, c => c.Name == "~JA");
        Assert.True(dry.RequiresAcknowledgement);
    }

    [Fact]
    public async Task TextThatLooksLikeACommandIsEscapedAndNotFlagged()
    {
        _env.Elements.AddText("~JA ^IDE:*.* ~WC", 5, 20);

        var dry = await _env.Printing.PrintLabel("Zebra ZD420", 1);

        Assert.False(dry.RequiresAcknowledgement);
        Assert.Empty(dry.RiskyCommands);
    }
}
