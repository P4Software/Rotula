using System.Globalization;
using System.Windows.Data;

namespace Rotula.App.Controls;

/// <summary>
/// Tells a number box whether its current text is unusable, so it can show the red error border (see <see cref="FieldState"/>).
/// Bind it to the box's own text: <c>ctl:FieldState.HasError="{Binding Text, RelativeSource={RelativeSource Self}, Converter={StaticResource FieldInvalid}, ConverterParameter=int:1:99999999}"</c>.
/// The parameter is <c>kind:min:max</c> with an optional <c>:opt</c> (an empty box is fine); kinds are <c>int</c> (digits only),
/// <c>signed</c> (integer with sign) and <c>double</c>. The wording of the problem still comes from the view-model's own message;
/// this only marks the field, mirroring the same rules.
/// </summary>
public sealed class FieldRuleConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = (value as string ?? string.Empty).Trim();
        var parts = (parameter as string ?? "int:0:2147483647").Split(':');
        bool optional = parts.Length > 3 && parts[3] == "opt";
        if (text.Length == 0) return !optional;
        double min = double.Parse(parts[1], CultureInfo.InvariantCulture), max = double.Parse(parts[2], CultureInfo.InvariantCulture);
        double n;
        switch (parts[0])
        {
            case "double":
                if (!double.TryParse(text, NumberStyles.Float, culture, out n) && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return true;
                break;
            case "signed":
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int s)) return true;
                n = s;
                break;
            default:
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return true;
                n = i;
                break;
        }
        return n < min || n > max;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
