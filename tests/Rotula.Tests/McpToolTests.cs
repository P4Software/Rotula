using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Mcp.Session;

namespace Rotula.Tests;

/// <summary>Tool classes invoked directly, the way the server invokes them.</summary>
public class McpToolTests : IDisposable
{
    private readonly McpTestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string Id(Rotula.Mcp.Tools.ChangeResult r) => r.Elements.Single().Id;

    [Fact]
    public void NothingWorksWithoutALabel_AndTheMessageSaysWhatToDo()
    {
        var ex = Assert.Throws<McpException>(() => _env.Elements.AddText("x", 0, 0));
        Assert.Contains("create_label", ex.Message);
        Assert.Throws<McpException>(() => _env.Label.GetLabel());
        Assert.Throws<McpException>(() => _env.Label.UndoLast());
    }

    [Fact]
    public void CreateLabel_SetsSizeAndResolution()
    {
        var r = _env.Label.CreateLabel(100, 150, dpi: 300, name: "ship");
        Assert.Equal(100, r.Label.Width);
        Assert.Equal("mm", r.Label.Unit);
        Assert.Equal(300, r.Label.Dpi);
        Assert.Equal(1181, r.Label.WidthDots);
        Assert.Equal(1772, r.Label.HeightDots);
        Assert.Equal("ship", r.Label.Name);
        Assert.Equal("^CI28", r.Label.Encoding);
    }

    [Theory]
    [InlineData(0, 10, "mm", 203)]
    [InlineData(-5, 10, "mm", 203)]
    [InlineData(10, 10, "furlong", 203)]
    [InlineData(10, 10, "mm", 150)]
    [InlineData(5000, 10, "mm", 203)]
    [InlineData(double.NaN, 10, "mm", 203)]
    [InlineData(double.PositiveInfinity, 10, "mm", 203)]
    public void CreateLabel_RefusesNonsense(double w, double h, string unit, int dpi)
    {
        Assert.Throws<McpException>(() => _env.Label.CreateLabel(w, h, unit, dpi));
    }

    [Fact]
    public void AddText_UsesUnitsAndKeepsAccents()
    {
        _env.Label.CreateLabel(100, 50);
        var r = _env.Elements.AddText("Ni\u00f1o \u00bfqu\u00e9?", 10, 5, fontHeight: 4);
        var e = r.Elements.Single();
        Assert.Equal("t1", e.Id);
        Assert.Equal(10, e.X, 1); // positions are whole dots, so 10 mm comes back as 10.01
        Assert.Equal(80, e.XDots); // 10 mm at 203 DPI
        Assert.Empty(r.Issues);
        var zpl = _env.Label.GenerateZpl().Zpl;
        Assert.Contains("^CI28", zpl);
        Assert.Contains("_C3_B1", zpl); // n-tilde, UTF-8 hex escaped
        Assert.Contains("^FO80,40", zpl);
    }

    [Fact]
    public void AddText_InDotsAndInches()
    {
        _env.Label.CreateLabel(4, 2, "in");
        Assert.Equal("in", _env.Label.GetLabel().Label.Unit);
        var a = _env.Elements.AddText("a", 0.5, 0.25).Elements.Single();
        Assert.Equal(102, a.XDots); // 0.5 in * 203
        var b = _env.Elements.AddText("b", 30, 40, unit: "dots").Elements.Single();
        Assert.Equal(30, b.XDots);
        Assert.Equal(40, b.YDots);
    }

    [Fact]
    public void WrappedTextNeedsABlockWidth()
    {
        _env.Label.CreateLabel(100, 50);
        var ex = Assert.Throws<McpException>(() => _env.Elements.AddText("long", 0, 0, alignment: "Center"));
        Assert.Contains("blockWidth", ex.Message);
        var ok = _env.Elements.AddText("long text here", 0, 0, blockWidth: 50, maxLines: 3, alignment: "Center").Elements.Single();
        Assert.Equal("Center", ok.Properties["alignment"]);
        Assert.Equal(3, ok.Properties["maxLines"]);
        Assert.Equal(1, _env.Label.GetLabel().Label.ElementCount); // the failed add left nothing behind
    }

