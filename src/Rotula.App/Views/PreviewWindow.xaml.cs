using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// A larger preview of the label, drawn by Rotula.Rendering at one pixel per printer dot. When the printer turns (^POI) or mirrors (^PMY)
/// the label, a switch shows it "as printed" (the default) or "as designed", with a sentence saying which one is on screen.
/// </summary>
public partial class PreviewWindow : DialogWindow
{
    private readonly PreviewViewModel _viewModel;

    /// <summary>Creates the window and draws the label.</summary>
    /// <param name="preview">The preview cache of the open label (its current record data is used).</param>
    /// <param name="title">Name of the label, shown in the title.</param>
    public PreviewWindow(PreviewCache preview, string title)
    {
        InitializeComponent();
        Title = Loc.Get("Preview.TitleFor", title);
        _viewModel = new PreviewViewModel(preview);
        DataContext = _viewModel;
        _viewModel.PictureChanged += (_, _) => Draw();
        Draw();
    }

    /// <summary>
    /// Draws the picture for the chosen view. The preview is a snapshot: it shows the label as it is when the window opens. This window
    /// shows what the printer will draw, so objects that are switched off for the shown record are absent (the canvas only dims them).
    /// </summary>
    private void Draw()
    {
        using var result = _viewModel.Render(out string? errorKey);
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
