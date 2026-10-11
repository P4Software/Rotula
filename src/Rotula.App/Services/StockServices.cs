using System.Globalization;
using System.Text.RegularExpressions;
using Rotula.Core.Model;
using Rotula.Core.Stocks;

namespace Rotula.App.Services;

/// <summary>
/// Where the label stock catalogue comes from and where the user's own stocks are kept. Behind an interface so the New label dialog
/// runs in tests with a temporary folder and never touches <c>%AppData%</c>.
/// </summary>
public interface IStockStore
{
    /// <summary>The bundled library plus the user's own stocks. Loaded on first use (a one-time parse of a few hundred kilobytes).</summary>
    StockCatalog Catalog { get; }

    /// <summary>Loads the catalogue on a worker thread, so the dialog opens at once and the list fills in a moment later.</summary>
    Task<StockCatalog> GetCatalogAsync();

    /// <summary>The folder with the user's catalogue files (<c>%AppData%\Rotula\stocks</c>).</summary>
    string UserFolder { get; }

    /// <summary>
    /// Adds one stock to the user's own file (<c>my-stocks.json</c>), replacing an entry with the same id, then reloads the catalogue.
    /// Returns the entry as stored.
    /// </summary>
    StockEntry SaveUserStock(StockEntry entry);

    /// <summary>Saves the JSON of an imported supplies list as a new file in the user folder, reloads the catalogue and returns the file name.</summary>
    string SaveImportedCatalog(string json);

    /// <summary>Forgets the cached catalogue so the next access reads the user folder again.</summary>
    void Reload();
}

/// <summary>The real store: the bundled resource plus the files of a folder.</summary>
public sealed class FileStockStore : IStockStore
{
    /// <summary>File that holds the stocks the user adds one by one.</summary>
    public const string OwnStocksFile = "my-stocks.json";

    /// <summary>Id of the source recorded for stocks the user entered by hand.</summary>
    public const string OwnSourceId = "user-entered";

    private readonly object _gate = new();
    private StockCatalog? _catalog;

    /// <summary>Creates a store; the default folder is <c>%AppData%\Rotula\stocks</c>.</summary>
    public FileStockStore(string? userFolder = null) =>
        UserFolder = userFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "stocks");

    /// <inheritdoc />
    public string UserFolder { get; }

    /// <inheritdoc />
    public StockCatalog Catalog
    {
        get
        {
            lock (_gate) return _catalog ??= StockCatalog.LoadCombined(UserFolder);
        }
    }

    /// <inheritdoc />
    public Task<StockCatalog> GetCatalogAsync() => Task.Run(() => Catalog);

    /// <inheritdoc />
    public void Reload()
    {
        lock (_gate) _catalog = null;
    }

    /// <inheritdoc />
    public StockEntry SaveUserStock(StockEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(UserFolder);
        string path = Path.Combine(UserFolder, OwnStocksFile);
        // Read what is there first (LoadFile never throws) and write all entries back: one file for all hand-made stocks keeps the
        // folder far below the limit on the number of files.
        var existing = File.Exists(path) ? StockCatalog.LoadFile(path).Entries : Array.Empty<StockEntry>();
        var all = existing.Where(e => !string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)).Append(entry).ToList();
        var meta = new StockCatalogMetadata(StockLimits.CurrentSchemaVersion, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "Stocks entered by the user in Rotula. Sizes were typed by the user; prices are never stored.",
            new[] { new StockSourceInfo(OwnSourceId, "Entered by the user in Rotula") });
        WriteAtomically(path, StockCatalog.ToJson(meta, all));
        Reload();
        return entry;
    }

    /// <inheritdoc />
    public string SaveImportedCatalog(string json)
    {
        Directory.CreateDirectory(UserFolder);
        string name = "import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        string path = Path.Combine(UserFolder, name);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(UserFolder, Path.GetFileNameWithoutExtension(name) + "-" + i + ".json");
        WriteAtomically(path, json);
        Reload();
        return Path.GetFileName(path);
    }

    /// <summary>Writes through a temporary file, so a failed write never leaves half a catalogue behind.</summary>
    private static void WriteAtomically(string path, string text)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp file is harmless */ }
        }
    }

    /// <summary>Builds the id of a hand-made stock: <c>user-</c> plus the part number or name, made of letters, digits and dashes.</summary>
    public static string MakeId(string partOrName, Func<string, bool> exists)
    {
        string slug = Regex.Replace(partOrName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim('-');
        if (slug.Length == 0) slug = "stock";
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        string id = "user-" + slug;
        for (int i = 2; exists(id); i++) id = "user-" + slug + "-" + i.ToString(CultureInfo.InvariantCulture);
        return id;
    }
}

/// <summary>
/// The process-wide stock store. The properties panel uses it to turn a label's stock id into a name; the New label dialog uses the
/// store it is given. Tests replace <see cref="Current"/> with a store over a temporary folder.
/// </summary>
public static class StockLibrary
{
    /// <summary>The store in use.</summary>
    public static IStockStore Current { get; set; } = new FileStockStore();

    /// <summary>Starts loading the catalogue in the background (called at start-up so the first New label dialog is instant).</summary>
    public static void Preload() => _ = Task.Run(() =>
    {
        try { _ = Current.Catalog; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the dialog loads it again and reports */ }
    });

    /// <summary>
    /// The sentence for the Label panel: "Label stock: name (part number)", or the plain id when the stock is no longer in the library.
    /// Null when the label has no stock.
    /// </summary>
    public static string? Describe(string? stockId, Func<string, string, string> format)
    {
        if (string.IsNullOrEmpty(stockId)) return null;
        var entry = Current.Catalog.Find(stockId);
        string what = entry is null ? stockId : entry.PartNumber is { Length: > 0 } pn ? $"{entry.Name} ({pn})" : entry.Name;
        return format(what, stockId);
    }
}

/// <summary>What the New label dialog needs from the application: the stock store, the lists to remember, the printer resolution.</summary>
public sealed class NewLabelContext
{
    /// <summary>Catalogue and user stocks.</summary>
    public IStockStore Stocks { get; init; } = StockLibrary.Current;

    /// <summary>The favourites and recent stocks live in the same settings object the main window saves, so neither overwrites the other.</summary>
    public AppSettings Settings { get; init; } = new();

    /// <summary>Saves <see cref="Settings"/>.</summary>
    public Action SaveSettings { get; init; } = () => { };

    /// <summary>The resolution to start from (the detected one of the selected printer, else the last used), or null for 203 dpi.</summary>
    public PrinterDpi? DefaultDpi { get; init; }

    /// <summary>Where <see cref="DefaultDpi"/> comes from, for the note under the box: true when the printer reported it.</summary>
    public bool DpiFromPrinter { get; init; }

    /// <summary>True when <see cref="DefaultDpi"/> is the resolution of the last print settings (no printer was read this session).</summary>
    public bool DpiFromLastUse { get; init; }

    /// <summary>File pickers (supplies list).</summary>
    public IFileDialogService? Files { get; init; }
}
