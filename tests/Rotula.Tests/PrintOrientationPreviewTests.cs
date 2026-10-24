using System.ComponentModel;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using Rotula.Rendering;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;
using SkiaSharp;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>
/// Print orientation (^PO) and mirror (^PM) in the preview: the "as printed" picture is turned or flipped exactly as the printer does,
/// the canvas and the exports stay as designed, and imported ^POI / ^PON set the document setting and survive a round trip.
/// </summary>
public class PrintOrientationPreviewTests
{
    // An asymmetric picture: a solid block in the top-left corner and a thin bar along the bottom edge, so every flip is told apart.
    private static RenderLabel Asymmetric() => Label(200, 100,
        new RenderBox { X = 10, Y = 5, WidthDots = 40, HeightDots = 20, ThicknessDots = 100 },
        new RenderBox { X = 120, Y = 90, WidthDots = 70, HeightDots = 4, ThicknessDots = 100 });

    private static bool[,] Dark(RenderLabel label)
    {
        using var r = LabelRenderer.RenderBitmap(label);
        var dark = new bool[r.Bitmap.Width, r.Bitmap.Height];
        for (int y = 0; y < r.Bitmap.Height; y++)
            for (int x = 0; x < r.Bitmap.Width; x++)
                dark[x, y] = IsDark(r.Bitmap, x, y);
        return dark;
    }

