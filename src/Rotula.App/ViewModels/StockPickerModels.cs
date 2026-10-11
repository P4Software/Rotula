using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Localization;
using Rotula.Core.Model;
using Rotula.Core.Stocks;
using Rotula.Core.Validation;

namespace Rotula.App.ViewModels;

/// <summary>What a row of the stock list stands for.</summary>
public enum StockItemKind
{
    /// <summary>One of the five common sizes.</summary>
    Common,
    /// <summary>The "Custom size..." row: type your own width and height.</summary>
    Custom,
    /// <summary>A stock of the library or of the user.</summary>
    Stock,
}

/// <summary>One row of the stock list in the New label dialog: a common size, the custom-size row, or a stock.</summary>
public sealed class StockListItem : ObservableObject
{
    private readonly string _groupKey;
    private bool _isFavorite;

    private StockListItem(StockItemKind kind, string groupKey)
    {
        Kind = kind;
        _groupKey = groupKey;
    }

    /// <summary>Row for a common size.</summary>
    public static StockListItem ForPreset(LabelPreset preset, string groupKey) => new(preset.NameKey == "Preset.Custom" ? StockItemKind.Custom : StockItemKind.Common, groupKey) { Preset = preset };

    /// <summary>Row for a stock.</summary>
    public static StockListItem ForStock(StockEntry stock, string groupKey, bool isFavorite) => new(StockItemKind.Stock, groupKey) { Stock = stock, _isFavorite = isFavorite };

    /// <summary>What the row is.</summary>
    public StockItemKind Kind { get; }

    /// <summary>The stock, for a stock row.</summary>
    public StockEntry? Stock { get; private init; }

    /// <summary>The preset, for a common size or the custom row.</summary>
    public LabelPreset? Preset { get; private init; }

    /// <summary>Translated heading of the group the row is listed under.</summary>
    public string GroupName => Loc.Get(_groupKey);

    /// <summary>Stable id used to find the row again (stock id, or the preset key).</summary>
    public string Key => Stock?.Id ?? Preset!.NameKey;

    /// <summary>Main line.</summary>
    public string Name => Stock?.Name ?? (Kind == StockItemKind.Custom ? Loc.Get("Stock.Custom.Name") : Preset!.Name);

    /// <summary>Second line: part number and family, or an invitation for the custom row.</summary>
    public string Subtitle
    {
        get
        {
            if (Kind == StockItemKind.Custom) return Loc.Get("Stock.Custom.Sub");
            // A common size shows its size in the other unit, so 4 x 6 in also reads 101.6 x 152.4 mm.
            if (Stock is null) return StockText.PresetOtherSize(Preset!);
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Stock.PartNumber)) parts.Add(Stock.PartNumber!);
            parts.Add(Stock.Origin == StockOrigin.User ? Loc.Get("Stock.Origin.User") : Stock.Brand);
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Size in both units, for example "4 x 6 in  ·  101.6 x 152.4 mm", in the language of the interface.</summary>
    public string SizeText => Stock is { } s ? StockText.Size(s) : string.Empty;

    /// <summary>Print type, or empty when not stated.</summary>
    public string MediaText => Stock is null ? string.Empty : StockText.Media(Stock.Media);

    /// <summary>True for a stock whose size is verified.</summary>
    public bool IsVerified => Stock?.Verified == true;

    /// <summary>True for a stock whose size is not verified (a small "unverified" tag is shown).</summary>
    public bool IsUnverified => Stock is { Verified: false };

    /// <summary>True when the row is a stock (shows the star and the badge).</summary>
    public bool IsStock => Stock is not null;

    /// <summary>True when the stock is starred.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value)) OnPropertyChanged(nameof(FavoriteName));
        }
    }

    /// <summary>Name of the star button for screen readers.</summary>
    public string FavoriteName => Loc.Get(IsFavorite ? "Stock.Favorite.Remove" : "Stock.Favorite.Add") + ": " + Name;

    /// <summary>One sentence for screen readers: name, part number, size, print type, verified.</summary>
    public string AutomationText
    {
        get
        {
            var parts = new List<string> { Name };
            if (Stock?.PartNumber is { Length: > 0 } pn) parts.Add(pn);
            if (SizeText.Length > 0) parts.Add(SizeText);
            if (MediaText.Length > 0) parts.Add(MediaText);
            if (Stock is not null) parts.Add(Loc.Get(IsVerified ? "Stock.Verified" : "Stock.Unverified"));
            if (IsFavorite) parts.Add(Loc.Get("Stock.Favorite.Is"));
            return string.Join(", ", parts);
        }
    }

    /// <inheritdoc />
    public override string ToString() => AutomationText;

    /// <summary>Re-reads the translated texts after a language switch.</summary>
    public void Refresh()
    {
        foreach (var n in new[] { nameof(GroupName), nameof(Name), nameof(Subtitle), nameof(SizeText), nameof(MediaText), nameof(FavoriteName), nameof(AutomationText) })
            OnPropertyChanged(n);
    }
}

