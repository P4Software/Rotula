using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rotula.App.Controls;

/// <summary>Shows an element when the bound boolean is false (the counterpart of <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>).</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Visibility.Collapsed;
}
