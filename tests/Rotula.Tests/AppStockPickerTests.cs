using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Model;
using Rotula.Core.Stocks;
using Rotula.Core.Validation;
using Rotula.Rendering.View;

namespace Rotula.Tests;

/// <summary>Scriptable fakes of the stock picker: file picker and dialogs.</summary>
internal sealed class FakeStockDialogs : IStockDialogs
{
    public bool ConfirmImport = true;
    public StockImportPreviewViewModel? LastPreview;
    public string? AboutTitle, AboutBody;
    public bool ShowImportPreview(StockImportPreviewViewModel preview) { LastPreview = preview; return ConfirmImport; }
    public void ShowAbout(string title, string body) { AboutTitle = title; AboutBody = body; }
}

/// <summary>A file service that answers the supplies-list picker with a scripted path.</summary>
internal sealed class FakeListPicker : IFileDialogService
{
    public string? ListPath, FolderPath;
    public string? PickOpenLabel() => null;
    public string? PickSaveLabel(string suggestedFileName) => null;
    public string? PickExportFile(ExportKind kind, string suggestedFileName) => null;
    public string? PickOpenZpl() => null;
    public string? PickOpenImage() => null;
    public string? PickOpenData() => null;
    public string? PickOpenDatabaseFile() => null;
    public string? PickOpenFile(string title, IReadOnlyList<string> extensions) => ListPath;
    public string? PickFolder(string title) => FolderPath;
}

/// <summary>
/// The stock picker of the New label dialog, driven through its view-model with the real bundled catalogue and a temporary user folder:
/// search, filters, favourites and recent stocks that persist in the settings, applying a stock with its warnings, a keyboard-only flow,
/// saving a custom size as a stock and importing a supplies list.
/// </summary>
[Collection("App")]
public class AppStockPickerTests : IDisposable
{
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly FileStockStore _store;
    private readonly AppSettings _settings = new();
    private readonly FakeStockDialogs _dialogs = new();
    private readonly FakeListPicker _files = new();
    private int _saves;