    /// <summary>Asserts that every pixel of <paramref name="actual"/> is the pixel of <paramref name="designed"/> that <paramref name="map"/> names.</summary>
    private static void AssertMapped(bool[,] designed, bool[,] actual, Func<int, int, (int X, int Y)> map)
    {
        int w = designed.GetLength(0), h = designed.GetLength(1);
        Assert.Equal(w, actual.GetLength(0));
        Assert.Equal(h, actual.GetLength(1));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (sx, sy) = map(x, y);
                Assert.True(designed[sx, sy] == actual[x, y], $"pixel ({x},{y}) should match designed ({sx},{sy})");
            }
    }

    // ------------------------------------------------------------------------------- renderer

    [Fact]
    public void Inverted_label_is_the_designed_picture_turned_by_180_degrees_pixel_for_pixel()
    {
        var designed = Dark(Asymmetric());
        var printed = Dark(Asymmetric() with { Inverted = true });
        AssertMapped(designed, printed, (x, y) => (199 - x, 99 - y));

        // The top-left block now sits in the bottom-right corner.
        using var r = LabelRenderer.RenderBitmap(Asymmetric() with { Inverted = true, Elements = [Asymmetric().Elements[0]] });
        Assert.Equal(new SKRectI(150, 75, 190, 95), DarkBounds(r.Bitmap));
    }

    [Fact]
    public void Mirrored_label_swaps_left_and_right_only()
    {
        var designed = Dark(Asymmetric());
        AssertMapped(designed, Dark(Asymmetric() with { Mirrored = true }), (x, y) => (199 - x, y));
    }

    [Fact]
    public void Inverted_and_mirrored_label_is_flipped_top_to_bottom()
    {
        var designed = Dark(Asymmetric());
        AssertMapped(designed, Dark(Asymmetric() with { Inverted = true, Mirrored = true }), (x, y) => (x, 99 - y));
    }

    [Fact]
    public void Inversion_also_holds_at_a_zoomed_scale()
    {
        var label = Asymmetric() with { Elements = [Asymmetric().Elements[0]] };
        using var r = LabelRenderer.RenderBitmap(label with { Inverted = true }, new RenderOptions { Scale = 2 });
        Assert.Equal(new SKRectI(300, 150, 380, 190), DarkBounds(r.Bitmap));
    }

    // ------------------------------------------------------------------------------- adapter, canvas and exports

    private static LabelDocument InvertedDoc()
    {
        var doc = CoreTestSupport.NewDoc(new BoxElement { X = 10, Y = 10, WidthDots = 40, HeightDots = 20, ThicknessDots = 20 });
        doc.Width = Measure.Dots(200);
        doc.Height = Measure.Dots(100);
        doc.Orientation = LabelOrientation.Inverted;
        return doc;
    }

    [Fact]
    public void Adapter_keeps_the_design_upright_and_only_AsPrinted_turns_it()
    {
        var doc = InvertedDoc();
        var designed = LabelDocumentAdapter.ToRenderLabel(doc);
        Assert.False(designed.Inverted);
        Assert.False(designed.Mirrored);

        var printed = LabelDocumentAdapter.AsPrinted(designed, doc);
        Assert.True(printed.Inverted);
        Assert.False(printed.Mirrored);
        Assert.True(LabelDocumentAdapter.ChangesWhenPrinted(doc));

        doc.MirrorImage = true;
        Assert.True(LabelDocumentAdapter.AsPrinted(designed, doc).Mirrored);

        var normal = CoreTestSupport.NewDoc();
        var label = LabelDocumentAdapter.ToRenderLabel(normal);
        Assert.False(LabelDocumentAdapter.ChangesWhenPrinted(normal));
        Assert.Same(label, LabelDocumentAdapter.AsPrinted(label, normal));
    }

    [Fact]
    public void Png_export_stays_as_designed_for_an_inverted_label()
    {
        // Exports have no "as printed" notion: they are the artwork, so they keep the design upright (see docs/zpl-coverage.md).
        var doc = InvertedDoc();
        using var ms = new MemoryStream();
        LabelExporter.SavePng(LabelDocumentAdapter.ToRenderLabel(doc), ms);
        ms.Position = 0;
        using var bmp = SKBitmap.Decode(ms);
        Assert.Equal(new SKRectI(10, 10, 50, 30), DarkBounds(bmp));
    }

    // ------------------------------------------------------------------------------- ZPL import and round trip

    [Theory]
    [InlineData("^XA^POI^PW400^LL200^XZ", LabelOrientation.Inverted)]
    [InlineData("^XA^POi^PW400^LL200^XZ", LabelOrientation.Inverted)]
    [InlineData("^XA^PON^PW400^LL200^XZ", LabelOrientation.Normal)]
    [InlineData("^XA^PW400^LL200^XZ", LabelOrientation.Normal)]
    public void Imported_print_orientation_sets_the_document_setting(string zpl, LabelOrientation expected)
    {
        var r = ZplParser.Parse(zpl);
        Assert.Empty(r.Warnings);
        Assert.Equal(expected, r.Document.Orientation);
    }

    [Fact]
    public void Inverted_and_mirrored_label_round_trips_through_zpl()
    {
        var doc = InvertedDoc();
        doc.MirrorImage = true;
        string zpl = CoreTestSupport.Zpl(doc);
        Assert.Contains("\n^POI\n", zpl);
        Assert.Contains("\n^PMY\n", zpl);

        var back = ZplParser.Parse(zpl);
        Assert.Empty(back.Warnings);
        Assert.Equal(LabelOrientation.Inverted, back.Document.Orientation);
        Assert.True(back.Document.MirrorImage);
        Assert.Equal(zpl, CoreTestSupport.Zpl(back.Document));

        // The imported label previews the same way as the one it came from.
        Assert.True(LabelDocumentAdapter.AsPrinted(LabelDocumentAdapter.ToRenderLabel(back.Document), back.Document).Inverted);
    }

    [Fact]
    public void Golden_inverted_label() =>
        CoreTestSupport.AssertGolden("label_print_inverted", CoreTestSupport.Zpl(InvertedDoc()));
}

/// <summary>The preview window's "as printed" / "as designed" switch.</summary>
[Collection("App")]
public class PreviewViewModelTests
{
    public PreviewViewModelTests() => Loc.Instance.SetLanguage("en");

