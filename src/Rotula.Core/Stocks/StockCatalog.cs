using Rotula.Core.Validation;

namespace Rotula.Core.Stocks;

/// <summary>A data source cited by catalogue entries (their <c>sources</c> list refers to <see cref="Id"/>).</summary>
/// <param name="Id">Short stable id such as <c>user-import</c>.</param>
/// <param name="Name">Human description of the source (English).</param>
public sealed record StockSourceInfo(string Id, string Name);

/// <summary>The <c>metadata</c> block of a catalogue file.</summary>
/// <param name="SchemaVersion">Schema version of the file (see <see cref="StockLimits.CurrentSchemaVersion"/>).</param>
/// <param name="GeneratedOn">Date the file was generated, as written in the file.</param>
/// <param name="Notes">Free notes: provenance, the public-data statement, known gaps.</param>
/// <param name="Sources">Sources cited by the entries.</param>
public sealed record StockCatalogMetadata(int SchemaVersion, string? GeneratedOn, string? Notes, IReadOnlyList<StockSourceInfo> Sources)
{
    /// <summary>Metadata of a catalogue that could not be read.</summary>
    public static StockCatalogMetadata Empty { get; } = new(0, null, null, Array.Empty<StockSourceInfo>());
}

/// <summary>
/// An immutable set of label stocks. <see cref="LoadBundled"/> returns the library shipped with Rotula;
/// <see cref="LoadUser"/> reads the user's own files; <see cref="Merged"/> combines them (user entries add or override
/// by id). Loading never throws: problems become <see cref="Issues"/> (message keys with arguments, no UI text).
/// </summary>
public sealed class StockCatalog
{
    private const string BundledResource = "Rotula.Core.Stocks.stocks.json";
    private static readonly Lazy<StockCatalog> BundledCache = new(LoadBundledCore, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, int> _byId;
    private readonly Dictionary<string, List<int>> _byPart;
    internal StockIndex Index { get; }

    /// <summary>Creates a catalogue from entries (later duplicates of an id are dropped).</summary>
    public StockCatalog(StockCatalogMetadata metadata, IEnumerable<StockEntry> entries, IEnumerable<ValidationIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(entries);
        Metadata = metadata;
        var list = new List<StockEntry>();
        _byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _byPart = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (_byId.ContainsKey(e.Id)) continue;
            _byId[e.Id] = list.Count;
            if (e.PartNumber is not null)
            {
                string key = StockText.Alnum(e.PartNumber);
                if (key.Length > 0)
                {
                    if (!_byPart.TryGetValue(key, out var bucket)) _byPart[key] = bucket = new List<int>();
                    bucket.Add(list.Count);
                }
            }
            list.Add(e);
        }
        Entries = list;
        Issues = issues?.ToList() ?? new List<ValidationIssue>();
        Index = new StockIndex(list);
    }

    /// <summary>An empty catalogue.</summary>
    public static StockCatalog Empty { get; } = new(StockCatalogMetadata.Empty, Array.Empty<StockEntry>());

    /// <summary>Metadata of the file this catalogue was read from.</summary>
    public StockCatalogMetadata Metadata { get; }

    /// <summary>All entries in file order.</summary>
    public IReadOnlyList<StockEntry> Entries { get; }

    /// <summary>Problems found while loading (skipped entries, damaged files). Empty for a clean load.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Number of entries.</summary>
    public int Count => Entries.Count;

    /// <summary>Finds an entry by id (case-insensitive).</summary>
    public StockEntry? Find(string id) => id is not null && _byId.TryGetValue(id, out int i) ? Entries[i] : null;

    /// <summary>Finds entries whose part number equals <paramref name="partNumber"/> ignoring case and punctuation.</summary>
    public IReadOnlyList<StockEntry> FindByPartNumber(string partNumber)
    {
        string key = StockText.Alnum(partNumber ?? string.Empty);
        if (key.Length == 0 || !_byPart.TryGetValue(key, out var bucket)) return Array.Empty<StockEntry>();
        return bucket.Select(i => Entries[i]).ToList();
    }