    public AppStockPickerTests()
    {
        Loc.Instance.SetLanguage("en");
        _store = new FileStockStore(Path.Combine(_dir, "stocks"));
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private NewLabelContext Context(PrinterDpi? dpi = null, bool fromPrinter = false) => new()
    {
        Stocks = _store, Settings = _settings, SaveSettings = () => _saves++, DefaultDpi = dpi, DpiFromPrinter = fromPrinter, Files = _files,
    };

    private async Task<NewLabelViewModel> OpenAsync(PrinterDpi? dpi = null, bool fromPrinter = false)
    {
        var vm = new NewLabelViewModel(Context(dpi, fromPrinter), _dialogs);
        await vm.LoadCatalogAsync();
        return vm;
    }

    private static StockEntry RealStock(Func<StockEntry, bool>? where = null) =>
        StockCatalog.LoadBundled().Entries.First(e => e.Verified && e.PartNumber is { Length: >= 5 } && e.HasFixedSize && (where?.Invoke(e) ?? true));

    // ------------------------------------------------------------------------------------ the starting list

    [Fact]
    public async Task TheStartingList_HasTheFiveCommonSizesAndCustomSizeAtTheTop_AndNothingElseSelectedByDefault()
    {
        var vm = await OpenAsync();
        var common = vm.Items.Where(i => i.GroupName == "Common sizes").ToList();
        Assert.Equal(6, common.Count);
        Assert.Equal(StockItemKind.Custom, common[^1].Kind);
        Assert.Equal("Custom size...", common[^1].Name);
        Assert.Equal("4 x 6 in", vm.SelectedItem!.Name);
        Assert.False(vm.ShowStockDetail);
        Assert.True(vm.CatalogCount > 100);
        // Create right away still gives the 4 x 6 in label that the dialog always offered first.
        var doc = vm.TryCreate()!;
        Assert.Equal(Measure.Inches(4), doc.Width);
        Assert.Equal(Measure.Inches(6), doc.Height);
    }

    [Fact]
    public async Task ACommonSize_ShowsItsSizeInTheOtherUnit()
    {
        var vm = await OpenAsync();
        Assert.Equal("101.6 x 152.4 mm", vm.Items.First(i => i.Name == "4 x 6 in").Subtitle);
        Assert.Equal("3.937 x 5.906 in", vm.Items.First(i => i.Name == "100 x 150 mm").Subtitle);
    }

    // ------------------------------------------------------------------------------------ search

    [Fact]
    public async Task APartNumberFromTheCatalogue_FindsItsStock_AsTheFirstResult()
    {
        var vm = await OpenAsync();
        var wanted = RealStock();
        vm.SearchText = wanted.PartNumber!;
        vm.RunSearch();
        Assert.Equal(wanted.Id, vm.Items[0].Stock!.Id);
        Assert.Equal(wanted.Id, vm.SelectedItem!.Stock!.Id);                     // Enter would create exactly this one
        Assert.True(vm.ShowStockDetail);
        Assert.Equal(wanted.Name, vm.StockTitle);
    }

    [Fact]
    public async Task TheStockWithTheOwnersExamplePartNumber_IsFound_ByTheWholeNumberAndByAPartOfIt()
    {
        var vm = await OpenAsync();
        vm.SearchText = "10010041";
        vm.RunSearch();
        Assert.Contains(vm.Items, i => i.Stock?.PartNumber == "10010041");
        vm.SearchText = "100100";
        vm.RunSearch();
        Assert.Contains(vm.Items, i => i.Stock?.PartNumber == "10010041");
        Assert.True(vm.IsSearching);
    }

    [Theory]
    [InlineData("4x6", 101.6, 152.4)]
    [InlineData("4 x 6 in", 101.6, 152.4)]
    [InlineData("6x4", 101.6, 152.4)]
    [InlineData("102x152", 102, 152)]
    [InlineData("100x150mm", 100, 150)]
    [InlineData("100 x 50 mm", 100, 50)]
    public async Task ASizeInManySpellings_FindsStocksOfThatSize(string query, double wantedW, double wantedH)
    {
        var vm = await OpenAsync();
        vm.SearchText = query;
        vm.RunSearch();
        var hits = vm.Items.Where(i => i.IsStock && i.GroupName == "Results").ToList();
        Assert.NotEmpty(hits);
        foreach (var h in hits)
        {
            double w = h.Stock!.WidthMm, ht = h.Stock.HeightMm!.Value;
            // Either orientation, within the catalogue rounding.
            Assert.True(Math.Abs(w - wantedW) < 1 && Math.Abs(ht - wantedH) < 1 || Math.Abs(w - wantedH) < 1 && Math.Abs(ht - wantedW) < 1, h.Stock.Name);
        }
    }

    [Fact]
    public async Task AFamilyWord_FindsStocksByName()
    {
        var vm = await OpenAsync();
        vm.SearchText = "wristband";
        vm.RunSearch();
        Assert.NotEmpty(vm.Items);
        Assert.Contains(vm.Items, i => i.Stock?.Category == "wristband");
    }

    [Fact]
    public async Task NothingFound_SaysSoInPlainLanguage_AndLeavesNothingSelected()
    {
        var vm = await OpenAsync();
        vm.SearchText = "qqqqzzzz-no-such-stock";
        vm.RunSearch();
        Assert.True(vm.HasNoResults);
        Assert.Contains("No stock matches", vm.ResultSummary);
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public async Task ASizeThatNothingMatchesExactly_OffersCloseSizesBelow()
    {
        var vm = await OpenAsync();
        vm.SearchText = "100x149.2mm";
        vm.RunSearch();
        Assert.NotEmpty(vm.Items);
        Assert.Contains(vm.Items, i => i.GroupName == "Results" || i.GroupName == "Close sizes");
    }

    // ------------------------------------------------------------------------------------ filters

    [Fact]
    public async Task Filters_NarrowTheList()
    {
        var vm = await OpenAsync();
        vm.VerifiedOnly = true;
        Assert.All(vm.Items, i => Assert.True(i.Stock!.Verified));
        vm.VerifiedOnly = false;

        vm.UnitFilter = 2;                                        // quoted in millimetres
        Assert.NotEmpty(vm.Items);
        Assert.All(vm.Items, i => Assert.Equal(StockUnit.Millimeters, i.Stock!.NativeUnit));
        vm.UnitFilter = 0;

        vm.LayoutFilter = 3;                                      // round
        Assert.All(vm.Items, i => Assert.Equal(StockLayout.Round, i.Stock!.Layout));
        vm.LayoutFilter = 0;

        string brand = vm.BrandChoices.First(b => b.Value.Length > 0).Value;
        vm.BrandFilter = brand;
        Assert.NotEmpty(vm.Items);
        Assert.All(vm.Items, i => Assert.Equal(brand, i.Stock!.Brand, ignoreCase: true));
        Assert.True(vm.BrandChoices.Count > 2);
    }

    [Fact]
    public async Task AListLongerThanTheLimit_SaysHowManyMoreThereAre()
    {
        var vm = await OpenAsync();
        vm.UnitFilter = 1;                                        // thousands of stocks match "inches" with an empty query
        Assert.Equal(NewLabelViewModel.MaxListed, vm.Items.Count);
        Assert.Contains("Showing 300 of", vm.ResultSummary);
    }


    [Fact]
    public async Task TheFamilyFilter_NarrowsToOneProductFamily()
    {
        var vm = await OpenAsync();
        Assert.True(vm.FamilyChoices.Count > 5);
        string family = vm.FamilyChoices.First(c => c.Value.Length > 0).Value;
        vm.FamilyFilter = family;
        Assert.NotEmpty(vm.Items);
        string key = new string(family.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        Assert.All(vm.Items, i =>
        {
            string have = new string(((i.Stock!.StockFamily ?? "") + (i.Stock.Series ?? "")).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            Assert.Contains(key, have);
        });
        Assert.True(vm.IsSearching);
        vm.FamilyFilter = string.Empty;
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task TheSelection_SurvivesTheListBeingRebuilt_EvenWhenTheListPushesItsOwnChoiceBack()
    {
        var vm = await OpenAsync();
        var stock = RealStock(e => e.Format == StockFormat.Roll);
        vm.SearchText = stock.PartNumber!;
        vm.RunSearch();
        vm.ToggleFavoriteCommand.Execute(null);                      // the list is rebuilt around the star
        Assert.Equal(stock.Id, vm.SelectedStock!.Id);
        vm.SearchText = string.Empty;
        vm.RunSearch();                                              // back to the starting lists, where the starred stock now has a row
        Assert.Equal(stock.Id, vm.SelectedStock!.Id);
        Assert.Equal("Favourites", vm.SelectedItem!.GroupName);

        // A ListBox that rebinds resets its selection first; the row to keep must already be remembered by then.
        vm.SelectedItem = null;
        vm.ToggleFavoriteCommand.Execute(vm.Items.First(i => i.IsStock));
        vm.RunSearch();
        Assert.NotNull(vm.SelectedItem);
    }

    [Fact]
    public async Task TheResolutionNote_SaysWhereTheValueCameFrom_AndNeverClaimsAPrinterThatWasNotRead()
    {
        var fromPrinter = new NewLabelViewModel(Context(PrinterDpi.Dpi300, fromPrinter: true), _dialogs);
        Assert.Contains("from your printer", fromPrinter.DpiNote, StringComparison.OrdinalIgnoreCase);

        var ctx = new NewLabelContext { Stocks = _store, Settings = _settings, SaveSettings = () => { }, DefaultDpi = PrinterDpi.Dpi600, DpiFromLastUse = true };
        var fromSettings = new NewLabelViewModel(ctx, _dialogs);
        Assert.Contains("last print settings", fromSettings.DpiNote);
        Assert.DoesNotContain("printer (", fromSettings.DpiNote);

        var none = new NewLabelViewModel(new NewLabelContext { Stocks = _store, Settings = _settings }, _dialogs);
        Assert.Equal(string.Empty, none.DpiNote);
        await Task.CompletedTask;
    }

    [Fact]
    public void ALabelSizedByHand_NoLongerClaimsToBeAStock()
    {
        var doc = new LabelDocument { StockId = RealStock().Id };
        var session = new Rotula.App.Editing.EditorSession(doc);
        var panel = new PropertyPanelViewModel(session);
        var width = panel.Rows.OfType<DoubleRow>().Single(r => r.LabelKey == "Prop.LabelWidth");
        width.Text = "60";
        Assert.Null(session.Document.StockId);
        Assert.Equal(string.Empty, panel.Rows.OfType<NoteRow>().Single(r => r.LabelKey == "Prop.Stock").Text);   // the line goes quiet
        session.Undo.Undo();
        Assert.NotNull(session.Document.StockId);                         // undo brings the stock back with the size
    }

    // ------------------------------------------------------------------------------------ favourites and recent

    [Fact]
    public async Task Favourites_ArePersistedInTheSettings_AndComeBackInANewDialog()
    {
        var vm = await OpenAsync();
        var stock = RealStock();
        vm.SearchText = stock.PartNumber!;
        vm.RunSearch();
        int saves = _saves;
        vm.ToggleFavoriteCommand.Execute(null);                    // the selected stock
        Assert.True(vm.IsSelectedFavorite);
        Assert.Contains(stock.Id, _settings.StockFavorites);
        Assert.True(_saves > saves);

        var again = await OpenAsync();
        Assert.Contains(again.Items, i => i.GroupName == "Favourites" && i.Stock!.Id == stock.Id);
        again.FavoritesOnly = true;
        Assert.Equal(stock.Id, Assert.Single(again.Items).Stock!.Id);

        again.FavoritesOnly = false;
        again.SearchText = stock.PartNumber!;
        again.RunSearch();
        again.ToggleFavoriteCommand.Execute(null);                 // un-star
        Assert.DoesNotContain(stock.Id, _settings.StockFavorites);
    }

    [Fact]
    public async Task TheStockThatWasUsed_IsListedAsRecent_NextTime()
    {
        var vm = await OpenAsync();
        var stock = RealStock(e => e.Format == StockFormat.Roll);
        vm.SearchText = stock.PartNumber!;
        vm.RunSearch();
        Assert.NotNull(vm.TryCreate());
        Assert.Equal(stock.Id, _settings.StockRecent[0]);

        var again = await OpenAsync();
        Assert.Contains(again.Items, i => i.GroupName == "Recent" && i.Stock!.Id == stock.Id);
    }

    // ------------------------------------------------------------------------------------ applying

    [Fact]
    public async Task ApplyingAStock_SetsSizeMediaAndStockId_AtTheChosenResolution()
    {
        var vm = await OpenAsync();
        var stock = RealStock(e => e.Media == StockMedia.DirectThermal && e.Format == StockFormat.Roll);
        vm.SearchText = stock.PartNumber!;
        vm.RunSearch();
        vm.Dpi = PrinterDpi.Dpi300;
        var doc = vm.TryCreate()!;
        Assert.Equal(stock.Id, doc.StockId);
        Assert.Equal(PrinterDpi.Dpi300, doc.Dpi);
        Assert.Equal(MediaType.DirectThermal, doc.MediaType);
        Assert.Equal(stock.WidthMm, UnitConverter.Convert(doc.Width.Value, doc.Width.Unit, LengthUnit.Millimeters, (int)doc.Dpi), 0.6);
        Assert.Equal(stock.HeightMm!.Value, UnitConverter.Convert(doc.Height.Value, doc.Height.Unit, LengthUnit.Millimeters, (int)doc.Dpi), 0.6);
    }

    [Fact]
    public async Task TheWarningsOfAStock_AreShownBeforeCreating_NamingTheStockNotItsId()
    {
        var vm = await OpenAsync();
        var stock = RealStock(e => e.Format == StockFormat.Roll && e.GapMm is null);
        vm.SearchText = stock.PartNumber!;
        vm.RunSearch();
        Assert.True(vm.HasWarnings);
        string all = string.Join("\n", vm.Warnings);
        Assert.Contains("gap between labels", all);
        Assert.Contains(stock.Name, all);
        Assert.DoesNotContain(stock.Id, all);
        Assert.DoesNotContain("{", all);
        Assert.DoesNotContain("!Issue", all);
    }

    [Fact]
    public async Task AnUnverifiedStock_WarnsThatItsSizeIsNotVerified()
    {
        var vm = await OpenAsync();
        var stock = vm.Catalog.Entries.First(e => !e.Verified && e.HasFixedSize);
        vm.SearchText = stock.PartNumber ?? stock.Name;
        vm.RunSearch();
        vm.SelectedItem = vm.Items.First(i => i.Stock?.Id == stock.Id);
        Assert.Contains(vm.Warnings, w => w.Contains("has not been verified"));
        Assert.False(vm.SelectedItem.IsVerified);
        Assert.True(vm.SelectedItem.IsUnverified);
    }

    [Fact]
    public async Task AContinuousRoll_NeedsTheLabelHeight_BeforeItCanBeCreated()
    {
        var vm = await OpenAsync();
        var roll = vm.Catalog.Entries.FirstOrDefault(e => e.IsContinuous && e.WidthMm > 0);
        if (roll is null) return;                                // the bundled data may hold none; the rule is covered by the applier tests
        vm.SearchText = roll.PartNumber ?? roll.Name;
        vm.RunSearch();
        vm.SelectedItem = vm.Items.First(i => i.Stock?.Id == roll.Id);
        Assert.True(vm.NeedsHeight);
        Assert.Null(vm.TryCreate());
        Assert.Contains("height", vm.Error);
        vm.ContinuousHeightText = "50";
        var doc = vm.TryCreate()!;
        Assert.Equal(roll.Id, doc.StockId);
        Assert.Equal(50, doc.Height.Value);
    }

    [Fact]
    public async Task TheResolution_StartsFromThePrinter_AndTheNoteGoesAwayWhenItIsChanged()
    {
        var vm = await OpenAsync(PrinterDpi.Dpi300, fromPrinter: true);
        Assert.Equal(PrinterDpi.Dpi300, vm.Dpi);
        Assert.Contains("300 dpi", vm.DpiNote);
        vm.Dpi = PrinterDpi.Dpi600;
        Assert.Equal(string.Empty, vm.DpiNote);

        var plain = await OpenAsync();
        Assert.Equal(PrinterDpi.Dpi203, plain.Dpi);
        Assert.Equal(string.Empty, plain.DpiNote);
    }

    // ------------------------------------------------------------------------------------ keyboard

    [Fact]
    public async Task AKeyboardOnlyFlow_TypeArrowDownEnter_CreatesTheLabelOfTheSecondResult()
    {
        var vm = await OpenAsync();
        vm.SearchText = "4x6";                                   // typing (the window debounces, then runs the search)
        vm.RunSearch();
        var first = vm.SelectedItem!;
        var second = vm.MoveSelection(1)!;                         // Down arrow in the search box
        Assert.NotSame(first, second);
        Assert.Same(second, vm.SelectedItem);
        Assert.Equal(second.Stock!.Id, vm.SelectedStock!.Id);
        var doc = vm.TryCreate()!;                                 // Enter (the default button)
        Assert.Equal(second.Stock.Id, doc.StockId);
        Assert.Same(first, vm.MoveSelection(-1));                  // Up arrow comes back
    }

    [Fact]
    public async Task TheArrowKeys_StopAtTheEndsOfTheList()
    {
        var vm = await OpenAsync();
        vm.MoveSelection(-5);
        Assert.Equal("4 x 6 in", vm.SelectedItem!.Name);
        vm.MoveSelection(500);
        Assert.Equal(StockItemKind.Custom, vm.SelectedItem!.Kind);
        Assert.True(vm.IsCustom);
    }

    // ------------------------------------------------------------------------------------ my own stocks

    [Fact]
    public async Task ACustomSize_CanBeSavedAsMyOwnStock_AndIsFoundByPartNumber()
    {
        var vm = await OpenAsync();
        vm.SelectedPreset = LabelPreset.All[^1];
        vm.Unit = LengthUnit.Millimeters;
        vm.WidthText = 75.ToString(CultureInfo.CurrentCulture);
        vm.HeightText = 45.5.ToString(CultureInfo.CurrentCulture);
        vm.OwnName = "Shelf edge strip";
        vm.OwnPart = "ZZ-7745";
        vm.SaveOwnStockCommand.Execute(null);
        Assert.Equal(string.Empty, vm.Error);
        Assert.Contains("Shelf edge strip", vm.StatusText);

        string file = Path.Combine(_store.UserFolder, FileStockStore.OwnStocksFile);
        Assert.True(File.Exists(file));
        var reloaded = StockCatalog.LoadUser(_store.UserFolder);
        var entry = Assert.Single(reloaded.FindByPartNumber("ZZ7745"));
        Assert.StartsWith("user-", entry.Id);
        Assert.Equal(75, entry.WidthMm, 0.01);
        Assert.Equal(45.5, entry.HeightMm!.Value, 0.01);
        Assert.Equal(StockOrigin.User, entry.Origin);

        // The open dialog finds it at once, and a second one saved later keeps both.
        vm.SearchText = "ZZ-7745";
        vm.RunSearch();
        Assert.Equal(entry.Id, vm.Items[0].Stock!.Id);
        vm.SelectedPreset = LabelPreset.All[^1];
        vm.Unit = LengthUnit.Inches;
        vm.WidthText = 2.ToString(CultureInfo.CurrentCulture);
        vm.HeightText = 3.ToString(CultureInfo.CurrentCulture);
        vm.OwnName = "Second";
        vm.OwnPart = string.Empty;
        vm.SaveOwnStockCommand.Execute(null);
        Assert.Equal(2, StockCatalog.LoadUser(_store.UserFolder).Count);
        var second = StockCatalog.LoadUser(_store.UserFolder).Entries.First(e => e.Name == "Second");
        Assert.Equal(2, second.WidthIn, 0.001);
        Assert.Equal(50.8, second.WidthMm, 0.01);
    }

    [Fact]
    public async Task SavingAnOwnStock_NeedsANameOrAPartNumber_AndASizeThatIsANumber()
    {
        var vm = await OpenAsync();
        vm.SelectedPreset = LabelPreset.All[^1];
        vm.SaveOwnStockCommand.Execute(null);
        Assert.Contains("name or a part number", vm.Error);
        vm.OwnName = "x";
        vm.WidthText = "abc";
        vm.SaveOwnStockCommand.Execute(null);
        Assert.NotEqual(string.Empty, vm.Error);
        Assert.False(File.Exists(Path.Combine(_store.UserFolder, FileStockStore.OwnStocksFile)));
    }

    // ------------------------------------------------------------------------------------ import

    private string WriteList(string text)
    {
        string path = Path.Combine(_dir, "supplies.csv");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task ASuppliesList_IsPreviewedWithCertaintyAndWarnings_AndKeptOnlyWhenConfirmed()
    {
        var vm = await OpenAsync();
        _files.ListPath = WriteList("Part number,Description,Size,Price\nQQ-100,Shipping label,4x6 in,12.50\nQQ-101,Small label,2 x 1 in,3.10\nQQ-102,No size here,,1.00\n");
        _dialogs.ConfirmImport = false;
        vm.ImportListCommand.Execute(null);
        var preview = _dialogs.LastPreview!;
        Assert.Equal(2, preview.Rows.Count);
        Assert.True(preview.CanSave);
        Assert.NotEmpty(preview.Warnings);                         // the price column and the row without a size
        Assert.DoesNotContain(preview.Warnings, w => w.Contains("{") || w.Contains("!Issue"));
        Assert.Equal("Save 2 stocks", preview.SaveText);
        Assert.False(Directory.Exists(_store.UserFolder) && Directory.EnumerateFiles(_store.UserFolder).Any());   // declined: nothing was written

        _dialogs.ConfirmImport = true;
        vm.ImportListCommand.Execute(null);
        var files = Directory.GetFiles(_store.UserFolder, "import-*.json");
        Assert.Single(files);
        Assert.Contains("2 stocks saved", vm.StatusText);
        vm.SearchText = "QQ-100";
        vm.RunSearch();
        Assert.Equal("QQ-100", vm.Items[0].Stock!.PartNumber);
        Assert.Equal(StockOrigin.User, vm.Items[0].Stock!.Origin);
    }

    [Fact]
    public async Task AListWithNothingUsable_ShowsThePreviewWithItsReasons_ButSavesNothing()
    {
        var vm = await OpenAsync();
        _files.ListPath = WriteList("Name,Colour\nA,red\n");
        vm.ImportListCommand.Execute(null);
        Assert.False(_dialogs.LastPreview!.CanSave);
        Assert.NotEmpty(_dialogs.LastPreview.Warnings);
        Assert.False(Directory.Exists(_store.UserFolder) && Directory.EnumerateFiles(_store.UserFolder).Any());
    }

    [Fact]
    public async Task CancellingTheFilePicker_DoesNothing()
    {
        var vm = await OpenAsync();
        _files.ListPath = null;
        vm.ImportListCommand.Execute(null);
        Assert.Null(_dialogs.LastPreview);
    }

    // ------------------------------------------------------------------------------------ about, language, panel

    [Fact]
    public async Task TheAboutText_CountsTheRealCatalogue_AndKeepsTheNonAffiliationLine()
    {
        var vm = await OpenAsync();
        vm.ShowAboutCommand.Execute(null);
        Assert.Equal("About the stock catalogue", _dialogs.AboutTitle);
        Assert.Contains(vm.CatalogCount.ToString(), _dialogs.AboutBody);
        Assert.Contains("not affiliated", _dialogs.AboutBody);
        Assert.Equal("Brand and product names belong to their owners; Rotula is not affiliated.", Loc.Get("Stock.Disclaimer"));
    }

    [Fact]
    public async Task SwitchingTheLanguage_RebuildsTheListAndTheDetail_InSpanish()
    {
        var vm = await OpenAsync();
        var stock = RealStock(e => e.Media == StockMedia.ThermalTransfer);
        vm.SearchText = stock.PartNumber!;
        vm.RunSearch();
        Loc.Instance.SetLanguage("es");
        Assert.Contains("Transferencia térmica", vm.Items[0].MediaText);
        Assert.Contains(vm.Facts, f => f.Label == "Número de parte");
        Assert.Equal("Resultados", vm.Items[0].GroupName);
        vm.SearchText = string.Empty;
        vm.RunSearch();
        Assert.Contains(vm.Items, i => i.GroupName == "Tamaños comunes");
        Assert.Equal("Tamaño personalizado...", vm.Items.First(i => i.Kind == StockItemKind.Custom).Name);
        vm.Dispose();
    }

    [Fact]
    public async Task EveryStockWarningKey_FormatsInEveryLanguage_WithoutLeftoverPlaceholders()
    {
        var stock = RealStock();
        var issues = typeof(StockMessageKeys).GetFields().Where(f => f.IsLiteral && ((string)f.GetRawConstantValue()!).StartsWith("Stocks.Apply.", StringComparison.Ordinal))
            .Select(f => new ValidationIssue(ValidationSeverity.Warning, (string)f.GetRawConstantValue()!, null, new[] { stock.Id, "2", "30" })).ToList();
        Assert.True(issues.Count >= 8);
        foreach (string lang in Loc.SupportedLanguages)
        {
            Loc.Instance.SetLanguage(lang);
            foreach (var issue in issues)
            {
                string text = Rotula.App.ViewModels.StockText.Warning(issue, stock);
                Assert.False(Regex.IsMatch(text, @"\{\d\}"), lang + " " + issue.MessageKey);
                Assert.DoesNotContain("!Issue", text);
                Assert.DoesNotContain(stock.Id, text);
            }
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheLabelPanel_ShowsTheNameOfTheStockTheLabelCameFrom()
    {
        var stock = RealStock();
        var previous = StockLibrary.Current;
        StockLibrary.Current = _store;
        try
        {
            var doc = new LabelDocument { StockId = stock.Id };
            var panel = new PropertyPanelViewModel(new Rotula.App.Editing.EditorSession(doc));
            var note = panel.Rows.OfType<NoteRow>().Single(r => r.LabelKey == "Prop.Stock");
            Assert.Contains(stock.Name, note.Text);
            Assert.Contains(stock.PartNumber!, note.Text);

            var missing = new PropertyPanelViewModel(new Rotula.App.Editing.EditorSession(new LabelDocument { StockId = "gone-1" }));
            Assert.Contains("no longer in the library", missing.Rows.OfType<NoteRow>().Single(r => r.LabelKey == "Prop.Stock").Text);

            var none = new PropertyPanelViewModel(new Rotula.App.Editing.EditorSession(new LabelDocument()));
            Assert.DoesNotContain(none.Rows, r => r is NoteRow { LabelKey: "Prop.Stock" });
        }
        finally { StockLibrary.Current = previous; }
        await Task.CompletedTask;
    }

    [Fact]
    public void EveryStockKeyUsedByTheDialog_ExistsInEveryLanguage()
    {
        // Keys built from enum values in StockText.
        var built = new List<string>();
        foreach (var m in Enum.GetValues<StockMedia>()) built.Add("Stock.Media." + m);
        foreach (var f in Enum.GetValues<StockFormat>()) built.Add("Stock.Format." + f);
        foreach (var l in Enum.GetValues<StockLayout>()) built.Add("Stock.Layout." + l);
        Assert.Empty(UiStrings.MissingInAnyLanguage(built));
    }
}