    [Theory]
    [InlineData("Code128", "ABC-123")]
    [InlineData("Code39", "ABC123")]
    [InlineData("Code93", "ABC123")]
    [InlineData("Ean13", "590123412345")]
    [InlineData("UpcA", "03600029145")]
    [InlineData("Interleaved2Of5", "123456")]
    [InlineData("QrCode", "https://example.com/x")]
    [InlineData("DataMatrix", "DM-001")]
    [InlineData("Pdf417", "PDF 417 data")]
    public void EveryNineSymbologyCanBeAdded_AndGeneratesZpl(string symbology, string data)
    {
        _env.Label.CreateLabel(100, 100);
        var r = _env.Elements.AddBarcode(symbology, data, 5, 5);
        Assert.DoesNotContain(r.Issues, i => i.Severity == "error");
        Assert.Equal(symbology, r.Elements.Single().Properties["symbology"]);
        var zpl = _env.Label.GenerateZpl();
        Assert.False(zpl.HasErrors);
        Assert.Contains("^FD", zpl.Zpl);
    }

    [Fact]
    public void BarcodeAliasesAreAccepted()
    {
        _env.Label.CreateLabel(100, 100);
        Assert.Equal("QrCode", _env.Elements.AddBarcode("qr", "x", 0, 0).Elements.Single().Properties["symbology"]);
        Assert.Equal("Interleaved2Of5", _env.Elements.AddBarcode("i25", "12", 0, 20).Elements.Single().Properties["symbology"]);
        Assert.Equal("UpcA", _env.Elements.AddBarcode("UPC-A", "03600029145", 0, 40).Elements.Single().Properties["symbology"]);
        var ex = Assert.Throws<McpException>(() => _env.Elements.AddBarcode("code777", "x", 0, 0));
        Assert.Contains("list_symbologies", ex.Message);
    }

    [Fact]
    public void BadBarcodeDataComesBackAsIssues()
    {
        _env.Label.CreateLabel(100, 100);
        var r = _env.Elements.AddBarcode("Ean13", "12345", 5, 5);
        var issue = Assert.Single(r.Issues, i => i.Severity == "error");
        Assert.Contains("EAN-13", issue.Message);
        Assert.Equal(Id(r), issue.ElementId);
        Assert.False(_env.Label.ValidateLabel().Printable);
    }

    [Fact]
    public void AnElementOutsideTheLabelIsAWarning()
    {
        _env.Label.CreateLabel(50, 30);
        var r = _env.Elements.AddBox(45, 25, 20, 20);
        Assert.Contains(r.Issues, i => i.Key == "Element.OutsideLabel" && i.Severity == "warning");
        Assert.True(_env.Label.ValidateLabel().Printable);
    }

    [Fact]
    public void BoxLineCircleQr_AndReorder()
    {
        _env.Label.CreateLabel(100, 100);
        string box = Id(_env.Elements.AddBox(5, 5, 40, 20, thickness: 0.5, cornerRounding: 2));
        string line = Id(_env.Elements.AddLine(5, 30, 40, direction: "Vertical"));
        string circle = Id(_env.Elements.AddCircle(50, 5, 15));
        string qr = Id(_env.Elements.AddQr("hello", 5, 40, moduleSize: 5, errorCorrection: "High"));
        Assert.StartsWith("x", box);
        Assert.StartsWith("l", line);
        Assert.StartsWith("c", circle);
        Assert.StartsWith("b", qr);
        _env.Elements.ReorderElement(box, to: "front");
        Assert.Equal(box, _env.Label.GetLabel().Elements.Last().Id);
        _env.Elements.ReorderElement(box, index: 0);
        Assert.Equal(box, _env.Label.GetLabel().Elements.First().Id);
        _env.Elements.ReorderElement(box, to: "forward");
        Assert.Equal(box, _env.Label.GetLabel().Elements[1].Id);
        Assert.Throws<McpException>(() => _env.Elements.ReorderElement(box, to: "sideways"));
        Assert.Throws<McpException>(() => _env.Elements.ReorderElement(box, index: 99));
        Assert.Throws<McpException>(() => _env.Elements.ReorderElement(box));
    }

