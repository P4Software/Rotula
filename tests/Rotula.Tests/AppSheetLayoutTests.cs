using System.Text;
using System.Text.RegularExpressions;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Sheets;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Prompts;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>The "Label sheets" dialog: choosing a sheet, the start position, the PDF and what is remembered with the label.</summary>
[Collection("App")]
public class AppSheetLayoutTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();

    public AppSheetLayoutTests()
    {
        _fake.Settings.Current.Language = "en";
        Loc.Instance.SetLanguage("en");
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private sealed class SerialVariables(MemoryCounterStore store) : IVariableServices
    {
        public ICounterStore? CounterStore => store;
        public IPromptMemory PromptMemory { get; } = new MemoryPromptMemory();
        public IReadOnlyDictionary<string, string>? Ask(PromptSet set) => new Dictionary<string, string>();
    }

    /// <summary>A main window with a new label of the given size (in inches) opened, and the sheet dialog shown for it.</summary>
    private (MainViewModel Main, SheetLayoutViewModel Sheet) Open(double widthIn, double heightIn, Action<LabelDocument>? design = null)
    {
        var doc = new LabelDocument { Width = Measure.Inches(widthIn), Height = Measure.Inches(heightIn) };
        doc.EnsureId();
        doc.Elements.Add(new TextElement { Id = "t", X = 10, Y = 10, Text = "Hello" });
        design?.Invoke(doc);
        _fake.NewLabel = doc;
        var main = new MainViewModel(_fake.Build());
        main.NewCommand.Execute(null);
        Assert.True(main.ShowSheetsCommand.CanExecute(null));
        main.ShowSheetsCommand.Execute(null);
        return (main, _fake.ToolDialogs.LastSheet!);
    }

    private static int PdfPages(string path) =>
        Regex.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(path)), @"/Type /Page\b").Count;

    [Fact]
    public void TheDialog_StartsOnTheTemplateWithTheLabelsSize_InInches()
    {
        var (_, sheet) = Open(2.625, 1);
        Assert.Equal("letter-30", sheet.SelectedTemplate.Template?.Id);
        Assert.True(sheet.UseInches);
        Assert.Equal("2.625", sheet.LabelWidthText);
        Assert.Equal("3", sheet.ColumnsText);
        Assert.Equal(30, sheet.Slots.Count);
        Assert.False(sheet.HasErrors);
        Assert.Contains("Labels: 1. Sheets: 1 (30 per sheet).", sheet.SummaryText);
        // Names come from the dimensions only.
        Assert.All(sheet.Templates.Skip(1), t => Assert.Matches(@"^(Letter|A4)", t.Name));
        Assert.DoesNotContain(sheet.Templates, t => t.Name.Contains("Avery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EditingANumber_MakesTheLayoutCustom_AndAProblemBlocksTheExport()
    {
        var (_, sheet) = Open(2.625, 1);
        sheet.TopText = "0.6";
        Assert.True(sheet.IsCustom);
        Assert.False(sheet.HasErrors);
        Assert.Equal(0.6 * 25.4, sheet.Settings!.Layout.TopMarginMm, 3);

        sheet.ColumnsText = "4";
        Assert.True(sheet.HasErrors);
        Assert.False(sheet.CanExport);
        Assert.Contains("do not fit across the paper", sheet.ProblemsText);
        Assert.Null(sheet.Settings);

        sheet.ColumnsText = "x";
        Assert.Contains("Labels across: type a number.", sheet.ProblemsText);
    }

    [Fact]
    public void ChoosingATemplate_FillsTheNumbers_AndMillimetresForA4()
    {
        var (_, sheet) = Open(2.625, 1);
        sheet.SelectedTemplate = sheet.Templates.First(t => t.Template?.Id == "a4-21");
        Assert.False(sheet.UseInches);
        Assert.Equal("63.5", sheet.LabelWidthText);
        Assert.Equal("15.15", sheet.TopText);
        Assert.Equal(SheetPageKind.A4, sheet.SelectedPage.Value);
        Assert.False(sheet.IsCustom);
        Assert.Equal(21, sheet.Slots.Count);
        // The design is larger than these labels, which is a note, not an error.
        Assert.False(sheet.HasErrors);
        Assert.Contains("larger than", sheet.ProblemsText);

        // Switching the unit shows the same layout in inches.
        sheet.UseInches = true;
        Assert.Equal("2.5", sheet.LabelWidthText);
        Assert.False(sheet.IsCustom);
    }

    [Fact]
    public void ClickingAPosition_MakesItTheStart_AndThePreviewShowsTheUsedOnes()
    {
        var (_, sheet) = Open(4, 2);
        Assert.Equal("letter-10", sheet.SelectedTemplate.Template?.Id);
        sheet.CopiesText = "8";
        sheet.PickStartCommand.Execute(sheet.Slots[4]);
        Assert.Equal("5", sheet.StartPositionText);
        Assert.All(sheet.Slots.Take(4), s => Assert.True(s.IsUsed));
        Assert.Equal("1", sheet.Slots[4].Caption);
        Assert.Equal("6", sheet.Slots[9].Caption);
        Assert.Contains("Sheets: 2", sheet.SummaryText);

        sheet.StartPositionText = "11";
        Assert.True(sheet.HasErrors);
        Assert.Contains("from 1 to 10", sheet.ProblemsText);
    }

    [Fact]
    public async Task Export_WritesThePdf_AndEveryCopyGetsItsOwnSerial_UsedOnlyOnce()
    {
        var store = new MemoryCounterStore();
        _fake.Variables = new SerialVariables(store);
        var (main, sheet) = Open(4, 2, d =>
        {
            d.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
            d.Elements.Add(new TextElement { Id = "sn", X = 10, Y = 60, Text = "{{serial}}" });
        });
        _fake.ExportPath = Path.Combine(_dir, "sheets.pdf");
        sheet.CopiesText = "12";
        sheet.StartPositionText = "5";
        await sheet.ExportPdfCommand.ExecuteAsync(null);

        Assert.False(sheet.ResultIsError, sheet.ResultText);
        Assert.Contains("labels: 12, sheets: 2", sheet.ResultText);
        Assert.Equal(2, PdfPages(_fake.ExportPath));
        Assert.Equal("SN0012", CounterAdmin.Peek(main.Session.Document, "serial", store).LastValue);
        // The layout is remembered with the label, which now has a change to save.
        Assert.Equal("letter-10", main.LabelSheet?.TemplateId);
        Assert.True(main.Session.IsDirty);
    }

    [Fact]
    public async Task Export_WithData_WritesTheTickedRecordsWithTheirCopies()
    {
        var (main, sheet) = Open(4, 2, d => d.Elements.Add(new TextElement { Id = "n", X = 10, Y = 60, Text = "Item {{n}}" }));
        string csv = Path.Combine(_dir, "data.csv");
        File.WriteAllText(csv, "n\n1\n2\n3\n4\n5\n6\n", new UTF8Encoding(false));
        Assert.True(await main.Data.LoadAsync(csv));
        main.Data.SelectNoneShownCommand.Execute(null);
        foreach (int i in new[] { 0, 2, 4 }) main.Data.VisibleRows[i].IsSelected = true;

        main.ShowSheetsCommand.Execute(null);
        sheet = _fake.ToolDialogs.LastSheet!;
        Assert.True(sheet.UsesRecords);
        sheet.CopiesText = "4";                          // 3 records x 4 copies = 12 labels on a 10-up sheet
        Assert.Contains("Labels: 12. Sheets: 2", sheet.SummaryText);
        _fake.ExportPath = Path.Combine(_dir, "records.pdf");
        await sheet.ExportPdfCommand.ExecuteAsync(null);

        Assert.False(sheet.ResultIsError, sheet.ResultText);
        Assert.Contains("labels: 12, sheets: 2", sheet.ResultText);
        Assert.Equal(2, PdfPages(_fake.ExportPath));
    }

    [Fact]
    public async Task Export_WhenNothingIsTicked_IsBlocked()
    {
        var (main, _) = Open(4, 2);
        string csv = Path.Combine(_dir, "data.csv");
        File.WriteAllText(csv, "n\n1\n2\n", new UTF8Encoding(false));
        Assert.True(await main.Data.LoadAsync(csv));
        main.Data.SelectNoneShownCommand.Execute(null);
        main.ShowSheetsCommand.Execute(null);
        var sheet = _fake.ToolDialogs.LastSheet!;
        Assert.True(sheet.HasErrors);
        _fake.ExportPath = Path.Combine(_dir, "none.pdf");
        await sheet.ExportPdfCommand.ExecuteAsync(null);
        Assert.True(sheet.ResultIsError);
        Assert.False(File.Exists(_fake.ExportPath));
    }

    [Fact]
    public void TheLayout_IsSavedWithTheLabel_AndComesBackWhenItIsOpened()
    {
        var (main, sheet) = Open(2.625, 1);
        sheet.SelectedGuides = sheet.GuideChoices.First(g => g.Value == SheetGuides.CropMarks);
        sheet.LeftText = "0.2";
        sheet.CloseCommand.Execute(null);
        Assert.NotNull(main.LabelSheet);
        Assert.Null(main.LabelSheet!.TemplateId);
        Assert.True(main.Session.IsDirty);

        _fake.SavePath = Path.Combine(_dir, "sheet.rotula");
        main.SaveCommand.Execute(null);
        Assert.Equal(SheetGuides.CropMarks, RotulaFileSerializer.LoadFile(_fake.SavePath).Sheet?.Guides);

        var reopened = new MainViewModel(_fake.Build());
        Assert.True(reopened.LoadFile(_fake.SavePath));
        Assert.Equal(main.LabelSheet, reopened.LabelSheet);
        reopened.ShowSheetsCommand.Execute(null);
        var again = _fake.ToolDialogs.LastSheet!;
        Assert.Equal("0.2", again.LeftText);
        Assert.Equal(SheetGuides.CropMarks, again.SelectedGuides.Value);
        Assert.True(again.IsCustom);
    }

    [Fact]
    public void ThePrintDialog_OffersLabelSheets()
    {
        var (main, _) = Open(4, 2);
        _fake.ToolDialogs.LastSheet = null;
        main.PrintCommand.Execute(null);
        var print = _fake.ToolDialogs.LastPrint!;
        Assert.True(print.CanShowSheets);
        print.ShowSheetsCommand.Execute(null);
        Assert.NotNull(_fake.ToolDialogs.LastSheet);
        print.Detach();
    }

    [Fact]
    public void SuggestSheet_UsesTheLabelsSheetStock()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(63.5), Height = Measure.Millimeters(38.1), StockId = "avery-L7160" };
        Assert.Equal("a4-21", MainViewModel.SuggestSheet(doc).TemplateId);
        var odd = new LabelDocument { Width = Measure.Millimeters(12), Height = Measure.Millimeters(12) };
        Assert.Equal(SheetTemplates.All[0].Id, MainViewModel.SuggestSheet(odd).TemplateId);
    }

    [Fact]
    public void TheDialog_ReadsWellInSpanish()
    {
        Loc.Instance.SetLanguage("es");
        var (_, sheet) = Open(2.625, 1);
        Assert.StartsWith("Carta", sheet.SelectedTemplate.Name);
        Assert.Contains("por hoja", sheet.SelectedTemplate.Name);
        Assert.Contains("Etiquetas: 1. Hojas: 1", sheet.SummaryText);
    }
}
