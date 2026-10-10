using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rotula.App.Editing;
using Rotula.App.Localization;

namespace Rotula.App.Views;

/// <summary>A larger preview of the label, drawn by Rotula.Rendering at one pixel per printer dot.</summary>
public partial class PreviewWindow : Window
{
    /// <summary>Creates the window and draws the label once.</summary>
    /// <param name="preview">The preview cache of the open label (its current record data is used).</param>
    /// <param name="title">Name of the label, shown in the title.</param>
    public PreviewWindow(PreviewCache preview, string title)
    {
        InitializeComponent();
        Title = Loc.Get("Preview.TitleFor", title);
        // The preview is a snapshot: it shows the label as it is when the window opens.
        var result = preview.GetBitmap(1.0, out string? errorKey);
        if (result is null)
        {
            Content = new System.Windows.Controls.TextBlock
            {
                Text = Loc.Get(errorKey ?? "Preview.InvalidSize"), Margin = new Thickness(32), TextWrapping = TextWrapping.Wrap,
            };
            return;
        }
        var bmp = result.Bitmap;
        var image = new WriteableBitmap(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null);
        image.WritePixels(new Int32Rect(0, 0, bmp.Width, bmp.Height), bmp.GetPixels(), bmp.RowBytes * bmp.Height, bmp.RowBytes);
        image.Freeze();
        LabelImage.Source = image;
    }
}
