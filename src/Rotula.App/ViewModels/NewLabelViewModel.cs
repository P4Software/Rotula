using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Localization;
using Rotula.Core.Model;

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

/// <summary>View-model of the New label dialog.</summary>
public sealed partial class NewLabelViewModel : ObservableObject
{
    /// <summary>Creates the dialog state with the 4 x 6 in preset selected.</summary>
    public NewLabelViewModel()
    {
        _selectedPreset = LabelPreset.All[0];
        _widthText = LabelPreset.All[0].Width.ToString("0.###", CultureInfo.CurrentCulture);
        _heightText = LabelPreset.All[0].Height.ToString("0.###", CultureInfo.CurrentCulture);
        _unit = LabelPreset.All[0].Unit;
    }

    /// <summary>Presets to choose from.</summary>
    public IReadOnlyList<LabelPreset> Presets => LabelPreset.All;

    /// <summary>The chosen preset; choosing one fills the size boxes.</summary>
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
    private PrinterDpi _dpi = PrinterDpi.Dpi203;

    /// <summary>Problem with the entered size, or empty.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>True when the size boxes can be edited (the Custom preset).</summary>
    public bool IsCustom => SelectedPreset.NameKey == "Preset.Custom";

    /// <summary>Printer resolutions to choose from.</summary>
    public IReadOnlyList<PrinterDpi> Dpis { get; } = Enum.GetValues<PrinterDpi>();

    /// <summary>Units to choose from.</summary>
    public IReadOnlyList<LengthUnit> Units { get; } = new[] { LengthUnit.Millimeters, LengthUnit.Inches };

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
    }

    /// <summary>Validates the entered size and builds a blank label. Returns null (and sets <see cref="Error"/>) when the size is not usable.</summary>
    public LabelDocument? TryCreate()
    {
        if (!double.TryParse(WidthText, NumberStyles.Float, CultureInfo.CurrentCulture, out double w)
            || !double.TryParse(HeightText, NumberStyles.Float, CultureInfo.CurrentCulture, out double h))
        {
            Error = Loc.Get("Validation.NotNumber");
            return null;
        }
        var width = new Measure(w, Unit);
        var height = new Measure(h, Unit);
        int wd = width.ToDots(Dpi), hd = height.ToDots(Dpi);
        if (wd < 1 || wd > 9999 || hd < 1 || hd > 32000)
        {
            Error = Loc.Get("Validation.LabelSize", 9999, 32000);
            return null;
        }
        Error = string.Empty;
        return new LabelDocument { Width = width, Height = height, Dpi = Dpi };
    }
}