/// <summary>One line of the facts table in the detail pane.</summary>
/// <param name="Label">Translated name of the fact.</param>
/// <param name="Value">The value.</param>
public sealed record StockFact(string Label, string Value);

/// <summary>Text helpers for stocks: sizes, print types, facts and warnings in plain language and in the interface language.</summary>
public static class StockText
{
    private static CultureInfo Culture => Loc.Instance.Culture;

    private static string Num(double v, string format = "0.###") => v.ToString(format, Culture);

    /// <summary>Both units, native unit first: "4 x 6 in  ·  101.6 x 152.4 mm". A continuous roll shows only its width.</summary>
    public static string Size(StockEntry s)
    {
        string mm = s.HeightMm is { } hm ? Loc.Get("Stock.Size.Mm", Num(s.WidthMm, "0.##"), Num(hm, "0.##")) : Loc.Get("Stock.Size.WidthMm", Num(s.WidthMm, "0.##"));
        string inch = s.HeightIn is { } hi ? Loc.Get("Stock.Size.In", Num(s.WidthIn), Num(hi)) : Loc.Get("Stock.Size.WidthIn", Num(s.WidthIn));
        return s.NativeUnit == StockUnit.Inches ? inch + "  ·  " + mm : mm + "  ·  " + inch;
    }

    /// <summary>The native-unit size of a stock on its own: "4 x 6 in".</summary>
    public static string NativeSize(StockEntry s)
    {
        if (s.NativeUnit == StockUnit.Inches)
            return s.HeightIn is { } hi ? Loc.Get("Stock.Size.In", Num(s.WidthIn), Num(hi)) : Loc.Get("Stock.Size.WidthIn", Num(s.WidthIn));
        return s.HeightMm is { } hm ? Loc.Get("Stock.Size.Mm", Num(s.WidthMm, "0.##"), Num(hm, "0.##")) : Loc.Get("Stock.Size.WidthMm", Num(s.WidthMm, "0.##"));
    }

    /// <summary>The size in the other unit of a stock: "101.6 x 152.4 mm" for an inch stock.</summary>
    public static string OtherSize(StockEntry s)
    {
        if (s.NativeUnit == StockUnit.Inches)
            return s.HeightMm is { } hm ? Loc.Get("Stock.Size.Mm", Num(s.WidthMm, "0.##"), Num(hm, "0.##")) : Loc.Get("Stock.Size.WidthMm", Num(s.WidthMm, "0.##"));
        return s.HeightIn is { } hi ? Loc.Get("Stock.Size.In", Num(s.WidthIn), Num(hi)) : Loc.Get("Stock.Size.WidthIn", Num(s.WidthIn));
    }

    /// <summary>Size of a common size, in its own unit.</summary>
    public static string PresetSize(LabelPreset p) =>
        Loc.Get(p.Unit == LengthUnit.Inches ? "Stock.Size.In" : "Stock.Size.Mm", Num(p.Width), Num(p.Height));

    /// <summary>The size of a common size in the other unit: "101.6 x 152.4 mm" for 4 x 6 in.</summary>
    public static string PresetOtherSize(LabelPreset p)
    {
        if (p.Unit == LengthUnit.Inches)
            return Loc.Get("Stock.Size.Mm", Num(Math.Round(p.Width * 25.4, 2), "0.##"), Num(Math.Round(p.Height * 25.4, 2), "0.##"));
        return Loc.Get("Stock.Size.In", Num(Math.Round(p.Width / 25.4, 3)), Num(Math.Round(p.Height / 25.4, 3)));
    }

    /// <summary>Print type in words.</summary>
    public static string Media(StockMedia media) => Loc.Get("Stock.Media." + media);

