using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Rotula.App.Controls;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Variables;
using Rotula.Data;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>
/// The field picker: the list of available fields, inserting and binding fields in the property panel, "Add to label" in the Data window,
/// the missing-column warnings and the legacy data-field element. Uses a CSV for the fake data set and the real SQLite path for the end-to-end run.
/// </summary>
[Collection("App")]
public class AppFieldsTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly MainViewModel _main;

    public AppFieldsTests()
    {
        _fake.Settings.Current.Language = "en";
        _main = new MainViewModel(_fake.Build());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Loc.Instance.SetLanguage("en");
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temp cleanup is best effort */ }
    }

    private FieldsViewModel Fields => _main.Fields;
    private async Task LoadCsv(string text, string name = "parts.csv")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        Assert.True(await _main.Data.LoadAsync(path));
    }

    private TextElement AddAndSelectText(string text)
    {
        var t = new TextElement { Text = text, X = 20, Y = 20 };
        _main.Commands.Add(t);
        return t;
    }

    private TextRow ContentRow(string key = "Prop.Text") => _main.Properties.Rows.OfType<TextRow>().Single(r => r.LabelKey == key);

    private const string Parts = "Sku,Description,Qty\nA-100,Hex nut,25\nB-200,Washer,40\n";

    // ------------------------------------------------------------------------------------ the list of fields

    [Fact]
    public async Task TheList_FollowsLoadReplaceAndClear_AndRaisesAnEvent()
    {
        int events = 0;
        Fields.FieldsChanged += (_, _) => events++;
        Assert.Empty(Fields.Fields);
        Assert.False(Fields.HasFields);

        await LoadCsv(Parts);
        Assert.Equal(new[] { "Sku", "Description", "Qty" }, Fields.Fields.Select(f => f.Token).ToArray());
        Assert.All(Fields.Fields, f => Assert.Equal(FieldKind.Column, f.Kind));
        Assert.Equal("A-100", Fields.Find("sku")!.Sample);   // case-insensitive lookup, sample from the shown record
        Assert.True(events > 0);

        _main.Data.CurrentIndex = 1;
        Assert.Equal("B-200", Fields.Find("Sku")!.Sample);   // the sample follows the record navigator

        await LoadCsv("Name,Price\nBolt,3\n", "other.csv");
        Assert.Equal(new[] { "Name", "Price" }, Fields.Fields.Select(f => f.Token).ToArray());

        _main.Data.ClearDataCommand.Execute(null);
        Assert.Empty(Fields.Fields);
        Assert.False(Fields.HasData);
    }

    [Fact]
    public async Task TheList_AlsoHoldsCountersAndTheLabelsOwnVariables_WithoutDuplicates()
    {
        _main.Session.Edit("Undo.Property", d => { d.Variables.Add(new LabelVariable("Note", "hello")); d.Variables.Add(new LabelVariable("sku", "dup")); });
        await LoadCsv(Parts);
        _main.Data.AddCounterCommand.Execute(null);
        var tokens = Fields.Fields.Select(f => f.Token).ToArray();
        Assert.Contains("Serial", tokens.Select(t => t.TrimStart('#')));
        Assert.Contains(Fields.Fields, f => f.Kind == FieldKind.Counter && f.Token.StartsWith('#'));
        var note = Fields.Find("Note")!;
        Assert.Equal(FieldKind.Variable, note.Kind);
        Assert.Equal("hello", note.Sample);
        Assert.Single(tokens, t => t.Equals("sku", StringComparison.OrdinalIgnoreCase));  // the column wins over the variable of the same name
        Assert.Equal(FieldKind.Column, Fields.Find("Sku")!.Kind);
    }

    [Fact]
    public async Task ColumnsThatCannotBePlaceholders_GetACleanName_AndAMapping()
    {
        await LoadCsv("Cód. artículo,Unit price,Sku\nA-1,2.5,S1\n");
        var tokens = Fields.Fields.Select(f => f.Token).ToArray();
        Assert.Equal(new[] { "Cod_articulo", "Unit_price", "Sku" }, tokens);
        Assert.All(Fields.Fields.Take(2), f => Assert.True(f.NeedsMapping));
        Assert.All(tokens, t => Assert.True(FieldNaming.IsValidToken(t)));

        var el = AddAndSelectText("Price {{x}}");
        var row = ContentRow();
        row.InsertField(Fields.Find("Unit_price")!, 6, 5);
        Assert.Equal("Price {{Unit_price}}", ((TextElement)_main.Session.Document.Elements.Single(e => e.Id == el.Id)).Text);
        // The match to the real column is stored with the label, and the preview resolves it.
        Assert.Contains(_main.Data.ToSettings().ColumnOverrides, kv => kv.Key == "Unit_price" && kv.Value == "Unit price");
        Assert.Contains("Price 2.5", _main.ZplText);
    }

    [Fact]
    public void ToToken_KeepsGoodNames_CleansTheRest_AndAvoidsClashes()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Name" };
        Assert.Equal("Sku_1", FieldNaming.ToToken("Sku_1", new HashSet<string>()));
        Assert.Equal("Unit_price", FieldNaming.ToToken("Unit price", taken));
        Assert.Equal("Descripcion", FieldNaming.ToToken("Descripción", taken));
        Assert.Equal("_2024_Sales", FieldNaming.ToToken("2024 Sales", taken));
        Assert.Equal("Field", FieldNaming.ToToken("###", taken));
        Assert.Equal("Name2", FieldNaming.ToToken("Name ", taken));   // clashes with an existing name
        Assert.Equal("Sku", FieldNaming.FindBinding(" {{ Sku }} "));
        Assert.Equal("#Serial", FieldNaming.FindBinding("{{#Serial}}"));
        Assert.Null(FieldNaming.FindBinding("LOT {{Lot}}"));
        Assert.Null(FieldNaming.FindBinding("{{a}}{{b}}"));
    }

    // ------------------------------------------------------------------------------------ inserting

    [Fact]
    public async Task InsertField_WritesThePlaceholderAtTheCaret_ReplacesASelection_AndUndoesAsOneStep()
    {
        await LoadCsv(Parts);
        var el = AddAndSelectText("LOT XX end");
        var row = ContentRow();
        int before = _main.Session.Undo.UndoCount;

        int caret = row.InsertField(Fields.Find("Sku")!, 4, 2);   // replaces the selected "XX"
        Assert.Equal("LOT {{Sku}} end", row.Text);
        Assert.Equal(4 + "{{Sku}}".Length, caret);
        Assert.Equal("LOT {{Sku}} end", ((TextElement)_main.Session.Document.Elements.Single()).Text);
        Assert.Equal(before + 1, _main.Session.Undo.UndoCount);
        Assert.Contains("LOT A-100 end", _main.ZplText);          // the canvas and ZPL view show the current record at once

        _main.UndoCommand.Execute(null);
        Assert.Equal("LOT XX end", ((TextElement)_main.Session.Document.Elements.Single()).Text);
        Assert.Equal("LOT XX end", ContentRow().Text);
        Assert.Equal(el.Id, _main.Session.Document.Elements.Single().Id);
    }

    [Fact]
    public async Task InsertField_NeverMergesWithTyping()
    {
        await LoadCsv(Parts);
        AddAndSelectText("a");
        var row = ContentRow();
        row.Text = "ab";                       // typing (merges with other typing within a second)
        row.InsertField(Fields.Find("Qty")!, 2, 0);
        Assert.Equal("ab{{Qty}}", row.Text);
        _main.UndoCommand.Execute(null);
        Assert.Equal("ab", ((TextElement)_main.Session.Document.Elements.Single()).Text);
    }

    [Fact]
    public async Task Barcodes_OfEverySymbology_TakeFieldsInTheirData()
    {
        await LoadCsv(Parts);
        var qr = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "https://example.com" };
        _main.Commands.Add(qr);
        var row = ContentRow("Prop.Data");
        Assert.True(row.IsFieldTarget);
        row.InsertField(Fields.Find("Sku")!, row.Text.Length, 0);
        Assert.Equal("https://example.com{{Sku}}", ((BarcodeElement)_main.Session.Document.Elements.Single()).Data);
        Assert.Contains("A-100", _main.ZplText);
    }

    [Fact]
    public async Task TypingAPlaceholderByHand_StillWorks_CaseInsensitively_AndUnknownNamesStayAsTyped()
    {
        await LoadCsv(Parts);
        AddAndSelectText("x");
        var row = ContentRow();
        row.Text = "{{description}} / {{Nope}}";
        Assert.Equal("{{description}} / {{Nope}}", ((TextElement)_main.Session.Document.Elements.Single()).Text);
        Assert.Contains("Hex nut", _main.ZplText);
        Assert.Contains("Nope", string.Join("|", row.WarningText));
    }

    // ------------------------------------------------------------------------------------ the two-state control

    [Fact]
    public async Task Binding_SetsAPureBinding_Unbinding_GoesBackToFixedText_Both_Undo()
    {
        await LoadCsv(Parts);
        AddAndSelectText("Hello");
        var row = ContentRow();
        Assert.True(row.IsFixed);

        row.BindTo(Fields.Find("Description")!);
        Assert.True(row.IsBound);
        Assert.Equal("Description", row.BoundToken);
        Assert.Equal("{{Description}}", ((TextElement)_main.Session.Document.Elements.Single()).Text);
        Assert.Equal("Hex nut", row.BoundSample);

        row.UnbindCommand.Execute(null);
        Assert.True(row.IsFixed);
        Assert.Equal("Hex nut", ((TextElement)_main.Session.Document.Elements.Single()).Text);   // the shown record's value becomes the fixed text

        _main.UndoCommand.Execute(null);
        Assert.Equal("{{Description}}", ((TextElement)_main.Session.Document.Elements.Single()).Text);
        _main.UndoCommand.Execute(null);
        Assert.Equal("Hello", ((TextElement)_main.Session.Document.Elements.Single()).Text);
    }

    [Fact]
    public async Task MixedContent_IsNotABinding()
    {
        await LoadCsv(Parts);
        AddAndSelectText("LOT {{Sku}}-{{Qty}}");
        Assert.False(ContentRow().IsBound);
        Assert.True(ContentRow().IsFixed);
    }

    // ------------------------------------------------------------------------------------ legacy data-field elements

    [Fact]
    public async Task ALegacyDataField_IsShownAsText_WithTheBadge_AndKeepsPrintingTheSame()
    {
        var legacy = new DataFieldElement { VariableName = "Sku", X = 30, Y = 40, FontHeight = 44 };
        _main.Session.Edit("Undo.Property", d => { d.Variables.Add(new LabelVariable("Sku", "S-0")); d.Elements.Add(legacy); });
        string zplBefore = _main.ZplText;
        _main.Session.Select(legacy);

        var layer = _main.Layers.Single();
        Assert.Equal("Text", layer.TypeTag);
        Assert.True(layer.IsBound);
        Assert.DoesNotContain("Data field", _main.Properties.Title);
        var row = ContentRow();
        Assert.Equal("{{Sku}}", row.Text);
        Assert.True(row.IsBound);

        // With data loaded the output is exactly what it was.
        await LoadCsv(Parts);
        Assert.Contains("A-100", _main.ZplText);
        Assert.NotEqual(zplBefore, _main.ZplText);

        // Rebinding to another single field keeps the element a data field; mixing text turns it into an ordinary text element, same id and place.
        ContentRow().BindTo(Fields.Find("Qty")!);
        Assert.IsType<DataFieldElement>(_main.Session.Document.Elements.Single());
        Assert.Equal("Qty", ((DataFieldElement)_main.Session.Document.Elements.Single()).VariableName);
        ContentRow().InsertField(Fields.Find("Sku")!, 0, 0);
        var now = Assert.IsType<TextElement>(_main.Session.Document.Elements.Single());
        Assert.Equal(legacy.Id, now.Id);
        Assert.Equal((30, 40, 44), (now.X, now.Y, now.FontHeight));
        Assert.Equal("{{Sku}}{{Qty}}", now.Text);
    }

    [Fact]
    public void AnOldFileWithADataField_OpensUnchanged_PrintsTheSame_AndViewingItNeverConvertsIt()
    {
        string path = Path.Combine(_dir, "old.rotula");
        File.WriteAllText(path, """
            {
              "formatVersion": 4,
              "label": { "width": { "value": 100 }, "height": { "value": 70 }, "variables": [ { "name": "Sku", "sampleValue": "S-0" } ] },
              "elements": [
                { "type": "dataField", "id": "d1", "x": 30, "y": 40, "variableName": "Sku", "fontHeight": 44 },
                { "type": "text", "id": "t1", "x": 30, "y": 100, "text": "Part {{Sku}}" }
              ]
            }
            """, new UTF8Encoding(false));
        Assert.True(_main.LoadFile(path));

        var legacy = Assert.IsType<DataFieldElement>(_main.Session.Document.Elements[0]);
        Assert.Equal("Sku", legacy.VariableName);
        Assert.False(_main.Session.IsDirty);
        Assert.Equal(0, _main.Session.Undo.UndoCount);
        var layer = _main.Layers.Single(l => l.Id == "d1");
        Assert.Equal("Text", layer.TypeTag);
        Assert.True(layer.IsBound);

        // Printing: exactly what Core produces for the file as it was read.
        string expected = Rotula.Core.Zpl.ZplGenerator.Generate(RotulaFileSerializer.LoadFile(path).Document).Zpl;
        Assert.Equal(expected, _main.ZplText);
        Assert.Contains("S-0", _main.ZplText);

        // Selecting it and reading the Properties rows changes nothing.
        _main.Session.Select(legacy);
        var row = ContentRow();
        Assert.Equal("{{Sku}}", row.Text);
        Assert.True(row.IsBound);
        Assert.IsType<DataFieldElement>(_main.Session.Document.Elements[0]);
        Assert.False(_main.Session.IsDirty);
        Assert.Equal(0, _main.Session.Undo.UndoCount);
        Assert.Equal(expected, _main.ZplText);
    }

    [Fact]
    public void NewElements_AreNeverDataFieldElements()
    {
        foreach (var item in _main.Toolbox.Where(t => t.Kind != Rotula.App.Editing.ToolboxKind.Image))
            _main.AddElementCommand.Execute(item.Kind);
        Assert.NotEmpty(_main.Session.Document.Elements);
        Assert.DoesNotContain(_main.Session.Document.Elements, e => e is DataFieldElement);
    }

    // ------------------------------------------------------------------------------------ add to the label

    [Fact]
    public async Task AddToLabel_CreatesABoundTextElement_Selected_AndUndoRemovesIt()
    {
        await LoadCsv(Parts);
        var field = Fields.Find("Description")!;
        Fields.AddTextCommand.Execute(field);
        var el = Assert.IsType<TextElement>(_main.Session.Document.Elements.Single());
        Assert.False(el is DataFieldElement);
        Assert.Equal("{{Description}}", el.Text);
        Assert.Equal(new[] { el.Id }, _main.Session.SelectedIds.ToArray());
        Assert.Contains(_main.Session.Document.Variables, v => v.Name == "Description" && v.SampleValue == "Hex nut");   // prints sensibly without data too
        Assert.Contains("Hex nut", _main.ZplText);
        Assert.True(_main.Layers.Single().IsBound);

        _main.UndoCommand.Execute(null);
        Assert.Empty(_main.Session.Document.Elements);
        Assert.DoesNotContain(_main.Session.Document.Variables, v => v.Name == "Description");
    }

    [Fact]
    public async Task AddAsBarcodeAndAsQr_CreateBarcodeElementsWhoseDataIsThePlaceholder()
    {
        await LoadCsv(Parts);
        Fields.AddBarcodeCommand.Execute(Fields.Find("Sku"));
        Fields.AddQrCommand.Execute(Fields.Find("Sku"));
        var code128 = Assert.IsType<BarcodeElement>(_main.Session.Document.Elements[0]);
        var qr = Assert.IsType<BarcodeElement>(_main.Session.Document.Elements[1]);
        Assert.Equal(BarcodeSymbology.Code128, code128.Symbology);
        Assert.Equal(BarcodeSymbology.QrCode, qr.Symbology);
        Assert.Equal("{{Sku}}", code128.Data);
        Assert.Equal("{{Sku}}", qr.Data);
        Assert.True(qr.Y > code128.Y);   // the second one goes below the first, not on top of it
        Assert.Contains("A-100", _main.ZplText);
    }

    [Fact]
    public async Task AddAll_StacksTheColumnsWithAnEvenGap_InsideTheLabel_InOneUndoStep()
    {
        await LoadCsv(Parts);
        int steps = _main.Session.Undo.UndoCount;
        var result = Fields.AddAll();
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Outside);
        Assert.Equal(steps + 1, _main.Session.Undo.UndoCount);
        var els = _main.Session.Document.Elements.Cast<TextElement>().ToList();
        Assert.Equal(new[] { "{{Sku}}", "{{Description}}", "{{Qty}}" }, els.Select(e => e.Text).ToArray());
        Assert.Equal(els.Select(e => e.Id).OrderBy(x => x), _main.Session.SelectedIds.OrderBy(x => x));
        var gaps = els.Zip(els.Skip(1), (a, b) => b.Y - a.Y).Distinct().ToList();
        Assert.Single(gaps);   // the same step between every pair
        int limit = _main.Session.Document.HeightDots;
        Assert.All(els, e => Assert.InRange(e.Y + (e.FontHeight ?? 30), 0, limit));

        _main.UndoCommand.Execute(null);
        Assert.Empty(_main.Session.Document.Elements);
    }

    [Fact]
    public async Task AddAll_OnASmallLabel_ShrinksTheText_AndWarnsWhenItStillDoesNotFit()
    {
        _main.Session.Edit("Undo.LabelSize", d => { d.Width = Measure.Dots(400); d.Height = Measure.Dots(200); });
        var header = string.Join(",", Enumerable.Range(1, 40).Select(i => "Col" + i));
        await LoadCsv(header + "\n" + string.Join(",", Enumerable.Range(1, 40).Select(i => "v" + i)) + "\n");
        var result = Fields.AddAll();
        Assert.Equal(40, result.Added);
        Assert.True(result.Outside > 0);
        Assert.Single(_fake.Infos);
        Assert.Contains("do not fit", _fake.Infos[0].Message);

        // A handful of fields on the same small label fits by using a smaller font, and says nothing.
        _fake.Infos.Clear();
        _main.UndoCommand.Execute(null);
        await LoadCsv("A,B,C,D,E\n1,2,3,4,5\n", "five.csv");
        var fit = Fields.AddAll();
        Assert.Equal(0, fit.Outside);
        Assert.Empty(_fake.Infos);
        Assert.All(_main.Session.Document.Elements.Cast<TextElement>(), e => Assert.InRange(e.Y + (e.FontHeight ?? 30), 0, 200));
    }

    [Fact]
    public async Task UseHere_PutsTheFieldIntoTheSelectedElement_ReplacingSampleText_AppendingToRealText()
    {
        await LoadCsv(Parts);
        Assert.False(Fields.UseHereCommand.CanExecute(Fields.Find("Sku")));    // nothing selected
        var sample = AddAndSelectText(Loc.Get("Element.DefaultText"));
        Assert.True(Fields.UseHereCommand.CanExecute(Fields.Find("Sku")));
        Fields.UseHereCommand.Execute(Fields.Find("Sku"));
        Assert.Equal("{{Sku}}", ((TextElement)_main.Session.Document.Elements.Single(e => e.Id == sample.Id)).Text);

        var real = AddAndSelectText("Part:");
        Fields.UseHereCommand.Execute(Fields.Find("Qty"));
        Assert.Equal("Part:{{Qty}}", ((TextElement)_main.Session.Document.Elements.Single(e => e.Id == real.Id)).Text);
        _main.UndoCommand.Execute(null);
        Assert.Equal("Part:", ((TextElement)_main.Session.Document.Elements.Single(e => e.Id == real.Id)).Text);

        _main.Session.Select(null);
        Assert.False(Fields.UseHereCommand.CanExecute(Fields.Find("Sku")));
        Assert.Equal(Loc.Get("Fields.Selected.None"), Fields.SelectionText);
    }

    // ------------------------------------------------------------------------------------ missing columns, disabled state

    [Fact]
    public async Task AFieldWithoutAColumn_GetsAQuietWarning_InTheObjectsListAndThePanel_OnlyWhileDataIsLoaded()
    {
        var el = AddAndSelectText("{{Sku}} {{Missing}}");
        Assert.False(_main.Layers.Single().HasMissing);          // no data: nothing can be missing
        Assert.Equal(string.Empty, ContentRow().WarningText);

        await LoadCsv(Parts);
        var layer = _main.Layers.Single();
        Assert.True(layer.HasMissing);
        Assert.Equal("No column called Missing in the loaded data", layer.MissingText);
        Assert.True(layer.IsBound);
        Assert.Equal("No column called Missing in the loaded data", ContentRow().WarningText);

        // Fix the name: the marker goes away without touching anything else.
        ContentRow().Text = "{{Sku}} {{Qty}}";
        Assert.False(_main.Layers.Single().HasMissing);
        Assert.Equal(string.Empty, ContentRow().WarningText);

        ContentRow().Text = "{{Sku}} {{Gone}}";
        Assert.True(_main.Layers.Single().HasMissing);
        _main.Data.ClearDataCommand.Execute(null);               // clearing the data clears the marker
        Assert.False(_main.Layers.Single().HasMissing);
        Assert.Equal(el.Id, _main.Layers.Single().Id);
    }

    [Fact]
    public async Task NamesTheLabelDefinesItself_AreNeverReportedMissing()
    {
        _main.Session.Edit("Undo.Property", d => d.Variables.Add(new LabelVariable("Today") { Kind = VariableKind.Date, Date = new DateVariableOptions() }));
        AddAndSelectText("{{Today}}");
        await LoadCsv(Parts);
        Assert.False(_main.Layers.Single().HasMissing);
    }

    [Fact]
    public async Task WithoutData_TheControlIsDisabled_AndExplainsHow_ThenEnablesWhenDataLoads()
    {
        Assert.False(Fields.HasFields);
        Assert.True(Fields.HasNoData);
        Assert.Equal("Load data first: Printer > Data merge", Fields.InsertTip);
        Assert.False(Fields.AddAllFieldsCommand.CanExecute(null));
        Fields.OpenDataCommand.Execute(null);
        Assert.NotNull(_fake.ToolDialogs.LastData);               // the "Open Data merge" link opens the window

        await LoadCsv(Parts);
        Assert.True(Fields.HasFields);
        Assert.False(Fields.HasNoData);
        Assert.NotEqual("Load data first: Printer > Data merge", Fields.InsertTip);
        Assert.True(Fields.AddAllFieldsCommand.CanExecute(null));
        Assert.Equal("3 fields available", Fields.CountText);
        Loc.Instance.SetLanguage("es");
        Assert.Equal("3 campos disponibles", Fields.CountText);
    }

    // ------------------------------------------------------------------------------------ autocomplete rules

    [Fact]
    public void Autocomplete_FindsTheOpenBraces_AndFiltersTheFields()
    {
        Assert.Equal((4, ""), FieldBox.FindContext("abc {{", 6));
        Assert.Equal((0, "Sk"), FieldBox.FindContext("{{Sk", 4));
        Assert.Null(FieldBox.FindContext("{{Sku}} and", 11));          // already closed
        Assert.Null(FieldBox.FindContext("{{ sku", 6));                // a space cannot be part of a name
        Assert.Null(FieldBox.FindContext("plain", 5));
        Assert.Null(FieldBox.FindContext("{{a\nb", 5));
        var items = new[] { new FieldItem("Sku", "Sku", FieldKind.Column), new FieldItem("Description", "Description", FieldKind.Column), new FieldItem("Unit_sku", "Unit sku", FieldKind.Column) };
        Assert.Equal(new[] { "Sku", "Unit_sku" }, FieldBox.Match(items, "sku").Select(f => f.Token).ToArray());   // names that start with it come first
        Assert.Equal(3, FieldBox.Match(items, "").Count);
        Assert.Empty(FieldBox.Match(items, "zzz"));
    }

    // ------------------------------------------------------------------------------------ strings

    [Fact]
    public void EveryFieldsString_ExistsInBothLanguages_AndIsTranslated()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rotula.sln"))) root = root.Parent;
        Dictionary<string, string> Read(string lang) => XDocument.Load(Path.Combine(root!.FullName, "src", "Rotula.App", "Resources", $"Strings.{lang}.resx")).Root!
            .Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
        var en = Read("en");
        var es = Read("es");
        var keys = en.Keys.Where(k => k.StartsWith("Fields.") || k is "Undo.InsertField" or "Undo.AddFields" or "Undo.UnbindField").ToList();
        Assert.True(keys.Count > 40);
        foreach (var k in keys)
        {
            Assert.True(es.ContainsKey(k), k);
            Assert.False(string.IsNullOrWhiteSpace(es[k]), k);
        }
        Assert.Equal("Add as barcode", en["Fields.AddAsBarcode"]);
        Assert.Equal("Añadir como código QR", es["Fields.AddAsQr"]);
    }

    // ------------------------------------------------------------------------------------ the real SQLite path

    private sealed class PlainProtector2 : ISecretProtector
    {
        public byte[] Protect(byte[] data, byte[] entropy) => data.Reverse().ToArray();
        public byte[] Unprotect(byte[] protectedData, byte[] entropy) => protectedData.Reverse().ToArray();
    }

    [Fact]
    public async Task SqliteEndToEnd_LoadATable_InsertAField_PreviewResolves_UndoRestores_AddToLabelWorks()
    {
        string db = Path.Combine(_dir, "parts.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Parts (Sku TEXT, Description TEXT, Qty INTEGER);
                INSERT INTO Parts VALUES ('A-100', 'Tuerca hexagonal', 25);
                INSERT INTO Parts VALUES ('B-200', 'Arandela', 40);
                """;
            cmd.ExecuteNonQuery();
        }
        var store = new JsonConnectionStore(Path.Combine(_dir, "connections.json"), new PlainProtector2());
        await store.SaveAsync(new DatabaseConnectionSettings { Name = "Parts DB", Provider = DatabaseProvider.Sqlite, FilePath = db }, null);
        var main = new MainViewModel(_fake.Build() with { Database = new DatabaseServices(store) });
        var data = main.Data;
        var picker = data.Database;

        data.IsDatabaseMode = true;
        await picker.RefreshConnectionsAsync();
        await picker.SelectConnectionAsync("Parts DB");
        picker.SelectedTable = picker.Tables.Single(t => t.Name == "Parts");
        for (int i = 0; i < 500 && picker.IsBusy; i++) await Task.Delay(10);
        await picker.UseDataAsync();
        Assert.True(data.HasData);

        var fields = main.Fields;
        Assert.Equal(new[] { "Sku", "Description", "Qty" }, fields.Fields.Select(f => f.Token).ToArray());
        Assert.Equal("Tuerca hexagonal", fields.Find("Description")!.Sample);

        // Insert into a text element through the property panel row.
        var text = new TextElement { Text = "Item: " };
        main.Commands.Add(text);
        var row = main.Properties.Rows.OfType<TextRow>().Single(r => r.LabelKey == "Prop.Text");
        row.InsertField(fields.Find("Description")!, 6, 0);
        Assert.Equal("Item: {{Description}}", ((TextElement)main.Session.Document.Elements.Single()).Text);
        Assert.Contains("Item: Tuerca hexagonal", main.ZplText);           // the preview resolves the sample record
        Assert.Equal("Tuerca hexagonal", main.Preview.Data!["Description"]);

        data.CurrentIndex = 1;
        Assert.Contains("Item: Arandela", main.ZplText);

        main.UndoCommand.Execute(null);
        Assert.Equal("Item: ", ((TextElement)main.Session.Document.Elements.Single()).Text);

        // Add to label, then everything.
        fields.AddTextCommand.Execute(fields.Find("Sku"));
        var added = (TextElement)main.Session.Document.Elements[^1];
        Assert.Equal("{{Sku}}", added.Text);
        Assert.Contains("B-200", main.ZplText);
        fields.AddAll();
        Assert.Equal(5, main.Session.Document.Elements.Count);
        Assert.Equal(0, main.Layers.Count(l => l.HasMissing));
    }
}
