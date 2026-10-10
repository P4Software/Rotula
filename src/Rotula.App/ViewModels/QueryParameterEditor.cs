using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Localization;
using Rotula.Files;

namespace Rotula.App.ViewModels;

/// <summary>
/// One <c>@parameter</c> found in the query text, with the value typed for it and how that value is sent to the database.
/// Text is the default because it is always safe; numbers and dates are offered for engines that do not convert implicitly.
/// </summary>
public sealed partial class QueryParameterEditor : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Type choices shared by all editors ("text", "number", "date").</summary>
    public static IReadOnlyList<Choice<string>> TypeChoices { get; } = new[]
    {
        new Choice<string>("text", "Data.Db.Param.Type.Text"), new Choice<string>("number", "Data.Db.Param.Type.Number"),
        new Choice<string>("date", "Data.Db.Param.Type.Date"),
    };

    /// <summary>Creates an editor.</summary>
    /// <param name="name">Parameter name without the @.</param>
    /// <param name="value">Initial value.</param>
    /// <param name="type">"text", "number" or "date".</param>
    /// <param name="changed">Called when the value or type changes.</param>
    public QueryParameterEditor(string name, string value, string type, Action changed)
    {
        Name = name;
        _value = value;
        _type = type is "number" or "date" ? type : "text";
        _changed = changed;
    }

    /// <summary>Name without the @.</summary>
    public string Name { get; }

    /// <summary>Name as written in the query, for the label.</summary>
    public string Display => "@" + Name;

    /// <summary>The value as typed.</summary>
    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value) => _changed();

    /// <summary>"text", "number" or "date".</summary>
    [ObservableProperty]
    private string _type;

    partial void OnTypeChanged(string value) => _changed();

    /// <summary>
    /// Converts the typed value to what the database driver receives. Numbers and dates are parsed culture-independently
    /// first (so a saved label behaves the same on every PC) and then with the user's own culture.
    /// </summary>
    /// <param name="value">The value to bind.</param>
    /// <param name="error">A plain sentence when the value does not fit the chosen type.</param>
    /// <returns>True when the value is usable.</returns>
    public bool TryGetValue(out object? value, out string? error)
    {
        value = null;
        error = null;
        string text = Value.Trim();
        switch (Type)
        {
            case "number":
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d)
                    || decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out d))
                {
                    value = d;
                    return true;
                }
                error = Loc.Get("Data.Db.Param.NumberInvalid", Display);
                return false;
            case "date":
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)
                    || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
                {
                    value = dt;
                    return true;
                }
                error = Loc.Get("Data.Db.Param.DateInvalid", Display);
                return false;
            default:
                // An empty text value is a legitimate (empty) string; only numbers and dates need content.
                value = Value;
                return true;
        }
    }

    /// <summary>The saved form.</summary>
    public QueryParameterSetting ToSetting() => new(Name, Value, Type);

    /// <summary>Re-reads translated text after a language change.</summary>
    public void RefreshLanguage() => OnPropertyChanged(nameof(Display));
}
