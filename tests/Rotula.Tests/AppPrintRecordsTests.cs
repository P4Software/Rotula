using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.App.Views;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Prompts;

namespace Rotula.Tests;

/// <summary>
/// The Print dialog when data is loaded: it is where the operator picks the records (the same ticks as in the Data window) and prints the
/// ticked ones as a batch. Without data it must behave exactly as before.
/// </summary>
[Collection("App")]
public class AppPrintRecordsTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly MainViewModel _main;

    public AppPrintRecordsTests()
    {
        _fake.Settings.Current.Language = "en";
        _main = Build(_fake);
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private static MainViewModel Build(FakeServices fake)
    {
        var main = new MainViewModel(fake.Build());
        main.Printer.SelectedPrinterName = "Test Printer";
        return main;
    }

    private DataMergeViewModel Data => _main.Data;

    private PrintViewModel Dialog(MainViewModel? main = null, bool withRecords = true)
    {
        main ??= _main;
        return new PrintViewModel(_fake.Printer, main.Printer, () => main.Session.Document, () => main.Data.CurrentData, _fake.Build().Files, "my-label",
            main.Variables, withRecords ? main.Data : null);
    }

    private string Csv(int rows, string name = "data.csv")
    {
        var sb = new StringBuilder("n,name\n");
        for (int i = 1; i <= rows; i++) sb.Append(i).Append(",Name").Append(i).Append('\n');
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private async Task Load(int rows, string template = "Item {{n}}", MainViewModel? main = null)
    {
        main ??= _main;
        main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = template }));
        Assert.True(await main.Data.LoadAsync(Csv(rows)));
    }

    private static void Tick(DataMergeViewModel data, params int[] positions)
    {
        data.SelectNoneShownCommand.Execute(null);
        foreach (int p in positions) data.VisibleRows[p].IsSelected = true;
    }

    private static string[] Items(IEnumerable<string> jobs) =>
        jobs.SelectMany(j => Regex.Matches(j, @"Item (\d+)").Select(m => m.Value)).ToArray();

    // ------------------------------------------------------------------------------------ the records card

    [Fact]
    public async Task WithData_TheDialogShowsTheRecordsCard_AndTheLiveSelectionSummary()
    {
        await Load(5);
        var dialog = Dialog();
        Assert.True(dialog.HasRecords);
        Assert.False(dialog.ShowRecordBanner);          // the old "uses the record on the canvas" note is replaced by the list
        Assert.Equal("5 of 5 selected", dialog.SelectionLine);
        Assert.True(dialog.CanPrint);

        Data.VisibleRows[0].IsSelected = false;         // the ticks are the Data window's: one source of truth
        Assert.Equal("4 of 5 selected", dialog.SelectionLine);
        dialog.Detach();
    }

    [Fact]
    public async Task WithoutData_TheDialogHasNoRecordsCard_AndKeepsTheSingleLabelNote()
    {
        var dialog = Dialog(withRecords: false);
        Assert.False(dialog.HasRecords);
        Assert.False(dialog.ShowRecordBanner);          // no data on the canvas, so no note either
        Assert.True(dialog.CanPrint);
        Assert.Equal(string.Empty, dialog.SelectionLine);

        // Data on the canvas but handed over without the list (older callers): the note stays.
        await Load(3);
        var legacy = new PrintViewModel(_fake.Printer, _main.Printer, () => _main.Session.Document, () => Data.CurrentData, _fake.Build().Files);
        Assert.True(legacy.ShowRecordBanner);
        Assert.False(legacy.HasRecords);
    }

    // ------------------------------------------------------------------------------------ printing the ticked records

    [Fact]
    public async Task Print_SendsExactlyTheTickedRecords_InDataOrder()
    {
        await Load(5);
        Tick(Data, 1, 2, 4);                            // records 2, 3 and 5
        var dialog = Dialog();
        await dialog.PrintCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Item 2", "Item 3", "Item 5" }, Items(_fake.Printer.ZplJobs.Select(j => j.Zpl)));
        Assert.Empty(_fake.Printer.Printed);            // the single-label path was not used
        Assert.Contains("3 selected records (of 5)", _fake.Confirms.Single());
        Assert.False(dialog.ResultIsError);
        Assert.Contains("3 labels", dialog.ResultText);
        Assert.False(dialog.IsBusy);
        dialog.Detach();
    }

    [Fact]
    public async Task Print_TheDialogsCopies_AreTheCopiesOfEachRecordsLabel_AndTheOpenLabelIsUntouched()
    {
        await Load(4);
        int original = _main.Session.Document.Print.Quantity;
        Tick(Data, 0, 3);
        var dialog = Dialog();
        dialog.CopiesText = "3";
        await dialog.PrintCommand.ExecuteAsync(null);

        string zpl = string.Concat(_fake.Printer.ZplJobs.Select(j => j.Zpl));
        Assert.Equal(2, Regex.Matches(zpl, @"\^PQ3,").Count);        // each record's label asks for 3 copies
        Assert.Contains("6 labels counting copies", _fake.Confirms.Single());
        Assert.Equal(original, _main.Session.Document.Print.Quantity);
        dialog.Detach();
    }

    [Fact]
    public async Task Print_WhenTheDataWindowSetsCopiesItself_ThatSettingWins_AndTheDialogSaysSo()
    {
        await Load(3);
        Data.CopiesSource = CopiesSource.Fixed;
        Data.FixedCopiesText = "4";
        var dialog = Dialog();
        Assert.True(dialog.CopiesFromData);
        dialog.CopiesText = "2";
        await dialog.PrintCommand.ExecuteAsync(null);
        string zpl = string.Concat(_fake.Printer.ZplJobs.Select(j => j.Zpl));
        Assert.Equal(3, Regex.Matches(zpl, @"\^PQ4,").Count);
        dialog.Detach();
    }

    [Fact]
    public async Task Print_SerialNumbers_AreNumberedByPositionAmongTheTickedRecords()
    {
        var store = new MemoryCounterStore();
        _fake.Variables = new SerialVariables(store);
        var main = Build(_fake);
        main.Session.Edit("Undo.Property", d =>
        {
            d.EnsureId();
            d.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
            d.Elements.Add(new TextElement { Text = "{{serial}} Item {{n}}" });
        });
        Assert.True(await main.Data.LoadAsync(Csv(5, "serial.csv")));
        Tick(main.Data, 1, 3, 4);                       // records 2, 4 and 5

        var dialog = Dialog(main);
        await dialog.PrintCommand.ExecuteAsync(null);
        string zpl = string.Concat(_fake.Printer.ZplJobs.Select(j => j.Zpl));
        Assert.Contains("SN0001 Item 2", zpl);
        Assert.Contains("SN0002 Item 4", zpl);
        Assert.Contains("SN0003 Item 5", zpl);
        Assert.Equal(3, store.Get(CounterKey.For(main.Session.Document.Id!, "serial"))!.Next);   // three numbers used, not five
        dialog.Detach();
    }

    [Fact]
    public async Task Print_WhenNothingIsTicked_IsBlocked_WithAReason_AndNothingIsSent()
    {
        await Load(3);
        Tick(Data);
        var dialog = Dialog();
        Assert.False(dialog.CanPrint);
        Assert.True(dialog.HasRecordsBlockedText);
        Assert.Contains("No record is ticked", dialog.RecordsBlockedText);

        await dialog.PrintCommand.ExecuteAsync(null);   // even when forced, nothing leaves
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.Empty(_fake.Printer.Printed);
        Assert.Empty(_fake.Confirms);
        Assert.True(dialog.ResultIsError);
        Assert.Contains("No record is ticked", dialog.ResultText);

        Data.VisibleRows[0].IsSelected = true;          // ticking one record lifts the block again
        Assert.True(dialog.CanPrint);
        Assert.False(dialog.HasRecordsBlockedText);
        dialog.Detach();
    }

    [Fact]
    public async Task Print_WhenTheOperatorDeclinesTheQuestion_NothingIsSent()
    {
        await Load(3);
        _fake.ConfirmAnswer = false;
        var dialog = Dialog();
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Single(_fake.Confirms);
        Assert.Empty(_fake.Printer.ZplJobs);
        dialog.Detach();
    }

    [Fact]
    public async Task PrintWithoutData_StillPrintsTheOneLabel_AsBefore()
    {
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "Item" }));
        var dialog = Dialog(withRecords: false);
        dialog.CopiesText = "2";
        await dialog.PrintCommand.ExecuteAsync(null);
        var call = Assert.Single(_fake.Printer.Printed);
        Assert.Equal(2, call.Doc.Print.Quantity);
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.Empty(_fake.Confirms);                   // no batch question for a single label
    }

    // ------------------------------------------------------------------------------------ save as ZPL

    [Fact]
    public async Task SaveZpl_ContainsOnlyTheTickedRecords()
    {
        await Load(5);
        Tick(Data, 0, 4);
        string path = Path.Combine(_dir, "ticked.zpl");
        _fake.ExportPath = path;
        var dialog = Dialog();
        await dialog.SaveZplCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Item 1", "Item 5" }, Items(new[] { File.ReadAllText(path) }));
        Assert.False(dialog.ResultIsError);
        Assert.Contains("2 labels", dialog.ResultText);
        dialog.Detach();
    }

    [Fact]
    public async Task SaveZpl_WhenNothingIsTicked_WritesNothing()
    {
        await Load(3);
        Tick(Data);
        string path = Path.Combine(_dir, "none.zpl");
        _fake.ExportPath = path;
        var dialog = Dialog();
        await dialog.SaveZplCommand.ExecuteAsync(null);
        Assert.False(File.Exists(path));
        Assert.True(dialog.ResultIsError);
        dialog.Detach();
    }

    // ------------------------------------------------------------------------------------ the Data window keeps working

    [Fact]
    public async Task TheDataWindowsOwnPrintButton_StillWorks_WhileTheDialogIsOpen()
    {
        await Load(4);
        Tick(Data, 0, 1);
        var dialog = Dialog();
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Item 1", "Item 2" }, Items(_fake.Printer.ZplJobs.Select(j => j.Zpl)));
        Assert.Contains("2 labels", Data.ResultText);
        dialog.Detach();
    }

    [Fact]
    public async Task ADetachedDialog_StopsListeningToTheData_SoItCanBeCollected()
    {
        await Load(3);
        var dialog = Dialog();
        var raised = new List<string?>();
        dialog.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Data.VisibleRows[0].IsSelected = false;
        Assert.Contains(nameof(PrintViewModel.SelectionLine), raised);

        dialog.Detach();
        raised.Clear();
        Data.VisibleRows[0].IsSelected = true;
        Assert.Empty(raised);
    }

    // ------------------------------------------------------------------------------------ hosted window

    private sealed class SerialVariables(MemoryCounterStore store) : IVariableServices
    {
        public ICounterStore? CounterStore => store;
        public IPromptMemory PromptMemory { get; } = new MemoryPromptMemory();
        public IReadOnlyDictionary<string, string>? Ask(PromptSet set) => new Dictionary<string, string>();
    }

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

    /// <summary>
    /// Remembers the subscribers of <see cref="Loc"/>'s events so a hosted test can undo what its view-models added. A window bound to a printer
    /// list creates a collection view owned by the WPF host thread; if the view-model kept reacting to later language switches (from another
    /// thread) it would change that list from the wrong thread and fail unrelated tests.
    /// </summary>
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

    [Fact]
    public async Task APrintWindowWithData_Builds_ShowsTheRecordsList_InsideABoundedFrame()
    {
        // The view-models are built here, not on the WPF host thread, and only hosted over there; their language subscriptions are undone afterwards.
        var snapshot = new LanguageEventSnapshot();
        try
        {
            var main = Build(_fake);
            await Load(60, main: main);
            var dialog = Dialog(main);
            var result = WpfTestHost.Run(() =>
            {
                var window = new PrintWindow(dialog);
                var content = (FrameworkElement)window.Content;
                var size = new Size(720, 2400);
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                var list = Descendants<DataRecordsView>(content).SingleOrDefault();
                var measured = (Found: list is not null, Height: list?.ActualHeight ?? 0, Rows: list?.Grid.Items.Count ?? 0);
                BindingOperations.ClearAllBindings(window);
                window.DataContext = null;
                if (list is not null) list.DataContext = null;
                return measured;
            });
            dialog.Detach();
            Assert.True(result.Found, "The Print window shows no records list although data is loaded.");
            Assert.Equal(60, result.Rows);
            Assert.InRange(result.Height, 200, 600);    // fixed region: the list scrolls inside it, the dialog's scroll viewer copes with the rest
        }
        finally { snapshot.Restore(); }
    }

    [Fact]
    public void APrintWindowWithoutData_Builds_AndHidesTheRecordsCard()
    {
        var snapshot = new LanguageEventSnapshot();
        try
        {
            var main = Build(_fake);
            var dialog = Dialog(main, withRecords: false);
            var visible = WpfTestHost.Run(() =>
            {
                var window = new PrintWindow(dialog);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(720, 2400));
                content.Arrange(new Rect(0, 0, 720, 2400));
                content.UpdateLayout();
                var list = Descendants<DataRecordsView>(content).SingleOrDefault();
                bool shown = list is not null && list.IsVisible;
                window.DataContext = null;
                return shown;
            });
            Assert.False(visible);
        }
        finally { snapshot.Restore(); }
    }
}
