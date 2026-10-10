using System.Globalization;

namespace Rotula.Core.Stocks;

/// <summary>Print technology a stock is made for.</summary>
public enum StockMedia
{
    /// <summary>Not stated by the source.</summary>
    Unknown,
    /// <summary>Direct thermal (no ribbon).</summary>
    DirectThermal,
    /// <summary>Thermal transfer (needs a ribbon).</summary>
    ThermalTransfer,
    /// <summary>Works with both technologies.</summary>
    Either,
}

/// <summary>How the stock is supplied.</summary>
public enum StockFormat
{
    /// <summary>Not stated by the source.</summary>
    Unknown,
    /// <summary>Die-cut labels on a roll.</summary>
    Roll,
    /// <summary>Continuous roll without label gaps (the label length is chosen by the user).</summary>
    Continuous,
    /// <summary>Liner-free roll.</summary>
    Linerless,
    /// <summary>Fan-folded stack.</summary>
    Fanfold,
    /// <summary>Cut sheets (office printer sheets).</summary>
    Sheet,
}

/// <summary>How a printer finds the start of the next label.</summary>
public enum StockSensing
{
    /// <summary>Not stated; leave it to printer calibration.</summary>
    Unknown,
    /// <summary>Gap or web between labels.</summary>
    Gap,
    /// <summary>Black mark on the back of the stock.</summary>
    Mark,
    /// <summary>No separation at all.</summary>
    Continuous,
}

/// <summary>Coarse physical layout used by search filters.</summary>
public enum StockLayout
{
    /// <summary>Rectangular labels on a roll or fan-fold stack.</summary>
    Roll,
    /// <summary>Labels on cut sheets.</summary>
    Sheet,
    /// <summary>Round or oval labels.</summary>
    Round,
}

/// <summary>Unit in which a stock is normally quoted by its maker (used for display and filtering).</summary>
public enum StockUnit
{
    /// <summary>Quoted in inches, for example 4 x 6 in.</summary>
    Inches,
    /// <summary>Quoted in millimetres, for example 100 x 150 mm.</summary>
    Millimeters,
}

/// <summary>Where an entry came from.</summary>
public enum StockOrigin
{
    /// <summary>Shipped inside Rotula.</summary>
    Bundled,
    /// <summary>Read from a user file in the stocks folder.</summary>
    User,
}

/// <summary>Sheet margins in millimetres (optional; no bundled entry has them yet).</summary>
/// <param name="Left">Left margin.</param>
/// <param name="Top">Top margin.</param>
/// <param name="Right">Right margin.</param>
/// <param name="Bottom">Bottom margin.</param>
public sealed record StockMargins(double Left, double Top, double Right, double Bottom);

/// <summary>
/// One label stock (a part number or a generic size): physical size, supply format and provenance. Instances are
/// immutable once built. Prices and printer-driver settings are deliberately not part of the model.
/// </summary>
public sealed class StockEntry
{
    /// <summary>Stable id, unique inside a catalogue (letters, digits, dot, dash, underscore).</summary>
    public required string Id { get; init; }
    /// <summary>Brand name, or <c>Generic</c>. Brand names belong to their owners.</summary>
    public string Brand { get; init; } = "Generic";
    /// <summary>Manufacturer part number, if the stock has one.</summary>
    public string? PartNumber { get; init; }
    /// <summary>Descriptive name.</summary>
    public required string Name { get; init; }
    /// <summary>Product series, for example <c>Z-Perform 1000D</c>.</summary>
    public string? Series { get; init; }
    /// <summary>Product family used for grouping and filtering.</summary>
    public string? StockFamily { get; init; }

    /// <summary>Label width in millimetres. For wristbands and other tall stocks this is the short side.</summary>
    public double WidthMm { get; init; }
    /// <summary>Label height (feed direction) in millimetres, or null for a continuous roll.</summary>
    public double? HeightMm { get; init; }
    /// <summary>Label width in inches (exact value where the source states one).</summary>
    public double WidthIn { get; init; }
    /// <summary>Label height in inches, or null for a continuous roll.</summary>
    public double? HeightIn { get; init; }

    /// <summary>Gap between labels along the roll in millimetres. Null means unknown (printer calibration).</summary>
    public double? GapMm { get; init; }
    /// <summary>Sensing mode when the source states one (a <c>"mark"</c> value in the file's <c>gapMm</c> field).</summary>
    public StockSensing Sensing { get; init; }
    /// <summary>Gap between label rows in millimetres (multi-up stocks only).</summary>
    public double? VGapMm { get; init; }
    /// <summary>Gap between label columns in millimetres (multi-up stocks only).</summary>
    public double? HGapMm { get; init; }
    /// <summary>Sheet margins, if known.</summary>
    public StockMargins? Margins { get; init; }
    /// <summary>Corner radius of the die cut in millimetres, or null when unknown.</summary>
    public double? CornerRadiusMm { get; init; }

