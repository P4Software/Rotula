using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

[Collection("App")]
public class AppPropertyPanelTests : IDisposable
{
    public AppPropertyPanelTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private static T Row<T>(PropertyPanelViewModel p, string labelKey) where T : PropertyRow =>
        p.Rows.OfType<T>().First(r => r.LabelKey == labelKey);

    [Fact]
    public void NothingSelected_ShowsLabelProperties()
    {
        var s = AppTestSupport.Session();
        var p = new PropertyPanelViewModel(s);
        Assert.Equal("Label", p.Title);
        Assert.Contains(p.Rows, r => r.LabelKey == "Prop.LabelWidth");
        Assert.Contains(p.Rows, r => r.LabelKey == "Prop.Dpi");
        Assert.Contains(p.Rows, r => r.LabelKey == "Prop.Variables");
    }

    [Fact]
    public void ValidNumber_IsApplied_AsAnUndoableEdit()
    {
        var box = AppTestSupport.Box(10, 10);
        var s = AppTestSupport.Session(box);
        var p = new PropertyPanelViewModel(s);
        s.Select(box);

        Row<IntRow>(p, "Prop.Width").Text = "250";
        Assert.Equal(250, ((BoxElement)s.Document.Elements[0]).WidthDots);
        Assert.Equal(string.Empty, Row<IntRow>(p, "Prop.Width").Error);
        s.PerformUndo();
        Assert.Equal(100, ((BoxElement)s.Document.Elements[0]).WidthDots);
    }

