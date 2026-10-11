using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rotula.App.Controls;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.App.Views;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Zpl;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using TextAlignment = Rotula.Core.Model.TextAlignment;

namespace Rotula.Tests;

/// <summary>
/// A rasterizer whose layout is simple arithmetic (every letter is half an em wide), so overflow depends on the numbers the test sets and
/// not on the fonts installed on the machine. The real SkiaSharp rasterizer is tested separately.
/// </summary>
internal sealed class FakeFontRasterizer : ITextRasterizer
{
    private readonly List<string> _families;

    public FakeFontRasterizer(int families = 3)
    {
        _families = new List<string> { "Arial", "Calibri", "Segoe UI" };
        for (int i = 3; i < families; i++) _families.Add($"Family {i:0000}");
    }

    public TextMeasurement Measure(TextRasterRequest r)
    {
        double em = FontChoice.PointsToDots(r.Font.PointSize, r.Dpi);
        double letter = em * 0.5;
        int perLine = r.BoxWidthDots > 0 ? Math.Max(1, (int)(r.BoxWidthDots / letter)) : int.MaxValue;
        int lines = r.BoxWidthDots > 0 ? (int)Math.Ceiling((double)r.Text.Length / perLine) : 1;
        var flags = TextOverflow.None;
        if (r.MaxLines > 0 && lines > r.MaxLines) { flags |= TextOverflow.Lines; lines = r.MaxLines; }
        bool known = _families.Contains(r.Font.FamilyName, StringComparer.OrdinalIgnoreCase);
        return new TextMeasurement(r.BoxWidthDots > 0 ? r.BoxWidthDots : (int)(r.Text.Length * letter), (int)(lines * em * 1.2), lines, (int)em, flags, !known, known ? r.Font.FamilyName : "Segoe UI");
    }

    public TextRasterResult Rasterize(TextRasterRequest request) => throw new NotSupportedException();

    public IReadOnlyList<string> GetFontFamilies() => _families;
}

/// <summary>The text formatting controls map one to one onto the model, and every change is one undo step.</summary>
[Collection("App")]
public class AppTextFormatTests
{
    private const string Long = "The quick brown fox jumps over the lazy dog and keeps running through the whole field until night";

    private static (EditorSession Session, TextFormatViewModel Format, TextElement Text) Setup(Action<TextElement>? configure = null, ITextRasterizer? rasterizer = null)
    {
        Loc.Instance.SetLanguage("en");
        // 800 x 1200 dots at 203 dpi (see AppTestSupport).
        var text = new TextElement { X = 20, Y = 30, Text = "Hello", FontId = 'A', FontHeight = 40, FontWidth = 40 };
        configure?.Invoke(text);
        var s = AppTestSupport.Session(text);
        var fmt = new TextFormatViewModel(s, () => rasterizer ?? new FakeFontRasterizer());
        s.Select(text);
        return (s, fmt, text);
    }

    private static TextElement Now(EditorSession s) => (TextElement)s.Document.Elements[0];

    private static FontItem Printer(TextFormatViewModel f, char id) => f.Fonts.First(i => !i.IsWindows && i.PrinterId == id);

    private static FontItem Windows(TextFormatViewModel f, string family) => f.Fonts.First(i => i.IsWindows && i.Family == family);

    // ------------------------------------------------------------------------------------ the font list

    [Fact]
    public void TheFontList_HasPrinterFontsWithPlainNames_ThenWindowsFonts_InTwoGroups()
    {
        var (_, f, _) = Setup();
        var scalable = Printer(f, '0');
        Assert.Equal("Printer font 0 (scalable)", scalable.Display);
        Assert.Equal("Printer fonts", scalable.Group);
        Assert.Contains(f.Fonts, i => !i.IsWindows && i.PrinterId == 'A' && i.Display == "Printer font A (fixed shape)");
        Assert.Equal("Windows fonts", Windows(f, "Calibri").Group);
        // Printer fonts come before Windows fonts, and the view groups by the heading.
        Assert.True(f.Fonts.ToList().FindIndex(i => i.IsWindows) > f.Fonts.ToList().FindLastIndex(i => !i.IsWindows));
        Assert.NotEmpty(f.FontsView.Groups!);
    }

    [Fact]
    public void TheSearchBox_ListsMatchingFonts_AndNeverDropsTheCurrentOne()
    {
        var (_, f, _) = Setup(rasterizer: new FakeFontRasterizer(60));
        f.FontSearch = "family 00";
        var shown = f.FontsView.Cast<FontItem>().ToList();
        Assert.Contains(shown, i => i.Family == "Family 0005");
        Assert.DoesNotContain(shown, i => i.Family == "Calibri");
        Assert.Contains(shown, i => ReferenceEquals(i, f.SelectedFont)); // the box would clear its text if the current font vanished
        f.FontSearch = string.Empty;
        Assert.Equal(f.Fonts.Count, f.FontsView.Cast<object>().Count());
    }

    [Fact]
    public void ATypedFontName_IsOnlyAppliedWhenItNamesAFont()
    {
        var (s, f, _) = Setup();
        Assert.False(f.CommitFontText("Aria"));            // half a name: nothing changes
        Assert.Null(Now(s).Font);
        Assert.True(f.CommitFontText("calibri"));          // capitals do not matter
        Assert.Equal("Calibri", Now(s).Font!.FamilyName);
        Assert.Equal(1, s.Undo.UndoCount);
    }