    /// <summary>Distinct brands, sorted.</summary>
    public IReadOnlyList<string> Brands => Entries.Select(e => e.Brand).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Distinct stock families, sorted.</summary>
    public IReadOnlyList<string> Families => Entries.Select(e => e.StockFamily).Where(f => f is not null).Select(f => f!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Searches the catalogue. See <see cref="StockSearchOptions"/> and <see cref="StockSearchResult"/>.</summary>
    public StockSearchResult Search(string? query, StockSearchOptions? options = null) => StockSearcher.Search(this, query, options ?? new StockSearchOptions());

    /// <summary>
    /// Stocks near a size, closest first (the "near this size" suggestions). Fixed-size labels only; both orientations are tried.
    /// </summary>
    /// <param name="widthMm">Wanted width in millimetres.</param>
    /// <param name="heightMm">Wanted height in millimetres.</param>
    /// <param name="toleranceMm">Largest difference in either dimension.</param>
    /// <param name="options">Filters; the size fields of the options are ignored.</param>
    public IReadOnlyList<StockSearchHit> FindNear(double widthMm, double heightMm, double toleranceMm = 3.0, StockSearchOptions? options = null)
        => StockSearcher.FindNear(this, widthMm, heightMm, toleranceMm, options ?? new StockSearchOptions(), excludeExactWithin: -1);

    /// <summary>
    /// Combines this catalogue with a user catalogue: user entries with a new id are added, user entries with an
    /// existing id replace the bundled one. Issues of both are kept.
    /// </summary>
    public StockCatalog Merged(StockCatalog user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var overridden = new HashSet<string>(user.Entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var all = new List<StockEntry>(Entries.Count + user.Count);
        foreach (var e in Entries)
            if (!overridden.Contains(e.Id)) all.Add(e);
        all.AddRange(user.Entries);
        return new StockCatalog(Metadata, all, Issues.Concat(user.Issues));
    }

    /// <summary>The library shipped with Rotula (parsed once and cached). A damaged resource yields an empty catalogue with issues.</summary>
    public static StockCatalog LoadBundled() => BundledCache.Value;

    /// <summary>Bundled library plus the user's files from <paramref name="userFolder"/>; the folder may be missing or null.</summary>
    public static StockCatalog LoadCombined(string? userFolder) => LoadBundled().Merged(LoadUser(userFolder));

    /// <summary>
    /// Reads every <c>*.json</c> file in <paramref name="folder"/> (typically <c>%AppData%\Rotula\stocks</c>), in file-name order;
    /// later files override earlier ones by id. A missing folder gives an empty catalogue. Never throws.
    /// </summary>
    public static StockCatalog LoadUser(string? folder)
    {
        var issues = new List<ValidationIssue>();
        var all = new Dictionary<string, StockEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new StockCatalog(StockCatalogMetadata.Empty, Array.Empty<StockEntry>());
            var files = Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count > StockLimits.MaxUserFiles)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileTooManyFiles, StockLimits.MaxUserFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                files = files.Take(StockLimits.MaxUserFiles).ToList();
            }
            foreach (var file in files)
            {
                var loaded = LoadFile(file, StockOrigin.User);
                issues.AddRange(loaded.Issues);
                foreach (var e in loaded.Entries)
                {
                    if (!all.ContainsKey(e.Id)) order.Add(e.Id);
                    all[e.Id] = e;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileUnreadable, folder ?? string.Empty));
        }
        return new StockCatalog(StockCatalogMetadata.Empty, order.Select(id => all[id]), issues);
    }

    /// <summary>Reads one catalogue file with all the safety limits. Never throws.</summary>
    public static StockCatalog LoadFile(string path, StockOrigin origin = StockOrigin.User)
    {
        var issues = new List<ValidationIssue>();
        string name = Path.GetFileName(path ?? string.Empty);
        try
        {
            var info = new FileInfo(path!);
            if (!info.Exists)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileUnreadable, name));
                return new StockCatalog(StockCatalogMetadata.Empty, Array.Empty<StockEntry>(), issues);
            }
            if (info.Length > StockLimits.MaxFileBytes)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileTooLarge, name, StockLimits.MaxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return new StockCatalog(StockCatalogMetadata.Empty, Array.Empty<StockEntry>(), issues);
            }
            byte[] bytes;
            using (var fs = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // Read one byte past the cap: the file may have grown since the length check.
                bytes = ReadCapped(fs, StockLimits.MaxFileBytes + 1);
            }
            if (bytes.Length > StockLimits.MaxFileBytes)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileTooLarge, name, StockLimits.MaxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return new StockCatalog(StockCatalogMetadata.Empty, Array.Empty<StockEntry>(), issues);
            }
            return FromBytes(bytes, name, origin, issues);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileUnreadable, name));
            return new StockCatalog(StockCatalogMetadata.Empty, Array.Empty<StockEntry>(), issues);
        }
    }

    /// <summary>Parses catalogue JSON text (UTF-8 bytes) with the same rules as a file. Never throws.</summary>
    public static StockCatalog FromJson(ReadOnlyMemory<byte> utf8, string fileName = "catalogue.json", StockOrigin origin = StockOrigin.User)
    {
        if (utf8.Length > StockLimits.MaxFileBytes)
            return new StockCatalog(StockCatalogMetadata.Empty, Array.Empty<StockEntry>(),
                new[] { StockMessageKeys.Warn(StockMessageKeys.FileTooLarge, fileName, StockLimits.MaxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
        return FromBytes(utf8.ToArray(), fileName, origin, new List<ValidationIssue>());
    }

    private static StockCatalog FromBytes(byte[] bytes, string name, StockOrigin origin, List<ValidationIssue> issues)
    {
        // A UTF-8 byte order mark is legal in files written by Windows editors but not accepted by the JSON reader.
        ReadOnlyMemory<byte> mem = bytes;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) mem = mem[3..];
        var (meta, entries) = StockJson.ParseDocument(mem, name, origin, issues);
        return new StockCatalog(meta, entries, issues);
    }

    private static byte[] ReadCapped(Stream s, int max)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int total = 0, n;
        while (total < max && (n = s.Read(buffer, 0, Math.Min(buffer.Length, max - total))) > 0)
        {
            ms.Write(buffer, 0, n);
            total += n;
        }
        return ms.ToArray();
    }

    private static StockCatalog LoadBundledCore()
    {
        var issues = new List<ValidationIssue>();
        try
        {
            using var stream = typeof(StockCatalog).Assembly.GetManifestResourceStream(BundledResource);
            if (stream is null)
            {
                issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileUnreadable, "stocks.json"));
                return new StockCatalog(StockCatalogMetadata.Empty, Array.Empty<StockEntry>(), issues);
            }
            return FromBytes(ReadCapped(stream, StockLimits.MaxFileBytes + 1), "stocks.json", StockOrigin.Bundled, issues);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileLoadException or BadImageFormatException)
        {
            issues.Add(StockMessageKeys.Warn(StockMessageKeys.FileUnreadable, "stocks.json"));
            return new StockCatalog(StockCatalogMetadata.Empty, Array.Empty<StockEntry>(), issues);
        }
    }

    /// <summary>Serializes entries as a user catalogue file in the current schema (null fields left out).</summary>
    public static string ToJson(StockCatalogMetadata metadata, IEnumerable<StockEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(entries);
        return StockJson.Write(metadata, entries);
    }
}