    [Theory]
    [InlineData("abc", "whole number")]
    [InlineData("12.5", "whole number")]
    [InlineData("", "cannot be empty")]
    [InlineData("0", "between 1 and 32000")]
    [InlineData("99999", "between 1 and 32000")]
    public void InvalidNumber_IsRejected_WithAClearMessage_AndTheDocumentIsUntouched(string typed, string expected)
    {
        var box = AppTestSupport.Box(10, 10);
        var s = AppTestSupport.Session(box);
        var p = new PropertyPanelViewModel(s);
        s.Select(box);

        var row = Row<IntRow>(p, "Prop.Width");
        row.Text = typed;
        Assert.Contains(expected, row.Error);
        Assert.True(row.HasError);
        Assert.Equal(100, ((BoxElement)s.Document.Elements[0]).WidthDots);
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void ErrorMessages_AreLocalized()
    {
        var box = AppTestSupport.Box(10, 10);
        var s = AppTestSupport.Session(box);
        var p = new PropertyPanelViewModel(s);
        s.Select(box);
        Loc.Instance.SetLanguage("es"); // rows are rebuilt on language change
        var row = Row<IntRow>(p, "Prop.Width");
        Assert.Equal("Ancho (puntos)", row.Label);
        row.Text = "abc";
        Assert.Contains("número entero", row.Error);
    }

    [Fact]
    public void ErrorClears_WhenTheUserTypesAValidValueAgain()
    {
        var box = AppTestSupport.Box(10, 10);
        var s = AppTestSupport.Session(box);
        var p = new PropertyPanelViewModel(s);
        s.Select(box);
        var row = Row<IntRow>(p, "Prop.Height");
        row.Text = "x";
        Assert.True(row.HasError);
        row.Text = "77";
        Assert.False(row.HasError);
        Assert.Equal(77, ((BoxElement)s.Document.Elements[0]).HeightDots);
    }

    [Fact]
    public void NullableNumbers_EmptyMeansInheritTheLabelDefault()
    {
        var text = new TextElement { Text = "t", FontHeight = 60 };
        var s = AppTestSupport.Session(text);
        var p = new PropertyPanelViewModel(s);
        s.Select(text);
        var row = Row<IntRow>(p, "Prop.FontHeight");
        Assert.Equal("60", row.Text);
        row.Text = "";
        Assert.Null(((TextElement)s.Document.Elements[0]).FontHeight);
        Assert.False(row.HasError);
        row.Text = "5";
        Assert.Contains("between 10 and 32000", row.Error);
    }

    [Fact]
    public void UndoRefreshesTheRowValues_WithoutRebuildingTheRows()
    {
        var box = AppTestSupport.Box(10, 10);
        var s = AppTestSupport.Session(box);
        var p = new PropertyPanelViewModel(s);
        s.Select(box);
        var rowBefore = Row<IntRow>(p, "Prop.X");
        rowBefore.Text = "55";
        s.PerformUndo();
        var rowAfter = Row<IntRow>(p, "Prop.X");
        Assert.Same(rowBefore, rowAfter); // same selection: rows (and focus) survive
        Assert.Equal("10", rowAfter.Text);
    }

    [Fact]
    public void Edits_AfterUndo_StillReachTheRightElement()
    {
        // Undo replaces the element objects; rows must look the element up by id, not hold a stale reference.
        var box = AppTestSupport.Box(10, 10);
        var s = AppTestSupport.Session(box);
        var p = new PropertyPanelViewModel(s);
        s.Select(box);
        Row<IntRow>(p, "Prop.Width").Text = "300";
        s.PerformUndo();
        s.PerformRedo();
        Row<IntRow>(p, "Prop.Height").Text = "90";
        var el = (BoxElement)s.Document.Elements[0];
        Assert.Equal((300, 90), (el.WidthDots, el.HeightDots));
    }

    [Fact]
    public void BarcodeRows_DependOnTheSymbology()
    {
        var bc = new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "123" };
        var s = AppTestSupport.Session(bc);
        var p = new PropertyPanelViewModel(s);
        s.Select(bc);
        Assert.Contains(p.Rows, r => r.LabelKey == "Prop.Code128Mode");
        // The catalogue says ^BC has no wide-to-narrow ratio, so the row is not offered; Code 39 does have it.
        Assert.DoesNotContain(p.Rows, r => r.LabelKey == "Prop.WideBarRatio");
        Assert.DoesNotContain(p.Rows, r => r.LabelKey == "Prop.QrErrorCorrection");

        var symbology = Row<ChoiceRow>(p, "Prop.Symbology");
        symbology.Selected = symbology.Items.First(i => Equals(i.Value, BarcodeSymbology.QrCode));
        Assert.Equal(BarcodeSymbology.QrCode, ((BarcodeElement)s.Document.Elements[0]).Symbology);
        Assert.Contains(p.Rows, r => r.LabelKey == "Prop.QrErrorCorrection");
        Assert.DoesNotContain(p.Rows, r => r.LabelKey == "Prop.Code128Mode");

        // PDF417 has its own options.
        var again = Row<ChoiceRow>(p, "Prop.Symbology");
        again.Selected = again.Items.First(i => Equals(i.Value, BarcodeSymbology.Pdf417));
        Assert.Contains(p.Rows, r => r.LabelKey == "Prop.Pdf417Security");
    }

    [Fact]
    public void BarcodeData_IsValidatedByCore_AndShownAsLocalizedMessages()
    {
        var bc = new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "5901234123457" };
        var vm = new MainViewModel(new FakeServices().Build());
        vm.Session.Load(new LabelDocument());
        vm.Commands.Add(bc);
        Assert.Empty(vm.Properties.IssueMessages);

        // Typing a wrong check digit is allowed (so the user can keep typing) but is explained at once.
        Row<TextRow>(vm.Properties, "Prop.Data").Text = "5901234123458";
        var message = Assert.Single(vm.Properties.IssueMessages);
        Assert.Contains("check digit", message);
        Assert.Contains("7", message); // the digit that would be correct
        Assert.StartsWith("Error:", message);

        Row<TextRow>(vm.Properties, "Prop.Data").Text = "590123412";
        Assert.Contains("12 digits", Assert.Single(vm.Properties.IssueMessages));