    /// <summary>Print technology.</summary>
    public StockMedia Media { get; init; }
    /// <summary>Supply format.</summary>
    public StockFormat Format { get; init; }
    /// <summary>Surface and adhesive description.</summary>
    public string? Finish { get; init; }
    /// <summary>Shape such as <c>rectangle</c> or <c>round</c>; null when not stated (rectangular).</summary>
    public string? Shape { get; init; }
    /// <summary>Coarse category such as <c>wristband</c>, <c>shipping</c> or <c>avery-sheet</c>.</summary>
    public string? Category { get; init; }
    /// <summary>Core diameter in millimetres.</summary>
    public double? CoreMm { get; init; }
    /// <summary>Labels per roll.</summary>
    public int? LabelsPerRoll { get; init; }
    /// <summary>Rolls per carton.</summary>
    public int? RollsPerCarton { get; init; }
    /// <summary>Roll length in metres (continuous and long rolls).</summary>
    public double? RollLengthM { get; init; }

    /// <summary>Labels across the web (1 = single). Multi-up layout is not applied to documents yet.</summary>
    public int Columns { get; init; } = 1;
    /// <summary>Labels down (sheets).</summary>
    public int Rows { get; init; } = 1;

    /// <summary>Sales regions the source lists, if any.</summary>
    public IReadOnlyList<string> Regions { get; init; } = Array.Empty<string>();
    /// <summary>Public page the data came from (https only), if any.</summary>
    public string? SourceUrl { get; init; }
    /// <summary>Provenance: ids of the sources that state this entry (see the catalogue metadata).</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    /// <summary>True when the size was cross-checked by an independent statement.</summary>
    public bool Verified { get; init; }
    /// <summary>How the verification was done, free text key such as <c>2+ sources agree on size</c>.</summary>
    public string? Verification { get; init; }
    /// <summary>Free notes (English).</summary>
    public string? Notes { get; init; }
    /// <summary>Whether the entry is bundled or from a user file.</summary>
    public StockOrigin Origin { get; init; }

    /// <summary>True for a continuous or liner-free roll without a fixed label height.</summary>
    public bool IsContinuous => HeightMm is null;

    /// <summary>Coarse layout used by filters.</summary>
    public StockLayout Layout
    {
        get
        {
            if (Format == StockFormat.Sheet || string.Equals(Category, "avery-sheet", StringComparison.OrdinalIgnoreCase)
                || Rows > 1 && Format == StockFormat.Unknown) return StockLayout.Sheet;
            if (Shape is { } s && (s.Equals("round", StringComparison.OrdinalIgnoreCase)
                || s.Equals("circle", StringComparison.OrdinalIgnoreCase)
                || s.Equals("oval", StringComparison.OrdinalIgnoreCase)
                || s.Equals("ellipse", StringComparison.OrdinalIgnoreCase))) return StockLayout.Round;
            return StockLayout.Roll;
        }
    }

    /// <summary>
    /// Unit the maker quotes the size in. Read from the name when it states a size with a unit (<c>2 x 1 in</c>,
    /// <c>102 x 152 mm</c>); otherwise inches when both inch figures are whole sixteenths, millimetres if not.
    /// Used for display, filters and to pick the unit a label is sized in.
    /// </summary>
    public StockUnit NativeUnit
    {
        get
        {
            if (_nativeUnit is { } cached) return cached;
            StockUnit result;
            var named = StockSizeParser.FindAll(Name).FirstOrDefault(s => s.UnitWritten && s.FirstUnit == s.SecondUnit);
            if (named is not null) result = named.FirstUnit == StockSizeUnit.Inches ? StockUnit.Inches : StockUnit.Millimeters;
            else if (!IsSixteenth(WidthIn) || (HeightIn is { } h && !IsSixteenth(h))) result = StockUnit.Millimeters;
            else result = StockUnit.Inches;
            _nativeUnit = result;
            return result;
        }
    }

    private StockUnit? _nativeUnit;

    /// <summary>Short size text in the native unit, for example <c>4 x 6 in</c> or <c>102 x 152 mm</c>.</summary>
    public string SizeText
    {
        get
        {
            var ci = CultureInfo.InvariantCulture;
            if (NativeUnit == StockUnit.Inches)
                return HeightIn is { } h ? string.Create(ci, $"{WidthIn:0.###} x {h:0.###} in") : string.Create(ci, $"{WidthIn:0.###} in wide");
            return HeightMm is { } hm ? string.Create(ci, $"{WidthMm:0.##} x {hm:0.##} mm") : string.Create(ci, $"{WidthMm:0.##} mm wide");
        }
    }

    /// <summary>True when width and height are both known (a fixed-size label).</summary>
    public bool HasFixedSize => WidthMm > 0 && HeightMm is > 0;

    internal static bool IsSixteenth(double inches)
    {
        double sixteenths = inches * 16.0;
        return Math.Abs(sixteenths - Math.Round(sixteenths)) < 0.008 && inches > 0;
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Id}: {Name} ({SizeText})");
}