    [Fact]
    public void AFontTheLabelUsesButTheComputerLacks_StaysInTheListAndIsReported()
    {
        var (_, f, _) = Setup(t => t.Font = FontChoice.ForWindows("Gone Font", 12));
        Assert.True(f.SelectedFont!.IsWindows);
        Assert.Equal("Gone Font (not installed)", f.SelectedFont.Display);
        Assert.True(f.HasMissingFont);
        Assert.Contains("Gone Font", f.MissingFontText);
    }

    // ------------------------------------------------------------------------------------ font switch

    [Fact]
    public void SwitchingFromAPrinterFontToAWindowsFont_KeepsThePhysicalSize()
    {
        var (s, f, _) = Setup();
        double expected = Math.Round(FontChoice.DotsToPoints(40, 203), 1);
        f.SelectedFont = Windows(f, "Arial");
        var t = Now(s);
        Assert.NotNull(t.Font);
        Assert.True(t.Font!.IsWindows);
        Assert.Equal("Arial", t.Font.FamilyName);
        Assert.Equal(expected, t.Font.PointSize);
        Assert.True(f.IsWindowsFont);
        Assert.Equal(1, s.Undo.UndoCount);
    }

    [Fact]
    public void SwitchingFromAWindowsFontToAPrinterFont_StoresTheIdAndKeepsTheHeightInDots()
    {
        var (s, f, _) = Setup(t => t.Font = FontChoice.ForWindows("Arial", 12, bold: true));
        f.SelectedFont = Printer(f, '0');
        var t = Now(s);
        Assert.Null(t.Font);                                // a label with a printer font is saved as it always was
        Assert.Equal('0', t.FontId);
        int dots = (int)Math.Round(FontChoice.PointsToDots(12, 203));
        Assert.Equal(dots, t.FontHeight);
        Assert.Equal(dots, t.FontWidth);
        Assert.False(f.IsWindowsFont);
        Assert.False(f.IsBold);
    }

    [Fact]
    public void ChoosingAnotherPrinterFont_ChangesOnlyTheId_AndLeavesTheSizeAlone()
    {
        var (s, f, _) = Setup();
        f.SelectedFont = Printer(f, 'E');
        Assert.Equal('E', Now(s).FontId);
        Assert.Equal(40, Now(s).FontHeight);
        Assert.Null(Now(s).Font);
    }

    // ------------------------------------------------------------------------------------ style flags

    [Fact]
    public void BoldItalicUnderline_MapToTheWindowsFont_EachAsOneUndoStep_AndUndoRestoresThem()
    {
        var (s, f, _) = Setup(t => t.Font = FontChoice.ForWindows("Arial", 12));
        Assert.True(f.StylesEnabled);
        f.IsBold = true;
        Assert.True(Now(s).Font!.Bold);
        f.IsItalic = true;
        f.IsUnderline = true;
        Assert.True(Now(s).Font!.Italic && Now(s).Font!.Underline && Now(s).Font!.Bold);
        Assert.Equal(3, s.Undo.UndoCount);                  // three quick clicks are three steps, never merged into a no-op
        s.PerformUndo();
        Assert.False(Now(s).Font!.Underline);
        s.PerformUndo();
        s.PerformUndo();
        Assert.False(Now(s).Font!.Bold || Now(s).Font!.Italic);   // the copy in the undo history was not changed in place
        Assert.False(f.IsBold);
        s.PerformRedo();
        Assert.True(f.IsBold);
    }

    [Fact]
    public void BoldOnThenOffQuickly_IsTwoSteps()
    {
        var (s, f, _) = Setup(t => t.Font = FontChoice.ForWindows("Arial", 12));
        f.IsBold = true;
        f.IsBold = false;
        Assert.Equal(2, s.Undo.UndoCount);
        Assert.False(Now(s).Font!.Bold);
    }

    [Fact]
    public void APrinterFont_DisablesTheStyleButtons_AndTheTipsSayWhy()
    {
        var (s, f, _) = Setup();
        Assert.False(f.StylesEnabled);
        string why = Loc.Get("Panel.Format.Tip.NeedsWindowsFont");
        Assert.Equal(why, f.BoldTip);
        Assert.Equal(why, f.ItalicTip);
        Assert.Equal(why, f.UnderlineTip);
        string? status = null;
        f.Status = m => status = m;
        f.IsBold = true;                                   // the shortcut path: nothing changes, and the user is told
        Assert.Null(Now(s).Font);
        Assert.False(f.IsBold);
        Assert.Equal(0, s.Undo.UndoCount);
        Assert.Equal(Loc.Get("Status.Format.NeedsWindowsFont"), status);

        f.SelectedFont = Windows(f, "Arial");              // choosing a Windows font enables them again, with the shortcut in the tip
        Assert.True(f.StylesEnabled);
        Assert.Equal("Bold (Ctrl+B)", f.BoldTip);
    }

    // ------------------------------------------------------------------------------------ size

