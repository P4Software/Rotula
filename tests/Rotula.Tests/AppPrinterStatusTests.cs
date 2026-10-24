using System.Text;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Validation;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// The printer status in the app: the per-transport switch, the "Check printer status" button, and how the Print dialog and the batch print
/// show a held-back job or the verification after it. The fake print service only records calls.
/// </summary>
[Collection("App")]
public class AppPrinterStatusTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly MainViewModel _main;

    public AppPrinterStatusTests()
    {
        _fake.Settings.Current.Language = "en";
        _main = new MainViewModel(_fake.Build());
        _main.AddElementCommand.Execute(ToolboxKind.Box);
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private PrintViewModel Dialog(bool network, bool withRecords = false)
    {
        if (network) { _main.Printer.UseNetwork = true; _main.Printer.Host = "192.168.1.50"; }
        else _main.Printer.SelectedPrinterName = "Test Printer";
        return new PrintViewModel(_fake.Printer, _main.Printer, () => _main.Session.Document, () => _main.Data.CurrentData, _fake.Build().Files, "my-label",
            _main.Variables, withRecords ? _main.Data : null);
    }

    private static PrinterStatus Status(string reply) => HostStatusParser.Parse(reply);

    private static ValidationIssue Note(string key, params string[] args) => new(ValidationSeverity.Warning, key, null, args);

    // ------------------------------------------------------------------------------------ the switch

    [Fact]
    public void TheCheck_IsOnForNetworkPrinters_AndOffForWindowsPrinters_ByDefault()
    {
        var p = _main.Printer;
        p.UseNetwork = false;
        Assert.False(p.CheckStatus);
        p.SelectedPrinterName = "Test Printer";
        Assert.Null(p.TryBuildTarget(out var windows));
        Assert.False(windows!.Status.Enabled);

        p.UseNetwork = true;
        p.Host = "192.168.1.50";
        Assert.True(p.CheckStatus);
        Assert.Null(p.TryBuildTarget(out var tcp));
        Assert.True(tcp!.Status.CheckBeforePrinting);
        Assert.True(tcp.Status.VerifyAfterPrinting);
    }

    [Fact]
    public void TheChoice_IsKeptPerTransport_AndStored()
    {
        var p = _main.Printer;
        p.UseNetwork = true;
        p.CheckStatus = false;
        p.UseNetwork = false;
        p.CheckStatus = true;
        p.UseNetwork = true;
        Assert.False(p.CheckStatus);
        p.Persist();
        Assert.False(_fake.Settings.Current.Print.StatusCheckNetwork);
        Assert.True(_fake.Settings.Current.Print.StatusCheckWindows);

        var next = new MainViewModel(_fake.Build());
        next.Printer.UseNetwork = false;
        Assert.True(next.Printer.CheckStatus);
    }

    // ------------------------------------------------------------------------------------ "Check printer status"

    [Fact]
    public async Task CheckStatus_ShowsAReadyPrinter()
    {
        _fake.Printer.Status = Status(HsSamples.Ready);
        var p = Dialog(network: true).Printer;
        await p.CheckPrinterStatusCommand.ExecuteAsync(null);
        Assert.Equal("192.168.1.50", Assert.Single(_fake.Printer.StatusQueries).Host);
        Assert.True(p.HasStatusOk);
        Assert.False(p.HasStatusProblem);
        Assert.Contains("The printer is ready.", p.StatusText);
        Assert.Contains("tear-off", p.StatusText);
        Assert.Contains("Direct thermal", p.StatusText);
        Assert.False(p.IsCheckingStatus);
    }

    [Fact]
    public async Task CheckStatus_NamesEveryProblem_InPlainLanguage()
    {
        _fake.Printer.Status = Status(HsSamples.S1(paperOut: 1, pause: 1, under: 1) + HsSamples.S2(remaining: 12));
        var p = Dialog(network: true).Printer;
        await p.CheckPrinterStatusCommand.ExecuteAsync(null);
        Assert.True(p.HasStatusProblem);
        Assert.Contains("cannot print right now: out of labels, paused", p.StatusText);
        Assert.Contains("printhead too cold", p.StatusText);
        Assert.Contains("Labels still to print: 12", p.StatusText);
    }

    [Fact]
    public async Task CheckStatus_SaysWhyThereIsNoAnswer()
    {
        var p = Dialog(network: false).Printer;
        await p.CheckPrinterStatusCommand.ExecuteAsync(null);
        Assert.True(p.HasStatusProblem);
        Assert.Contains("does not answer status requests", p.StatusText);
        Assert.Contains("You can still print", p.StatusText);
    }

    [Fact]
    public async Task CheckStatus_WithoutAPrinter_SaysWhatIsMissing_AndAsksNothing()
    {
        var p = _main.Printer;
        p.UseNetwork = true;
        p.Host = "";
        await p.CheckPrinterStatusCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.StatusQueries);
        Assert.True(p.HasStatusProblem);
    }

    [Fact]
    public async Task ChoosingAnotherPrinter_ClearsTheOldAnswer()
    {
        _fake.Printer.Status = Status(HsSamples.Ready);
        var p = Dialog(network: true).Printer;
        await p.CheckPrinterStatusCommand.ExecuteAsync(null);
        Assert.True(p.HasStatusText);
        p.Host = "192.168.1.51";
        Assert.False(p.HasStatusText);
    }

    // ------------------------------------------------------------------------------------ the Print dialog

    [Fact]
    public async Task Print_AHeldBackJob_ShowsTheReason_AndTheOtherConditions()
    {
        _fake.Printer.Result = _ => PrintResult.Fail(PrintErrorKind.PaperOut, "192.168.1.50:9100", "PaperOut", "Paused");
        var dialog = Dialog(network: true);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.True(dialog.ResultIsError);
        Assert.Contains("Nothing was sent", dialog.ResultText);
        Assert.Contains("out of labels", dialog.ResultText);
        Assert.Contains("The printer also reports: paused.", dialog.ResultText);
        Assert.True(Assert.Single(_fake.Printer.Printed).Target.Status.CheckBeforePrinting);
    }

    [Fact]
    public async Task Print_Verified_IsShownWithTheSuccessLine()
    {
        _fake.Printer.Result = _ => PrintResult.Ok(10).WithNotices([Note(PrinterStatusKeys.Verified)]);
        var dialog = Dialog(network: true);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.False(dialog.ResultIsError);
        Assert.Contains("Printed and verified", dialog.ResultText);
    }

    [Fact]
    public async Task Print_AFaultAfterTheJob_IsShownAsAProblem()
    {
        _fake.Printer.Result = _ => PrintResult.Ok(10).WithNotices([Note(PrinterStatusKeys.FaultAfterJob, "RibbonOut")]);
        var dialog = Dialog(network: true);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.True(dialog.ResultIsError);
        Assert.Contains("the printer reported a problem: ribbon out", dialog.ResultText);
    }

    [Theory]
    [InlineData(PrinterStatusKeys.NotChecked, "NoReply", "did not answer in time")]
    [InlineData(PrinterStatusKeys.NotVerified, PrinterStatusKeys.StillPrinting, "still printing")]
    [InlineData(PrinterStatusKeys.NotVerified, PrinterStatusKeys.Stopped, "the check was stopped")]
    [InlineData(PrinterStatusKeys.NotVerified, "SomethingNew", "the reason is unknown")]
    [InlineData(PrinterStatusKeys.Warning, "HeadTooCold", "printhead too cold")]
    public void StatusNotes_ReadAsSentences(string key, string arg, string expected)
    {
        string text = PrintMessages.DescribeNotice(Note(key, arg));
        Assert.Contains(expected, text);
        Assert.DoesNotContain("{", text);
    }

    [Theory]
    [MemberData(nameof(UiStrings.LanguageData), MemberType = typeof(UiStrings))]
    public void EveryReasonAndEveryHeldBackKind_HasText(string lang)
    {
        Loc.Instance.SetLanguage(lang);
        foreach (var reason in Enum.GetValues<StatusUnavailableReason>())
        {
            var (text, problem) = PrintMessages.DescribeStatus(PrinterStatus.Unavailable(reason));
            Assert.True(problem);
            Assert.DoesNotContain("Print.Status.Reason.Unknown", text);
            Assert.True(Loc.Instance.HasKey("Print.Status.Reason." + reason), reason.ToString());
        }
        foreach (var condition in Enum.GetValues<PrinterCondition>().Where(PrinterStatus.IsBlockingCondition))
        {
            var error = PrintError.Create(Enum.Parse<PrintErrorKind>(condition.ToString()), "P", condition.ToString(), "Paused");
            string text = PrintMessages.Describe(error);
            // The printer name is quoted in the language's own style: "P" in English, « P » in Canadian French.
            Assert.Matches("(\"P\"|«\u00A0P\u00A0»)", text);
            Assert.DoesNotContain("{", text);
        }
    }

    // ------------------------------------------------------------------------------------ batch printing

    private async Task LoadRecords(int rows)
    {
        var sb = new StringBuilder("n\n");
        for (int i = 1; i <= rows; i++) sb.Append(i).Append('\n');
        string path = Path.Combine(_dir, "data.csv");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "Item {{n}}" }));
        Assert.True(await _main.Data.LoadAsync(path));
    }

    [Fact]
    public async Task ABatch_ChecksEveryJob_AndVerifiesOnceAtTheEnd()
    {
        await LoadRecords(4);
        _main.Data.MaxRecordsText = "2";
        _fake.Printer.VerifyNotes = [Note(PrinterStatusKeys.Verified)];
        var dialog = Dialog(network: true, withRecords: true);
        await dialog.PrintCommand.ExecuteAsync(null);

        Assert.Equal(2, _fake.Printer.ZplJobs.Count);
        Assert.All(_fake.Printer.ZplJobs, j => Assert.True(j.Target.Status.CheckBeforePrinting));
        Assert.All(_fake.Printer.ZplJobs, j => Assert.False(j.Target.Status.VerifyAfterPrinting));
        Assert.True(Assert.Single(_fake.Printer.Verifications).Status.VerifyAfterPrinting);
        Assert.Contains("Printed and verified", dialog.ResultText);
        Assert.False(dialog.ResultIsError);
        dialog.Detach();
    }

    [Fact]
    public async Task ABatch_WithAFaultAfterTheLastJob_IsShownAsAProblem()
    {
        await LoadRecords(2);
        _fake.Printer.VerifyNotes = [Note(PrinterStatusKeys.FaultAfterJob, "PaperOut")];
        var dialog = Dialog(network: true, withRecords: true);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.True(dialog.ResultIsError);
        Assert.Contains("out of labels", dialog.ResultText);
        dialog.Detach();
    }

    [Fact]
    public async Task ABatch_ToAWindowsPrinter_IsNotVerifiedByDefault()
    {
        await LoadRecords(2);
        var dialog = Dialog(network: false, withRecords: true);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.NotEmpty(_fake.Printer.ZplJobs);
        Assert.Empty(_fake.Printer.Verifications);
        dialog.Detach();
    }

    [Fact]
    public async Task ABatch_ThatIsHeldBack_IsNotVerified()
    {
        await LoadRecords(2);
        _fake.Printer.Result = _ => PrintResult.Fail(PrintErrorKind.HeadOpen, "192.168.1.50:9100", "HeadOpen");
        var dialog = Dialog(network: true, withRecords: true);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Verifications);
        Assert.True(dialog.ResultIsError);
        Assert.Contains("printhead is open", dialog.ResultText);
        dialog.Detach();
    }
}
