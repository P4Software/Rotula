using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.App.Views;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// The Print dialog in front of the Windows-driver (picture) print mode: the mode line, the one-time question, the pre-flight findings and the
/// single acknowledgement. A printer in printer-language (ZPL) mode must behave exactly as before: no pre-flight, no findings, no new step.
/// Nothing here reaches a real printer: the services are fakes, or the real print service wired to a fake spooler and a fake driver.
/// </summary>
[Collection("App")]
public class AppDriverPrintTests : IDisposable
{
    private const string Queue = "Brother QL-820NWB";
    private readonly FakeServices _fake = new();
    private readonly MainViewModel _main;

    /// <summary>What the fake driver reports when a test needs media: a 4 x 6 inch page at 203 dpi, printable to its edges.</summary>
    private static readonly DriverMedia Media = new(203, 203, 812, 1218, 812, 1218, 0, 0, true);

    public AppDriverPrintTests()
    {
        _fake.Settings.Current.Language = "en";
        _fake.Printer.Printers = new[] { new PrinterInfo(Queue, "USB001", "Brother QL-820NWB", true, false) };
        _main = new MainViewModel(_fake.Build());
        _main.AddElementCommand.Execute(ToolboxKind.Box);
    }

    public void Dispose() => Loc.Instance.SetLanguage("en");

    /// <summary>The dialog for <see cref="Queue"/>, with the given mode answered first (null leaves the printer's question unanswered).</summary>
    private PrintViewModel Dialog(PrinterOutputMode? mode, IPrintService? service = null)
    {
        _main.Printer.SelectedPrinterName = Queue;
        if (mode is { } m) _main.Printer.ChooseModeCommand.Execute(m);
        return new PrintViewModel(service ?? _fake.Printer, _main.Printer, () => _main.Session.Document, () => _main.Data.CurrentData, _fake.Build().Files, "my-label");
    }

    private static ValidationIssue Warning(string key, params string[] args) => new(ValidationSeverity.Warning, key, null, args);

    private static ValidationIssue Error(string key, params string[] args) => new(ValidationSeverity.Error, key, null, args);

    /// <summary>Makes the fake pre-flight answer these findings for every document.</summary>
    private void Findings(params ValidationIssue[] findings) => _fake.Printer.Preflight = (_, _) => new DriverPreflightResult(findings, Media);

    private static string Fingerprint(params ValidationIssue[] findings) => new DriverPreflightResult(findings, null).Fingerprint;

    // ------------------------------------------------------------------------------------ printer language (ZPL) is untouched

    [Fact]
    public async Task ZplModeNeverCallsPreflight()
    {
        // Even a fake that would report findings is never asked: a ZPL print has no pre-flight step at all.
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        var dialog = Dialog(PrinterOutputMode.PrinterLanguage);
        dialog.CopiesText = "3";
        await dialog.PrintCommand.ExecuteAsync(null);
        await dialog.PrintTestCommand.ExecuteAsync(null);

        Assert.Empty(_fake.Printer.Preflights);
        var call = Assert.Single(_fake.Printer.Printed);
        Assert.Equal(PrinterOutputMode.PrinterLanguage, call.Target.Mode);
        Assert.Null(call.Target.AcknowledgedFindings);
        Assert.Equal(3, call.Doc.Print.Quantity);
        var test = Assert.Single(_fake.Printer.TestLabels);
        Assert.Null(test.Target.AcknowledgedFindings);
        Assert.False(dialog.HasPreflightLines);
        Assert.False(dialog.NeedsAcknowledgement);
        Assert.False(dialog.ResultIsError);
        Assert.Equal(Loc.Get("Print.Done.Test"), dialog.ResultText);
    }

