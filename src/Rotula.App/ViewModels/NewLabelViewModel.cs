using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Core.Stocks;
using Rotula.Rendering.View;

namespace Rotula.App.ViewModels;

/// <summary>A label size offered in the New dialog.</summary>
/// <param name="NameKey">Resource key of the display name.</param>
/// <param name="Width">Width in <paramref name="Unit"/>.</param>
/// <param name="Height">Height in <paramref name="Unit"/>.</param>
/// <param name="Unit">Unit of the size.</param>
public sealed record LabelPreset(string NameKey, double Width, double Height, LengthUnit Unit)
{
    /// <summary>Translated name.</summary>
    public string Name => Loc.Instance[NameKey];

    /// <summary>The built-in presets.</summary>
    public static IReadOnlyList<LabelPreset> All { get; } = new[]
    {
        new LabelPreset("Preset.4x6in", 4, 6, LengthUnit.Inches),
        new LabelPreset("Preset.4x3in", 4, 3, LengthUnit.Inches),
        new LabelPreset("Preset.2x1in", 2, 1, LengthUnit.Inches),
        new LabelPreset("Preset.100x150mm", 100, 150, LengthUnit.Millimeters),
        new LabelPreset("Preset.100x50mm", 100, 50, LengthUnit.Millimeters),
        new LabelPreset("Preset.Custom", 100, 150, LengthUnit.Millimeters),
    };
}

/// <summary>What the dialogs of the stock picker need from the window layer (replaced by a fake in tests).</summary>
public interface IStockDialogs
{
    /// <summary>Shows the preview of an imported supplies list; true when the user confirmed that it should be kept.</summary>
    bool ShowImportPreview(StockImportPreviewViewModel preview);

    /// <summary>Shows the short in-app text about the stock catalogue.</summary>
    void ShowAbout(string title, string body);
}

/// <summary>
/// View-model of the New label dialog: a stock picker with search, filters, favourites and recent stocks, a detail pane with the
/// facts and the warnings of applying a stock, and the original manual size (common sizes and a custom width and height).
/// </summary>
public sealed partial class NewLabelViewModel : ObservableObject, IDisposable
{
    /// <summary>Most hits listed; the summary says how many more there are.</summary>
    public const int MaxListed = 300;

    private readonly NewLabelContext _ctx;
    private readonly IStockDialogs? _dialogs;
    private readonly StockUserList _lists;
    private StockCatalog _catalog = StockCatalog.Empty;
    private bool _syncing;
    private readonly PrinterDpi _initialDpi;

    /// <summary>Creates the dialog state with the 4 x 6 in size selected and no stock library (tests and the manual size only).</summary>
    public NewLabelViewModel() : this(null, null) { }