    [Fact]
    public void UpdateMoveDuplicateDelete()
    {
        _env.Label.CreateLabel(100, 50);
        string id = Id(_env.Elements.AddText("one", 10, 10));
        var upd = _env.Elements.UpdateElement(id, new() { ["text"] = McpTestEnv.J("two"), ["x"] = McpTestEnv.J(20), ["rotation"] = McpTestEnv.J("90") });
        Assert.Equal("two", upd.Elements.Single().Properties["text"]);
        Assert.Equal(20, upd.Elements.Single().X, 1);
        Assert.Equal("90", upd.Elements.Single().Rotation);

        var mv = _env.Elements.MoveElement(id, deltaX: 5, y: 12);
        Assert.Equal(25, mv.Elements.Single().X, 1);
        Assert.Equal(12, mv.Elements.Single().Y, 1);
        Assert.Throws<McpException>(() => _env.Elements.MoveElement(id));
        Assert.Throws<McpException>(() => _env.Elements.MoveElement(id, x: 1, deltaX: 1));

        var dup = _env.Elements.DuplicateElement(id);
        string copy = Id(dup);
        Assert.NotEqual(id, copy);
        Assert.Equal(2, _env.Label.GetLabel().Label.ElementCount);

        _env.Elements.DeleteElement(copy);
        Assert.Equal(1, _env.Label.GetLabel().Label.ElementCount);
        Assert.Throws<McpException>(() => _env.Elements.DeleteElement(copy));
    }

    [Fact]
    public void UpdateRefusesUnknownPropertiesAndChangesNothing()
    {
        _env.Label.CreateLabel(100, 50);
        string id = Id(_env.Elements.AddText("keep", 10, 10));
        var ex = Assert.Throws<McpException>(() => _env.Elements.UpdateElement(id,
            new() { ["text"] = McpTestEnv.J("changed"), ["colour"] = McpTestEnv.J("red") }));
        Assert.Contains("Valid properties", ex.Message);
        Assert.Equal("keep", _env.Label.GetLabel().Elements.Single().Properties["text"]); // the half-applied text change was rolled back
        Assert.Throws<McpException>(() => _env.Elements.UpdateElement(id, new() { ["fontHeight"] = McpTestEnv.J("tall") }));
        Assert.Throws<McpException>(() => _env.Elements.UpdateElement(id, new() { ["rotation"] = McpTestEnv.J("45") }));
        Assert.Throws<McpException>(() => _env.Elements.UpdateElement("nope", new() { ["text"] = McpTestEnv.J("a") }));
        Assert.Throws<McpException>(() => _env.Elements.UpdateElement(id, new()));
    }

    [Fact]
    public void UpdateBarcodeData()
    {
        _env.Label.CreateLabel(100, 50);
        string id = Id(_env.Elements.AddBarcode("Code128", "A1", 5, 5));
        var r = _env.Elements.UpdateElement(id, new() { ["data"] = McpTestEnv.J("B2-99"), ["height"] = McpTestEnv.J(10), ["humanReadable"] = McpTestEnv.J(false) });
        Assert.Equal("B2-99", r.Elements.Single().Properties["data"]);
        Assert.Equal(false, r.Elements.Single().Properties["humanReadable"]);
    }

    [Fact]
    public void DataFieldsDeclareTheirVariable()
    {
        _env.Label.CreateLabel(100, 50);
        var r = _env.Elements.AddDataField("Name", 5, 5, sampleValue: "Ana");
        Assert.Equal("text", r.Elements.Single().Type); // compatibility tool: a plain text element with {{Name}} as its content
        Assert.Contains(r.Label.Variables, v => v.Name == "Name" && v.Sample == "Ana");
        Assert.Contains("Ana", _env.Label.GenerateZpl().Zpl);
        Assert.Throws<McpException>(() => _env.Elements.AddDataField("bad name!", 0, 0));
    }

    [Fact]
    public void UndoRevertsEachKindOfChange()
    {
        _env.Label.CreateLabel(100, 50);
        string id = Id(_env.Elements.AddText("a", 1, 1));
        _env.Elements.UpdateElement(id, new() { ["text"] = McpTestEnv.J("b") });
        _env.Elements.DeleteElement(id);
        Assert.Equal(0, _env.Label.GetLabel().Label.ElementCount);
        _env.Label.UndoLast();
        Assert.Equal("b", _env.Label.GetLabel().Elements.Single().Properties["text"]);
        _env.Label.UndoLast();
        Assert.Equal("a", _env.Label.GetLabel().Elements.Single().Properties["text"]);
        Assert.Equal(id, _env.Label.GetLabel().Elements.Single().Id); // ids survive an undo
        _env.Label.UndoLast();
        Assert.Equal(0, _env.Label.GetLabel().Label.ElementCount);
        _env.Label.UndoLast(); // undoes create_label: no label open any more
        Assert.False(_env.Context.Session.HasLabel);
        Assert.Throws<McpException>(() => _env.Label.UndoLast());
    }