    [Fact]
    public async Task ZplModeOnANetworkPrinter_NeverCallsPreflightAndShowsNoQuestion()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        _main.Printer.UseNetwork = true;
        _main.Printer.Host = "192.168.1.50";
        var dialog = new PrintViewModel(_fake.Printer, _main.Printer, () => _main.Session.Document, () => null, _fake.Build().Files);
        Assert.False(_main.Printer.ShowModeQuestion);
        Assert.False(_main.Printer.ShowModeLine);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Preflights);
        Assert.Single(_fake.Printer.Printed);
    }

    // ------------------------------------------------------------------------------------ findings and the one acknowledgement

    [Fact]
    public async Task DriverModeWithWarningsStopsUntilAcknowledged()
    {
        var finding = Warning(DriverPreflight.ModuleBelowMinimum, "SKU", "6.6", "9.8");
        Findings(finding);
        var dialog = Dialog(PrinterOutputMode.WindowsDriver);

        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Single(_fake.Printer.Preflights);
        Assert.Single(dialog.PreflightLines);
        Assert.True(dialog.HasPreflightLines);
        Assert.True(dialog.NeedsAcknowledgement);
        Assert.False(dialog.Acknowledged);
        Assert.Empty(_fake.Printer.Printed);
        Assert.True(dialog.ResultIsError);
        Assert.Equal(Loc.Get("Print.Driver.TickToContinue"), dialog.ResultText);
        Assert.Contains("SKU", dialog.PreflightLines[0]);

        dialog.Acknowledged = true;
        await dialog.PrintCommand.ExecuteAsync(null);
        var call = Assert.Single(_fake.Printer.Printed);
        Assert.Equal(Fingerprint(finding), call.Target.AcknowledgedFindings);
        Assert.Equal(PrinterOutputMode.WindowsDriver, call.Target.Mode);
        // The document that was checked is the very instance that is printed, so the service works out the same fingerprint.
        Assert.Same(_fake.Printer.Preflights[^1].Doc, call.Doc);
        Assert.False(dialog.ResultIsError);
        // The findings stay on screen after a successful print: they describe what the label looked like on paper.
        Assert.True(dialog.HasPreflightLines);
    }

    [Fact]
    public async Task DriverModeWithErrorNeverPrintsEvenWhenTicked()
    {
        var oneDot = Error(DriverPreflight.ModuleOneDot, "SKU");
        Findings(oneDot, Warning(DriverPreflight.RawBlocks, "1"));
        var dialog = Dialog(PrinterOutputMode.WindowsDriver);

        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Printed);
        Assert.True(dialog.ResultIsError);
        Assert.Contains(DriverPreflightGate.FormatFinding(oneDot), dialog.ResultText);
        Assert.Equal(2, dialog.PreflightLines.Count);
        Assert.False(dialog.NeedsAcknowledgement);   // no box to tick: nothing can override an error

        dialog.Acknowledged = true;
        await dialog.PrintCommand.ExecuteAsync(null);
        await dialog.PrintTestCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Printed);
        Assert.Empty(_fake.Printer.TestLabels);
        Assert.True(dialog.ResultIsError);
    }

    [Fact]
    public async Task AcknowledgementResetsWhenTheDesignChanges()
    {
        // The finding depends on the design, so editing it changes the fingerprint.
        _fake.Printer.Preflight = (_, doc) =>
            new DriverPreflightResult(new[] { Warning(DriverPreflight.RawBlocks, doc.Elements.Count.ToString(CultureInfo.InvariantCulture)) }, Media);
        var dialog = Dialog(PrinterOutputMode.WindowsDriver);
        await dialog.PrintCommand.ExecuteAsync(null);
        dialog.Acknowledged = true;

        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new RawZplElement { Zpl = "^FO10,10^GB50,50,2^FS", IsVisual = true }));
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.False(dialog.Acknowledged);
        Assert.Empty(_fake.Printer.Printed);
        Assert.Equal(Loc.Get("Print.Driver.TickToContinue"), dialog.ResultText);
        Assert.Contains("2", dialog.PreflightLines.Single());

        dialog.Acknowledged = true;
        await dialog.PrintCommand.ExecuteAsync(null);
        var call = Assert.Single(_fake.Printer.Printed);
        Assert.Equal(Fingerprint(Warning(DriverPreflight.RawBlocks, "2")), call.Target.AcknowledgedFindings);
    }

    [Fact]
    public async Task AcknowledgementResetsWhenThePrinterOrTheModeChanges()
    {
        _fake.Printer.Printers = new[]
        {
            new PrinterInfo(Queue, "USB001", "Brother QL-820NWB", true, false),
            new PrinterInfo("DYMO LabelWriter", "USB002", "DYMO LabelWriter 450", false, false),
        };
        await _main.Printer.RefreshPrintersAsync();
        _main.Printer.SelectedPrinterName = "DYMO LabelWriter";
        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);
        Findings(Warning(DriverPreflight.PaperMismatch, "100x50", "101.6x152.4"));
        var dialog = Dialog(PrinterOutputMode.WindowsDriver);

        await dialog.PrintCommand.ExecuteAsync(null);
        dialog.Acknowledged = true;
        // Two printers can report identical findings; the tick still belongs to the printer it was given for.
        _main.Printer.SelectedPrinterName = "DYMO LabelWriter";
        Assert.False(dialog.Acknowledged);
        Assert.False(dialog.HasPreflightLines);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Printed);

        dialog.Acknowledged = true;
        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.PrinterLanguage);
        Assert.False(dialog.Acknowledged);
        Assert.False(dialog.HasPreflightLines);
    }

    [Fact]
    public async Task ATestLabelInDriverMode_IsCheckedAndCarriesTheAcknowledgement()
    {
        var finding = Warning(DriverPreflight.PaperMismatch, "100x50", "101.6x152.4");
        Findings(finding);
        var dialog = Dialog(PrinterOutputMode.WindowsDriver);
        await dialog.PrintTestCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.TestLabels);
        Assert.True(dialog.NeedsAcknowledgement);

        dialog.Acknowledged = true;
        await dialog.PrintTestCommand.ExecuteAsync(null);
        var test = Assert.Single(_fake.Printer.TestLabels);
        Assert.Equal(Fingerprint(finding), test.Target.AcknowledgedFindings);
        // The document checked is the test label the service builds from the same profile.
        var checkedDoc = _fake.Printer.Preflights[^1].Doc;
        Assert.Equal(TestLabel.Build(test.Profile).Width, checkedDoc.Width);
        Assert.Equal(TestLabel.Build(test.Profile).Elements.Count, checkedDoc.Elements.Count);
    }

    [Fact]
    public async Task ACancelledPreflight_IsACalmStop_NotAListOfProblems()
    {
        Findings(Error(DriverPreflight.Cancelled));
        var dialog = Dialog(PrinterOutputMode.WindowsDriver);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Printed);
        Assert.False(dialog.ResultIsError);
        Assert.Equal(Loc.Get("Common.OperationCancelled"), dialog.ResultText);
        Assert.False(dialog.HasPreflightLines);
    }

    [Fact]
    public async Task NoFindings_PrintsAtOnce_WithoutABox()
    {
        Findings();
        var dialog = Dialog(PrinterOutputMode.WindowsDriver);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Single(_fake.Printer.Printed);
        Assert.False(dialog.HasPreflightLines);
        Assert.False(dialog.NeedsAcknowledgement);
        Assert.False(dialog.ResultIsError);
    }

    // ------------------------------------------------------------------------------------ the real print service agrees on the fingerprint

    /// <summary>The real print service with a fake spooler and a fake driver whose media refuses the label size (one PaperMismatch warning).</summary>
    private static (PrintService Service, FakeDriverPrinter Driver) RealService()
    {
        var spooler = new FakeSpooler();
        var driver = new FakeDriverPrinter { Media = Media with { CustomSizeAccepted = false } };
        return (new PrintService(spooler, new TcpRawPrinter(), spooler, new PrinterStatusReader(), driver), driver);
    }

    [Fact]
    public async Task TheRealService_PrintsTheLabelOnceTicked_SoTheDialogCheckedTheSameDocument()
    {
        var (service, driver) = RealService();
        var dialog = Dialog(PrinterOutputMode.WindowsDriver, service);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(driver.Jobs);
        Assert.True(dialog.NeedsAcknowledgement);
        Assert.Contains(dialog.PreflightLines, l => l.Contains("100", StringComparison.Ordinal));

        dialog.Acknowledged = true;
        await dialog.PrintCommand.ExecuteAsync(null);
        // A different document would give another fingerprint and the service would refuse it as not acknowledged.
        Assert.False(dialog.ResultIsError, dialog.ResultText);
        Assert.Single(driver.Jobs);
    }

    [Fact]
    public async Task TheRealService_PrintsTheTestLabelOnceTicked_SoTheDialogCheckedTheSameTestLabel()
    {
        var (service, driver) = RealService();
        var dialog = Dialog(PrinterOutputMode.WindowsDriver, service);
        dialog.Printer.WidthText = "80";
        await dialog.PrintTestCommand.ExecuteAsync(null);
        Assert.Empty(driver.Jobs);
        Assert.True(dialog.NeedsAcknowledgement);

        dialog.Acknowledged = true;
        await dialog.PrintTestCommand.ExecuteAsync(null);
        Assert.False(dialog.ResultIsError, dialog.ResultText);
        var job = Assert.Single(driver.Jobs);
        Assert.Equal(80, job.WidthMm, 1);
    }

    // ------------------------------------------------------------------------------------ the one-time question

    [Fact]
    public async Task APrinterThatHasNotAnswered_DoesNotPrint_AndTheQuestionShows()
    {
        await _main.Printer.RefreshPrintersAsync();
        var dialog = Dialog(null);
        Assert.True(_main.Printer.NeedsModeQuestion);
        Assert.True(_main.Printer.ShowModeQuestion);
        Assert.False(_main.Printer.ShowModeLine);

        await dialog.PrintCommand.ExecuteAsync(null);
        await dialog.PrintTestCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Printed);
        Assert.Empty(_fake.Printer.TestLabels);
        Assert.Empty(_fake.Printer.Preflights);
        Assert.True(dialog.ResultIsError);
        Assert.Equal(Loc.Get("Print.Mode.AnswerFirst"), dialog.ResultText);

        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.PrinterLanguage);
        Assert.False(_main.Printer.ShowModeQuestion);
        Assert.True(_main.Printer.ShowModeLine);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Single(_fake.Printer.Printed);
    }

    [Fact]
    public async Task AQueueThatIsNotInstalled_GetsNoQuestion_AndPrintSaysWhatToDo()
    {
        await _main.Printer.RefreshPrintersAsync();
        _main.Printer.SelectedPrinterName = "Old Printer";
        var dialog = new PrintViewModel(_fake.Printer, _main.Printer, () => _main.Session.Document, () => null, _fake.Build().Files);
        Assert.True(_main.Printer.NeedsModeQuestion);
        Assert.False(_main.Printer.ShowModeQuestion);

        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Printed);
        Assert.True(dialog.ResultIsError);
        Assert.Equal(Loc.Get("Print.Mode.PrinterNotListed", "Old Printer"), dialog.ResultText);
    }

    [Fact]
    public void TheModeLine_ExplainsTheMode_AndOffersTheOtherOne()
    {
        var dialog = Dialog(PrinterOutputMode.PrinterLanguage);
        Assert.Equal(Loc.Get("Print.Mode.Explain.PrinterLanguage"), dialog.ModeExplanation);
        var raised = new List<string?>();
        dialog.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);
        Assert.Equal(Loc.Get("Print.Mode.Explain.WindowsDriver"), dialog.ModeExplanation);
        Assert.Contains(nameof(PrintViewModel.ModeExplanation), raised);
        dialog.Detach();
    }

    [Fact]
    public void ADetachedDialog_StopsListeningToThePrinter()
    {
        var dialog = Dialog(PrinterOutputMode.PrinterLanguage);
        var raised = new List<string?>();
        dialog.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        dialog.Detach();
        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);
        Assert.Empty(raised);
    }

    // ------------------------------------------------------------------------------------ results of a picture-mode print

    [Fact]
    public async Task EveryPictureModeFailure_IsAPlainSentence_InEveryLanguage()
    {
        Findings();
        var notices = new[] { Warning(DriverPreflight.RawBlocks, "2"), Error(DevicePictureRenderer.BarcodeNotDrawn, "EAN-13", "12A") };
        var results = new[]
        {
            PrintResult.Fail(PrintErrorKind.NotAcknowledged).WithNotices(notices),
            PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, Queue, "StartPage (Win32 error 1)")),
            PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, DevicePictureRenderer.BarcodeNotDrawn)).WithNotices(notices),
            PrintResult.Fail(PrintErrorKind.Cancelled),
        };
        foreach (var lang in Loc.SupportedLanguages)
        {
            Loc.Instance.SetLanguage(lang);
            foreach (var result in results)
            {
                _fake.Printer.Result = _ => result;
                var dialog = Dialog(PrinterOutputMode.WindowsDriver);
                await dialog.PrintCommand.ExecuteAsync(null);
                Assert.Empty(MissingMarker(dialog.ResultText));
                Assert.DoesNotMatch(new Regex(@"\{\d\}"), dialog.ResultText);
                Assert.DoesNotContain(Loc.Get("Issue.Unknown", "x").Split('(')[0], dialog.ResultText);
                Assert.True(dialog.ResultText.Length > 20, result.Error!.Kind.ToString());
            }
        }
    }

    /// <summary>The part of a text that looks like a missing resource ("!Key!"), or empty.</summary>
    private static string MissingMarker(string text) => Regex.Match(text, @"![A-Za-z]+\.[A-Za-z.]+!").Value;

    [Fact]
    public async Task ALabelThatStoppedTheJobAfterPagesWentOut_SaysHowManyWereSent_NeverThatNothingWas()
    {
        // Picture mode: three labels went to the driver, then a later label could not be drawn. "The label has errors, so nothing was sent"
        // would be false and would invite printing those three again.
        Findings();
        var notices = new[] { Error(DevicePictureRenderer.BarcodeNotDrawn, "EAN-13", "12A") };
        var partial = PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, DevicePictureRenderer.BarcodeNotDrawn)).WithNotices(notices).WithPages(3);
        foreach (var lang in Loc.SupportedLanguages)
        {
            Loc.Instance.SetLanguage(lang);
            _fake.Printer.Result = _ => partial;
            var dialog = Dialog(PrinterOutputMode.WindowsDriver);
            await dialog.PrintCommand.ExecuteAsync(null);

            Assert.True(dialog.ResultIsError);
            Assert.StartsWith(Loc.Get("Print.Error.InvalidLabel.Partial", 3), dialog.ResultText);
            Assert.Contains("3", Loc.Get("Print.Error.InvalidLabel.Partial", 3));
            Assert.DoesNotContain(Loc.Get("Print.Error.InvalidLabel"), dialog.ResultText);
            // The reason still follows, worded like every other finding.
            Assert.Contains(DriverPreflightGate.FormatFinding(notices[0]), dialog.ResultText);
            Assert.Empty(MissingMarker(dialog.ResultText));
        }
    }

    [Fact]
    public void TheOrdinaryInvalidLabelText_StaysWhenNothingWasSent()
    {
        // A refused label (and every ZPL result, which never counts pages) keeps the sentence it always had; one page out already changes it.
        var none = PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, "Label.Width.Range"));
        Assert.Equal(Loc.Get("Print.Error.InvalidLabel"), PrintMessages.Describe(none));
        Assert.Equal(Loc.Get("Print.Error.InvalidLabel.Partial", 1), PrintMessages.Describe(none.WithPages(1)));
        Assert.DoesNotContain("nothing was sent", PrintMessages.Describe(none.WithPages(1))!, StringComparison.OrdinalIgnoreCase);
        // Other kinds with pages keep their own texts (DriverRefused already says labels may have printed).
        var refused = PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, Queue, "StartPage")).WithPages(2);
        Assert.Equal(PrintMessages.Describe(refused.Error!), PrintMessages.Describe(refused));
        Assert.Null(PrintMessages.Describe(PrintResult.Ok(10)));
    }

    [Fact]
    public void DriverRefused_NoLongerClaimsThatNothingWasSent()
    {
        // Pages the driver accepted before it refused one may still print, so the message must not promise the opposite.
        foreach (var lang in UiStrings.Languages)
        {
            string text = UiStrings.Read(lang)["Print.Error.DriverRefused"];
            Assert.DoesNotContain(UiStrings.Read(lang)["Print.Error.NotAcknowledged"].Split(':')[0].Trim(), text);
        }
        Assert.DoesNotContain("Nothing was sent", UiStrings.Read("en")["Print.Error.DriverRefused"]);
    }

    // ------------------------------------------------------------------------------------ texts

    /// <summary>
    /// Sample arguments for every finding key, in the order the library writes them. A key added to the library without an entry here fails
    /// the test below, so its text is always checked.
    /// </summary>
    private static readonly Dictionary<string, string[][]> FindingSamples = new()
    {
        [DriverPreflight.DriverUnavailable] = new[] { Array.Empty<string>() },
        [DriverPreflight.RawBlocks] = new[] { new[] { "2" }, new[] { "1" } },
        [DriverPreflight.ModuleOneDot] = new[] { new[] { "SKU" } },
        // The same one-decimal number twice is a real case (9.84 mils under 9.85 mils), so it must read right too.
        [DriverPreflight.ModuleBelowMinimum] = new[] { new[] { "SKU", "3.3", "9.8" }, new[] { "SKU", "9.8", "9.8" } },
        [DriverPreflight.LabelLargerThanPaper] = new[] { new[] { "100x150", "101.6x101.6" } },
        [DriverPreflight.MarginBand] = new[] { new[] { "101.6x152.4", "98.6x149.4", "1.5" } },
        [DriverPreflight.PaperMismatch] = new[] { new[] { "100x50", "210x297" } },
        [DriverPreflight.AxisRatio] = new[] { new[] { "600", "250" } },
        [DriverPreflight.TooLarge] = new[] { new[] { "52000000", "40000000" } },
        // An unnamed text whose first line is empty gives an empty name; the sentence must still read well.
        [DriverPreflight.PrinterCounter] = new[] { new[] { "Lot" }, new[] { string.Empty } },
        [DriverPreflight.Cancelled] = new[] { Array.Empty<string>() },
        [DriverPreflight.TooManyLabels] = new[] { new[] { "250000", "100000" } },
        [DriverPreflight.LabelTurned] = new[] { new[] { "150x100", "100x150" } },
        [DevicePictureRenderer.BarcodeNotDrawn] = new[] { new[] { "EAN-13", "12A" } },
        [DevicePictureRenderer.ImageNotDrawn] = new[] { Array.Empty<string>() },
        [DevicePictureRenderer.ElementNotDrawn] = new[] { Array.Empty<string>() },
    };

    /// <summary>Every finding key the printing library can hand to the app: the public message-key constants of the pre-flight and the picture renderer.</summary>
    private static IEnumerable<string> LibraryFindingKeys() =>
        new[] { typeof(DriverPreflight), typeof(DevicePictureRenderer) }
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(k => k.StartsWith("Driver.", StringComparison.Ordinal));

    /// <summary>The dialog's own texts and the variants the formatter picks at run time (invisible to the scan of literal keys in code).</summary>
    private static readonly string[] UiKeys =
    {
        "Print.Driver.AcknowledgeBox", "Print.Driver.TickToContinue", "Print.Driver.FindingsTitle", "Preview.DriverNote", "Preview.DriverNote.Short",
        "Preview.DriverNote.Name", "Print.Mode.AnswerFirst", "Print.Mode.PrinterNotListed", "Print.Mode.Change.ToWindowsDriver",
        "Print.Mode.Change.ToPrinterLanguage", "Print.Busy.Checking", "Print.Error.NotAcknowledged", "Print.Error.DriverRefused", "Print.Driver.MoreInList",
        "Print.Error.InvalidLabel.Partial",
        "Issue.Driver.Preflight.RawBlocks.One", "Issue.Driver.Preflight.ModuleBelowMinimum.Slightly", "Issue.Driver.Preflight.PrinterCounter.NoName",
    };

    [Fact]
    public void EveryFindingKeyHasTextInAllFourLanguages()
    {
        var keys = LibraryFindingKeys().ToList();
        Assert.Equal(16, keys.Count);
        Assert.DoesNotContain(keys, k => !FindingSamples.ContainsKey(k));
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys.Select(k => "Issue." + k).Concat(UiKeys)));

        foreach (var lang in UiStrings.Languages)
        {
            var resx = UiStrings.Read(lang);
            Assert.DoesNotContain(keys.Select(k => "Issue." + k).Concat(UiKeys), k => string.IsNullOrWhiteSpace(resx[k]));
            Loc.Instance.SetLanguage(lang);
            string unknown = Loc.Get("Issue.Unknown", "§");
            foreach (var key in keys)
            {
                foreach (var args in FindingSamples[key])
                {
                    var issue = new ValidationIssue(ValidationSeverity.Warning, key, null, args);
                    foreach (string text in new[] { DriverPreflightGate.FormatFinding(issue), PrintMessages.DescribeNotice(issue) })
                    {
                        Assert.DoesNotContain("!Issue", text);
                        Assert.NotEqual(unknown.Replace("§", key, StringComparison.Ordinal), text);
                        Assert.DoesNotMatch(new Regex(@"\{\d\}"), text);
                        Assert.True(text.Length > 25, $"{lang} {key}: {text}");
                        Assert.DoesNotContain("\"\"", text);
                        Assert.DoesNotContain("« »", text.Replace(' ', ' '));
                    }
                }
            }
            // The plain issue formatter used by other paths finds every finding text too (the Issue. prefix convention).
            foreach (var key in keys)
                Assert.NotEqual(unknown.Replace("§", key, StringComparison.Ordinal), Loc.FormatIssue(new ValidationIssue(ValidationSeverity.Warning, key, null, "1", "2", "3")));
        }
    }

    [Fact]
    public void TheEqualMilsCase_SaysSlightlyUnder_AndTheEmptyName_ReadsWithoutQuotes()
    {
        string equal = DriverPreflightGate.FormatFinding(Warning(DriverPreflight.ModuleBelowMinimum, "SKU", "9.8", "9.8"));
        string clear = DriverPreflightGate.FormatFinding(Warning(DriverPreflight.ModuleBelowMinimum, "SKU", "3.3", "9.8"));
        Assert.Contains("slightly", equal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("9.8 mils wide", equal);
        Assert.Contains("3.3", clear);
        Assert.Contains("9.8", clear);

        string named = DriverPreflightGate.FormatFinding(Error(DriverPreflight.PrinterCounter, "Lot"));
        string unnamed = DriverPreflightGate.FormatFinding(Error(DriverPreflight.PrinterCounter, string.Empty));
        Assert.Contains("\"Lot\"", named);
        Assert.DoesNotContain("\"", unnamed);
        Assert.Contains("counter", unnamed, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("1 part ", DriverPreflightGate.FormatFinding(Warning(DriverPreflight.RawBlocks, "1")));
        Assert.Contains("2 parts", DriverPreflightGate.FormatFinding(Warning(DriverPreflight.RawBlocks, "2")));
    }

    [Fact]
    public void TheMarginBand_SaysWhatThePrinterCannotPrint_NotWhereTheDesignIs()
    {
        // The warning comes for every label larger than the printable area, whatever its content, so it says what the printer cannot print
        // (the widest edge, worked out by the library) and how to clear it, never that the design reaches the edge.
        string en = DriverPreflightGate.FormatFinding(Warning(DriverPreflight.MarginBand, "101.6x152.4", "98.6x149.4", "1.5"));
        Assert.Contains("101.6 × 152.4", en);
        Assert.Contains("98.6 × 149.4", en);
        Assert.Contains("outer 1.5 mm", en);
        Assert.Contains("at least 1.5 mm", en);
        Assert.Contains("cut off", en);
        Assert.Contains("paper size", en);
        Assert.DoesNotContain("Move the content", en);
        Assert.DoesNotContain("larger paper", en);
        Assert.DoesNotContain("Part of the label is", en);

        // The edge follows the language's decimal separator, as every number of these texts does.
        Loc.Instance.SetLanguage("es");
        string es = DriverPreflightGate.FormatFinding(Warning(DriverPreflight.MarginBand, "101.6x152.4", "98.6x152.4", "3.0"));
        Assert.Contains("3,0", es);
        Assert.DoesNotContain("{", es);
    }

    [Fact]
    public void TheMarginBand_ReadsThroughThePlainIssueFormatterToo()
    {
        // Every hole of the text is an argument of the finding itself, so a caller that uses the generic formatter (Loc.FormatIssue) gets a
        // whole sentence in every language instead of a format error.
        foreach (var lang in UiStrings.Languages)
        {
            Loc.Instance.SetLanguage(lang);
            string text = Loc.FormatIssue(Warning(DriverPreflight.MarginBand, "101.6x152.4", "98.6x149.4", "1.5"));
            Assert.Contains("1.5", text);
            Assert.DoesNotMatch(new Regex(@"\{\d\}"), text);
        }
    }

    [Fact]
    public void Describe_PutsOneFindingPerLine()
    {
        var findings = new[] { Warning(DriverPreflight.RawBlocks, "2"), Error(DriverPreflight.ModuleOneDot, "SKU") };
        string text = DriverPreflightGate.Describe(findings);
        Assert.Equal(2, text.Split(Environment.NewLine).Length);
        Assert.Equal(Loc.Get("Print.Driver.FindingsTitle"), DriverPreflightGate.Title);
        Assert.Equal(string.Empty, DriverPreflightGate.Describe(Array.Empty<ValidationIssue>()));
    }

    [Fact]
    public void PreviewNoteChangesInDriverMode()
    {
        _main.Printer.SelectedPrinterName = Queue;
        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.PrinterLanguage);
        Assert.Equal(Loc.Get("Preview.Approximate"), _main.ApproximateNotice);
        Assert.Equal(Loc.Get("Preview.Approximate.Short"), _main.ApproximateNoticeShort);

        var raised = new List<string?>();
        _main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);
        Assert.Equal(Loc.Get("Preview.DriverNote"), _main.ApproximateNotice);
        Assert.Equal(Loc.Get("Preview.DriverNote.Short"), _main.ApproximateNoticeShort);
        Assert.Equal(Loc.Get("Preview.DriverNote.Name"), _main.ApproximateNoticeName);
        Assert.Contains(nameof(MainViewModel.ApproximateNotice), raised);
        Assert.Contains(nameof(MainViewModel.ApproximateNoticeShort), raised);

        _main.ShowPreviewCommand.Execute(null);
        Assert.Equal(Loc.Get("Preview.DriverNote"), _fake.ToolDialogs.LastPreviewNote);

        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.PrinterLanguage);
        _main.ShowPreviewCommand.Execute(null);
        Assert.Equal(Loc.Get("Preview.Approximate"), _fake.ToolDialogs.LastPreviewNote);
    }

    // ------------------------------------------------------------------------------------ hosted window

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).Single(e => e.Name == name);

    /// <summary>Remembers <see cref="Loc"/>'s subscribers so a hosted test can undo what its view-models and bindings added (see AppPrintRecordsTests).</summary>
    private sealed class LanguageEventSnapshot
    {
        private static readonly System.Reflection.FieldInfo[] Fields = new[] { "LanguageChanged", "PropertyChanged" }
            .Select(n => typeof(Loc).GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!)
            .ToArray();

        private readonly Delegate?[] _saved = Fields.Select(f => (Delegate?)f.GetValue(Loc.Instance)).ToArray();

        public void Restore()
        {
            for (int i = 0; i < Fields.Length; i++) Fields[i].SetValue(Loc.Instance, _saved[i]);
        }
    }

    /// <summary>
    /// True when <paramref name="element"/> and every parent up to <paramref name="root"/> are set to show. <see cref="UIElement.IsVisible"/> cannot
    /// be used: a window measured off screen is never shown, so it reports false for everything inside it.
    /// </summary>
    private static bool Shown(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? d = element; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(d, root)) return true;
        }
        return false;
    }

    /// <summary>
    /// The content area of the Print window at its minimum size (MinWidth 560, MinHeight 520): the dialog frame takes a 40-pixel caption and a
    /// 1-pixel border on each side (the DialogWindow style in Theme.xaml).
    /// </summary>
    private const double MinContentWidth = 560 - 2, MinContentHeight = 520 - 40 - 2;

    /// <summary>What the hosted window showed, measured on the WPF thread.</summary>
    /// <param name="ViewportHeight">
    /// Height left to the scrolled content (findings, printer, mode, copies). The footer and the result banner are docked outside it and never
    /// scroll, so a long result must not squeeze this to nothing.
    /// </param>
    private sealed record WindowFacts(bool FindingsShown, int FindingRows, bool BoxShown, bool StatusButtonShown, bool StatusBoxShown,
        bool QuestionShown, bool ModeLineShown, bool PrintButtonInside, bool FindingsFitWidth, string? DriverButtonMode, string? LanguageButtonMode,
        double ViewportHeight);

    /// <summary>
    /// Builds the Print window for <paramref name="dialog"/> on the WPF host thread at the given size and reads what it shows. With
    /// <paramref name="clickDriverButton"/> the visible "Windows driver" answer of the question is then executed with its own command and parameter.
    /// </summary>
    private static WindowFacts Host(PrintViewModel dialog, double width, double height, bool clickDriverButton = false) => WpfTestHost.Run(() =>
    {
        var window = new PrintWindow(dialog);
        var content = (FrameworkElement)window.Content;
        var size = new Size(width, height);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();

        var findings = Named<FrameworkElement>(content, "FindingsCard");
        var box = Named<CheckBox>(content, "AcknowledgeBox");
        var statusButton = Named<Button>(content, "StatusCheckButton");
        var statusBox = Named<CheckBox>(content, "StatusCheckBox");
        var question = Named<FrameworkElement>(content, "ModeQuestionPanel");
        var modeLine = Named<FrameworkElement>(content, "ModeLine");
        // The Print button lives in the footer, which never scrolls: at the minimum size it must still be laid out inside the window.
        var print = Descendants<Button>(content).Single(b => b.IsDefault);
        var printAt = print.TranslatePoint(new Point(0, 0), content);
        bool printInside = Shown(print, content) && print.ActualWidth > 0 && print.ActualHeight > 0 && printAt.X >= 0 && printAt.Y >= 0
            && printAt.X + print.ActualWidth <= width + 0.5 && printAt.Y + print.ActualHeight <= height + 0.5;
        var rows = Descendants<TextBlock>(findings).Where(t => t.Name == "FindingLine").ToList();
        // The content scrolls vertically only, so the card must wrap its sentences inside the viewport instead of running off to the right.
        var viewer = Descendants<ScrollViewer>(content).First();
        bool fits = !Shown(findings, content) || (findings.ActualWidth > 0 && findings.ActualWidth <= viewer.ViewportWidth + 0.5);
        // Each visible question button carries its mode as an x:Static parameter (a string would be refused by the typed command).
        string? driverMode = null, languageMode = null;
        Button? driverButton = null;
        foreach (var b in Descendants<Button>(question).Where(b => Shown(b, content) && b.CommandParameter is PrinterOutputMode))
        {
            if ((PrinterOutputMode)b.CommandParameter == PrinterOutputMode.WindowsDriver) { driverMode = b.CommandParameter.ToString(); driverButton = b; }
            else languageMode = b.CommandParameter.ToString();
        }
        var facts = new WindowFacts(Shown(findings, content), rows.Count, Shown(box, content), Shown(statusButton, content), Shown(statusBox, content),
            Shown(question, content), Shown(modeLine, content), printInside, fits, driverMode, languageMode, viewer.ViewportHeight);
        // Executing the button's own command with its own parameter proves the binding without a screen.
        if (clickDriverButton && driverButton?.Command is { } command) command.Execute(driverButton.CommandParameter);
        BindingOperations.ClearAllBindings(window);
        window.DataContext = null;
        window.Close();
        return facts;
    });

    /// <summary>
    /// A main view-model of its own for a hosted test, built after the test took its language snapshot: its printer list gets a collection view
    /// on the WPF host thread, so it must not stay subscribed to later language switches (the snapshot restore removes it).
    /// </summary>
    private async Task<MainViewModel> HostedMainAsync()
    {
        var main = new MainViewModel(_fake.Build());
        main.AddElementCommand.Execute(ToolboxKind.Box);
        await main.Printer.RefreshPrintersAsync();
        return main;
    }

    private PrintViewModel HostedDialog(MainViewModel main, PrinterOutputMode? mode)
    {
        main.Printer.SelectedPrinterName = Queue;
        if (mode is { } m) main.Printer.ChooseModeCommand.Execute(m);
        return new PrintViewModel(_fake.Printer, main.Printer, () => main.Session.Document, () => null, _fake.Build().Files, "my-label");
    }

    [Fact]
    public async Task PrintWindowInDriverMode_ShowsTheFindingsAndTheBox_HidesTheStatusCheck_AndFitsTheMinimumSize()
    {
        var snapshot = new LanguageEventSnapshot();
        try
        {
            var main = await HostedMainAsync();
            Findings(
                Warning(DriverPreflight.RawBlocks, "2"),
                Warning(DriverPreflight.ModuleBelowMinimum, "A rather long barcode name that has to wrap inside the card", "6.6", "9.8"),
                Warning(DriverPreflight.PaperMismatch, "100x50", "210x297"),
                Warning(DriverPreflight.AxisRatio, "600", "250"));
            var dialog = HostedDialog(main, PrinterOutputMode.WindowsDriver);
            await dialog.PrintCommand.ExecuteAsync(null);

            // The dialog's minimum size (MinWidth 560, MinHeight 520), without its frame.
            var facts = Host(dialog, MinContentWidth, MinContentHeight);
            dialog.Detach();
            Assert.True(facts.FindingsShown);
            Assert.Equal(4, facts.FindingRows);
            Assert.True(facts.BoxShown);
            Assert.False(facts.StatusButtonShown);
            Assert.False(facts.StatusBoxShown);
            Assert.False(facts.QuestionShown);
            Assert.True(facts.ModeLineShown);
            Assert.True(facts.PrintButtonInside);
            Assert.True(facts.FindingsFitWidth);
        }
        finally { snapshot.Restore(); }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr-CA")]   // the longest of the four languages
    public async Task PrintWindowWithAnError_ShowsTheFindingsWithoutABox_AndFitsTheMinimumSize(string language)
    {
        var snapshot = new LanguageEventSnapshot();
        try
        {
            Loc.Instance.SetLanguage(language);
            var main = await HostedMainAsync();
            // A shipping label with eight barcodes sent to a coarse office driver: every one is a one-dot error, plus two more findings.
            var findings = Enumerable.Range(1, 8).Select(i => Error(DriverPreflight.ModuleOneDot, "Barcode " + i.ToString(CultureInfo.InvariantCulture)))
                .Append(Warning(DriverPreflight.RawBlocks, "1"))
                .Append(Error(DriverPreflight.PrinterCounter, string.Empty))
                .ToArray();
            Findings(findings);
            var dialog = HostedDialog(main, PrinterOutputMode.WindowsDriver);
            await dialog.PrintCommand.ExecuteAsync(null);
            Assert.True(dialog.HasError);
            // Nine errors: the result writes out the first two and points at the card for the other seven.
            Assert.Contains(DriverPreflightGate.FormatFinding(findings[0]), dialog.ResultText);
            Assert.Contains(DriverPreflightGate.FormatFinding(findings[1]), dialog.ResultText);
            Assert.DoesNotContain(DriverPreflightGate.FormatFinding(findings[2]), dialog.ResultText);
            Assert.Contains(Loc.Get("Print.Driver.MoreInList", 7), dialog.ResultText);

            var facts = Host(dialog, MinContentWidth, MinContentHeight);
            dialog.Detach();
            Assert.True(facts.FindingsShown);
            Assert.Equal(10, facts.FindingRows);
            Assert.False(facts.BoxShown);
            Assert.True(facts.PrintButtonInside);
            Assert.True(facts.FindingsFitWidth);
            // The result banner sits outside the scrolled area: however many errors there are, the findings, the printer and the way back to
            // ZPL must stay reachable in the scrolled part.
            Assert.True(facts.ViewportHeight > 100, $"Only {facts.ViewportHeight:0} pixels are left to the scrolled content.");
        }
        finally { snapshot.Restore(); }
    }

    [Fact]
    public async Task PrintWindowInZplMode_ShowsNothingNew_ButTheModeLine()
    {
        var snapshot = new LanguageEventSnapshot();
        try
        {
            var main = await HostedMainAsync();
            var dialog = HostedDialog(main, PrinterOutputMode.PrinterLanguage);
            await dialog.PrintCommand.ExecuteAsync(null);
            var facts = Host(dialog, 720, 900);
            dialog.Detach();
            Assert.False(facts.FindingsShown);
            Assert.False(facts.BoxShown);
            Assert.True(facts.StatusButtonShown);
            Assert.True(facts.StatusBoxShown);
            Assert.False(facts.QuestionShown);
            Assert.True(facts.ModeLineShown);
        }
        finally { snapshot.Restore(); }
    }

    [Fact]
    public async Task PrintWindowForANewPrinter_ShowsTheQuestion_WithBothModesAsParameters()
    {
        var snapshot = new LanguageEventSnapshot();
        try
        {
            var main = await HostedMainAsync();
            var dialog = HostedDialog(main, null);
            var facts = Host(dialog, MinContentWidth, MinContentHeight, clickDriverButton: true);
            dialog.Detach();
            Assert.True(facts.QuestionShown);
            Assert.False(facts.ModeLineShown);
            Assert.Equal(nameof(PrinterOutputMode.WindowsDriver), facts.DriverButtonMode);
            Assert.Equal(nameof(PrinterOutputMode.PrinterLanguage), facts.LanguageButtonMode);
            Assert.True(facts.PrintButtonInside);
            // The click stored the answer for this printer and saved it.
            Assert.Equal(PrinterOutputMode.WindowsDriver, main.Printer.Mode);
            Assert.Equal(nameof(PrinterOutputMode.WindowsDriver), _fake.Settings.Current.Print.PrintModes[Queue]);
        }
        finally { snapshot.Restore(); }
    }

    // ------------------------------------------------------------------------------------ the limit on labels in one picture job

    [Fact]
    public async Task ThePreflight_IsAskedWithTheCopiesOfTheWholeJob()
    {
        var dialog = Dialog(PrinterOutputMode.WindowsDriver);
        dialog.CopiesText = "7";
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 7 }, _fake.Printer.PreflightLabelCounts);

        // The test label is one label, whatever the copies box says.
        await dialog.PrintTestCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 7, 1 }, _fake.Printer.PreflightLabelCounts);
    }

    [Fact]
    public async Task APictureJobOverTheLimit_IsShownAsAnError_LikeAnyOther_AndNothingCanOverrideIt()
    {
        var (service, driver) = RealService();
        var dialog = Dialog(PrinterOutputMode.WindowsDriver, service);
        dialog.CopiesText = "100001";
        await dialog.PrintCommand.ExecuteAsync(null);

        Assert.Empty(driver.Probes);
        Assert.Empty(driver.Jobs);
        Assert.True(dialog.ResultIsError);
        Assert.False(dialog.NeedsAcknowledgement);   // no box to tick: nothing can override an error
        string line = Assert.Single(dialog.PreflightLines);
        // The sentence says what the limit is and what to do about it.
        Assert.Contains("100,001", line);
        Assert.Contains("100,000", line);
        Assert.Contains("smaller batches", line);
        Assert.Equal(line, dialog.ResultText);

        dialog.Acknowledged = true;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(driver.Jobs);
        Assert.True(dialog.ResultIsError);

        // At the limit the size is no longer a finding; what the driver reports (a refused paper size here) is judged as ever and still needs its tick.
        dialog.CopiesText = "100000";
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.DoesNotContain(dialog.PreflightLines, l => l.Contains("smaller batches", StringComparison.Ordinal));
        Assert.True(dialog.NeedsAcknowledgement);
        Assert.Empty(driver.Jobs);
    }

    [Fact]
    public async Task APrinterLanguagePrint_HasNoSuchLimit_AndNeverAsksTheDriver()
    {
        var dialog = Dialog(PrinterOutputMode.PrinterLanguage);
        dialog.CopiesText = "100001";
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Single(_fake.Printer.Printed);
        Assert.Empty(_fake.Printer.PreflightLabelCounts);
        Assert.False(dialog.HasPreflightLines);
    }

    [Fact]
    public void TheLimitSentence_GroupsItsNumbers_InEveryLanguage()
    {
        foreach (var lang in UiStrings.Languages)
        {
            Loc.Instance.SetLanguage(lang);
            string text = DriverPreflightGate.FormatFinding(Error(DriverPreflight.TooManyLabels, "250000", "100000"));
            Assert.Contains(250000.ToString("N0", Loc.Instance.Culture), text);
            Assert.Contains(100000.ToString("N0", Loc.Instance.Culture), text);
            Assert.DoesNotContain("250000", text);
        }
    }

    // ------------------------------------------------------------------------------------ darkness and speed are ZPL-only

    [Fact]
    public void InDriverMode_HiddenDarknessAndSpeed_NeverBlockThePrint_AndTheirValuesAreKept()
    {
        var printer = _main.Printer;
        printer.SelectedPrinterName = Queue;
        printer.ChooseModeCommand.Execute(PrinterOutputMode.PrinterLanguage);
        printer.DarknessText = "20";
        printer.SpeedText = "5";
        printer.Persist();
        Assert.Equal(20, _fake.Settings.Current.Print.Darkness);
        Assert.Equal(5, _fake.Settings.Current.Print.SpeedIps);

        // Picture mode: the boxes are hidden. Even text that is not a number (left over, or typed before the switch) is ignored, not an error.
        printer.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);
        Assert.True(printer.IsDriverMode);
        printer.DarknessText = "dark";
        printer.SpeedText = "99";
        Assert.Null(printer.TryBuildProfile(out var profile));
        Assert.Null(profile!.Darkness);
        Assert.Null(profile.SpeedIps);

        // Saving in picture mode does not clear what printer-language mode stored.
        printer.Persist();
        Assert.Equal(20, _fake.Settings.Current.Print.Darkness);
        Assert.Equal(5, _fake.Settings.Current.Print.SpeedIps);

        // Back in printer-language mode the typed text is still there and is judged again, so invalid text is an error again.
        printer.ChooseModeCommand.Execute(PrinterOutputMode.PrinterLanguage);
        Assert.Equal("dark", printer.DarknessText);
        Assert.NotNull(printer.TryBuildProfile(out _));
        printer.DarknessText = "20";
        printer.SpeedText = "5";
        Assert.Null(printer.TryBuildProfile(out var restored));
        Assert.Equal(20, restored!.Darkness);
        Assert.Equal(5, restored.SpeedIps);
    }

    [Fact]
    public void TheView_HidesDarknessSpeedAndTheirHint_InDriverMode_ButNotTheDpi()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Rotula.sln"))) dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(dir!, "src", "Rotula.App", "Views", "PrinterSettingsView.xaml"));

        // The element that carries the label text (or is the label itself), and the Visibility bound on it or on the panel around it.
        bool HiddenInDriverMode(string key)
        {
            var label = doc.Descendants().First(e => (string?)e.Attribute("Text") == "{loc:T " + key + "}");
            return label.AncestorsAndSelf().Take(2).Any(e => ((string?)e.Attribute("Visibility") ?? "").Contains("IsDriverMode, Converter={StaticResource InverseB2V}", StringComparison.Ordinal));
        }

        Assert.True(HiddenInDriverMode("Print.Profile.Darkness"));
        Assert.True(HiddenInDriverMode("Print.Profile.Speed"));
        Assert.True(HiddenInDriverMode("Print.Profile.Hint"));
        Assert.False(HiddenInDriverMode("Print.Profile.Dpi"));
        Assert.False(HiddenInDriverMode("Print.Profile.Width"));
    }

    [Fact]
    public void TheTurnedLabelFinding_SaysInPlainWords_WhatIsDone_AndHowToFixTheDirection()
    {
        string text = DriverPreflightGate.FormatFinding(Warning(DriverPreflight.LabelTurned, "150x100", "100x150"));
        Assert.Contains("150 × 100", text);
        Assert.Contains("100 × 150", text);
        Assert.Contains("clockwise", text);
        Assert.Contains("upside down", text);
    }
}
