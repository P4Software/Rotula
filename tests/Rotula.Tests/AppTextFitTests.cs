using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Text;

namespace Rotula.Tests;

/// <summary>The "Text fit" section of the text panel: it maps onto the model, every change is one undo step, and the overflow warning speaks about the fitted result.</summary>
[Collection("App")]
public class AppTextFitTests
{
    private const string Long = "The quick brown fox jumps over the lazy dog and keeps running through the whole field until night";

    private static (EditorSession Session, TextFormatViewModel Format, TextElement Text) Setup(Action<TextElement>? configure = null)
    {
        Loc.Instance.SetLanguage("en");
        // A printer-font block 400 dots wide and 4 lines high: the long text does not fit it at 40 dots.
        var text = new TextElement
        {
            X = 20, Y = 30, Text = Long, FontId = '0', FontHeight = 40, FontWidth = 40,
            Block = new TextBlockOptions { WidthDots = 400, MaxLines = 4 },
        };
        configure?.Invoke(text);
        var s = AppTestSupport.Session(text);
        var fmt = new TextFormatViewModel(s, () => new FakeFontRasterizer());
        s.Select(text);
        return (s, fmt, text);
    }

    private static TextElement Now(EditorSession s) => (TextElement)s.Document.Elements[0];

    [Fact]
    public void TheSection_IsOnlyShownForAWrappedBlock()
    {
        var (_, plain, _) = Setup(t => t.Block = null);
        Assert.False(plain.ShowFit);
        var (_, wrapped, _) = Setup();
        Assert.True(wrapped.ShowFit);
        Assert.Equal(new[] { "Leave as designed", "Shrink to fit", "Grow height" }, wrapped.FitModes);
        Assert.Equal(0, wrapped.FitModeIndex);
    }

    [Fact]
    public void ChoosingShrink_StartsFromTheCurrentSize_AndIsOneUndoStep()
    {
        var (s, f, _) = Setup();
        Assert.True(f.HasOverflow);          // 40 dots does not fit
        f.FitModeIndex = 1;

        var fit = Now(s).Block!.Fit!;
        Assert.Equal(TextFitMode.Shrink, fit.Mode);
        // 40 dots at 203 dpi is 14.2 pt: the largest size starts there, the smallest at about half.
        Assert.Equal(14, fit.MaxSize);
        Assert.Equal(7, fit.MinSize);
        Assert.True(f.FitIsShrink);
        Assert.Equal(1, s.Undo.UndoCount);
        Assert.False(f.HasOverflow);         // the warning speaks about the fitted size, which fits

        s.Undo.Undo();
        Assert.Null(Now(s).Block!.Fit);
        Assert.Equal(0, f.FitModeIndex);
        Assert.True(f.HasOverflow);
    }

    [Fact]
    public void TheLimits_AreChangedByTyping_AndABadOrderIsExplained()
    {
        var (s, f, _) = Setup();
        f.FitModeIndex = 1;
        f.FitMinText = "5";
        f.FitMaxText = "11.5";
        Assert.Equal((5d, 11.5), (Now(s).Block!.Fit!.MinSize, Now(s).Block!.Fit!.MaxSize));

        f.FitMinText = "20";                  // above the largest size
        Assert.True(f.HasFitError);
        Assert.Equal(Loc.Get("Panel.Format.Fit.Error.Order"), f.FitError);
        Assert.Equal(5, Now(s).Block!.Fit!.MinSize); // nothing was changed

        f.FitMinText = "abc";
        Assert.Equal(Loc.Get("Validation.NotNumber"), f.FitError);
        f.FitMinText = "6";
        Assert.False(f.HasFitError);
        Assert.Equal(6, Now(s).Block!.Fit!.MinSize);
    }

    [Fact]
    public void TheMinimumReached_KeepsTheWarning_AndIgnoreSilencesIt()
    {
        var (s, f, _) = Setup(t => t.Text = string.Concat(Enumerable.Repeat(Long + " ", 10)));
        f.FitModeIndex = 1;
        f.FitMinText = "10";
        f.FitMaxText = "14";
        Assert.True(f.HasOverflow);

        f.FitIgnoreWarning = true;
        Assert.True(Now(s).Block!.Fit!.IgnoreOverflowWarning);
        Assert.False(f.HasOverflow);
    }

    [Fact]
    public void Grow_ShowsTheCap_AndTheWarningClearsWhenTheLinesFit()
    {
        var (s, f, _) = Setup();
        f.FitModeIndex = 2;
        Assert.True(f.FitIsGrow);
        Assert.False(f.HasOverflow);
        f.FitCapText = "2";                   // not enough lines for the text
        Assert.True(f.HasOverflow);
        f.FitCapText = "99999";
        Assert.True(f.HasFitError);
        Assert.Equal(2, Now(s).Block!.Fit!.MaxLinesCap);
    }

    [Fact]
    public void BackToNone_RemovesTheFit_SoTheFileIsAsItWasBefore()
    {
        var (s, f, _) = Setup();
        f.FitModeIndex = 1;
        f.FitModeIndex = 0;
        Assert.Null(Now(s).Block!.Fit);
    }

    [Fact]
    public void CopyingTheText_KeepsItsFit()
    {
        var (s, f, text) = Setup();
        f.FitModeIndex = 1;
        var copy = (TextElement)ElementCloner.Clone(Now(s), newId: true);
        Assert.Equal(TextFitMode.Shrink, copy.Block!.Fit!.Mode);
        Assert.NotSame(Now(s).Block!.Fit, copy.Block.Fit);
    }
}
