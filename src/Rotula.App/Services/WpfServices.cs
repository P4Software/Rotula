using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Rotula.App.Localization;
using Rotula.App.Views;
using Rotula.Core.Imaging;
using Rotula.Core.Model;

namespace Rotula.App.Services;

/// <summary>Windows implementations of the service interfaces used by the view-models.</summary>
public sealed class WpfFileDialogService : IFileDialogService
{
    private static string Filter(string nameKey, string pattern) => $"{Loc.Get(nameKey)} ({pattern})|{pattern}|{Loc.Get("Filter.AllFiles")} (*.*)|*.*";

    /// <inheritdoc />
    public string? PickOpenLabel() => Open(Loc.Get("Menu.Open"), Filter("Filter.Rotula", "*.rotula"));

    /// <inheritdoc />
    public string? PickSaveLabel(string suggestedFileName) => Save(Loc.Get("Menu.SaveAs"), Filter("Filter.Rotula", "*.rotula"), suggestedFileName, ".rotula");

    /// <inheritdoc />
    public string? PickExportFile(ExportKind kind, string suggestedFileName) => kind switch
    {
        ExportKind.Png => Save(Loc.Get("Menu.ExportPng"), Filter("Filter.Png", "*.png"), suggestedFileName, ".png"),
        ExportKind.Jpeg => Save(Loc.Get("Menu.ExportJpeg"), Filter("Filter.Jpeg", "*.jpg;*.jpeg"), suggestedFileName, ".jpg"),
        ExportKind.Pdf => Save(Loc.Get("Menu.ExportPdf"), Filter("Filter.Pdf", "*.pdf"), suggestedFileName, ".pdf"),
        _ => Save(Loc.Get("Menu.ExportZpl"), Filter("Filter.Zpl", "*.zpl;*.txt"), suggestedFileName, ".zpl"),
    };

    /// <inheritdoc />
    public string? PickOpenZpl() => Open(Loc.Get("Menu.ImportZpl"), Filter("Filter.Zpl", "*.zpl;*.txt"));

    /// <inheritdoc />
    public string? PickOpenImage() => Open(Loc.Get("Tool.Image"), Filter("Filter.Images", "*.png;*.jpg;*.jpeg;*.bmp;*.gif"));

    /// <inheritdoc />
    public string? PickOpenData() => Open(Loc.Get("Data.Open"),
        $"{Loc.Get("Filter.Data")} (*.csv;*.txt;*.xlsx)|*.csv;*.txt;*.xlsx|{Loc.Get("Filter.AllFiles")} (*.*)|*.*");

    /// <inheritdoc />
    public string? PickOpenDatabaseFile() => Open(Loc.Get("Conn.Sqlite.Pick"),
        $"{Loc.Get("Filter.Database")} (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|{Loc.Get("Filter.AllFiles")} (*.*)|*.*");

    /// <inheritdoc />
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }

    /// <inheritdoc />
    public string? PickOpenFile(string title, IReadOnlyList<string> extensions)
    {
        string pattern = string.Join(";", extensions.Select(e => "*" + e));
        return Open(title, $"{title} ({pattern})|{pattern}|{Loc.Get("Filter.AllFiles")} (*.*)|*.*");
    }

    private static string? Open(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    private static string? Save(string title, string filter, string suggested, string ext)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = suggested, DefaultExt = ext, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}

/// <summary>Message boxes, shown with the program's own styled dialog (<see cref="MessageDialogWindow"/>).</summary>
public sealed class WpfMessageService : IMessageService
{
    /// <inheritdoc />
    public void ShowError(string title, string message) =>
        MessageDialogWindow.Show(MessageKind.Error, title, message, new DialogChoice(Loc.Get("Common.Ok"), IsPrimary: true, IsDefault: true, IsCancel: true));

    /// <inheritdoc />
    public void ShowInfo(string title, string message) =>
        MessageDialogWindow.Show(MessageKind.Info, title, message, new DialogChoice(Loc.Get("Common.Ok"), IsPrimary: true, IsDefault: true, IsCancel: true));

    /// <inheritdoc />
    public bool Confirm(string title, string message) =>
        MessageDialogWindow.Show(MessageKind.Question, title, message,
            new DialogChoice(Loc.Get("Common.Cancel"), IsCancel: true), new DialogChoice(Loc.Get("Common.Ok"), IsPrimary: true, IsDefault: true)) == 1;

    /// <inheritdoc />
    public SaveChangesAnswer AskSaveChanges(string documentName)
    {
        // Buttons are named for what they do (Save / Don't save / Cancel); Save is the recommended, default choice and Esc means Cancel.
        int answer = MessageDialogWindow.Show(MessageKind.Question, Loc.Get("Dialog.SaveChanges.Title"), Loc.Get("Dialog.SaveChanges.Body", documentName),
            new DialogChoice(Loc.Get("Dialog.SaveChanges.Discard")),
            new DialogChoice(Loc.Get("Common.Cancel"), IsCancel: true),
            new DialogChoice(Loc.Get("Dialog.SaveChanges.Save"), IsPrimary: true, IsDefault: true));
        return answer switch
        {
            2 => SaveChangesAnswer.Save,
            0 => SaveChangesAnswer.Discard,
            _ => SaveChangesAnswer.Cancel,
        };
    }
}

/// <summary>Windows clipboard (text).</summary>
public sealed class WpfClipboardService : IClipboardService
{
    /// <inheritdoc />
    public string? GetText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (COMException) { return null; } // another program holds the clipboard; treat as empty
    }

    /// <inheritdoc />
    public void SetText(string text)
    {
        // The clipboard can be briefly locked by another process; retry a few times before giving up.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return; }
            catch (COMException) { System.Threading.Thread.Sleep(40); }
        }
    }
}

