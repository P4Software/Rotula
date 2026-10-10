using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rotula.Core.Stocks;

/// <summary>
/// The user's favourite and recently used stocks, as stock ids. This is only the model: the caller decides where it is
/// stored (for example <c>%AppData%\Rotula\stock-favorites.json</c>) and calls <see cref="ToJson"/> / <see cref="FromJson"/>.
/// Ids that are no longer in the catalogue are kept (a user file may be missing for a moment) and skipped by <see cref="Resolve"/>.
/// </summary>
public sealed class StockUserList
{
    /// <summary>Most favourites kept.</summary>
    public const int MaxFavorites = 200;
    /// <summary>Most recent stocks kept.</summary>
    public const int MaxRecent = 20;

    private readonly List<string> _favorites = new();
    private readonly List<string> _recent = new();

    /// <summary>Favourite ids in the order they were added.</summary>
    public IReadOnlyList<string> Favorites => _favorites;

    /// <summary>Recently used ids, most recent first.</summary>
    public IReadOnlyList<string> Recent => _recent;

    /// <summary>True when the id is a favourite.</summary>
    public bool IsFavorite(string id) => _favorites.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds the id to the favourites when absent, removes it when present. Returns true if it is now a favourite.</summary>
    public bool ToggleFavorite(string id)
    {
        if (!Valid(id)) return false;
        int i = _favorites.FindIndex(f => string.Equals(f, id, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            _favorites.RemoveAt(i);
            return false;
        }
        if (_favorites.Count >= MaxFavorites) return false;
        _favorites.Add(id);
        return true;
    }

    /// <summary>Records that a stock was just used: moves it to the front of <see cref="Recent"/>.</summary>
    public void Touch(string id)
    {
        if (!Valid(id)) return;
        _recent.RemoveAll(r => string.Equals(r, id, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, id);
        if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
    }

    /// <summary>Empties the recent list.</summary>
    public void ClearRecent() => _recent.Clear();

    /// <summary>The favourites that exist in <paramref name="catalog"/>, in order.</summary>
    public IReadOnlyList<StockEntry> ResolveFavorites(StockCatalog catalog) => Resolve(_favorites, catalog);

    /// <summary>The recent stocks that exist in <paramref name="catalog"/>, most recent first.</summary>
    public IReadOnlyList<StockEntry> ResolveRecent(StockCatalog catalog) => Resolve(_recent, catalog);

    private static IReadOnlyList<StockEntry> Resolve(List<string> ids, StockCatalog catalog)
    {
        var list = new List<StockEntry>();
        foreach (string id in ids)
            if (catalog.Find(id) is { } e) list.Add(e);
        return list;
    }

    private static bool Valid(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= StockLimits.MaxIdLength && StockLimits.IdPattern.IsMatch(id);

    /// <summary>Serializes the list as JSON: <c>{"version":1,"favorites":[...],"recent":[...]}</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Dto { Version = 1, Favorites = _favorites, Recent = _recent });

    /// <summary>Reads a list written by <see cref="ToJson"/>. A missing, damaged or oversized text gives an empty list; never throws.</summary>
    public static StockUserList FromJson(string? json)
    {
        var list = new StockUserList();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 200_000) return list;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json, new JsonSerializerOptions { MaxDepth = 8 });
            if (dto is null || dto.Version != 1) return list;
            foreach (string id in dto.Favorites ?? new List<string>())
                if (Valid(id) && !list.IsFavorite(id) && list._favorites.Count < MaxFavorites) list._favorites.Add(id);
            foreach (string id in dto.Recent ?? new List<string>())
                if (Valid(id) && !list._recent.Contains(id, StringComparer.OrdinalIgnoreCase) && list._recent.Count < MaxRecent) list._recent.Add(id);
        }
        catch (JsonException)
        {
            return new StockUserList();
        }
        catch (NotSupportedException)
        {
            return new StockUserList();
        }
        return list;
    }

    private sealed class Dto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("favorites")]
        public List<string>? Favorites { get; set; }
        [JsonPropertyName("recent")]
        public List<string>? Recent { get; set; }
    }
}
