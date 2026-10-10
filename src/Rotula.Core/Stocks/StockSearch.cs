using System.Globalization;
using System.Text;

namespace Rotula.Core.Stocks;

/// <summary>Text normalisation shared by indexing and querying.</summary>
internal static class StockText
{
    /// <summary>Lower-case letters and digits only, accents removed: <c>Z-Perform 1000D</c> becomes <c>zperform1000d</c>.</summary>
    public static string Alnum(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in Fold(s))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Splits into normalised words (runs of letters and digits).</summary>
    public static List<string> Words(string s)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in Fold(s))
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    private static string Fold(string s)
    {
        bool ascii = true;
        foreach (char c in s)
            if (c > 127) { ascii = false; break; }
        if (ascii) return s;
        string d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>Precomputed search keys, one per entry, built once per catalogue.</summary>
internal sealed class StockIndex
{
    public sealed class Row
    {
        public required StockEntry Entry { get; init; }
        public required string Part { get; init; }
        public required string Joined { get; init; }
        public required bool[] WordStart { get; init; }
        public required string BrandKey { get; init; }
        public required string FamilyKey { get; init; }
    }

    public StockIndex(IReadOnlyList<StockEntry> entries)
    {
        Rows = new Row[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var sb = new StringBuilder();
            var starts = new List<int>();
            foreach (string field in new[] { e.Brand, e.Name, e.Series ?? string.Empty, e.StockFamily ?? string.Empty, e.Category ?? string.Empty, e.PartNumber ?? string.Empty, e.Finish ?? string.Empty })
                foreach (string w in StockText.Words(field))
                {
                    starts.Add(sb.Length);
                    sb.Append(w);
                }
            var flags = new bool[sb.Length + 1];
            foreach (int s in starts) flags[s] = true;
            Rows[i] = new Row
            {
                Entry = e,
                Part = e.PartNumber is null ? string.Empty : StockText.Alnum(e.PartNumber),
                Joined = sb.ToString(),
                WordStart = flags,
                BrandKey = StockText.Alnum(e.Brand),
                FamilyKey = StockText.Alnum((e.StockFamily ?? string.Empty) + (e.Series ?? string.Empty)),
            };
        }
    }

    public Row[] Rows { get; }
}

/// <summary>Which parts of a query an entry matched.</summary>
[Flags]
public enum StockMatchKind
{
    /// <summary>Nothing specific (listing without a query).</summary>
    None = 0,
    /// <summary>The whole query is the part number.</summary>
    PartExact = 1,
    /// <summary>A query word is the start of the part number.</summary>
    PartPrefix = 2,
    /// <summary>A query word occurs inside the part number.</summary>
    PartContains = 4,
    /// <summary>Query words matched the name, family or brand.</summary>
    Text = 8,
    /// <summary>The size in the query matches the entry within the exact tolerance.</summary>
    Size = 16,
    /// <summary>The size matched with width and height swapped.</summary>
    SizeSwapped = 32,
}

/// <summary>Search settings and filters. All filters are optional.</summary>
public sealed class StockSearchOptions
{
    /// <summary>Only this brand (case-insensitive, exact).</summary>
    public string? Brand { get; set; }
    /// <summary>Only families or series containing this text (punctuation and case ignored).</summary>
    public string? Family { get; set; }
    /// <summary>Only stocks normally quoted in this unit.</summary>
    public StockUnit? NativeUnit { get; set; }
    /// <summary>Hide entries whose size is not verified.</summary>
    public bool VerifiedOnly { get; set; }
    /// <summary>Only this layout (roll, sheet, round).</summary>
    public StockLayout? Layout { get; set; }
    /// <summary>
    /// Only stocks usable with this technology: <see cref="StockMedia.DirectThermal"/> or <see cref="StockMedia.ThermalTransfer"/>
    /// (entries marked <see cref="StockMedia.Either"/> match both; entries with unknown media are hidden). Other values are ignored.
    /// </summary>
    public StockMedia? Media { get; set; }
    /// <summary>Largest difference in width and in height for a size to count as a match, in millimetres (default 0.6, the catalogue rounding).</summary>
    public double SizeToleranceMm { get; set; } = 0.6;
    /// <summary>Largest difference for the "near this size" suggestions in millimetres; 0 turns suggestions off.</summary>
    public double NearToleranceMm { get; set; } = 3.0;
    /// <summary>Also accept a size with width and height swapped (typed 6x4, stored 4x6).</summary>
    public bool AllowSwappedSize { get; set; } = true;
    /// <summary>Most hits returned.</summary>
    public int MaxResults { get; set; } = 100;
    /// <summary>Most near-size suggestions returned.</summary>
    public int MaxNear { get; set; } = 10;
}

/// <summary>One ranked search result.</summary>
/// <param name="Entry">The stock.</param>
/// <param name="Score">Ranking score; higher is better.</param>
/// <param name="Matched">What matched.</param>
/// <param name="SizeDeltaMm">Largest width/height difference to the searched size in millimetres, or null when no size was searched.</param>
public sealed record StockSearchHit(StockEntry Entry, int Score, StockMatchKind Matched, double? SizeDeltaMm);

/// <summary>Result of a search.</summary>
public sealed class StockSearchResult
{
    internal StockSearchResult(IReadOnlyList<StockSearchHit> hits, int total, IReadOnlyList<StockSearchHit> near, ParsedSize? size)
    {
        Hits = hits;
        TotalHits = total;
        NearHits = near;
        Size = size;
    }

    /// <summary>Ranked matches, best first, at most <see cref="StockSearchOptions.MaxResults"/>.</summary>
    public IReadOnlyList<StockSearchHit> Hits { get; }
    /// <summary>How many entries matched in all (before the result cap).</summary>
    public int TotalHits { get; }
    /// <summary>Stocks that miss the searched size by a little, closest first (empty when the query had no size).</summary>
    public IReadOnlyList<StockSearchHit> NearHits { get; }
    /// <summary>The size found in the query, if any.</summary>
    public ParsedSize? Size { get; }
}

internal static class StockSearcher
{
    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal) { "in", "inch", "inches", "mm", "cm", "x", "by", "label", "labels", "etiqueta", "etiquetas" };

    public static StockSearchResult Search(StockCatalog catalog, string? query, StockSearchOptions o)
    {
        string q = query is { Length: > 300 } ? query[..300] : query ?? string.Empty;
        var sizes = StockSizeParser.FindAll(q);
        // A size that is implausible in every reading (30x2 with no unit) is not a size query: its text stays
        // in the words, so it cannot turn into an empty query that lists the whole catalogue.
        ParsedSize? size = sizes.Count > 0 && sizes[0].Candidates.Count > 0 ? sizes[0] : null;
        string rest = size is null ? q : StockSizeParser.Remove(q, new[] { size });

        var tokens = new List<string>();
        foreach (string raw in rest.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = StockText.Alnum(raw);
            if (t.Length == 0 || NoiseWords.Contains(t)) continue;
            tokens.Add(t);
        }
        string whole = StockText.Alnum(rest);

        var candidates = size?.Candidates ?? Array.Empty<(double WidthMm, double HeightMm, StockSizeUnit Unit)>();
        bool sizeSearch = size is not null;

        var hits = new List<StockSearchHit>();
        var near = new List<StockSearchHit>();
        foreach (var row in catalog.Index.Rows)
        {
            if (!PassesFilters(row, o)) continue;

            int score = 0;
            var kind = StockMatchKind.None;
            if (tokens.Count > 0 && !MatchTokens(row, tokens, whole, ref score, ref kind)) continue;

            double? delta = null;
            bool swapped = false;
            if (sizeSearch)
            {
                (delta, swapped) = BestDelta(row.Entry, candidates, o.AllowSwappedSize);
                if (delta is null) continue;
                if (delta > o.SizeToleranceMm)
                {
                    if (o.NearToleranceMm > 0 && delta <= Math.Max(o.NearToleranceMm, o.SizeToleranceMm))
                        near.Add(new StockSearchHit(row.Entry, 100 - (int)(delta.Value * 10) + (row.Entry.Verified ? 5 : 0), kind, delta));
                    continue;
                }
                score += 500 - (int)(delta.Value * 100) - (swapped ? 50 : 0);
                kind |= swapped ? StockMatchKind.SizeSwapped : StockMatchKind.Size;
            }
            if (row.Entry.Verified) score += 5;
            hits.Add(new StockSearchHit(row.Entry, score, kind, delta));
        }

        hits.Sort(CompareHits);
        near.Sort((a, b) => a.SizeDeltaMm!.Value != b.SizeDeltaMm!.Value ? a.SizeDeltaMm.Value.CompareTo(b.SizeDeltaMm.Value) : CompareHits(a, b));
        int total = hits.Count;
        int max = Math.Clamp(o.MaxResults, 1, 5000);
        if (hits.Count > max) hits.RemoveRange(max, hits.Count - max);
        int maxNear = Math.Clamp(o.MaxNear, 0, 500);
        if (near.Count > maxNear) near.RemoveRange(maxNear, near.Count - maxNear);
        return new StockSearchResult(hits, total, near, size);
    }

    public static IReadOnlyList<StockSearchHit> FindNear(StockCatalog catalog, double widthMm, double heightMm, double toleranceMm, StockSearchOptions o, double excludeExactWithin)
    {
        var cand = new[] { (widthMm, heightMm, StockSizeUnit.Millimeters) };
        var list = new List<StockSearchHit>();
        foreach (var row in catalog.Index.Rows)
        {
            if (!PassesFilters(row, o)) continue;
            var (delta, _) = BestDelta(row.Entry, cand, o.AllowSwappedSize);
            if (delta is null || delta > toleranceMm || delta <= excludeExactWithin) continue;
            list.Add(new StockSearchHit(row.Entry, 1000 - (int)(delta.Value * 100) + (row.Entry.Verified ? 5 : 0), StockMatchKind.Size, delta));
        }
        list.Sort((a, b) => a.SizeDeltaMm!.Value != b.SizeDeltaMm!.Value ? a.SizeDeltaMm.Value.CompareTo(b.SizeDeltaMm.Value) : CompareHits(a, b));
        int max = Math.Clamp(o.MaxResults, 1, 5000);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    private static int CompareHits(StockSearchHit a, StockSearchHit b)
    {
        int c = b.Score.CompareTo(a.Score);
        if (c != 0) return c;
        c = b.Entry.Verified.CompareTo(a.Entry.Verified);
        if (c != 0) return c;
        // Deterministic order: shorter names first, then by id.
        c = a.Entry.Name.Length.CompareTo(b.Entry.Name.Length);
        return c != 0 ? c : string.CompareOrdinal(a.Entry.Id, b.Entry.Id);
    }

    private static bool PassesFilters(StockIndex.Row row, StockSearchOptions o)
    {
        var e = row.Entry;
        if (o.VerifiedOnly && !e.Verified) return false;
        if (o.Layout is { } l && e.Layout != l) return false;
        if (o.NativeUnit is { } u && e.NativeUnit != u) return false;
        if (!string.IsNullOrWhiteSpace(o.Brand) && row.BrandKey != StockText.Alnum(o.Brand)) return false;
        if (!string.IsNullOrWhiteSpace(o.Family))
        {
            string f = StockText.Alnum(o.Family);
            if (f.Length > 0 && !row.FamilyKey.Contains(f, StringComparison.Ordinal)) return false;
        }
        if (o.Media is { } m && m is StockMedia.DirectThermal or StockMedia.ThermalTransfer)
        {
            if (e.Media != m && e.Media != StockMedia.Either) return false;
        }
        return true;
    }

    private static (double? Delta, bool Swapped) BestDelta(StockEntry e, IReadOnlyList<(double WidthMm, double HeightMm, StockSizeUnit Unit)> candidates, bool allowSwap)
    {
        if (e.HeightMm is not { } h) return (null, false);
        double best = double.MaxValue;
        bool swapped = false;
        foreach (var (cw, ch, _) in candidates)
        {
            double d = Math.Max(Math.Abs(e.WidthMm - cw), Math.Abs(h - ch));
            if (d < best) { best = d; swapped = false; }
            if (allowSwap)
            {
                double s = Math.Max(Math.Abs(e.WidthMm - ch), Math.Abs(h - cw));
                if (s < best) { best = s; swapped = true; }
            }
        }
        return best == double.MaxValue ? (null, false) : (Math.Round(best, 3), swapped);
    }

    private static bool MatchTokens(StockIndex.Row row, List<string> tokens, string whole, ref int score, ref StockMatchKind kind)
    {
        // The whole query as one part number beats every word-by-word reading.
        if (row.Part.Length > 0 && whole.Length > 0 && row.Part == whole)
        {
            score += 1000;
            kind |= StockMatchKind.PartExact;
            return true;
        }
        foreach (string t in tokens)
        {
            int best = 0;
            var k = StockMatchKind.None;
            if (row.Part.Length > 0)
            {
                if (row.Part == t) { best = 900; k = StockMatchKind.PartExact; }
                else if (row.Part.StartsWith(t, StringComparison.Ordinal)) { best = 700; k = StockMatchKind.PartPrefix; }
                else if (t.Length >= 3 && row.Part.Contains(t, StringComparison.Ordinal)) { best = 400; k = StockMatchKind.PartContains; }
            }
            if (best < 120)
            {
                int ts = TextScore(row, t);
                if (ts > best) { best = ts; k = StockMatchKind.Text; }
            }
            if (best == 0) return false;
            score += best;
            kind |= k;
        }
        return true;
    }

    private static int TextScore(StockIndex.Row row, string t)
    {
        string j = row.Joined;
        int from = 0, best = 0;
        while (from <= j.Length - t.Length)
        {
            int at = j.IndexOf(t, from, StringComparison.Ordinal);
            if (at < 0) break;
            if (row.WordStart[at]) return 120;
            if (t.Length >= 3) best = 40;
            from = at + 1;
        }
        return best;
    }
}