    [Fact]
    public void TheSize_IsShownInPoints_ForBothKindsOfFont()
    {
        var (_, f, _) = Setup(t => t.Font = FontChoice.ForWindows("Arial", 10.5));
        Assert.Equal("10.5", f.SizeText);
        var (_, p, _) = Setup(); // 40 dots at 203 dpi
        Assert.Equal(FontChoice.DotsToPoints(40, 203).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture), p.SizeText);
    }

    [Fact]
    public void TypingASize_SetsThePointSizeOfAWindowsFont_AndTheDotsOfAPrinterFont()
    {
        var (s, f, _) = Setup(t => t.Font = FontChoice.ForWindows("Arial", 12));
        f.SizeText = "18";
        Assert.Equal(18, Now(s).Font!.PointSize);

        var (s2, p, _) = Setup();
        p.SizeText = "10";
        Assert.Equal((int)Math.Round(FontChoice.PointsToDots(10, 203)), Now(s2).FontHeight);
        Assert.Equal(Now(s2).FontHeight, Now(s2).FontWidth);   // an explicit width follows the height, so the letters keep their shape
    }

    [Fact]
    public void TypingASize_MergesIntoOneUndoStep_AndUndoPutsTheOldSizeBack()
    {
        var (s, f, _) = Setup(t => t.Font = FontChoice.ForWindows("Arial", 12));
        f.SizeText = "1";
        f.SizeText = "14";
        f.SizeText = "140";
        Assert.Equal(1, s.Undo.UndoCount);
        Assert.Equal(140, Now(s).Font!.PointSize);
        s.PerformUndo();
        Assert.Equal(12, Now(s).Font!.PointSize);
        Assert.Equal("12", f.SizeText);
    }

    [Fact]
    public void ABadSize_ShowsAPlainMessage_AndChangesNothing()
    {
        var (s, f, _) = Setup(t => t.Font = FontChoice.ForWindows("Arial", 12));
        f.SizeText = "abc";
        Assert.True(f.HasSizeError);
        Assert.Equal(Loc.Get("Validation.NotNumber"), f.SizeError);
        f.SizeText = "5000";
        Assert.Equal(Loc.Get("Validation.Range", "1", "1000"), f.SizeError);
        f.SizeText = "";
        Assert.Equal(Loc.Get("Validation.Required"), f.SizeError);
        Assert.Equal(12, Now(s).Font!.PointSize);
        Assert.Equal(0, s.Undo.UndoCount);
        f.SizeText = "9.5";
        Assert.False(f.HasSizeError);
        Assert.Equal(9.5, Now(s).Font!.PointSize);
    }

    [Fact]
    public void TheDotsRow_ConvertsToPoints_ForAWindowsFont()
    {
        var (s, _, t) = Setup(x => x.Font = FontChoice.ForWindows("Arial", 12));
        var panel = new PropertyPanelViewModel(s, null, () => new FakeFontRasterizer());
        s.Select(t);
        var dots = panel.Rows.OfType<IntRow>().First(r => r.LabelKey == "Prop.FontHeight");
        Assert.Equal(((int)Math.Round(FontChoice.PointsToDots(12, 203))).ToString(), dots.Text);
        dots.Text = "100";
        Assert.Equal(Math.Round(FontChoice.DotsToPoints(100, 203), 2), Now(s).Font!.PointSize);
        // The width of a printer font is not shown for a Windows font.
        var width = panel.Rows.OfType<IntRow>().First(r => r.LabelKey == "Prop.FontWidth");
        Assert.False(width.IsVisible);
    }

    // ------------------------------------------------------------------------------------ alignment and wrap

    [Fact]
    public void Alignment_NeedsABox_AndSaysSoInItsTip()
    {
        var (s, f, _) = Setup();
        Assert.False(f.IsWrap);
        Assert.Equal(Loc.Get("Panel.Format.Tip.NeedsWrap"), f.CenterTip);
        f.AlignCenter = true;                               // ignored: there is no box to align in
        Assert.Null(Now(s).Block);
        Assert.Equal(0, s.Undo.UndoCount);

        f.SetWrap(true);
        Assert.True(f.IsWrap);
        Assert.Equal("Centre", f.CenterTip);
    }

    [Fact]
    public void EachAlignmentButton_SetsTheBlockAlignment_AsOneStep()
    {
        var (s, f, _) = Setup();
        f.SetWrap(true);
        foreach (var (apply, expected) in new (Action, TextAlignment)[]
        {
            (() => f.AlignCenter = true, TextAlignment.Center), (() => f.AlignRight = true, TextAlignment.Right),
            (() => f.AlignJustified = true, TextAlignment.Justified), (() => f.AlignLeft = true, TextAlignment.Left),
        })
        {
            int before = s.Undo.UndoCount;
            apply();
            Assert.Equal(expected, Now(s).Block!.Alignment);
            Assert.Equal(before + 1, s.Undo.UndoCount);
        }
        Assert.True(f.AlignLeft && !f.AlignCenter && !f.AlignRight && !f.AlignJustified);
        f.AlignLeft = false;                                 // clicking the one that is on keeps it on
        Assert.True(f.AlignLeft);
    }

    [Fact]
    public void WordWrap_OnUsesTheSizingFix_OffRemovesTheBox_BothUndoable()
    {
        var (s, f, t) = Setup();
        var expected = PropertyPanelViewModel.DefaultBlockFor(s.Document, t);
        f.SetWrap(true);
        var block = Now(s).Block!;
        Assert.Equal(expected.WidthDots, block.WidthDots);
        Assert.Equal(expected.MaxLines, block.MaxLines);
        Assert.True(block.MaxLines >= 2);                    // not the old 400 x 1 block that cut the text to one line
        f.SetWrap(false);
        Assert.Null(Now(s).Block);
        s.PerformUndo();
        Assert.NotNull(Now(s).Block);
        s.PerformUndo();
        Assert.Null(Now(s).Block);
    }

    [Fact]
    public void TheWrapSwitchInThePanel_GoesThroughTheSameCode()
    {
        var (s, f, t) = Setup();
        var panel = new PropertyPanelViewModel(s, null, () => new FakeFontRasterizer());
        s.Select(t);
        ((BoolRow)panel.Rows.First(r => r.LabelKey == "Prop.Block")).Value = true;
        Assert.True(panel.Format.IsWrap);
        Assert.Equal(PropertyPanelViewModel.DefaultBlockFor(s.Document, Now(s)).WidthDots, Now(s).Block!.WidthDots);
        Assert.Contains(panel.Rows, r => r.LabelKey == "Prop.Block.Width");
    }

    [Fact]
    public void TheFormattingBlock_IsAtTheTopOfATextsPanel_RightAfterItsContent()
    {
        var (s, _, t) = Setup();
        var panel = new PropertyPanelViewModel(s, null, () => new FakeFontRasterizer());
        s.Select(t);
        var keys = panel.Rows.Select(r => r.LabelKey).ToList();
        Assert.Equal("Panel.Text", keys[0]);
        Assert.Equal("Prop.Text", keys[1]);
        Assert.Equal("Panel.Format", keys[2]);
        Assert.IsType<TextFormatRow>(panel.Rows[3]);
        Assert.True(keys.IndexOf("Panel.Position") > 3);
    }

    // ------------------------------------------------------------------------------------ overflow warning

    [Fact]
    public void WindowsFontOverflow_IsMeasuredExactly_AppearsAndClears()
    {
        var (s, f, _) = Setup(t => { t.Text = Long; t.Font = FontChoice.ForWindows("Arial", 12); t.Block = new TextBlockOptions { WidthDots = 300, MaxLines = 1 }; });
        Assert.True(f.HasOverflow);
        Assert.Equal(Loc.Get("Issue.Text.Overflow"), f.OverflowText);
        Assert.NotEqual(Loc.Get("Issue.Text.Overflow.Estimated"), f.OverflowText);

        s.Edit("Undo.Property", d => ((TextElement)d.Elements[0]).Block!.MaxLines = 20);
        Assert.False(f.HasOverflow);
        Assert.Equal(string.Empty, f.OverflowText);

        s.Edit("Undo.Property", d => ((TextElement)d.Elements[0]).Block!.MaxLines = 1);
        Assert.True(f.HasOverflow);
        s.PerformUndo();
        Assert.False(f.HasOverflow);
    }

    [Fact]
    public void PrinterFontOverflow_IsOnlyAnEstimate_AndSaysMayNotFit()
    {
        var (_, f, _) = Setup(t => { t.Text = Long; t.Block = new TextBlockOptions { WidthDots = 200, MaxLines = 1 }; });
        Assert.True(f.HasOverflow);
        Assert.Equal(Loc.Get("Issue.Text.Overflow.Estimated"), f.OverflowText);
        Assert.Contains("may not fit", f.OverflowText);
    }

    [Fact]
    public void NoBoxOrNoText_NeverWarns()
    {
        var (_, f, _) = Setup(t => { t.Text = Long; t.Font = FontChoice.ForWindows("Arial", 12); });
        Assert.False(f.HasOverflow);
        var (_, g, _) = Setup(t => { t.Text = ""; t.Font = FontChoice.ForWindows("Arial", 12); t.Block = new TextBlockOptions { WidthDots = 10, MaxLines = 1 }; });
        Assert.False(g.HasOverflow);
    }

    [Fact]
    public void TheOverflowWarning_FollowsTheRecordShownOnTheCanvas()
    {
        var (s, f, _) = Setup(t => { t.Text = "{{desc}}"; t.Font = FontChoice.ForWindows("Arial", 12); t.Block = new TextBlockOptions { WidthDots = 300, MaxLines = 2 }; });
        IReadOnlyDictionary<string, string>? record = new Dictionary<string, string> { ["desc"] = "short" };
        f.DataProvider = () => record;
        f.Refresh();
        Assert.False(f.HasOverflow);
        record = new Dictionary<string, string> { ["desc"] = Long + Long };
        f.Refresh();
        Assert.True(f.HasOverflow);
    }

    [Fact]
    public void TheFirstFix_MakesTheBoxBigger_InOneUndoStep()
    {
        var (s, f, _) = Setup(t => { t.Text = Long; t.Font = FontChoice.ForWindows("Arial", 12); t.Block = new TextBlockOptions { WidthDots = 300, MaxLines = 1 }; });
        Assert.True(f.CanEnlargeBox);
        f.EnlargeBoxCommand.Execute(null);
        Assert.False(f.HasOverflow);
        Assert.True(Now(s).Block!.MaxLines > 1);
        Assert.Equal(12, Now(s).Font!.PointSize);            // the text was not touched
        Assert.Equal(1, s.Undo.UndoCount);
        s.PerformUndo();
        Assert.True(f.HasOverflow);
    }

    [Fact]
    public void TheSecondFix_MakesTheTextSmaller_InOneUndoStep()
    {
        var (s, f, _) = Setup(t => { t.Text = Long; t.Font = FontChoice.ForWindows("Arial", 20); t.Block = new TextBlockOptions { WidthDots = 600, MaxLines = 3 }; });
        Assert.True(f.HasOverflow);
        f.ShrinkTextCommand.Execute(null);
        Assert.False(f.HasOverflow);
        Assert.True(Now(s).Font!.PointSize < 20);
        Assert.Equal(3, Now(s).Block!.MaxLines);              // the box was not touched
        Assert.Equal(1, s.Undo.UndoCount);
    }

    [Fact]
    public void AWordWiderThanTheBox_IsFixedByWideningIt()
    {
        var (s, f, _) = Setup(t => { t.Text = "Extraordinarily"; t.FontHeight = 40; t.FontWidth = 40; t.Block = new TextBlockOptions { WidthDots = 30, MaxLines = 3 }; });
        Assert.True(f.HasOverflow);
        f.EnlargeBoxCommand.Execute(null);
        Assert.False(f.HasOverflow);
        Assert.True(Now(s).Block!.WidthDots > 30);
    }

    // ------------------------------------------------------------------------------------ main view-model: shortcuts, ZPL and the rasterizer

    private static MainViewModel BuildMain(out FakeServices fake, ITextRasterizer? rasterizer = null)
    {
        fake = new FakeServices();
        fake.Settings.Current.Language = "en";
        fake.Variables = new VariableServices(null, null, null, rasterizer ?? new SkiaTextRasterizer());
        return new MainViewModel(fake.Build());
    }

    [Fact]
    public void CtrlB_I_U_ToggleTheSelectedText_AndExplainThemselvesOnAPrinterFont()
    {
        var vm = BuildMain(out _);
        var text = new TextElement { Text = "Hello", X = 10, Y = 10, Font = FontChoice.ForWindows("Arial", 12) };
        vm.Session.Edit("Undo.Add", d => d.Elements.Add(text), selectionAfter: () => new[] { text.Id });
        Assert.True(vm.ToggleBoldCommand.CanExecute(null));
        vm.ToggleBoldCommand.Execute(null);
        vm.ToggleItalicCommand.Execute(null);
        vm.ToggleUnderlineCommand.Execute(null);
        var f = ((TextElement)vm.Session.Document.Elements[0]).Font!;
        Assert.True(f.Bold && f.Italic && f.Underline);
        vm.ToggleBoldCommand.Execute(null);
        Assert.False(((TextElement)vm.Session.Document.Elements[0]).Font!.Bold);

        vm.Format.SelectedFont = vm.Format.Fonts.First(i => !i.IsWindows && i.PrinterId == '0');
        vm.StatusMessage = "";
        vm.ToggleBoldCommand.Execute(null);
        Assert.Equal(Loc.Get("Status.Format.NeedsWindowsFont"), vm.StatusMessage);

        vm.Session.SetSelection(Array.Empty<string>());
        Assert.False(vm.ToggleBoldCommand.CanExecute(null));   // nothing selected
    }

    [Fact]
    public void AWindowsFontText_IsAGraphicInTheZplView_WithTheRasterizerRegistered_AndNotABlockingError()
    {
        var vm = BuildMain(out _);
        var text = new TextElement { Text = "Hello", X = 10, Y = 10, Font = FontChoice.ForWindows("Arial", 12) };
        vm.Session.Edit("Undo.Add", d => d.Elements.Add(text));
        Assert.Equal(0, vm.ErrorCount);
        Assert.Contains("^GF", vm.ZplText);
        Assert.DoesNotContain("^FDHello", vm.ZplText);
    }

    [Fact]
    public void TheRasterNotice_IsShownForAWindowsFont_UntilDismissed_AndTheChoiceIsSaved()
    {
        var vm = BuildMain(out var fake);
        var text = new TextElement { Text = "Hello", X = 10, Y = 10 };
        vm.Session.Edit("Undo.Add", d => d.Elements.Add(text), selectionAfter: () => new[] { text.Id });
        Assert.False(vm.Format.ShowRasterNotice);
        vm.Format.SelectedFont = vm.Format.Fonts.First(i => i.IsWindows);
        Assert.True(vm.Format.ShowRasterNotice);
        vm.Format.DismissRasterNoticeCommand.Execute(null);
        Assert.False(vm.Format.ShowRasterNotice);
        Assert.True(fake.Settings.Current.RasterNoticeDismissed);
        Assert.False(BuildMain(out fake).Format.ShowRasterNotice);   // (a new window starts from its own settings)
    }

    // ------------------------------------------------------------------------------------ preview and printed picture agree

    private static int InkLines(Rotula.Core.Imaging.MonochromeBitmap bmp)
    {
        int lines = 0;
        bool inLine = false;
        for (int y = 0; y < bmp.Height; y++)
        {
            bool ink = false;
            for (int x = 0; x < bmp.Width && !ink; x++) ink = bmp.IsBlack(x, y);
            if (ink && !inLine) lines++;
            inLine = ink;
        }
        return lines;
    }

    [Fact]
    public void ThePreview_AndThePrintedPicture_BreakTheLinesTheSameWay()
    {
        const string text = "Alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau";
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(600), Dpi = PrinterDpi.Dpi203 };
        var t = new TextElement
        {
            X = 20, Y = 20, Text = text, Font = FontChoice.ForWindows("Arial", 14),
            Block = new TextBlockOptions { WidthDots = 360, MaxLines = 20, LineSpacing = 6 },
        };
        doc.Elements.Add(t);
        var skia = new SkiaTextRasterizer();

        // What the printer gets: the picture the rasterizer draws for the finished text.
        var request = new TextRasterRequest(text, t.Font!, 203, 360, 0, 20, 6);
        var printed = skia.Rasterize(request);
        int printedLines = InkLines(printed.Bitmap);

        // What the canvas shows: the label renderer drawing the same element.
        using var preview = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(doc));
        var span = preview.Bitmap.GetPixelSpan();
        int previewLines = 0;
        bool inLine = false;
        int firstInkRow = -1, lastInkRow = -1;
        for (int y = 0; y < preview.Bitmap.Height; y++)
        {
            bool ink = false;
            for (int x = 0; x < preview.Bitmap.Width && !ink; x++) ink = span[y * preview.Bitmap.RowBytes + x * 4] < 128;
            if (ink && !inLine) previewLines++;
            if (ink) { if (firstInkRow < 0) firstInkRow = y; lastInkRow = y; }
            inLine = ink;
        }

        Assert.True(printedLines > 1, "the text should wrap");
        Assert.Equal(printedLines, previewLines);
        Assert.Equal(printedLines, printed.Measurement.LineCount);
        // The ZPL of the same label carries that picture, not text.
        var zpl = ZplGenerator.Generate(doc, null, skia);
        Assert.Contains("^GF", zpl.Zpl);
        Assert.DoesNotContain("^FD", zpl.Zpl);
        Assert.False(zpl.HasErrors);
        // The picture is as tall as the lines the preview drew (to within the white margin above the first and below the last line).
        Assert.InRange(printed.Bitmap.Height, lastInkRow - firstInkRow + 1, lastInkRow - firstInkRow + 1 + 40);
    }

    // ------------------------------------------------------------------------------------ the canvas

    [Fact]
    public void AWrappedTextsFrame_IsItsBox_NotItsInk()
    {
        var (s, _, t) = Setup(x => { x.Text = "Hi"; x.Font = FontChoice.ForWindows("Arial", 12); x.Block = new TextBlockOptions { WidthDots = 500, MaxLines = 3, Alignment = TextAlignment.Right }; });
        Assert.True(s.Bounds.HasTextBoxFrame(Now(s)));
        var frame = s.Bounds.GetFrame(Now(s));
        Assert.Equal(20, frame.X);
        Assert.Equal(500, frame.Width);
        Assert.True(s.Bounds.GetBounds(Now(s)).Width < 500);   // right aligned "Hi": the ink is far narrower than the box
        // A single-line text keeps its ink as the frame, so nothing else changes.
        s.Edit("Undo.Property", d => ((TextElement)d.Elements[0]).Block = null);
        Assert.False(s.Bounds.HasTextBoxFrame(Now(s)));
        Assert.Equal(s.Bounds.GetBounds(Now(s)), s.Bounds.GetFrame(Now(s)));
    }

    [Fact]
    public void DraggingTheRightHandleOfAWrappedText_ChangesTheWrapWidth_AndShowsItInTheStatusBar()
    {
        // The main view-model is made here, not on the shared WPF thread: it listens to the language change for the rest of the run.
        var main = BuildMain(out _);
        var text = new TextElement { Text = Long, X = 100, Y = 100, Font = FontChoice.ForWindows("Arial", 12), Block = new TextBlockOptions { WidthDots = 300, MaxLines = 10 } };
        main.Session.Edit("Undo.Add", d => d.Elements.Add(text), selectionAfter: () => new[] { text.Id });
        var result = WpfTestHost.Run(() =>
        {
            var canvas = new DesignCanvas { DataContext = main };
            // Drive the resize the way the mouse handlers do: remember the original, then move the pointer.
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var type = typeof(DesignCanvas);
            type.GetField("_resizeOriginal", flags)!.SetValue(canvas, ElementCloner.Clone(text));
            type.GetField("_handle", flags)!.SetValue(canvas, ResizeHandle.Right);
            var gesture = main.Session.BeginGesture();
            type.GetField("_gesture", flags)!.SetValue(canvas, gesture);
            type.GetMethod("DoResizeTextBox", flags)!.Invoke(canvas, new object[] { 650.0 });
            var live = (TextElement)main.Session.Document.Elements[0];
            main.Session.EndGesture(gesture, "Undo.Resize");
            return (Width: live.Block!.WidthDots, X: live.X, Y: live.Y, Height: live.Block.MaxLines, Font: live.Font!.PointSize, Status: main.StatusMessage, Undo: main.Session.Undo.UndoCount);
        });
        Assert.Equal(550, result.Width);                       // pointer at 650, text starts at 100
        Assert.Equal(100, result.X);                           // nothing else moved
        Assert.Equal(12, result.Font);
        Assert.Contains("550", result.Status);
        Assert.Contains("mm", result.Status);
    }

    // ------------------------------------------------------------------------------------ resize robustness (probe canvas)

    private static EditorSession Label600Wrapped(out TextElement text, bool windowsFont)
    {
        // 4 x 3 inch at 600 dpi, a wrapped text bound to a field: the case that failed with "Unable to allocate pixels".
        var doc = new LabelDocument { Width = Measure.Inches(4), Height = Measure.Inches(3), Dpi = PrinterDpi.Dpi600 };
        doc.Variables.Add(new LabelVariable("description", Long));
        text = new TextElement { X = 100, Y = 100, Text = "{{sku}} {{description}}", FontHeight = 50, FontWidth = 40, Block = new TextBlockOptions { WidthDots = 2300, MaxLines = 30 } };
        if (windowsFont) text.Font = FontChoice.ForWindows("Arial", 12);
        doc.Elements.Add(text);
        return new EditorSession(doc);
    }

    [Fact]
    public void TheProbeCanvas_IsSizedFromTheElement_AndNeverBeyondTheCap()
    {
        var s = Label600Wrapped(out var text, true);
        var (w, h) = ElementBoundsCalculator.ProbeSize(text, s.Document);
        // The old canvas was 4400 x 3500 = 15.4 million pixels, allocated on every mouse move of a resize.
        Assert.True((long)w * h <= 12_000_000, $"{w} x {h}");
        Assert.True(w <= 6000 && h <= 6000);
        text.Block!.WidthDots = 9999;
        text.Block.MaxLines = 9999;
        (w, h) = ElementBoundsCalculator.ProbeSize(text, s.Document);
        Assert.True((long)w * h <= 12_000_000 && w <= 6000 && h <= 6000, $"{w} x {h}");
        text.Rotation = FieldRotation.Rotated90;
        (w, h) = ElementBoundsCalculator.ProbeSize(text, s.Document);
        Assert.True((long)w * h <= 12_000_000 && w <= 6000 && h <= 6000, $"{w} x {h}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResizingAWrappedText_ThroughExtremeWidths_NeverThrows_AndKeepsReasonableBounds(bool windowsFont)
    {
        var s = Label600Wrapped(out var text, windowsFont);
        s.Select(text);
        var commands = new EditorCommands(s);
        var start = s.Bounds.GetFrame(Now(s));
        var live = Now(s);
        // Successive widths the way a drag produces them, including zero, negative and far past the label edge.
        foreach (double width in new double[] { 2300, 2250, 1800, 900, 300, 40, 1, 0, -50, -3000, 5000, 20000, 1e7, 1e12, double.MaxValue / 1e300, 800, 2300 })
        {
            var fresh = (TextElement)ElementCloner.Clone(text);
            s.Document.Elements[0] = fresh;
            commands.ApplyResize(fresh, start, ResizeHandle.Right, width, start.Height);
            var bounds = s.Bounds.GetBounds(fresh);
            Assert.True(fresh.Block!.WidthDots is >= 1 and <= 9999, $"width {fresh.Block.WidthDots}");
            Assert.True(bounds.Width > 0 && bounds.Height > 0 && !double.IsNaN(bounds.X));
        }
        Assert.Equal(0, s.Bounds.ProbeFailureCount);
        _ = live;
    }

    [Fact]
    public void ARenderThatCannotHappen_ShowsPreviewUnavailable_NotAnException()
    {
        var s = new EditorSession(new LabelDocument { Width = Measure.Dots(900_000), Height = Measure.Dots(900_000) });
        using var cache = new PreviewCache(s);
        var bitmap = cache.GetBitmap(1.0, out var key);
        Assert.Null(bitmap);
        Assert.False(string.IsNullOrEmpty(key));
        Assert.True(Loc.Instance.HasKey(key!));
        Assert.True(Loc.Instance.HasKey("Preview.Unavailable"));
    }

    // ------------------------------------------------------------------------------------ controls: names, layout, virtualization

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

    private static void Layout(FrameworkElement e, double width, double height = 2000)
    {
        e.Measure(new Size(width, height));
        e.Arrange(new Rect(0, 0, width, e.DesiredSize.Height));
        e.UpdateLayout();
    }

    private static (EditorSession Session, TextFormatViewModel Format) WindowsTextWithOverflow()
    {
        var (s, f, _) = Setup(t => { t.Text = Long; t.Font = FontChoice.ForWindows("Arial", 12); t.Block = new TextBlockOptions { WidthDots = 300, MaxLines = 1 }; });
        return (s, f);
    }

    [Fact]
    public void EveryControlOfThePanelAndTheBar_HasAnAccessibleName_InBothLanguages()
    {
        foreach (var lang in new[] { "en", "es" })
        {
            Loc.Instance.SetLanguage(lang);
            var problems = WpfTestHost.Run(() =>
            {
                var (_, f) = WindowsTextWithOverflow();
                var panel = new TextFormatPanel { DataContext = f };
                var bar = new TextFormatBar { DataContext = f };
                Layout(panel, 320);
                Layout(bar, 900, 200);
                var bad = new List<string>();
                foreach (var root in new FrameworkElement[] { panel, bar })
                    foreach (var c in Descendants<Control>(root).Where(c => c is ButtonBase or ComboBox))
                    {
                        // The inner button of a combo box is part of its template; the combo box itself carries the name.
                        if (c is ToggleButton { Focusable: false } tb && VisualTreeHelper.GetParent(tb) is not null && Descendants<ToggleButton>(root).Any(x => x == tb) && tb.TemplatedParent is ComboBox) continue;
                        if (c.TemplatedParent is ComboBox) continue;
                        string name = AutomationProperties.GetName(c);
                        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('!')) bad.Add($"{c.GetType().Name} in {root.GetType().Name}");
                    }
                return bad;
            });
            Loc.Instance.SetLanguage("en");
            Assert.True(problems.Count == 0, $"[{lang}] controls without a name: {string.Join(", ", problems)}");
        }
    }

    [Fact]
    public void ThePanelShowsTheOverflowChip_OnlyWhenTheTextDoesNotFit_AndTheStyleButtonsFollowTheFont()
    {
        var result = WpfTestHost.Run(() =>
        {
            var (s, f) = WindowsTextWithOverflow();
            var panel = new TextFormatPanel { DataContext = f };
            Layout(panel, 320);
            var chip = (Border)panel.FindName("OverflowChip");
            bool shown = chip.Visibility == Visibility.Visible && chip.ActualHeight > 20;
            var toggles = Descendants<ToggleButton>(panel).Where(t => !(t.TemplatedParent is ComboBox)).ToList();
            bool stylesOn = toggles.Take(3).All(t => t.IsEnabled);

            s.Edit("Undo.Property", d => ((TextElement)d.Elements[0]).Block!.MaxLines = 30);
            Layout(panel, 320);
            bool clearedWhenFits = chip.Visibility != Visibility.Visible;

            f.SelectedFont = f.Fonts.First(i => !i.IsWindows && i.PrinterId == '0');
            Layout(panel, 320);
            bool stylesOffForPrinterFont = toggles.Take(3).All(t => !t.IsEnabled);
            string tip = (string)toggles[0].ToolTip;
            return (shown, stylesOn, clearedWhenFits, stylesOffForPrinterFont, tip);
        });
        Assert.True(result.shown, "the chip should be visible while the text overflows");
        Assert.True(result.stylesOn);
        Assert.True(result.clearedWhenFits);
        Assert.True(result.stylesOffForPrinterFont);
        Assert.Equal(Loc.Get("Panel.Format.Tip.NeedsWindowsFont"), result.tip);
    }

    [Fact]
    public void TheFontList_IsVirtualized_SoHundredsOfFamiliesDoNotStallThePanel()
    {
        var realised = WpfTestHost.Run(() =>
        {
            var (_, f, _) = Setup(rasterizer: new FakeFontRasterizer(1200));
            var panel = new TextFormatPanel { DataContext = f };
            Layout(panel, 320);
            var combo = (ComboBox)panel.FindName("FontBox");
            combo.ApplyTemplate();
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var child = (FrameworkElement)popup.Child;
            // The drop-down is laid out the way a popup would be: unlimited width, as tall as MaxDropDownHeight.
            child.Measure(new Size(320, combo.MaxDropDownHeight));
            child.Arrange(new Rect(0, 0, 320, combo.MaxDropDownHeight));
            child.UpdateLayout();
            return Descendants<ComboBoxItem>(child).Count();
        });
        Assert.InRange(realised, 1, 120);
    }

    [Fact]
    public void ThePanelAndBar_RenderIntoPictures_ForAVisualCheck()
    {
        // Not an assertion about looks: it proves both controls lay out and draw without throwing, and keeps a picture for a human to look at when
        // the folder named by ROTULA_SHOTS is set (it is never written during a normal run).
        string? folder = Environment.GetEnvironmentVariable("ROTULA_SHOTS");
        var sizes = WpfTestHost.Run(() =>
        {
            var (_, f) = WindowsTextWithOverflow();
            var panel = new TextFormatPanel { DataContext = f, Width = 320 };
            var bar = new TextFormatBar { DataContext = f };
            Layout(panel, 320);
            Layout(bar, 900, 200);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
                Save(panel, Path.Combine(folder, "format-panel.png"));
                Save(bar, Path.Combine(folder, "format-bar.png"));
            }
            return (panel.ActualHeight, bar.ActualWidth);
        });
        Assert.True(sizes.ActualHeight > 150);
        Assert.True(sizes.ActualWidth > 300);
    }

    [Fact]
    public void TheMainWindow_ShowsThePanelAndTheBarForASelectedText_ForAVisualCheck()
    {
        // Draws pictures of the whole window only when ROTULA_SHOTS names a folder (never in a normal run).
        string? folder = Environment.GetEnvironmentVariable("ROTULA_SHOTS");
        // Why gated: a main view-model bound to a window on the shared WPF thread stays subscribed to the language change for the rest of the run, and
        // the next test that switches the language from its own thread would then touch that window's collections from the wrong thread.
        if (string.IsNullOrEmpty(folder)) return;
        var barShown = new List<bool>();
        foreach (var (lang, fontKind, name) in new[] { ("en", "windows", "main-windows-font-en"), ("es", "printer", "main-printer-font-es"), ("en", "wrapped", "main-wrapped-en") })
        {
            barShown.Add(WpfTestHost.Run(() =>
            {
                var main = BuildMain(out _);
                Loc.Instance.SetLanguage(lang);
                var text = new TextElement { Text = Long, X = 60, Y = 220, Font = fontKind == "printer" ? null : FontChoice.ForWindows("Arial", 14), FontId = 'A', FontHeight = 40, FontWidth = 40 };
                if (fontKind == "wrapped") text.Block = new TextBlockOptions { WidthDots = 500, MaxLines = 2, Alignment = TextAlignment.Center };
                main.Session.Edit("Undo.Add", d => d.Elements.Add(text), selectionAfter: () => new[] { text.Id });
                main.SetZoomPercent(100);
                var window = new Rotula.App.MainWindow(main) { Width = 1380, Height = 860 };
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1380, 860));
                content.Arrange(new Rect(0, 0, 1380, 860));
                content.UpdateLayout();
                for (int i = 0; i < 3; i++)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    content.UpdateLayout();
                }
                var bar = Descendants<TextFormatBar>(content).First();
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                    Save(content, Path.Combine(folder, name + ".png"));
                }
                if (!string.IsNullOrEmpty(folder))
                {
                    // The whole Properties panel of this text, tall enough to see every row.
                    var props = new PropertiesPanel { DataContext = main, Width = 360, Height = 1500 };
                    props.Measure(new Size(360, 1500));
                    props.Arrange(new Rect(0, 0, 360, 1500));
                    props.UpdateLayout();
                    Save(props, Path.Combine(folder, name + "-panel.png"));
                }
                bool shown = bar.Visibility == Visibility.Visible;
                Loc.Instance.SetLanguage("en");
                return shown;
            }));
        }
        Assert.All(barShown, Assert.True);
    }

    private static void Save(FrameworkElement e, string path)
    {
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth) + 20, (int)Math.Ceiling(e.ActualHeight) + 20, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, rtb.Width, rtb.Height));
            dc.DrawRectangle(new VisualBrush(e), null, new Rect(10, 10, e.ActualWidth, e.ActualHeight));
        }
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
