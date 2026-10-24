using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>Turns a <see cref="StationImage"/> (premultiplied BGRA pixels from the renderer) into a frozen WPF bitmap.</summary>
public sealed class StationImageConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not StationImage image || image.Width <= 0 || image.Height <= 0) return null;
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