    private static PreviewCache Cache(LabelOrientation orientation, bool mirror = false)
    {
        var session = AppTestSupport.Session(AppTestSupport.Box(10, 10, 40, 20));
        session.Document.Width = Measure.Dots(200);
        session.Document.Height = Measure.Dots(100);
        session.Document.Orientation = orientation;
        session.Document.MirrorImage = mirror;
        session.Touch();
        return new PreviewCache(session);
    }

    private static SKRectI? Bounds(PreviewViewModel vm)
    {
        using var r = vm.Render(out string? error);
        Assert.Null(error);
        return DarkBounds(r!.Bitmap);
    }

    [Fact]
    public void Inverted_label_opens_as_printed_and_switches_to_as_designed()
    {
        using var cache = Cache(LabelOrientation.Inverted);
        var vm = new PreviewViewModel(cache);
        Assert.True(vm.ChangesWhenPrinted);
        Assert.True(vm.AsPrinted);
        Assert.False(vm.AsDesigned);
        Assert.Equal(Loc.Get("Preview.Note.PrintedInverted"), vm.ModeNote);
        Assert.Equal(new SKRectI(150, 70, 190, 90), Bounds(vm));

        int redraws = 0;
        var changed = new List<string?>();
        vm.PictureChanged += (_, _) => redraws++;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.AsDesigned = true;
        Assert.False(vm.AsPrinted);
        Assert.Equal(1, redraws);
        Assert.Contains(nameof(PreviewViewModel.ModeNote), changed);
        Assert.Contains(nameof(PreviewViewModel.AsDesigned), changed);
        Assert.Equal(Loc.Get("Preview.Note.Designed"), vm.ModeNote);
        Assert.Equal(new SKRectI(10, 10, 50, 30), Bounds(vm));

        // A radio unchecked by its sibling writes false back; that must not flip the choice again.
        vm.AsDesigned = false;
        Assert.False(vm.AsPrinted);
        Assert.Equal(1, redraws);

        vm.AsPrinted = true;
        Assert.Equal(2, redraws);
        Assert.Equal(new SKRectI(150, 70, 190, 90), Bounds(vm));
    }

    [Fact]
    public void Mirrored_and_combined_labels_say_why_the_picture_differs()
    {
        using var mirrored = Cache(LabelOrientation.Normal, mirror: true);
        var vm = new PreviewViewModel(mirrored);
        Assert.Equal(Loc.Get("Preview.Note.PrintedMirrored"), vm.ModeNote);
        Assert.Equal(new SKRectI(150, 10, 190, 30), Bounds(vm));

        using var both = Cache(LabelOrientation.Inverted, mirror: true);
        Assert.Equal(Loc.Get("Preview.Note.PrintedBoth"), new PreviewViewModel(both).ModeNote);
    }

    [Fact]
    public void Normal_label_has_no_switch_and_no_note()
    {
        using var cache = Cache(LabelOrientation.Normal);
        var vm = new PreviewViewModel(cache);
        Assert.False(vm.ChangesWhenPrinted);
        Assert.Equal(string.Empty, vm.ModeNote);
        Assert.Equal(new SKRectI(10, 10, 50, 30), Bounds(vm));
    }

    [Fact]
    public void Status_bar_names_a_label_that_prints_upside_down()
    {
        var vm = new MainViewModel(new FakeServices().Build());
        Assert.Equal(string.Empty, vm.PrintOrientationText);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Session.Edit("Undo.LabelSize", d => d.Orientation = LabelOrientation.Inverted);
        Assert.Contains(nameof(MainViewModel.PrintOrientationText), changed);
        Assert.Equal(Loc.Get("Status.PrintsInverted"), vm.PrintOrientationText);

        vm.Session.Edit("Undo.LabelSize", d => d.MirrorImage = true);
        Assert.Equal(Loc.Get("Status.PrintsInvertedMirrored"), vm.PrintOrientationText);

        // The canvas keeps drawing the design upright.
        Assert.False(vm.Preview.GetDesignLabel().Inverted);
    }
}