    /// <summary>Creates the dialog state.</summary>
    /// <param name="context">Stock store, remembered lists and printer resolution; null for a stand-alone dialog.</param>
    /// <param name="dialogs">Windows of the picker (import preview, about text); null disables them.</param>
    public NewLabelViewModel(NewLabelContext? context, IStockDialogs? dialogs)
    {
        _ctx = context ?? new NewLabelContext { Stocks = new EmptyStore() };
        _dialogs = dialogs;
        _lists = StockUserList.FromJson(JsonSerializer.Serialize(new { version = 1, favorites = _ctx.Settings.StockFavorites, recent = _ctx.Settings.StockRecent }));
        _selectedPreset = LabelPreset.All[0];
        _widthText = LabelPreset.All[0].Width.ToString("0.###", CultureInfo.CurrentCulture);
        _heightText = LabelPreset.All[0].Height.ToString("0.###", CultureInfo.CurrentCulture);
        _unit = LabelPreset.All[0].Unit;
        _dpi = _initialDpi = _ctx.DefaultDpi ?? PrinterDpi.Dpi203;
        _dpiNote = _ctx.DefaultDpi is { } d
            ? _ctx.DpiFromPrinter ? Loc.Get("Stock.Dpi.FromPrinter", (int)d) : _ctx.DpiFromLastUse ? Loc.Get("Stock.Dpi.FromSettings", (int)d) : string.Empty
            : string.Empty;
        BrandChoices = new List<TextChoice> { new(string.Empty, "Stock.Filter.AllBrands") };
        UnitFilters = new List<Choice<int>> { new(0, "Stock.Filter.AllUnits"), new(1, "Stock.Filter.Inches"), new(2, "Stock.Filter.Millimeters") };
        LayoutFilters = new List<Choice<int>> { new(0, "Stock.Filter.AllLayouts"), new(1, "Stock.Layout.Roll"), new(2, "Stock.Layout.Sheet"), new(3, "Stock.Layout.Round") };
        BuildList();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <inheritdoc />
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var c in BrandChoices) c.Refresh();
        foreach (var c in FamilyChoices) c.Refresh();
        foreach (var c in UnitFilters) c.Refresh();
        foreach (var c in LayoutFilters) c.Refresh();
        RunSearch();
        UpdateDetail();
        OnPropertyChanged(nameof(ResultSummary));
    }

    // ----------------------------------------------------------------------------------- catalogue

    /// <summary>True while the library is being read.</summary>
    [ObservableProperty]
    private bool _isLoadingCatalog;

    /// <summary>Reads the stock library off the UI thread and fills the lists. Call it once when the dialog opens.</summary>
    public async Task LoadCatalogAsync()
    {
        IsLoadingCatalog = true;
        try
        {
            _catalog = await _ctx.Stocks.GetCatalogAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The library could not be read; the common sizes and the custom size still work.
            _catalog = StockCatalog.Empty;
        }
        finally
        {
            IsLoadingCatalog = false;
        }
        OnCatalogChanged();
    }

    private void OnCatalogChanged()
    {
        var brands = _catalog.Brands;
        string keep = BrandFilter;
        BrandChoices = new List<TextChoice> { new(string.Empty, "Stock.Filter.AllBrands") }.Concat(brands.Select(b => new TextChoice(b, null))).ToList();
        OnPropertyChanged(nameof(BrandChoices));
        BrandFilter = brands.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep : string.Empty;
        var families = _catalog.Families;
        string keepFamily = FamilyFilter;
        FamilyChoices = new List<TextChoice> { new(string.Empty, "Stock.Filter.AllFamilies") }.Concat(families.Select(x => new TextChoice(x, null))).ToList();
        OnPropertyChanged(nameof(FamilyChoices));
        FamilyFilter = families.Contains(keepFamily, StringComparer.OrdinalIgnoreCase) ? keepFamily : string.Empty;
        OnPropertyChanged(nameof(SearchHint));
        RunSearch();
    }

    /// <summary>Number of stocks in the library (for the summary and the about text).</summary>
    public int CatalogCount => _catalog.Count;

    /// <summary>The library, for the detail pane and tests.</summary>
    public StockCatalog Catalog => _catalog;

    /// <summary>A real part number from the library to show as an example in the empty search hint.</summary>
    public string SearchHint
    {
        get
        {
            string sample = _catalog.Entries.FirstOrDefault(e => e.Verified && e.PartNumber is { Length: > 0 })?.PartNumber ?? "10010041";
            return Loc.Get("Stock.Search.Hint", sample);
        }
    }

    // ----------------------------------------------------------------------------------- search and filters

    /// <summary>The search text: a part number (or part of one), a size in almost any spelling, or words from the name.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Selected brand ("" = all).</summary>
    [ObservableProperty]
    private string _brandFilter = string.Empty;

    /// <summary>Selected product family ("" = all), for example <c>Z-Perform 1000D</c>.</summary>
    [ObservableProperty]
    private string _familyFilter = string.Empty;

    /// <summary>0 = any unit, 1 = quoted in inches, 2 = quoted in millimetres.</summary>
    [ObservableProperty]
    private int _unitFilter;

    /// <summary>0 = any type, 1 = roll, 2 = sheet, 3 = round.</summary>
    [ObservableProperty]
    private int _layoutFilter;

    /// <summary>Hide stocks whose size is not verified.</summary>
    [ObservableProperty]
    private bool _verifiedOnly;

    /// <summary>Show only starred stocks.</summary>
    [ObservableProperty]
    private bool _favoritesOnly;

    /// <summary>Brands to filter by (first entry: all).</summary>
    public IReadOnlyList<TextChoice> BrandChoices { get; private set; }

    /// <summary>Product families to filter by (first entry: all).</summary>
    public IReadOnlyList<TextChoice> FamilyChoices { get; private set; } = new List<TextChoice> { new(string.Empty, "Stock.Filter.AllFamilies") };

    /// <summary>Unit filter choices.</summary>
    public IReadOnlyList<Choice<int>> UnitFilters { get; }

    /// <summary>Type filter choices.</summary>
    public IReadOnlyList<Choice<int>> LayoutFilters { get; }

    partial void OnBrandFilterChanged(string value) => RunSearch();

    partial void OnFamilyFilterChanged(string value) => RunSearch();

    partial void OnUnitFilterChanged(int value) => RunSearch();

    partial void OnLayoutFilterChanged(int value) => RunSearch();

    partial void OnVerifiedOnlyChanged(bool value) => RunSearch();

    partial void OnFavoritesOnlyChanged(bool value) => RunSearch();

    /// <summary>The list as shown (grouped).</summary>
    public ICollectionView Results { get; private set; } = CollectionViewSource.GetDefaultView(Array.Empty<StockListItem>());

    /// <summary>The rows behind <see cref="Results"/>.</summary>
    public IReadOnlyList<StockListItem> Items { get; private set; } = Array.Empty<StockListItem>();

    /// <summary>One line under the list: how many stocks match.</summary>
    [ObservableProperty]
    private string _resultSummary = string.Empty;

    /// <summary>True when a search or filter found nothing.</summary>
    [ObservableProperty]
    private bool _hasNoResults;

    /// <summary>True when the list shows search results (a query or a filter is set) rather than the starting lists.</summary>
    public bool IsSearching => SearchText.Trim().Length > 0 || BrandFilter.Length > 0 || FamilyFilter.Length > 0 || UnitFilter != 0 || LayoutFilter != 0 || VerifiedOnly || FavoritesOnly;

    /// <summary>Searches the library for <see cref="SearchText"/> with the filters and fills the list. The search itself takes well under a millisecond; the window waits a moment after typing before calling it.</summary>
    public void RunSearch()
    {
        // Rebinding the list makes the ListBox push its own idea of the selection (nothing, or the first row) back into SelectedItem, so the
        // row to keep is remembered before the list is replaced.
        string? keepKey = SelectedItem?.Key;
        OnPropertyChanged(nameof(IsSearching));
        if (!IsSearching)
        {
            BuildList(keepKey);
            return;
        }
        var options = new StockSearchOptions
        {
            Brand = BrandFilter.Length > 0 ? BrandFilter : null,
            NativeUnit = UnitFilter == 1 ? StockUnit.Inches : UnitFilter == 2 ? StockUnit.Millimeters : null,
            Layout = LayoutFilter switch { 1 => StockLayout.Roll, 2 => StockLayout.Sheet, 3 => StockLayout.Round, _ => null },
            Family = FamilyFilter.Length > 0 ? FamilyFilter : null,
            VerifiedOnly = VerifiedOnly,
            MaxResults = FavoritesOnly ? 5000 : MaxListed,
        };
        var result = _catalog.Search(SearchText, options);
        var hits = result.Hits.AsEnumerable();
        if (FavoritesOnly) hits = hits.Where(h => _lists.IsFavorite(h.Entry.Id));
        var hitList = hits.Take(MaxListed).ToList();
        var items = hitList.Select(h => StockListItem.ForStock(h.Entry, "Stock.Group.Results", _lists.IsFavorite(h.Entry.Id))).ToList();
        // Stocks that miss the searched size by a few millimetres are offered below, so "100 x 149" still leads somewhere.
        var shown = new HashSet<string>(hitList.Select(h => h.Entry.Id), StringComparer.OrdinalIgnoreCase);
        var near = FavoritesOnly ? Array.Empty<StockSearchHit>() : result.NearHits.Where(n => !shown.Contains(n.Entry.Id)).ToArray();
        items.AddRange(near.Select(n => StockListItem.ForStock(n.Entry, "Stock.Group.Near", _lists.IsFavorite(n.Entry.Id))));
        int total = FavoritesOnly ? hitList.Count : result.TotalHits;
        SetItems(items);
        HasNoResults = items.Count == 0;
        ResultSummary = items.Count == 0 ? Loc.Get(IsLoadingCatalog ? "Stock.Loading" : "Stock.Result.None")
            : total > hitList.Count ? Loc.Get("Stock.Result.Count", hitList.Count, total)
            : hitList.Count == 1 && near.Length == 0 ? Loc.Get("Stock.Result.One")
            : near.Length > 0 ? Loc.Get("Stock.Result.WithNear", hitList.Count, near.Length)
            : Loc.Get("Stock.Result.All", hitList.Count);
        SelectFirstOrKeep(keepKey, preferStocks: true);
    }

    /// <summary>The starting lists: the common sizes (with "Custom size..."), then favourites, then recent stocks.</summary>
    private void BuildList(string? keepKey = null)
    {
        var items = new List<StockListItem>();
        foreach (var p in LabelPreset.All) items.Add(StockListItem.ForPreset(p, "Stock.Group.Common"));
        foreach (var e in _lists.ResolveFavorites(_catalog)) items.Add(StockListItem.ForStock(e, "Stock.Group.Favorites", true));
        foreach (var e in _lists.ResolveRecent(_catalog).Take(8)) items.Add(StockListItem.ForStock(e, "Stock.Group.Recent", _lists.IsFavorite(e.Id)));
        SetItems(items);
        HasNoResults = false;
        ResultSummary = _catalog.Count > 0 ? Loc.Get("Stock.Result.Total", _catalog.Count) : Loc.Get(IsLoadingCatalog ? "Stock.Loading" : "Stock.Result.Empty");
        SelectFirstOrKeep(keepKey, preferStocks: false);
    }

    private void SetItems(List<StockListItem> items)
    {
        Items = items;
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StockListItem.GroupName)));
        Results = view;
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(Results));
    }

    /// <summary>Keeps the selected row when it is still listed; otherwise picks the best row so Enter does something sensible.</summary>
    private void SelectFirstOrKeep(string? key, bool preferStocks)
    {
        var keep = key is null ? null : Items.FirstOrDefault(i => i.Key == key && (!preferStocks || i.IsStock));
        var pick = keep ?? (preferStocks ? Items.FirstOrDefault(i => i.IsStock) : Items.FirstOrDefault(i => i.Kind == StockItemKind.Common && i.Preset == SelectedPreset))
                   ?? Items.FirstOrDefault();
        SelectItem(pick);
    }

    private void SelectItem(StockListItem? item)
    {
        _syncing = true;
        try { SelectedItem = item; }
        finally { _syncing = false; }
        ApplySelection(item);
    }

    /// <summary>Moves the selection by <paramref name="delta"/> rows (the arrow keys in the search box). Returns the new row.</summary>
    public StockListItem? MoveSelection(int delta)
    {
        if (Items.Count == 0) return null;
        var ordered = OrderedItems();
        int index = SelectedItem is null ? -1 : ordered.IndexOf(SelectedItem);
        int next = Math.Clamp(index + delta, 0, ordered.Count - 1);
        SelectedItem = ordered[next];
        return SelectedItem;
    }

    /// <summary>The rows in the order they appear on screen (grouping puts rows of a group together).</summary>
    private List<StockListItem> OrderedItems() => Results.Cast<StockListItem>().ToList();

    // ----------------------------------------------------------------------------------- selection and detail

    /// <summary>The selected row.</summary>
    [ObservableProperty]
    private StockListItem? _selectedItem;

    partial void OnSelectedItemChanged(StockListItem? value)
    {
        if (_syncing) return;
        ApplySelection(value);
    }

    private void ApplySelection(StockListItem? item)
    {
        if (item?.Preset is { } preset)
        {
            _syncing = true;
            try { SelectedPreset = preset; }
            finally { _syncing = false; }
        }
        SelectedStock = item?.Stock;
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(ShowStockDetail));
        OnPropertyChanged(nameof(ShowSizeFields));
        UpdateDetail();
        Error = string.Empty;
    }

    /// <summary>The selected stock, or null for a common or custom size.</summary>
    [ObservableProperty]
    private StockEntry? _selectedStock;

    /// <summary>True when the detail pane shows a stock.</summary>
    public bool ShowStockDetail => SelectedStock is not null;

    /// <summary>True when the width and height boxes are shown (a common size or the custom size).</summary>
    public bool ShowSizeFields => SelectedStock is null;

    /// <summary>Name of the stock in the detail pane.</summary>
    [ObservableProperty]
    private string _stockTitle = string.Empty;

    /// <summary>Brand, part number and family of the stock.</summary>
    [ObservableProperty]
    private string _stockSubtitle = string.Empty;

    /// <summary>The size in the unit the maker quotes it in, large.</summary>
    [ObservableProperty]
    private string _stockSize = string.Empty;

    /// <summary>The size in the other unit.</summary>
    [ObservableProperty]
    private string _stockSizeOther = string.Empty;

    /// <summary>All known facts of the stock.</summary>
    public ObservableCollection<StockFact> Facts { get; } = new();

    /// <summary>What applying the stock will warn about, in plain language.</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>True when applying the stock warns about something.</summary>
    [ObservableProperty]
    private bool _hasWarnings;

    /// <summary>True when the selected stock is a continuous roll, so the label height is typed.</summary>
    [ObservableProperty]
    private bool _needsHeight;

    /// <summary>Height typed for a continuous roll, in the unit of the stock.</summary>
    [ObservableProperty]
    private string _continuousHeightText = string.Empty;

    /// <summary>"in" or "mm" next to the continuous height box.</summary>
    [ObservableProperty]
    private string _continuousHeightUnit = string.Empty;

    /// <summary>True when the selected stock is starred.</summary>
    [ObservableProperty]
    private bool _isSelectedFavorite;

    /// <summary>Text of the star button in the detail pane.</summary>
    public string FavoriteToggleText => Loc.Get(IsSelectedFavorite ? "Stock.Favorite.Remove" : "Stock.Favorite.Add");

    partial void OnIsSelectedFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteToggleText));

    private void UpdateDetail()
    {
        Facts.Clear();
        Warnings.Clear();
        var s = SelectedStock;
        if (s is null)
        {
            StockTitle = StockSubtitle = StockSize = StockSizeOther = string.Empty;
            HasWarnings = false;
            NeedsHeight = false;
            IsSelectedFavorite = false;
            return;
        }
        StockTitle = s.Name;
        StockSubtitle = string.Join("  ·  ", new[] { s.Origin == StockOrigin.User ? Loc.Get("Stock.Origin.User") : s.Brand, s.PartNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
        StockSize = StockText.NativeSize(s);
        StockSizeOther = StockText.OtherSize(s);
        foreach (var f in StockText.Facts(s, _catalog)) Facts.Add(f);
        // The warnings are exactly those of creating the label, computed on a blank one (applying never touches anything).
        try
        {
            var applied = StockApplier.Apply(null, s, Dpi);
            foreach (var w in applied.Warnings) Warnings.Add(StockText.Warning(w, s));
        }
        catch (ArgumentException)
        {
            Warnings.Add(Loc.Get("Stock.Apply.NoWidth"));
        }
        HasWarnings = Warnings.Count > 0;
        NeedsHeight = s.IsContinuous;
        ContinuousHeightUnit = Loc.Get(s.NativeUnit == StockUnit.Inches ? "Unit.Short.Inches" : "Unit.Short.Millimeters");
        IsSelectedFavorite = _lists.IsFavorite(s.Id);
    }

    /// <summary>Stars or unstars the selected stock (or the one in <paramref name="item"/>) and remembers it.</summary>
    [RelayCommand]
    private void ToggleFavorite(StockListItem? item)
    {
        var stock = item?.Stock ?? SelectedStock;
        if (stock is null) return;
        bool now = _lists.ToggleFavorite(stock.Id);
        foreach (var row in Items.Where(i => i.Stock?.Id == stock.Id)) row.IsFavorite = now;
        if (SelectedStock?.Id == stock.Id) IsSelectedFavorite = now;
        SaveLists();
        // The starting lists and the "favourites only" filter change with the stars.
        if (!IsSearching || FavoritesOnly) RunSearch();
    }

    private void SaveLists()
    {
        _ctx.Settings.StockFavorites = _lists.Favorites.ToList();
        _ctx.Settings.StockRecent = _lists.Recent.ToList();
        _ctx.SaveSettings();
    }

    // ----------------------------------------------------------------------------------- manual size (as before)

    /// <summary>The chosen common size or the custom row; choosing one fills the size boxes.</summary>
    [ObservableProperty]
    private LabelPreset _selectedPreset;

    /// <summary>Width text.</summary>
    [ObservableProperty]
    private string _widthText;

    /// <summary>Height text.</summary>
    [ObservableProperty]
    private string _heightText;

    /// <summary>Unit of the size boxes.</summary>
    [ObservableProperty]
    private LengthUnit _unit;

    /// <summary>Printer resolution.</summary>
    [ObservableProperty]
    private PrinterDpi _dpi;

    /// <summary>Why the resolution was preset ("Taken from your printer"), or empty.</summary>
    [ObservableProperty]
    private string _dpiNote;

    /// <summary>Problem with the entered size, or empty.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>Presets to choose from.</summary>
    public IReadOnlyList<LabelPreset> Presets => LabelPreset.All;

    /// <summary>True when the size boxes can be edited (the custom size).</summary>
    public bool IsCustom => SelectedStock is null && SelectedPreset.NameKey == "Preset.Custom";

    /// <summary>Printer resolutions to choose from (translated names; bind SelectedValue to <see cref="Dpi"/>).</summary>
    public IReadOnlyList<ChoiceItem> Dpis { get; } = ChoiceRow.ForEnum<PrinterDpi>();

    /// <summary>Units to choose from.</summary>
    public IReadOnlyList<ChoiceItem> Units { get; } = ChoiceRow.ForEnum(new[] { LengthUnit.Millimeters, LengthUnit.Inches });

    partial void OnDpiChanged(PrinterDpi value)
    {
        if (value != _initialDpi) DpiNote = string.Empty;
        UpdateDetail();
    }

    partial void OnSelectedPresetChanged(LabelPreset value)
    {
        OnPropertyChanged(nameof(IsCustom));
        if (value.NameKey != "Preset.Custom")
        {
            WidthText = value.Width.ToString("0.###", CultureInfo.CurrentCulture);
            HeightText = value.Height.ToString("0.###", CultureInfo.CurrentCulture);
            Unit = value.Unit;
        }
        Error = string.Empty;
        // Choosing a size from code (or a test) selects its row in the list.
        if (!_syncing)
        {
            var row = Items.FirstOrDefault(i => i.Preset == value);
            if (row is not null) SelectItem(row);
        }
    }

    // ----------------------------------------------------------------------------------- creating the label

    /// <summary>Validates the choice and builds a blank label. Returns null (and sets <see cref="Error"/>) when it is not usable.</summary>
    public LabelDocument? TryCreate()
    {
        if (SelectedStock is { } stock) return CreateFromStock(stock);
        if (!TryParseSize(out var width, out var height)) return null;
        Error = string.Empty;
        return new LabelDocument { Width = width, Height = height, Dpi = Dpi };
    }

    private bool TryParseSize(out Measure width, out Measure height)
    {
        width = height = default;
        if (!double.TryParse(WidthText, NumberStyles.Float, CultureInfo.CurrentCulture, out double w)
            || !double.TryParse(HeightText, NumberStyles.Float, CultureInfo.CurrentCulture, out double h))
        {
            Error = Loc.Get("Validation.NotNumber");
            return false;
        }
        width = new Measure(w, Unit);
        height = new Measure(h, Unit);
        int wd = width.ToDots(Dpi), hd = height.ToDots(Dpi);
        if (wd < 1 || wd > 9999 || hd < 1 || hd > 32000)
        {
            Error = Loc.Get("Validation.LabelSize", 9999, 32000);
            return false;
        }
        return true;
    }

    private LabelDocument? CreateFromStock(StockEntry stock)
    {
        StockApplyResult applied;
        try { applied = StockApplier.Apply(null, stock, Dpi); }
        catch (ArgumentException)
        {
            Error = Loc.Get("Stock.Apply.NoWidth");
            return null;
        }
        var doc = applied.Document;
        if (!applied.HeightApplied)
        {
            // A continuous roll has no label length of its own: the user types the length of the label.
            var unit = stock.NativeUnit == StockUnit.Inches ? LengthUnit.Inches : LengthUnit.Millimeters;
            if (!double.TryParse(ContinuousHeightText, NumberStyles.Float, CultureInfo.CurrentCulture, out double h) || h <= 0)
            {
                Error = Loc.Get("Stock.Height.Required");
                return null;
            }
            var height = new Measure(h, unit);
            int hd = height.ToDots(Dpi);
            if (hd < 1 || hd > 32000)
            {
                Error = Loc.Get("Validation.LabelSize", 9999, 32000);
                return null;
            }
            doc.Height = height;
        }
        if (doc.Width.ToDots(Dpi) is < 1 or > 9999)
        {
            Error = Loc.Get("Validation.LabelSize", 9999, 32000);
            return null;
        }
        Error = string.Empty;
        _lists.Touch(stock.Id);
        SaveLists();
        return doc;
    }

    // ----------------------------------------------------------------------------------- my own stocks

    /// <summary>Name of the stock being saved from the custom size.</summary>
    [ObservableProperty]
    private string _ownName = string.Empty;

    /// <summary>Optional part number of the stock being saved.</summary>
    [ObservableProperty]
    private string _ownPart = string.Empty;

    /// <summary>Result of the last stock action (saved, imported, problem), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = string.Empty;

    /// <summary>True when there is a status line.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Saves the size typed in the custom boxes as a stock of the user, so it can be found by name or part number next time.</summary>
    [RelayCommand]
    private void SaveOwnStock()
    {
        string name = OwnName.Trim(), part = OwnPart.Trim();
        if (name.Length == 0 && part.Length == 0) { Error = Loc.Get("Stock.Own.NeedName"); return; }
        if (!TryParseSize(out var w, out var h)) return;
        double wmm = Math.Round(UnitConverter.Convert(w.Value, w.Unit, LengthUnit.Millimeters, (int)Dpi), 2);
        double hmm = Math.Round(UnitConverter.Convert(h.Value, h.Unit, LengthUnit.Millimeters, (int)Dpi), 2);
        double win = Math.Round(wmm / 25.4, 3), hin = Math.Round(hmm / 25.4, 3);
        if (w.Unit == LengthUnit.Inches) { win = w.Value; wmm = Math.Round(w.Value * 25.4, 2); }
        if (h.Unit == LengthUnit.Inches) { hin = h.Value; hmm = Math.Round(h.Value * 25.4, 2); }
        string id = FileStockStore.MakeId(part.Length > 0 ? part : name, i => _catalog.Find(i) is not null);
        var entry = new StockEntry
        {
            Id = id,
            Brand = "User",
            PartNumber = part.Length > 0 ? part : null,
            Name = (name.Length > 0 ? name : part) is { Length: > 120 } n ? n[..120] : (name.Length > 0 ? name : part),
            WidthMm = wmm, HeightMm = hmm, WidthIn = win, HeightIn = hin,
            Format = StockFormat.Roll,
            Sources = new[] { FileStockStore.OwnSourceId },
            Verified = true,
            Verification = "Entered by the user",
            Origin = StockOrigin.User,
        };
        try
        {
            _ctx.Stocks.SaveUserStock(entry);
            _catalog = _ctx.Stocks.Catalog;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Get("Stock.Own.Error", ex.Message);
            return;
        }
        if (_catalog.Find(id) is null)
        {
            // The loader refused the entry (a size outside what a label printer can use): nothing was added to the library.
            Error = Loc.Get("Stock.Own.Refused");
            return;
        }
        Error = string.Empty;
        StatusText = Loc.Get("Stock.Own.Saved", entry.Name);
        OwnName = OwnPart = string.Empty;
        OnCatalogChanged();
    }

    // ----------------------------------------------------------------------------------- import and about

    /// <summary>Reads a supplies list (CSV or Excel), shows what was understood, and keeps it as a user catalogue file when the user confirms.</summary>
    [RelayCommand]
    private void ImportList()
    {
        string? path = _ctx.Files?.PickOpenFile(Loc.Get("Stock.Import.Pick"), new[] { ".csv", ".xlsx", ".tsv", ".txt" });
        if (path is null) return;
        var result = SuppliesImporter.ImportFile(path);
        var preview = new StockImportPreviewViewModel(result, Path.GetFileName(path));
        if (_dialogs is null || !_dialogs.ShowImportPreview(preview) || result.IsEmpty) return;
        try
        {
            string file = _ctx.Stocks.SaveImportedCatalog(result.ToCatalogJson());
            _catalog = _ctx.Stocks.Catalog;
            StatusText = Loc.Get("Stock.Import.Saved", result.Imported.Count, file);
            OnCatalogChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Get("Stock.Own.Error", ex.Message);
        }
    }

    /// <summary>Shows the short text about what the catalogue is and where its data comes from.</summary>
    [RelayCommand]
    private void ShowAbout()
    {
        int verified = _catalog.Entries.Count(e => e.Verified);
        _dialogs?.ShowAbout(Loc.Get("Stock.About.Title"), Loc.Get("Stock.About.Body", _catalog.Count, verified, _ctx.Stocks.UserFolder));
    }

    /// <summary>Stand-alone dialogs (no application context) have no library.</summary>
    private sealed class EmptyStore : IStockStore
    {
        public StockCatalog Catalog => StockCatalog.Empty;
        public Task<StockCatalog> GetCatalogAsync() => Task.FromResult(StockCatalog.Empty);
        public string UserFolder => string.Empty;
        public StockEntry SaveUserStock(StockEntry entry) => throw new IOException("No stock folder.");
        public string SaveImportedCatalog(string json) => throw new IOException("No stock folder.");
        public void Reload() { }
    }
}

/// <summary>A drop-down entry whose name is either a translated key ("All brands") or literal text (a brand).</summary>
public sealed class TextChoice : ObservableObject
{
    private readonly string? _nameKey;

    /// <summary>Creates an entry; <paramref name="nameKey"/> null means the value itself is the name.</summary>
    public TextChoice(string value, string? nameKey)
    {
        Value = value;
        _nameKey = nameKey;
    }

    /// <summary>The stored value ("" for all).</summary>
    public string Value { get; }

    /// <summary>The name shown.</summary>
    public string Name => _nameKey is null ? Value : Loc.Get(_nameKey);

    /// <summary>Re-reads the name after a language switch.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Name));
}

/// <summary>One row of the import preview.</summary>
public sealed class ImportPreviewRow
{
    /// <summary>Creates a row.</summary>
    public ImportPreviewRow(ImportedStock imported)
    {
        Row = imported.Row;
        Part = imported.Entry.PartNumber ?? string.Empty;
        Name = imported.Entry.Name;
        SizeMm = imported.Entry.HeightMm is { } h ? Loc.Get("Stock.Size.Mm", imported.Entry.WidthMm.ToString("0.##", Loc.Instance.Culture), h.ToString("0.##", Loc.Instance.Culture)) : string.Empty;
        SizeIn = imported.Entry.HeightIn is { } hi ? Loc.Get("Stock.Size.In", imported.Entry.WidthIn.ToString("0.###", Loc.Instance.Culture), hi.ToString("0.###", Loc.Instance.Culture)) : string.Empty;
        IsMedium = imported.Confidence == ImportConfidence.Medium;
    }

    /// <summary>Row number in the file (the header is row 1).</summary>
    public int Row { get; }
    /// <summary>Part number.</summary>
    public string Part { get; }
    /// <summary>Description.</summary>
    public string Name { get; }
    /// <summary>Size in millimetres.</summary>
    public string SizeMm { get; }
    /// <summary>Size in inches.</summary>
    public string SizeIn { get; }
    /// <summary>True when the size came from free text: the user should check it.</summary>
    public bool IsMedium { get; }
    /// <summary>Plain-language certainty.</summary>
    public string Confidence => Loc.Get(IsMedium ? "Stock.Import.Medium" : "Stock.Import.High");
    /// <summary>Why the certainty is what it is.</summary>
    public string ConfidenceTip => Loc.Get(IsMedium ? "Stock.Import.Medium.Tip" : "Stock.Import.High.Tip");
}

/// <summary>The preview of an imported supplies list: what was understood (with how sure Rotula is), and what was skipped and why.</summary>
public sealed class StockImportPreviewViewModel
{
    /// <summary>Creates the preview.</summary>
    public StockImportPreviewViewModel(SuppliesImportResult result, string fileName)
    {
        FileName = fileName;
        Rows = result.Imported.Select(i => new ImportPreviewRow(i)).ToList();
        Warnings = result.Warnings.Select(Loc.FormatIssue).ToList();
        Summary = result.IsEmpty ? Loc.Get("Stock.Import.Nothing") : Loc.Get("Stock.Import.Counts", result.RowsRead, result.Imported.Count);
        CanSave = !result.IsEmpty;
    }

    /// <summary>Name of the file read.</summary>
    public string FileName { get; }
    /// <summary>The stocks found.</summary>
    public IReadOnlyList<ImportPreviewRow> Rows { get; }
    /// <summary>Notes about skipped rows and guessed columns, in plain language.</summary>
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>True when there are notes.</summary>
    public bool HasWarnings => Warnings.Count > 0;
    /// <summary>"N rows read, M stocks found".</summary>
    public string Summary { get; }
    /// <summary>True when there is something to keep.</summary>
    public bool CanSave { get; }
    /// <summary>Text of the confirm button.</summary>
    public string SaveText => Loc.Get("Stock.Import.Save", Rows.Count);
}
