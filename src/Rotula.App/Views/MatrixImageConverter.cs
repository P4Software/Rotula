using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rotula.Rendering.Barcodes;

namespace Rotula.App.Views;

/// <summary>
/// Draws a <see cref="BarcodeMatrix"/> (the module grid the label preview is made from) as a bitmap for the builder dialogs: black modules on
/// white with a quiet zone, every module a whole number of pixels so the edges stay sharp at any display scale.
/// </summary>
public sealed class MatrixImageConverter : IValueConverter
{
    /// <summary>The longest side of the bitmap in pixels that the module size aims for.</summary>
    private const int TargetPixels = 360;

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is BarcodeMatrix m ? Render(m) : null;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    /// <summary>Renders the matrix; public so tests can check the pixels.</summary>
    public static BitmapSource Render(BarcodeMatrix m)
    {
        // The quiet zone keeps phone cameras and the eye from mistaking the page for part of the code.
        int quiet = m.IsLinear ? 10 : 4;
        int cols = m.Width + 2 * quiet;
        int rowsOfModules = m.IsLinear ? 1 : m.Height;
        int scale = Math.Max(1, TargetPixels / Math.Max(cols, m.IsLinear ? 1 : m.Height + 2 * quiet));
        // A linear symbol has one row of bars; stretch it to a readable height instead of one module tall.
        int barHeight = m.IsLinear ? Math.Max(60, scale * 30) : 0;
        int width = cols * scale;
        int height = m.IsLinear ? barHeight + 2 * scale * 4 : (rowsOfModules + 2 * quiet) * scale;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        for (int y = 0; y < (m.IsLinear ? 1 : m.Height); y++)
            for (int x = 0; x < m.Width; x++)
            {
                if (!m.IsDark(x, y)) continue;
                int px = (x + quiet) * scale;
                int py = m.IsLinear ? scale * 4 : (y + quiet) * scale;
                int h = m.IsLinear ? barHeight : scale;
                for (int dy = 0; dy < h; dy++)
                {
                    int row = (py + dy) * width;
                    for (int dx = 0; dx < scale; dx++) pixels[row + px + dx] = 0;
                }
            }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return bitmap;
    }
}