        Loc.Instance.SetLanguage("es");
        Assert.Contains("dígitos", Assert.Single(vm.Properties.IssueMessages));
    }

    [Fact]
    public void LabelSizeUnitChange_ConvertsNumbers_KeepingThePhysicalSize()
    {
        var s = AppTestSupport.Session();
        s.Edit("Undo.LabelSettings", d => { d.Width = Measure.Inches(4); d.Height = Measure.Inches(6); });
        var p = new PropertyPanelViewModel(s);
        var unit = Row<ChoiceRow>(p, "Prop.LabelUnit");
        unit.Selected = unit.Items.First(i => Equals(i.Value, LengthUnit.Millimeters));
        Assert.Equal(LengthUnit.Millimeters, s.Document.Width.Unit);
        Assert.Equal(101.6, s.Document.Width.Value, 2);
        Assert.Equal(152.4, s.Document.Height.Value, 2);
        Assert.Equal("101.6", Row<DoubleRow>(p, "Prop.LabelWidth").Text.Replace(',', '.'));
    }

    [Fact]
    public void LabelDoubleRow_RejectsNonsense()
    {
        var s = AppTestSupport.Session();
        var p = new PropertyPanelViewModel(s);
        var width = Row<DoubleRow>(p, "Prop.LabelWidth");
        width.Text = "wide";
        Assert.Contains("not a number", width.Error);
        width.Text = "-5";
        Assert.Contains("between", width.Error);
        width.Text = "50";
        Assert.False(width.HasError);
        Assert.Equal(50, s.Document.Width.Value);
    }

    [Fact]
    public void Variables_ParseAndValidate()
    {
        Assert.True(PropertyPanelViewModel.ParseVariables("sku=ABC\r\nqty=5\n\nname=a=b", out var list));
        Assert.Equal(3, list.Count);
        Assert.Equal("a=b", list[2].SampleValue);
        Assert.False(PropertyPanelViewModel.ParseVariables("no equals sign", out _));
        Assert.False(PropertyPanelViewModel.ParseVariables("bad name=1", out _));
        Assert.False(PropertyPanelViewModel.ParseVariables("a=1\na=2", out _));

        var s = AppTestSupport.Session();
        var p = new PropertyPanelViewModel(s);
        var row = Row<TextRow>(p, "Prop.Variables");
        row.Text = "x=1\ny=2";
        Assert.Equal(2, s.Document.Variables.Count);
        row.Text = "broken line";
        Assert.True(row.HasError);
        Assert.Equal(2, s.Document.Variables.Count);
    }

    [Fact]
    public void MultipleSelection_ShowsAHintInsteadOfRows()
    {
        var s = AppTestSupport.Session(AppTestSupport.Box(0, 0), AppTestSupport.Box(10, 10));
        var p = new PropertyPanelViewModel(s);
        s.SelectAll();
        Assert.Empty(p.Rows);
        Assert.Contains("2 elements", p.Hint);
    }

    [Fact]
    public void TextBlockToggle_AddsBlockRows()
    {
        var t = new TextElement { Text = "x" };
        var s = AppTestSupport.Session(t);
        var p = new PropertyPanelViewModel(s);
        s.Select(t);
        Assert.DoesNotContain(p.Rows, r => r.LabelKey == "Prop.Block.Width");
        Row<BoolRow>(p, "Prop.Block").Value = true;
        Assert.NotNull(((TextElement)s.Document.Elements[0]).Block);
        Assert.Contains(p.Rows, r => r.LabelKey == "Prop.Block.Width");
    }

    [Fact]
    public void ElementDisplayName_UsesNameWhenGiven()
    {
        Assert.Equal("Box", PropertyPanelViewModel.ElementDisplayName(new BoxElement()));
        Assert.Equal("Header (Box)", PropertyPanelViewModel.ElementDisplayName(new BoxElement { Name = "Header" }));
        Assert.Equal("EAN-13", PropertyPanelViewModel.ElementDisplayName(new BarcodeElement { Symbology = BarcodeSymbology.Ean13 }));
    }
}