    /// <summary>Supply format in words.</summary>
    public static string Format(StockFormat format) => Loc.Get("Stock.Format." + format);

    /// <summary>Every known fact of a stock, for the detail pane. Unknown values are left out, except the gap, which says that it is unknown.</summary>
    public static IReadOnlyList<StockFact> Facts(StockEntry s, StockCatalog catalog)
    {
        var list = new List<StockFact>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(new StockFact(Loc.Get(key), value!));
        }
        Add("Stock.Fact.Brand", s.Origin == StockOrigin.User ? Loc.Get("Stock.Origin.User") : s.Brand);
        Add("Stock.Fact.Part", s.PartNumber);
        Add("Stock.Fact.Family", s.StockFamily ?? s.Series);
        Add("Stock.Fact.Size", Size(s));
        if (s.Media != StockMedia.Unknown) Add("Stock.Fact.Media", Media(s.Media));
        if (s.Format != StockFormat.Unknown) Add("Stock.Fact.Format", Format(s.Format));
        Add("Stock.Fact.Finish", s.Finish);
        if (s.CoreMm is { } core) Add("Stock.Fact.Core", Loc.Get("Stock.Size.CoreMm", Num(core, "0.##"), Num(core / 25.4, "0.##")));
        if (s.LabelsPerRoll is { } n) Add("Stock.Fact.PerRoll", n.ToString("N0", Culture));
        if (s.RollLengthM is { } len) Add("Stock.Fact.RollLength", Loc.Get("Stock.Size.Metres", Num(len, "0.##")));
        if (s.Columns > 1 || s.Rows > 1) Add("Stock.Fact.Across", Loc.Get("Stock.Across.Format", s.Columns, s.Rows));
        else Add("Stock.Fact.Across", "1");
        if (s.CornerRadiusMm is { } r) Add("Stock.Fact.Corner", Loc.Get("Stock.Size.Mm1", Num(r, "0.##")));
        if (s.Sensing == StockSensing.Mark) Add("Stock.Fact.Gap", Loc.Get("Stock.Gap.Mark"));
        else if (s.GapMm is { } gap) Add("Stock.Fact.Gap", Loc.Get("Stock.Size.Mm1", Num(gap, "0.##")));
        else if (!s.IsContinuous) Add("Stock.Fact.Gap", Loc.Get("Stock.Gap.Unknown"));
        if (s.HGapMm is { } hg) Add("Stock.Fact.HGap", Loc.Get("Stock.Size.Mm1", Num(hg, "0.##")));
        if (s.VGapMm is { } vg) Add("Stock.Fact.VGap", Loc.Get("Stock.Size.Mm1", Num(vg, "0.##")));
        if (s.Sources.Count > 0)
        {
            var names = s.Sources.Select(id => catalog.Metadata.Sources.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id);
            Add("Stock.Fact.Sources", string.Join("; ", names));
        }
        string verdict = Loc.Get(s.Verified ? "Stock.Verified.Tip" : "Stock.Unverified.Tip");
        // The note of the catalogue is a short English label of how the check was done; it is kept for whoever wants to trace the entry.
        Add("Stock.Fact.Verification", s.Verification is { Length: > 0 } v ? $"{verdict} ({v})" : verdict);
        return list;
    }

    /// <summary>
    /// A warning of <see cref="StockApplier"/> in plain language. The library names the stock by id; here the reader sees its name and
    /// part number instead, and the roll-length remark is added only when a length is known.
    /// </summary>
    public static string Warning(ValidationIssue issue, StockEntry stock)
    {
        string label = stock.PartNumber is { Length: > 0 } pn ? $"{stock.Name} ({pn})" : stock.Name;
        var args = issue.Args.Select(a => string.Equals(a, stock.Id, StringComparison.OrdinalIgnoreCase) ? label : a).ToArray();
        if (issue.MessageKey == StockMessageKeys.ApplyContinuousNoHeight)
        {
            string text = Loc.Get("Issue." + issue.MessageKey, label, string.Empty);
            return issue.Args.Count > 1 && issue.Args[1].Length > 0 ? text + " " + Loc.Get("Stock.RollLength.Sentence", issue.Args[1]) : text;
        }
        return Loc.FormatIssue(new ValidationIssue(issue.Severity, issue.MessageKey, issue.ElementId, args));
    }
}
