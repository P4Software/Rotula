using Rotula.App.Editing;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Zpl;
using Rotula.Rendering;
using Rotula.Rendering.Input;

namespace Rotula.Tests;

/// <summary>Switching word wrap on must give a block that is sized from the label, and the preview and the ZPL must both wrap.</summary>
[Collection("App")]
public class AppWordWrapTests
{
    private const string Long = "Stainless steel water bottle, double wall vacuum insulated, keeps drinks cold for 24 hours and hot for 12 hours, " +
                                "dishwasher safe lid, available in six colours, BPA free, 750 ml capacity, made in Portugal, EAN on the base";

    private static EditorSession Label600(out TextElement text)
    {
        // 4 x 3 inch at 600 dpi.
        var doc = new LabelDocument { Width = Measure.Inches(4), Height = Measure.Inches(3), Dpi = PrinterDpi.Dpi600 };
        doc.Variables.Add(new LabelVariable("description", Long));
        text = new TextElement { X = 100, Y = 100, Text = "{{description}}", FontHeight = 50, FontWidth = 40 };
        doc.Elements.Add(text);
        return new EditorSession(doc);
    }

    private static int InkRows(EditorSession s)
    {
        using var r = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(s.Document));
        var span = r.Bitmap.GetPixelSpan();
        int first = -1, last = -1;
        for (int y = 0; y < r.Bitmap.Height; y++)
        {
            bool ink = false;
            for (int x = 0; x < r.Bitmap.Width && !ink; x++) ink = span[y * r.Bitmap.RowBytes + x * 4] < 128;
            if (ink) { if (first < 0) first = y; last = y; }
        }
        return first < 0 ? 0 : last - first + 1;
    }

    [Fact]
    public void WrapOn_SizesTheBlockFromTheLabel_AndPreviewAndZplBothWrap()
    {
        var s = Label600(out var text);
        int single = InkRows(s);
        s.Select(text);
        var panel = new PropertyPanelViewModel(s);
        ((BoolRow)panel.Rows.First(r => r.LabelKey == "Prop.Block")).Value = true;

        var block = ((TextElement)s.Document.Elements[0]).Block!;
        Assert.Equal(2400 - 100, block.WidthDots);       // the room to the right of the text
        Assert.True(block.MaxLines >= 2 && block.MaxLines <= 9999);
        Assert.Equal((1800 - 100) / 50, block.MaxLines); // the room below, in lines of the font

        // The preview re-renders at once and shows several lines.
        Assert.True(InkRows(s) > single * 1.8, "the preview did not wrap");
        var rt = (RenderText)LabelDocumentAdapter.ToRenderLabel(s.Document).Elements[0];
        Assert.Equal(block.WidthDots, rt.WidthDots);
        Assert.Equal(block.MaxLines, rt.MaxLines);

        // The printer gets the same numbers.
        string zpl = ZplGenerator.Generate(s.Document).Zpl;
        Assert.Contains($"^FB{block.WidthDots},{block.MaxLines},0,L,0", zpl);
    }

    [Fact]
    public void ChangingWidthAndMaxLines_ReRendersAndKeepsZplInStep()
    {
        var s = Label600(out var text);
        s.Select(text);
        var panel = new PropertyPanelViewModel(s);
        ((BoolRow)panel.Rows.First(r => r.LabelKey == "Prop.Block")).Value = true;
        int wide = InkRows(s);
        s.Edit("Undo.Property", d => ((TextElement)d.Elements[0]).Block!.WidthDots = 800);
        Assert.True(InkRows(s) > wide);
        s.Edit("Undo.Property", d => ((TextElement)d.Elements[0]).Block!.MaxLines = 2);
        var rt = (RenderText)LabelDocumentAdapter.ToRenderLabel(s.Document).Elements[0];
        Assert.Equal(2, rt.MaxLines);
        Assert.Contains("^FB800,2,0,L,0", ZplGenerator.Generate(s.Document).Zpl);
    }

    [Fact]
    public void TheObjectsPanel_IsClosedByDefault_AndTheChoiceIsRemembered()
    {
        var fake = new FakeServices();
        fake.Settings.Current.Language = "en";
        var main = new MainViewModel(fake.Build());
        Assert.False(main.ObjectsPanelExpanded);
        main.ObjectsPanelExpanded = true;
        Assert.True(fake.Settings.Current.ObjectsPanelExpanded);
        fake.Settings.Current.ObjectsPanelExpanded = true;
        Assert.True(new MainViewModel(fake.Build()).ObjectsPanelExpanded);
    }

    [Fact]
    public void TheBlock_CountsFromTheLabelHome_NotFromTheEdgeOfTheMedia()
    {
        var s = Label600(out var text);
        s.Document.Margins = new LabelMargins { Left = Measure.Dots(200), Top = Measure.Dots(150), Right = Measure.Dots(50), Bottom = Measure.Dots(50) };
        var block = PropertyPanelViewModel.DefaultBlockFor(s.Document, text);
        Assert.Equal(2400 - 200 - 100 - 50, block.WidthDots);
        Assert.Equal((1800 - 150 - 100 - 50) / 50, block.MaxLines);
    }

    [Fact]
    public void ATurnedText_GetsItsWidthFromTheRoomBelow_AndAWindowsFontFromItsPointSize()
    {
        var s = Label600(out var text);
        text.Rotation = FieldRotation.Rotated90;
        var turned = PropertyPanelViewModel.DefaultBlockFor(s.Document, text);
        Assert.Equal(1800 - 100, turned.WidthDots);
        Assert.Equal((2400 - 100) / 50, turned.MaxLines);

        text.Rotation = null;
        text.Font = FontChoice.ForWindows("Arial", 12); // 12 pt at 600 dpi = 100 dots, so far fewer lines than a 50-dot font
        Assert.Equal((int)((1800 - 100) / (100 * 1.2)), PropertyPanelViewModel.DefaultBlockFor(s.Document, text).MaxLines);
    }
}

/// <summary>
/// A missing resource in a XAML file only fails when the window is created, never at build time. These tests create the new windows on the
/// shared WPF test thread so such a mistake shows up here.
/// </summary>
public class NewWindowsLoadTests
{
    [Fact]
    public void ThePromptDialog_Loads_WithATextAndAChoiceQuestion()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Prompt("shift", p => { p.InputType = Rotula.Core.Variables.PromptInputType.Choice; p.Choices.AddRange(new[] { "Day", "Night" }); p.Required = true; }));
        doc.Variables.Add(Vars.Prompt("note", p => p.HelpText = "Anything to add?"));
        var set = Rotula.Core.Variables.Prompts.PromptSet.Build(doc);
        int fields = WpfTestHost.Run(() =>
        {
            var vm = new PromptDialogViewModel(set);
            var window = new Rotula.App.Views.PromptDialogWindow(vm);
            window.Measure(new System.Windows.Size(520, 700));
            window.Arrange(new System.Windows.Rect(0, 0, 520, 700));
            window.UpdateLayout();
            return vm.Fields.Count;
        });
        Assert.Equal(2, fields);
    }
}