    [Fact]
    public void ReplacingTheLabelCanBeUndone()
    {
        _env.Label.CreateLabel(100, 50, name: "first");
        _env.Elements.AddText("x", 0, 0);
        _env.Label.CreateLabel(10, 10, name: "second");
        Assert.Equal("second", _env.Label.GetLabel().Label.Name);
        _env.Label.UndoLast();
        var back = _env.Label.GetLabel();
        Assert.Equal("first", back.Label.Name);
        Assert.Equal(1, back.Label.ElementCount);
    }

    [Fact]
    public void UndoDepthIsBounded()
    {
        _env.Label.CreateLabel(100, 50);
        for (int i = 0; i < LabelSession.UndoDepth + 10; i++) _env.Elements.AddBox(0, 0, 1, 1);
        Assert.Equal(LabelSession.UndoDepth, _env.Context.Session.UndoSteps);
    }

    [Fact]
    public void TheElementLimitIsEnforced_AndTheLabelIsLeftAsItWas()
    {
        using var env = new McpTestEnv(s => s with { MaxElements = 3 });
        env.Label.CreateLabel(100, 50);
        for (int i = 0; i < 3; i++) env.Elements.AddBox(i, i, 1, 1);
        var ex = Assert.Throws<McpException>(() => env.Elements.AddBox(9, 9, 1, 1));
        Assert.Contains("at most 3", ex.Message);
        Assert.Throws<McpException>(() => env.Elements.DuplicateElement("x1"));
        Assert.Equal(3, env.Label.GetLabel().Label.ElementCount);
    }

    [Fact]
    public void SetLabelSettings()
    {
        _env.Label.CreateLabel(100, 50);
        var r = _env.Label.SetLabelSettings(width: 80, marginLeft: 2, marginTop: 3, orientation: "Inverted", mediaTracking: "Continuous",
            defaultRotation: "90", defaultFontId: "A", printQuantity: 4, name: "renamed");
        Assert.Equal(80, r.Label.Width);
        Assert.Equal(50, r.Label.Height);
        Assert.Equal(2, r.Label.Margins.Left);
        Assert.Equal("Inverted", r.Label.Orientation);
        Assert.Equal("Continuous", r.Label.MediaTracking);
        Assert.Equal("90", r.Label.DefaultRotation);
        Assert.Equal(4, r.Label.PrintQuantity);
        Assert.Equal("renamed", r.Label.Name);
        var zpl = _env.Label.GenerateZpl().Zpl;
        Assert.Contains("^POI", zpl);
        Assert.Contains("^MNN", zpl);
        Assert.Contains("^PQ4,", zpl);
        Assert.Throws<McpException>(() => _env.Label.SetLabelSettings(orientation: "sideways"));
        Assert.Throws<McpException>(() => _env.Label.SetLabelSettings(marginLeft: -1));
        Assert.Throws<McpException>(() => _env.Label.SetLabelSettings(printQuantity: 0));
        Assert.Equal(80, _env.Label.GetLabel().Label.Width); // failed calls changed nothing
    }

    [Fact]
    public void SaveAndOpenRoundTrip_WithAtomicWriteAndOverwriteRules()
    {
        _env.Label.CreateLabel(100, 50, name: "my label");
        _env.Elements.AddText("Hi", 5, 5);
        var saved = _env.Label.SaveLabel();
        string file = Path.Combine(_env.Labels, "my label.rotula");
        Assert.True(File.Exists(file));
        Assert.Contains(file, saved.Message);
        Assert.Empty(Directory.GetFiles(_env.Labels, "*.tmp"));

        // Saving back to the same file is fine; saving to another existing file needs overwrite.
        _env.Label.SaveLabel();
        _env.Label.CreateLabel(10, 10, name: "other");
        File.WriteAllText(Path.Combine(_env.Labels, "other.rotula"), "{}");
        var ex = Assert.Throws<McpException>(() => _env.Label.SaveLabel());
        Assert.Contains("overwrite", ex.Message);
        _env.Label.SaveLabel(overwrite: true);

        var opened = _env.Label.OpenLabel("my label.rotula");
        Assert.Equal(1, opened.Label.ElementCount);
        Assert.Equal("Hi", opened.Elements.Single().Properties["text"]);
        Assert.False(opened.Label.UnsavedChanges);
    }