/// <summary>Decodes image files into luminance bitmaps.</summary>
public sealed class WpfImageLoader : IImageLoader
{
    /// <inheritdoc />
    public GrayscaleBitmap Load(string path)
    {
        BitmapSource source;
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            throw new InvalidDataException("The file is not a supported image.", ex);
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        if (w < 1 || h < 1) throw new InvalidDataException("The image is empty.");
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        var gray = new byte[w * h];
        for (int i = 0; i < gray.Length; i++)
        {
            double a = pixels[i * 4 + 3] / 255.0;
            // Transparent areas become white, because paper is white: alpha-blend each channel over 255.
            double b = pixels[i * 4] * a + 255 * (1 - a), g = pixels[i * 4 + 1] * a + 255 * (1 - a), r = pixels[i * 4 + 2] * a + 255 * (1 - a);
            gray[i] = (byte)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
        }
        return new GrayscaleBitmap(w, h, gray);
    }
}

/// <summary>Shows the New label dialog.</summary>
public sealed class WpfNewLabelDialogService : INewLabelDialogService
{
    /// <inheritdoc />
    public LabelDocument? ShowNewLabelDialog() => ShowNewLabelDialog(new NewLabelContext());

    /// <inheritdoc />
    public LabelDocument? ShowNewLabelDialog(NewLabelContext context)
    {
        var window = new NewLabelWindow(context) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true ? window.Result : null;
    }
}

/// <summary>Shows the print and data merge windows.</summary>
public sealed class WpfToolDialogService : IToolDialogService
{
    private DataWindow? _dataWindow;

    /// <inheritdoc />
    public void ShowPrintDialog(Rotula.App.ViewModels.PrintViewModel viewModel)
    {
        var window = new PrintWindow(viewModel) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
    }

    /// <inheritdoc />
    public void ShowPreviewDialog(Rotula.App.Editing.PreviewCache preview, string title)
    {
        var window = new PreviewWindow(preview, title) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
    }

    /// <inheritdoc />
    public void ShowConnectionManager(Rotula.App.ViewModels.ConnectionManagerViewModel viewModel)
    {
        // Owner is the Data window when it is open, so the dialog stays on top of it and not behind the main window.
        var owner = (Window?)_dataWindow ?? Application.Current?.MainWindow;
        var window = new ConnectionManagerWindow(viewModel) { Owner = owner };
        window.ShowDialog();
    }

    /// <inheritdoc />
    public bool ShowQrContentBuilder(Rotula.App.ViewModels.QrContentBuilderViewModel viewModel)
    {
        var window = new ContentBuilderWindow(viewModel) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        return viewModel.Applied;
    }

    /// <inheritdoc />
    public bool ShowGs1Builder(Rotula.App.ViewModels.Gs1BuilderViewModel viewModel)
    {
        var window = new Gs1BuilderWindow(viewModel) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        return viewModel.Applied;
    }

    /// <inheritdoc />
    public bool ShowPromptDialog(Rotula.App.ViewModels.PromptDialogViewModel viewModel)
    {
        // Owner is the window the operator is printing from (the Print or Data window), not always the main window.
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsLoaded) ?? Application.Current?.MainWindow;
        var window = new PromptDialogWindow(viewModel) { Owner = owner };
        window.ShowDialog();
        return viewModel.Accepted;
    }

    /// <inheritdoc />
    public void ShowDataWindow(Rotula.App.ViewModels.DataMergeViewModel viewModel)
    {
        if (_dataWindow is { IsLoaded: true })
        {
            if (_dataWindow.WindowState == WindowState.Minimized) _dataWindow.WindowState = WindowState.Normal;
            _dataWindow.Activate();
            return;
        }
        _dataWindow = new DataWindow(viewModel) { Owner = Application.Current?.MainWindow };
        _dataWindow.Closed += (_, _) => _dataWindow = null;
        _dataWindow.Show();
    }
}

/// <summary>Shows the add-on details list and the approval prompt.</summary>
public sealed class WpfPackDialogService : Rotula.App.ViewModels.IPackDialogService
{
    /// <inheritdoc />
    public void ShowDetails(Rotula.App.ViewModels.PackNoticeViewModel viewModel)
        => new PackDetailsWindow(viewModel) { Owner = Application.Current?.MainWindow }.ShowDialog();

    /// <inheritdoc />
    public bool ShowTrustPrompt(Rotula.Data.Packs.PackTrustPrompt prompt)
    {
        // The details window is the active window when the user presses Review, so it becomes the owner and the prompt stays on top of it.
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        return new PackTrustWindow(prompt) { Owner = owner }.ShowDialog() == true;
    }
}

/// <summary>Shows the import preview and the text about the stock catalogue.</summary>
public sealed class WpfStockDialogs : Rotula.App.ViewModels.IStockDialogs
{
    /// <inheritdoc />
    public bool ShowImportPreview(Rotula.App.ViewModels.StockImportPreviewViewModel preview)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        return new StockImportWindow(preview) { Owner = owner }.ShowDialog() == true;
    }

    /// <inheritdoc />
    public void ShowAbout(string title, string body)
    {
        MessageDialogWindow.Show(MessageKind.Info, title, body, new DialogChoice(Loc.Get("Common.Close"), IsPrimary: true, IsDefault: true, IsCancel: true));
    }
}
