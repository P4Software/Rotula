using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;

namespace Rotula.Tests;

/// <summary>
/// All App tests share one collection: they touch process-wide state (the language of <see cref="Loc"/>), so they must not
/// run in parallel with each other.
/// </summary>
[CollectionDefinition("App")]
public sealed class AppCollection { }

/// <summary>Helpers and fakes shared by the App tests.</summary>
internal static class AppTestSupport
{
    /// <summary>A unique temporary folder, deleted by the caller.</summary>
    public static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rotula-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A small 4 x 4 grey bitmap.</summary>
    public static GrayscaleBitmap SmallBitmap() =>
        new(4, 4, Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray());

    /// <summary>One of every element kind, with non-default values so round trips prove something.</summary>
    public static LabelDocument RichDocument()
    {
        var doc = new LabelDocument
        {
            Width = Measure.Inches(4), Height = Measure.Inches(6), Dpi = PrinterDpi.Dpi300,
            Orientation = LabelOrientation.Inverted, MediaType = MediaType.DirectThermal, MediaTracking = MediaTracking.Mark,
            DefaultRotation = FieldRotation.Rotated90, DefaultFont = new FontSpec('A', 40, 20),
            Encoding = Rotula.Core.Zpl.ZplEncoding.Cp1252,
            Print = new PrintOptions { Quantity = 12, PauseEvery = 3, Replicates = 2, OverridePause = true },
        };
        doc.Margins = new LabelMargins { Left = Measure.Dots(10), Top = Measure.Millimeters(2), Right = Measure.Dots(5), Bottom = Measure.Inches(0.1) };
        doc.Variables.Add(new LabelVariable("sku", "ABC-123"));
        doc.Variables.Add(new LabelVariable("qty", "5"));
        doc.Elements.Add(new TextElement
        {
            Text = "Hello ñ \"quoted\"", X = 10, Y = 20, Name = "Title", FontId = 'B', FontHeight = 50, FontWidth = 25,
            Rotation = FieldRotation.Inverted180, OriginKind = FieldOriginKind.Typeset, ReverseField = true,
            Block = new TextBlockOptions { WidthDots = 300, MaxLines = 4, LineSpacing = -2, Alignment = TextAlignment.Justified, HangingIndent = 6 },
        });
        doc.Elements.Add(new DataFieldElement { VariableName = "sku", X = 11, Y = 22 });
        doc.Elements.Add(new BarcodeElement
        {
            Symbology = BarcodeSymbology.Pdf417, Data = "{{sku}}", Height = 120, ModuleWidth = 3, WideBarRatio = 2.5, HumanReadable = false,
            HumanReadablePosition = HumanReadablePosition.Above, CheckDigit = true, Code128Mode = Code128Mode.Ean,
            QrErrorCorrection = QrErrorCorrection.High, QrModel = 1, ModuleSize = 6, Columns = 5, Rows = 20, Pdf417SecurityLevel = 5, Pdf417Truncate = true,
            X = 30, Y = 40,
        });
        doc.Elements.Add(new BoxElement { WidthDots = 120, HeightDots = 80, ThicknessDots = 5, Color = GraphicColor.White, CornerRounding = 3, X = 50, Y = 60 });
        doc.Elements.Add(new LineElement { LengthDots = 210, ThicknessDots = 4, Direction = LineDirection.Vertical, Color = GraphicColor.White, X = 70, Y = 80 });
        doc.Elements.Add(new CircleElement { DiameterDots = 90, ThicknessDots = 7, Color = GraphicColor.White, X = 90, Y = 100 });
        doc.Elements.Add(new ImageElement
        {
            Source = SmallBitmap(), WidthDots = 64, HeightDots = 32, Dither = ImageDitherMode.Ordered, Threshold = 90, Invert = true, X = 110, Y = 120,
        });
        return doc;
    }

    /// <summary>A session with a small label (203 dpi) holding the given elements.</summary>
    public static EditorSession Session(params LabelElement[] elements)
    {
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(1200), Dpi = PrinterDpi.Dpi203 };
        doc.Elements.AddRange(elements);
        return new EditorSession(doc);
    }

    /// <summary>A box at the given place.</summary>
    public static BoxElement Box(int x, int y, int w = 100, int h = 50) => new() { X = x, Y = y, WidthDots = w, HeightDots = h };
}

/// <summary>Scriptable fakes for every service of the main view-model.</summary>
internal sealed class FakeServices
{
    public string? OpenPath, SavePath, ExportPath, ZplPath, ImagePath;
    public readonly List<(string Title, string Message)> Errors = new();
    public readonly List<(string Title, string Message)> Infos = new();
    public SaveChangesAnswer SaveAnswer = SaveChangesAnswer.Discard;
    public int SaveQuestions;
    public string? ClipboardText;
    public LabelDocument? NewLabel;
    public IZplImporter Importer = new StubZplImporter();
    public IPrintService Printer = new StubPrintService();
    public GrayscaleBitmap? Image = AppTestSupport.SmallBitmap();
    public readonly InMemorySettings Settings = new();

    public MainServices Build() => new(
        new Dialogs(this), new Messages(this), new Clip(this), new Images(this), new NewDialog(this), Importer, Printer, Settings);

    private sealed class Dialogs(FakeServices f) : IFileDialogService
    {
        public string? PickOpenLabel() => f.OpenPath;
        public string? PickSaveLabel(string suggestedFileName) => f.SavePath;
        public string? PickExportFile(ExportKind kind, string suggestedFileName) => f.ExportPath;
        public string? PickOpenZpl() => f.ZplPath;
        public string? PickOpenImage() => f.ImagePath;
    }

    private sealed class Messages(FakeServices f) : IMessageService
    {
        public void ShowError(string title, string message) => f.Errors.Add((title, message));
        public void ShowInfo(string title, string message) => f.Infos.Add((title, message));
        public SaveChangesAnswer AskSaveChanges(string documentName) { f.SaveQuestions++; return f.SaveAnswer; }
    }

    private sealed class Clip(FakeServices f) : IClipboardService
    {
        public string? GetText() => f.ClipboardText;
        public void SetText(string text) => f.ClipboardText = text;
    }

    private sealed class Images(FakeServices f) : IImageLoader
    {
        public GrayscaleBitmap Load(string path) => f.Image ?? throw new InvalidDataException("not an image");
    }

    private sealed class NewDialog(FakeServices f) : INewLabelDialogService
    {
        public LabelDocument? ShowNewLabelDialog() => f.NewLabel;
    }
}

/// <summary>Settings store that never touches the disk.</summary>
internal sealed class InMemorySettings : ISettingsStore
{
    public AppSettings Current = new();
    public int Saves;
    public bool LoadProblem => false;
    public AppSettings Load() => Current;
    public bool Save(AppSettings settings) { Current = settings; Saves++; return true; }
}