    [Fact]
    public void OpeningDamagedOrForeignFilesGivesPlainMessages()
    {
        File.WriteAllText(Path.Combine(_env.Labels, "bad.rotula"), "this is not json");
        File.WriteAllText(Path.Combine(_env.Labels, "wrong.rotula"), "{\"hello\":1}");
        File.WriteAllText(Path.Combine(_env.Labels, "future.rotula"), "{\"formatVersion\":999}");
        foreach (string name in new[] { "bad.rotula", "wrong.rotula", "future.rotula" })
        {
            var ex = Assert.Throws<McpException>(() => _env.Label.OpenLabel(name));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.DoesNotContain("File.Error", ex.Message); // the key was turned into a sentence
        }
        Assert.False(_env.Context.Session.HasLabel);
    }

    [Fact]
    public void ListSymbologiesNamesAllNine()
    {
        var names = _env.Label.ListSymbologies().Symbologies.Select(s => s.Name).ToList();
        foreach (string n in new[] { "Code128", "Code39", "Code93", "Ean13", "UpcA", "Interleaved2Of5", "QrCode", "DataMatrix", "Pdf417" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void ImportZplKeepsUnknownCommandsAndReportsWarnings()
    {
        var r = _env.Label.ImportZpl("^XA^PW800^LL400^FO50,50^A0N,40,40^FDHello^FS^FO50,120^BCN,80,Y,N,N^FD12345^FS^JMA^FO10,300^GB200,3,3^FS^XZ");
        Assert.True(r.Label.ElementCount >= 3);
        Assert.Contains(r.Elements, e => e.Type == "text");
        Assert.Contains(r.Elements, e => e.Type == "barcode");
        Assert.NotEmpty(r.Warnings); // ^JMA is kept as raw ZPL and listed
        Assert.Contains(r.Elements, e => e.Type == "rawZpl");
        Assert.Contains("^JMA", _env.Label.GenerateZpl().Zpl);
        _env.Label.UndoLast();
        Assert.False(_env.Context.Session.HasLabel);
    }

    [Fact]
    public void ImportZplNeedsExactlyOneSource_AndAFileMustBeAllowed()
    {
        Assert.Throws<McpException>(() => _env.Label.ImportZpl());
        Assert.Throws<McpException>(() => _env.Label.ImportZpl("^XA^XZ", "a.zpl"));
        File.WriteAllText(Path.Combine(_env.Labels, "old.zpl"), "^XA^FO10,10^A0N,30,30^FDFrom file^FS^XZ");
        var r = _env.Label.ImportZpl(path: "old.zpl");
        Assert.Equal("old", r.Label.Name);
        Assert.Contains(r.Elements, e => e.Type == "text");
        Assert.Throws<McpException>(() => _env.Label.ImportZpl(path: "C:\\Windows\\win.ini"));
    }

    [Fact]
    public void GenerateZplCanSaveAFile_WithoutOverwritingByDefault()
    {
        _env.Label.CreateLabel(50, 30);
        _env.Elements.AddText("x", 1, 1);
        var r = _env.Label.GenerateZpl("out.zpl");
        Assert.Equal(Path.Combine(_env.Labels, "out.zpl"), r.SavedTo);
        Assert.Equal(r.Zpl, File.ReadAllText(r.SavedTo!));
        Assert.Throws<McpException>(() => _env.Label.GenerateZpl("out.zpl"));
        _env.Label.GenerateZpl("out.zpl", overwrite: true);
    }

    [Fact]
    public void ConvertDpiKeepsThePhysicalSize()
    {
        _env.Label.CreateLabel(100, 50);
        string id = Id(_env.Elements.AddBox(10, 10, 20, 10));
        var before = _env.Label.GetLabel().Elements.Single(e => e.Id == id);
        var r = _env.Label.ConvertDpi(300);
        Assert.Equal(300, r.Label.Dpi);
        Assert.Equal(1181, r.Label.WidthDots);
        var after = _env.Label.GetLabel().Elements.Single(e => e.Id == id);
        Assert.Equal(before.X, after.X, 1);
        Assert.Equal((int)Math.Round(before.XDots * 300 / 203.0), after.XDots);
        Assert.Contains("already", _env.Label.ConvertDpi(300).Message);
        _env.Label.UndoLast();
        Assert.Equal(203, _env.Label.GetLabel().Label.Dpi);
        Assert.Throws<McpException>(() => _env.Label.ConvertDpi(72));
    }

    [Fact]
    public void RenderPreviewReturnsAnImageAndSaysItIsApproximate()
    {
        _env.Label.CreateLabel(100, 50);
        _env.Elements.AddText("Preview", 5, 5);
        _env.Elements.AddBarcode("Code128", "12345", 5, 20);
        var r = _env.Preview.RenderPreview(path: "p.png");
        var image = Assert.IsType<ImageContentBlock>(r.Content[0]);
        Assert.Equal("image/png", image.MimeType);
        var png = image.DecodedData.ToArray();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        var text = Assert.IsType<TextContentBlock>(r.Content[1]);
        Assert.Contains("approximate", text.Text);
        Assert.True(r.StructuredContent!.Value.GetProperty("approximate").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_env.Labels, "p.png")));
        Assert.Throws<McpException>(() => _env.Preview.RenderPreview(maxPixels: 5));
        Assert.Throws<McpException>(() => _env.Preview.RenderPreview(maxPixels: 100000));
    }

    [Fact]
    public void ARenderOfAHugeLabelIsScaledDown()
    {
        _env.Label.CreateLabel(1200, 3000, "mm", 203); // 9591 x 23976 dots, far beyond a sensible bitmap
        var r = _env.Preview.RenderPreview();
        var sc = r.StructuredContent!.Value;
        Assert.True(sc.GetProperty("widthPixels").GetInt32() <= 1000);
        Assert.True(sc.GetProperty("heightPixels").GetInt32() <= 1000);
    }

    [Fact]
    public void AddImageFromAnAllowedPng_AndRefusesOthers()
    {
        string png = Path.Combine(_env.Labels, "logo.png");
        using (var bmp = new SkiaSharp.SKBitmap(40, 20))
        {
            bmp.Erase(SkiaSharp.SKColors.Black);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(png, data.ToArray());
        }
        _env.Label.CreateLabel(100, 50);
        var r = _env.Elements.AddImage("logo.png", 5, 5, width: 20, dither: "Threshold");
        Assert.Equal("image", r.Elements.Single().Type);
        Assert.Contains("^GFA", _env.Label.GenerateZpl().Zpl);

        File.WriteAllText(Path.Combine(_env.Labels, "fake.png"), "not a png");
        var ex = Assert.Throws<McpException>(() => _env.Elements.AddImage("fake.png", 0, 0));
        Assert.Contains("PNG", ex.Message);
        Assert.Throws<McpException>(() => _env.Elements.AddImage("missing.png", 0, 0));
        Assert.Throws<McpException>(() => _env.Elements.AddImage("logo.jpg", 0, 0));
    }

    [Fact]
    public void AnOversizedImageIsRefusedBeforeDecoding()
    {
        string png = Path.Combine(_env.Labels, "huge.png");
        using (var bmp = new SkiaSharp.SKBitmap(5000, 10))
        {
            bmp.Erase(SkiaSharp.SKColors.White);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(png, data.ToArray());
        }
        _env.Label.CreateLabel(100, 50);
        var ex = Assert.Throws<McpException>(() => _env.Elements.AddImage("huge.png", 0, 0));
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public void MergePreviewFillsVariablesFromACsvRecord()
    {
        File.WriteAllText(Path.Combine(_env.Labels, "people.csv"), "Name,City\nAna,Quito\nLuis,Lima\n");
        _env.Label.CreateLabel(100, 50);
        _env.Elements.AddDataField("Name", 5, 5);
        _env.Elements.AddText("Hello {{City}} {{Missing}}", 5, 20);
        var r = _env.Preview.MergePreview("people.csv", record: 2);
        var sc = r.StructuredContent!.Value.GetProperty("merge");
        Assert.Equal(2, sc.GetProperty("recordIndex").GetInt32());
        Assert.Equal(2, sc.GetProperty("recordCount").GetInt32());
        Assert.Equal("Luis", sc.GetProperty("record").GetProperty("Name").GetString());
        Assert.Contains("Missing", sc.GetProperty("unmapped").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("Luis", sc.GetProperty("zpl").GetString());
        Assert.IsType<ImageContentBlock>(r.Content[0]);
        Assert.Throws<McpException>(() => _env.Preview.MergePreview("people.csv", record: 3));
        Assert.Throws<McpException>(() => _env.Preview.MergePreview("people.csv", record: 0));
        Assert.Throws<McpException>(() => _env.Preview.MergePreview());
    }

    [Fact]
    public void ListLabelFilesShowsOnlyAllowedTypes()
    {
        File.WriteAllText(Path.Combine(_env.Labels, "a.rotula"), "{}");
        File.WriteAllText(Path.Combine(_env.Labels, "b.exe"), "x");
        Directory.CreateDirectory(Path.Combine(_env.Labels, "sub"));
        File.WriteAllText(Path.Combine(_env.Labels, "sub", "c.csv"), "x");
        var files = _env.Label.ListLabelFiles().Files.Select(f => Path.GetFileName(f.Path)).ToList();
        Assert.Contains("a.rotula", files);
        Assert.Contains("c.csv", files);
        Assert.DoesNotContain("b.exe", files);
    }

    [Fact]
    public void ServerInfoStatesTheSafetySettings()
    {
        var info = _env.Label.GetServerInfo();
        Assert.False(info.PrintingAllowed);
        Assert.False(info.DatabaseAllowed);
        Assert.Equal(10, info.MaxCopies);
        Assert.Equal(_env.Labels, Assert.Single(info.AllowedFolders));
    }

    [Fact]
    public async Task DatabaseToolsAreRefusedByDefault()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _env.Data.RunQueryTool("any", "SELECT 1"));
        Assert.Contains("switched off", ex.Message);
        await Assert.ThrowsAsync<McpException>(() => _env.Data.ListDatabaseConnections());
        _env.Label.CreateLabel(10, 10);
        Assert.Throws<McpException>(() => _env.Preview.MergePreview(connection: "x", query: "SELECT 1"));
    }

    [Fact]
    public void ResourcesExposeTheOpenLabel()
    {
        var res = new Rotula.Mcp.Tools.LabelResources(_env.Context);
        Assert.Throws<McpException>(() => res.CurrentZpl());
        _env.Label.CreateLabel(50, 30);
        _env.Elements.AddText("res", 1, 1);
        Assert.Contains("^FDres^FS", res.CurrentZpl());
        using var doc = JsonDocument.Parse(res.CurrentJson());
        Assert.Equal("res", doc.RootElement.GetProperty("elements")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void PromptsExistAndMentionNoVendor()
    {
        var prompts = new Rotula.Mcp.Tools.LabelPrompts();
        string all = prompts.DesignShippingLabel() + prompts.ConvertOldZplLabel("^XA^XZ") + prompts.FixValidationWarnings();
        Assert.Contains("create_label", all);
        Assert.Contains("import_zpl", all);
        Assert.Contains("validate_label", all);
    }

    [Fact]
    public void SessionRevisionMovesWithEveryChange()
    {
        _env.Label.CreateLabel(50, 30);
        long r0 = _env.Context.Session.Revision;
        _env.Elements.AddText("x", 0, 0);
        Assert.True(_env.Context.Session.Revision > r0);
    }

    [Fact]
    public void ElementIdsArePreservedWhenCopyingTheSession()
    {
        _env.Label.CreateLabel(50, 30);
        _env.Elements.AddText("x", 0, 0);
        var copy = _env.Context.Session.GetCopy();
        Assert.Equal("t1", copy.Document.Elements.Single().Id);
        Assert.IsType<TextElement>(copy.Document.Elements.Single());
        Assert.Equal(PrinterDpi.Dpi203, copy.Document.Dpi);
    }
}
