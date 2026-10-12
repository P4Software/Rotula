using System.Text.Json;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Layout;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Files;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;
using SkiaSharp;
using static Rotula.Tests.CoreTestSupport;

namespace Rotula.Tests;

/// <summary>
/// Relative positioning with a nine-point anchor: the Core maths for every anchor against the label and against another element, cycle
/// detection, the ZPL generator (absolute ^FO per record), the .rotula file (format version 11), the preview picture, the editing commands and
/// the property panel.
/// </summary>
[Collection("App")]
public class RelativePositionTests : IDisposable
{
    public RelativePositionTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    /// <summary>A 100 x 50 box anchored to the label with the given offset.</summary>
    private static BoxElement Anchored(AnchorPoint anchor, int x = 0, int y = 0, string id = "box") =>
        new() { Id = id, X = x, Y = y, WidthDots = 100, HeightDots = 50, ThicknessDots = 3, Anchor = anchor };

    private static (int X, int Y) OriginOf(LabelDocument doc, LabelElement e) => AnchorResolver.Resolve(doc).OriginOf(e);

    // ------------------------------------------------------------------------------------ the nine anchors against the label

    [Theory]
    [InlineData(AnchorPoint.TopLeft, 0, 0)]
    [InlineData(AnchorPoint.TopCenter, 350, 0)]
    [InlineData(AnchorPoint.TopRight, 700, 0)]
    [InlineData(AnchorPoint.MiddleLeft, 0, 175)]
    [InlineData(AnchorPoint.Center, 350, 175)]
    [InlineData(AnchorPoint.MiddleRight, 700, 175)]
    [InlineData(AnchorPoint.BottomLeft, 0, 350)]
    [InlineData(AnchorPoint.BottomCenter, 350, 350)]
    [InlineData(AnchorPoint.BottomRight, 700, 350)]
    public void EachAnchor_PutsTheSamePointOfTheBoxOnTheSamePointOfTheLabel(AnchorPoint anchor, int expectedX, int expectedY)
    {
        // An 800 x 400 label and a 100 x 50 box: with a zero offset the box's corner/edge/centre sits exactly on the label's.
        var doc = NewDoc(Anchored(anchor));
        Assert.Equal((expectedX, expectedY), OriginOf(doc, doc.Elements[0]));
        Assert.Contains($"^FO{expectedX},{expectedY}^GB100,50,3,B,0^FS", ZplGenerator.Generate(doc).Zpl);
    }

    [Fact]
    public void TheOffset_IsADistanceFromTheReferencePoint_SoNegativeValuesMoveInwardFromTheFarEdges()
    {
        var doc = NewDoc(Anchored(AnchorPoint.BottomRight, -10, -20));
        Assert.Equal((690, 330), OriginOf(doc, doc.Elements[0]));
        var centred = NewDoc(Anchored(AnchorPoint.Center, 25, -5));
        Assert.Equal((375, 170), OriginOf(centred, centred.Elements[0]));
    }

    [Fact]
    public void TheLabelBox_IncludesTheMargins_BecausePositionsCountFromTheLabelHome()
    {
        var doc = NewDoc(Anchored(AnchorPoint.TopLeft), Anchored(AnchorPoint.BottomRight, 0, 0, "br"));
        doc.Margins.Left = Measure.Dots(30);
        doc.Margins.Top = Measure.Dots(20);
        // The label's own corner is 30/20 dots left/above the home, so the top-left anchor lands at (-30, -20) ...
        Assert.Equal((-30, -20), OriginOf(doc, doc.Elements[0]));
        // ... and the bottom-right one at the label's size minus the home and the box.
        Assert.Equal((800 - 30 - 100, 400 - 20 - 50), OriginOf(doc, doc.Elements[1]));
    }

    [Fact]
    public void ResizingTheLabel_KeepsAnchoredObjectsAttached_AndLeavesFreeOnesWhereTheyWere()
    {
        var doc = NewDoc(
            Anchored(AnchorPoint.BottomRight, -10, -10, "corner"),
            new BoxElement { Id = "free", X = 10, Y = 10, WidthDots = 100, HeightDots = 50, ThicknessDots = 3 });
        string before = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("^FO690,340^GB100,50", before);
        Assert.Contains("^FO10,10^GB100,50", before);

        doc.Width = Measure.Dots(1200);
        doc.Height = Measure.Dots(600);
        string after = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("^FO1090,540^GB100,50", after);
        Assert.Contains("^FO10,10^GB100,50", after);
    }

    // ------------------------------------------------------------------------------------ typeset origin and rotation

    [Fact]
    public void ATypesetOrRotatedObject_IsAnchoredByItsVisibleBox_NotByItsOriginPoint()
    {
        // ^FT: the origin is the bottom-left corner, so a bottom-right anchor at the label corner puts the origin at (700, 400).
        var typeset = NewDoc(new BoxElement { Id = "t", WidthDots = 100, HeightDots = 50, OriginKind = FieldOriginKind.Typeset, Anchor = AnchorPoint.BottomRight });
        Assert.Equal((700, 400), OriginOf(typeset, typeset.Elements[0]));

        // Rotated a quarter turn the box is 50 wide and 100 tall; the anchor works on that box.
        var rotated = NewDoc(new BoxElement { Id = "r", WidthDots = 100, HeightDots = 50, Rotation = FieldRotation.Rotated90, Anchor = AnchorPoint.BottomRight });
        Assert.Equal((750, 300), OriginOf(rotated, rotated.Elements[0]));
    }

    // ------------------------------------------------------------------------------------ relative to another element

    [Fact]
    public void AnElementRelativeToAnother_FollowsThatElement_AndTheChainResolvesInAnyOrder()
    {
        var a = new BoxElement { Id = "a", X = 100, Y = 100, WidthDots = 200, HeightDots = 80, ThicknessDots = 3 };
        // b: the same point (top-right) on both boxes; b's top-right corner is 60 dots right of a's, so b starts 10 dots right of a's edge.
        var b = new BoxElement { Id = "b", X = 60, Y = 0, WidthDots = 50, HeightDots = 50, ThicknessDots = 3, Anchor = AnchorPoint.TopRight, RelativeTo = AnchorReference.Element, ReferenceElementId = "a" };
        // c: centred under b (its top-centre 55 dots under b's top-centre, which is 5 dots below b), listed first to prove order does not matter.
        var c = new BoxElement { Id = "c", X = 0, Y = 55, WidthDots = 30, HeightDots = 30, ThicknessDots = 3, Anchor = AnchorPoint.TopCenter, RelativeTo = AnchorReference.Element, ReferenceElementId = "b" };
        var doc = NewDoc(c, a, b);

        Assert.Equal((310, 100), OriginOf(doc, b));
        Assert.Equal((310 + 25 - 15, 155), OriginOf(doc, c));

        // Moving a moves both followers by the same distance.
        a.X += 40;
        a.Y -= 20;
        Assert.Equal((350, 80), OriginOf(doc, b));
        Assert.Equal((360, 135), OriginOf(doc, c));
        // Growing a moves b (anchored to its right edge); c, anchored to b's top-centre, follows b.
        a.WidthDots = 300;
        Assert.Equal((450, 80), OriginOf(doc, b));
        Assert.Equal((460, 135), OriginOf(doc, c));
    }

    [Fact]
    public void TextWidth_DecidesWhereARightAnchoredTextStarts_PerRecord()
    {
        // The width of a printer-font text is an estimate from the nominal character size (see ElementSizeEstimator), so a longer value on one
        // record moves that record's text to the left and nothing else.
        var text = new TextElement { Id = "t", Text = "{{Name}}", FontId = '0', FontHeight = 30, FontWidth = 30, X = 0, Y = 0, Anchor = AnchorPoint.TopRight };
        var doc = NewDoc(text);
        doc.Variables.Add(new LabelVariable("Name", "AB"));
        string shortName = ZplGenerator.Generate(doc, new Dictionary<string, string> { ["Name"] = "AB" }).Zpl;
        string longName = ZplGenerator.Generate(doc, new Dictionary<string, string> { ["Name"] = "ABCDEFGH" }).Zpl;
        int Fo(string zpl) => int.Parse(System.Text.RegularExpressions.Regex.Match(zpl, @"\^FO(\d+),0").Groups[1].Value);
        int shortX = Fo(shortName), longX = Fo(longName);
        Assert.True(longX < shortX, $"{longX} should be left of {shortX}");
        Assert.True(shortX < 800 && longX > 0);
        // Twice the letters, half the distance from the edge: the estimate is linear in the character count.
        Assert.Equal((800 - shortX) * 4, 800 - longX);
    }

    [Fact]
    public void TheSizeEstimate_IsExactForShapesAndImages_AndBelievableForBarcodes()
    {
        var doc = NewDoc();
        Assert.Equal((100, 50), ElementSizeEstimator.Estimate(doc, new BoxElement { WidthDots = 100, HeightDots = 50 }));
        Assert.Equal((4, 120), ElementSizeEstimator.Estimate(doc, new LineElement { LengthDots = 120, ThicknessDots = 4, Direction = LineDirection.Vertical }));
        Assert.Equal((90, 90), ElementSizeEstimator.Estimate(doc, new CircleElement { DiameterDots = 90 }));
        Assert.Equal((64, 32), ElementSizeEstimator.Estimate(doc, new ImageElement { Source = AppTestSupport.SmallBitmap(), WidthDots = 64, HeightDots = 32 }));
        // EAN-13 is always 95 modules wide; with the text band it is taller than the bars.
        var ean = new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "4006381333931", ModuleWidth = 2, Height = 100 };
        var (w, h) = ElementSizeEstimator.Estimate(doc, ean);
        Assert.Equal(190, w);
        Assert.True(h > 100);
        // A QR code is square and grows with its data.
        var qr = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "short", ModuleSize = 4 };
        var (qw, qh) = ElementSizeEstimator.Estimate(doc, qr);
        Assert.Equal(qw, qh);
        qr.Data = new string('x', 300);
        Assert.True(ElementSizeEstimator.Estimate(doc, qr).Width > qw);
    }

    // ------------------------------------------------------------------------------------ cycles and missing references

    [Fact]
    public void ACycle_IsDetected_ReportedAsAnError_AndTheObjectsAreLeftOutOfTheZpl()
    {
        var a = new BoxElement { Id = "a", Name = "First", X = 10, Y = 10, Anchor = AnchorPoint.TopLeft, RelativeTo = AnchorReference.Element, ReferenceElementId = "b" };
        var b = new BoxElement { Id = "b", Name = "Second", X = 10, Y = 10, Anchor = AnchorPoint.TopLeft, RelativeTo = AnchorReference.Element, ReferenceElementId = "a" };
        var c = new BoxElement { Id = "c", X = 5, Y = 5, Anchor = AnchorPoint.TopLeft, RelativeTo = AnchorReference.Element, ReferenceElementId = "a" };
        var free = new BoxElement { Id = "free", X = 300, Y = 300 };
        var doc = NewDoc(a, b, c, free);

        Assert.Equal(new[] { "a", "b" }, AnchorResolver.FindCycles(doc).OrderBy(x => x));
        var result = ZplGenerator.Generate(doc);
        Assert.True(result.HasErrors);
        var errors = result.Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, i => Assert.Equal(AnchorResolver.CycleIssue, i.MessageKey));
        Assert.Contains(errors, i => i.ElementId == "a" && i.Args[0] == "Second");
        Assert.DoesNotContain("^FO10,10", result.Zpl);
        // c hangs off the circle: it keeps a place (the raw numbers of a stand in, so c lands at 10 + 5) and the free box prints as always.
        Assert.Contains("^FO15,15", result.Zpl);
        Assert.Contains("^FO300,300", result.Zpl);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result.Zpl, @"\^GB").Count);
    }

    [Theory]
    [InlineData("a", "b", true)]   // b is relative to a, so a cannot be relative to b
    [InlineData("a", "a", true)]   // never to itself
    [InlineData("c", "a", true)]   // a -> ... no: a is free, but c -> a is fine
    [InlineData("a", "c", true)]   // c is (through b) relative to a
    [InlineData("b", "c", true)]   // c is relative to b
    [InlineData("free", "c", false)]
    public void WouldCreateCycle_LooksThroughTheWholeChain(string element, string reference, bool expected)
    {
        var a = new BoxElement { Id = "a" };
        var b = new BoxElement { Id = "b", Anchor = AnchorPoint.Center, RelativeTo = AnchorReference.Element, ReferenceElementId = "a" };
        var c = new BoxElement { Id = "c", Anchor = AnchorPoint.Center, RelativeTo = AnchorReference.Element, ReferenceElementId = "b" };
        var doc = NewDoc(a, b, c, new BoxElement { Id = "free" });
        // "c relative to a" is allowed: a is the root of c's own chain.
        if (element == "c" && reference == "a") expected = false;
        Assert.Equal(expected, AnchorResolver.WouldCreateCycle(doc, element, reference));
    }

    [Fact]
    public void AMissingReference_FallsBackToTheLabel_WithAWarning()
    {
        var doc = NewDoc(new BoxElement { Id = "b", X = -10, Y = -10, WidthDots = 100, HeightDots = 50, ThicknessDots = 3, Anchor = AnchorPoint.BottomRight, RelativeTo = AnchorReference.Element, ReferenceElementId = "gone" });
        var result = ZplGenerator.Generate(doc);
        Assert.False(result.HasErrors);
        var warning = Assert.Single(result.Issues);
        Assert.Equal(AnchorResolver.MissingReferenceIssue, warning.MessageKey);
        Assert.Contains("^FO690,340^GB100,50", result.Zpl);
    }

    // ------------------------------------------------------------------------------------ golden ZPL

    [Fact]
    public void AnchoredLabel_MatchesTheGoldenFixture()
    {
        var frame = new BoxElement { Id = "frame", Name = "Frame", X = 0, Y = 0, WidthDots = 780, HeightDots = 380, ThicknessDots = 3, Anchor = AnchorPoint.Center };
        var title = new TextElement { Id = "title", X = 20, Y = 20, FontHeight = 40, FontWidth = 30, Text = "Rotula", Anchor = AnchorPoint.TopLeft, RelativeTo = AnchorReference.Element, ReferenceElementId = "frame" };
        var code = new BarcodeElement { Id = "code", X = -20, Y = -20, Data = "{{Sku}}", Height = 90, ModuleWidth = 2, Anchor = AnchorPoint.BottomRight, RelativeTo = AnchorReference.Element, ReferenceElementId = "frame" };
        var stamp = new TextElement { Id = "stamp", X = 0, Y = -4, FontHeight = 24, FontWidth = 18, Text = "{{Sku}}", Anchor = AnchorPoint.BottomCenter };
        var doc = NewDoc(frame, title, code, stamp);
        doc.Variables.Add(new LabelVariable("Sku", "SKU-0042"));
        AssertGolden("anchor_relative", Zpl(doc));
    }

    // ------------------------------------------------------------------------------------ file format

    private static string FormatOf(LabelDocument doc) => JsonDocument.Parse(RotulaFileSerializer.Serialize(doc)).RootElement.GetProperty("formatVersion").ToString();

    [Fact]
    public void Anchors_RoundTripThroughTheFile_AndMarkItVersionEleven()
    {
        var doc = NewDoc(
            new BoxElement { Id = "a", X = -5, Y = 7, Anchor = AnchorPoint.BottomRight },
            new TextElement { Id = "b", Text = "x", X = 3, Y = 4, Anchor = AnchorPoint.Center, RelativeTo = AnchorReference.Element, ReferenceElementId = "a" },
            new LineElement { Id = "plain", X = 1, Y = 2 });

        string json = RotulaFileSerializer.Serialize(doc);
        Assert.Equal("11", FormatOf(doc));
        Assert.Equal(11, RotulaFileSerializer.RelativePositionFormatVersion);
        Assert.Equal(RotulaFileSerializer.RelativePositionFormatVersion, RotulaFileSerializer.CurrentFormatVersion);
        Assert.Contains("\"anchor\": \"BottomRight\"", json);
        Assert.Contains("\"relativeTo\": \"Element\"", json);
        Assert.Contains("\"referenceElementId\": \"a\"", json);

        var back = RotulaFileSerializer.Deserialize(json);
        Assert.Equal(AnchorPoint.BottomRight, back.Elements[0].Anchor);
        Assert.Equal(AnchorReference.Label, back.Elements[0].RelativeTo);
        Assert.Equal((-5, 7), (back.Elements[0].X, back.Elements[0].Y));
        Assert.Equal(AnchorPoint.Center, back.Elements[1].Anchor);
        Assert.Equal(AnchorReference.Element, back.Elements[1].RelativeTo);
        Assert.Equal("a", back.Elements[1].ReferenceElementId);
        Assert.Null(back.Elements[2].Anchor);
        // A free object writes none of the three, so an unchanged element looks exactly as before.
        var plainJson = JsonDocument.Parse(json).RootElement.GetProperty("elements")[2];
        Assert.False(plainJson.TryGetProperty("anchor", out _));
        Assert.False(plainJson.TryGetProperty("relativeTo", out _));
        Assert.False(plainJson.TryGetProperty("referenceElementId", out _));
    }

    [Fact]
    public void ALabelWithoutAnchors_KeepsItsOldFormatVersion_AndOldFilesOpenUnchanged()
    {
        Assert.Equal("1", FormatOf(NewDoc(new TextElement { Text = "x" })));
        Assert.Equal("10", FormatOf(NewDoc(new TextElement { Text = "x", Locked = true })));

        const string v10 = """
            {"formatVersion":10,"label":{"width":{"value":80,"unit":"Millimeters"},"height":{"value":40,"unit":"Millimeters"},"dpi":203},
             "elements":[{"type":"text","id":"a","x":10,"y":20,"text":"Old file","locked":true},{"type":"recall","id":"r","command":"XG","reference":"R:LOGO.GRF","zpl":"^XGR:LOGO.GRF,1,1"}]}
            """;
        var doc = RotulaFileSerializer.Deserialize(v10);
        Assert.Null(doc.Elements[0].Anchor);
        Assert.Equal((10, 20), (doc.Elements[0].X, doc.Elements[0].Y));
        // A recall element's own "reference" (the stored object) is untouched by the new field, which is why the new one is "relativeTo".
        Assert.Equal("R:LOGO.GRF", ((RecallPlaceholderElement)doc.Elements[1]).Reference);
        Assert.Contains("^FO10,20", ZplGenerator.Generate(doc).Zpl);
    }

    [Fact]
    public void UnreadableAnchorData_LeavesTheObjectFree_OrRelativeToTheLabel_NeverFailingTheFile()
    {
        static LabelElement Load(string extra) =>
            RotulaFileSerializer.Deserialize("{\"formatVersion\":11,\"label\":{},\"elements\":[{\"type\":\"box\",\"id\":\"a\",\"x\":5,\"y\":6" + extra + "}]}").Elements[0];

        Assert.Null(Load(",\"anchor\":\"Somewhere\"").Anchor);
        Assert.Null(Load(",\"anchor\":\"3\"").Anchor);
        var noId = Load(",\"anchor\":\"Center\",\"relativeTo\":\"Element\"");
        Assert.Equal(AnchorReference.Label, noId.RelativeTo);
        var badId = Load(",\"anchor\":\"Center\",\"relativeTo\":\"Element\",\"referenceElementId\":\"" + new string('x', 200) + "\"");
        Assert.Equal(AnchorReference.Label, badId.RelativeTo);
        Assert.Null(badId.ReferenceElementId);
        var fine = Load(",\"anchor\":\"Center\",\"relativeTo\":\"Element\",\"referenceElementId\":\"other-1\"");
        Assert.Equal(AnchorReference.Element, fine.RelativeTo);
        Assert.Equal("other-1", fine.ReferenceElementId);
    }

    [Fact]
    public void TheClipboard_TheDuplicate_AndTheDpiConversion_KeepTheAnchor()
    {
        var box = new BoxElement { Id = "b", X = -20, Y = 10, Anchor = AnchorPoint.MiddleRight, RelativeTo = AnchorReference.Element, ReferenceElementId = "other" };
        var pasted = RotulaFileSerializer.DeserializeElements(RotulaFileSerializer.SerializeElements(new[] { box }));
        Assert.Equal(AnchorPoint.MiddleRight, pasted[0].Anchor);
        Assert.Equal("other", pasted[0].ReferenceElementId);

        var copy = ElementCloner.Clone(box, newId: true);
        Assert.Equal(AnchorPoint.MiddleRight, copy.Anchor);
        Assert.Equal(AnchorReference.Element, copy.RelativeTo);

        var converted = LabelDpiConverter.Convert(NewDoc(box), PrinterDpi.Dpi300).Document.Elements[0];
        Assert.Equal(AnchorPoint.MiddleRight, converted.Anchor);
        Assert.Equal("other", converted.ReferenceElementId);
        // The offset is a dot distance, so it scales with the resolution like every other dot value.
        Assert.Equal((-30, 15), (converted.X, converted.Y));
    }

    // ------------------------------------------------------------------------------------ pictures

    private static (int Width, int Height) SizeOf(SKBitmap bmp) => (bmp.Width, bmp.Height);

    [Fact]
    public void ThePreview_PlacesAnchoredObjectsWhereTheZplDoes_BeforeAndAfterALabelResize()
    {
        // A solid 60 x 40 box anchored to the bottom-right corner, 10 dots in from both edges, and a free one at the top-left.
        var doc = new LabelDocument { Width = Measure.Dots(300), Height = Measure.Dots(200), Dpi = PrinterDpi.Dpi203 };
        doc.Elements.Add(new BoxElement { Id = "free", X = 10, Y = 10, WidthDots = 60, HeightDots = 40, ThicknessDots = 60 });
        doc.Elements.Add(new BoxElement { Id = "corner", X = -10, Y = -10, WidthDots = 60, HeightDots = 40, ThicknessDots = 60, Anchor = AnchorPoint.BottomRight });

        string dir = Environment.GetEnvironmentVariable("ROTULA_ANCHOR_PNG_DIR") is { Length: > 0 } wanted ? wanted : AppTestSupport.TempDir();
        try
        {
            string before = Path.Combine(dir, "anchored-before.png");
            Assert.Empty(LabelExporter.SavePng(LabelDocumentAdapter.ToRenderLabel(doc), before));
            using (var bmp = SKBitmap.Decode(before))
            {
                Assert.Equal((300, 200), SizeOf(bmp));
                Assert.Equal(SKColors.Black, bmp.GetPixel(40, 30));    // the free box
                Assert.Equal(SKColors.Black, bmp.GetPixel(260, 170));  // the anchored box: x 230..289, y 150..189
                Assert.Equal(SKColors.White, bmp.GetPixel(295, 195));  // the 10-dot margin stays white
                Assert.Equal(SKColors.White, bmp.GetPixel(225, 145));
            }

            doc.Width = Measure.Dots(500);
            doc.Height = Measure.Dots(300);
            string after = Path.Combine(dir, "anchored-after.png");
            Assert.Empty(LabelExporter.SavePng(LabelDocumentAdapter.ToRenderLabel(doc), after));
            using (var bmp = SKBitmap.Decode(after))
            {
                Assert.Equal((500, 300), SizeOf(bmp));
                Assert.Equal(SKColors.Black, bmp.GetPixel(40, 30));    // the free box did not move
                Assert.Equal(SKColors.White, bmp.GetPixel(260, 170));  // where the anchored box used to be
                Assert.Equal(SKColors.Black, bmp.GetPixel(460, 270));  // it followed the corner: x 430..489, y 250..289
                Assert.Equal(SKColors.White, bmp.GetPixel(495, 295));
            }
            // The ZPL says the same.
            Assert.Contains("^FO430,250^GB60,40", ZplGenerator.Generate(doc).Zpl);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("ROTULA_ANCHOR_PNG_DIR") is not { Length: > 0 } && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------------------------ editing

    [Fact]
    public void TheSelectionFrame_IsDrawnAtTheResolvedPosition_AndHitTestingFindsItThere()
    {
        var box = Anchored(AnchorPoint.BottomRight, -10, -10);
        var s = AppTestSupport.Session(box); // 800 x 1200
        var bounds = s.Bounds.GetBounds(box);
        Assert.Equal(new RectD(690, 1140, 100, 50), bounds);
        Assert.Same(box, HitTesting.HitElement(s, 700, 1150, 1));
        Assert.Null(HitTesting.HitElement(s, 5, 5, 1));
    }

    [Fact]
    public void Nudging_MovesTheOffset_SoTheObjectMovesByTheSameDistance()
    {
        var box = Anchored(AnchorPoint.BottomRight, -10, -10);
        var s = AppTestSupport.Session(box);
        var c = new EditorCommands(s);
        s.Select(box);
        c.Nudge(5, -3);
        var moved = s.Document.Elements[0];
        Assert.Equal((-5, -13), (moved.X, moved.Y));
        Assert.Equal(new RectD(695, 1137, 100, 50), s.Bounds.GetBounds(moved));
    }

    [Fact]
    public void MovingAReferenceAndItsFollowerTogether_MovesTheFollowerOnce()
    {
        var a = new BoxElement { Id = "a", X = 100, Y = 100, WidthDots = 100, HeightDots = 50 };
        var b = new BoxElement { Id = "b", X = 20, Y = 0, WidthDots = 50, HeightDots = 50, Anchor = AnchorPoint.TopLeft, RelativeTo = AnchorReference.Element, ReferenceElementId = "a" };
        var s = AppTestSupport.Session(a, b);
        var c = new EditorCommands(s);
        s.SelectAll();
        Assert.Equal(new[] { "a" }, EditorCommands.IndependentlyMovable(s.Document, s.SelectedElements));

        Assert.Equal(new RectD(120, 100, 50, 50), s.Bounds.GetBounds(b)); // 20 dots right of a's top-left corner
        c.Nudge(10, 10);
        Assert.Equal((110, 110), (s.Document.Elements[0].X, s.Document.Elements[0].Y));
        Assert.Equal((20, 0), (s.Document.Elements[1].X, s.Document.Elements[1].Y)); // the offset is untouched ...
        Assert.Equal(new RectD(130, 110, 50, 50), s.Bounds.GetBounds(s.Document.Elements[1])); // ... and the box moved exactly 10/10

        // Aligning the pair to the label's left edge moves a to 0; b follows and is not aligned a second time.
        c.AlignToLabel(AlignKind.Left);
        Assert.Equal(new RectD(0, 110, 100, 50), s.Bounds.GetBounds(s.Document.Elements[0]));
        Assert.Equal(new RectD(20, 110, 50, 50), s.Bounds.GetBounds(s.Document.Elements[1]));
    }

    [Fact]
    public void DeletingAReference_MakesItsFollowersRelativeToTheLabel_WithoutMovingThem()
    {
        var a = new BoxElement { Id = "a", X = 100, Y = 100, WidthDots = 100, HeightDots = 50 };
        var b = new BoxElement { Id = "b", X = 20, Y = 0, WidthDots = 50, HeightDots = 50, Anchor = AnchorPoint.TopLeft, RelativeTo = AnchorReference.Element, ReferenceElementId = "a" };
        var s = AppTestSupport.Session(a, b);
        var before = s.Bounds.GetBounds(b);
        s.Select(a);
        new EditorCommands(s).DeleteSelected();

        var left = Assert.Single(s.Document.Elements);
        Assert.Equal("b", left.Id);
        Assert.Equal(AnchorReference.Label, left.RelativeTo);
        Assert.Null(left.ReferenceElementId);
        Assert.Equal(AnchorPoint.TopLeft, left.Anchor);
        Assert.Equal(before, s.Bounds.GetBounds(left));
        Assert.Empty(ZplGenerator.Generate(s.Document).Issues);

        s.PerformUndo();
        Assert.Equal("a", s.Document.Elements[1].ReferenceElementId);
    }

    [Fact]
    public void DuplicatingAPair_PointsTheCopyAtTheCopiedReference()
    {
        var a = new BoxElement { Id = "a", X = 100, Y = 100, WidthDots = 100, HeightDots = 50 };
        var b = new BoxElement { Id = "b", X = 20, Y = 0, WidthDots = 50, HeightDots = 50, Anchor = AnchorPoint.TopLeft, RelativeTo = AnchorReference.Element, ReferenceElementId = "a" };
        var s = AppTestSupport.Session(a, b);
        s.SelectAll();
        new EditorCommands(s).DuplicateSelected();
        var copies = s.SelectedElements;
        Assert.Equal(2, copies.Count);
        Assert.Equal(copies[0].Id, copies[1].ReferenceElementId);
        Assert.NotEqual("a", copies[1].ReferenceElementId);
    }

    [Fact]
    public void AlignToLabel_LinesSeveralObjectsUpWithTheLabelEdge_NotWithEachOther()
    {
        var one = AppTestSupport.Box(100, 100);
        var two = AppTestSupport.Box(300, 300);
        var s = AppTestSupport.Session(one, two);
        var c = new EditorCommands(s);
        s.SelectAll();
        c.AlignToLabel(AlignKind.Right);
        Assert.All(s.Document.Elements, e => Assert.Equal(800, s.Bounds.GetBounds(e).Right));
        c.AlignToLabel(AlignKind.CenterVertical);
        Assert.All(s.Document.Elements, e => Assert.Equal(600, s.Bounds.GetBounds(e).CenterY));
        Assert.True(s.Undo.CanUndo);
    }

    // ------------------------------------------------------------------------------------ property panel

    private static T Row<T>(PropertyPanelViewModel p, string labelKey) where T : PropertyRow => p.Rows.OfType<T>().First(r => r.LabelKey == labelKey);

    [Fact]
    public void ThePositionSection_OffersTheAnchorPicker_AndChoosingAPointKeepsTheObjectWhereItIs()
    {
        var box = AppTestSupport.Box(650, 1100); // 100 x 50 on an 800 x 1200 label
        var s = AppTestSupport.Session(box);
        var panel = new PropertyPanelViewModel(s);
        s.Select(box);

        var anchorRow = Row<AnchorRow>(panel, "Prop.Anchor");
        Assert.True(anchorRow.IsFree);
        Assert.Equal(9, anchorRow.Cells.Count);
        Assert.DoesNotContain(panel.Rows, r => r.LabelKey == "Prop.Reference");
        Assert.Contains(panel.Rows, r => r.LabelKey == "Prop.X");

        anchorRow.PickCommand.Execute(AnchorPoint.BottomRight);
        var e = s.Document.Elements[0];
        Assert.Equal(AnchorPoint.BottomRight, e.Anchor);
        // Same place, new numbers: 50 dots in from the right and bottom edges.
        Assert.Equal((-50, -50), (e.X, e.Y));
        Assert.Equal(new RectD(650, 1100, 100, 50), s.Bounds.GetBounds(e));

        // The panel rebuilt: offsets instead of positions, and the reference drop-down appeared.
        Assert.Contains(panel.Rows, r => r.LabelKey == "Prop.X.Offset");
        Assert.DoesNotContain(panel.Rows, r => r.LabelKey == "Prop.X");
        var reference = Row<ChoiceRow>(panel, "Prop.Reference");
        Assert.Null(reference.Selected!.Value);
        Assert.True(Row<AnchorRow>(panel, "Prop.Anchor").Cells.Single(c => c.IsSelected).Point == AnchorPoint.BottomRight);

        // Negative offsets are accepted now.
        Row<IntRow>(panel, "Prop.X.Offset").Text = "-80";
        Assert.Equal(-80, s.Document.Elements[0].X);
        Assert.False(Row<IntRow>(panel, "Prop.X.Offset").HasError);

        // Back to free: the absolute position comes back as the numbers.
        Row<AnchorRow>(panel, "Prop.Anchor").PickCommand.Execute(null);
        e = s.Document.Elements[0];
        Assert.Null(e.Anchor);
        Assert.Equal((620, 1100), (e.X, e.Y));
        Assert.Contains(panel.Rows, r => r.LabelKey == "Prop.X");

        s.PerformUndo();
        Assert.Equal(AnchorPoint.BottomRight, s.Document.Elements[0].Anchor);
    }

    [Fact]
    public void TheReferenceList_OffersTheLabelAndEveryOtherObject_AndRefusesACycleWithAMessage()
    {
        var a = new BoxElement { Id = "a", Name = "Header", X = 100, Y = 100, WidthDots = 100, HeightDots = 50 };
        var b = new BoxElement { Id = "b", X = 300, Y = 300, WidthDots = 50, HeightDots = 50, Anchor = AnchorPoint.TopLeft };
        var s = AppTestSupport.Session(a, b);
        var panel = new PropertyPanelViewModel(s);
        s.Select(b);

        var reference = Row<ChoiceRow>(panel, "Prop.Reference");
        Assert.Equal(new object?[] { null, "a" }, reference.Items.Select(i => i.Value));
        Assert.Equal("Header (Box)", reference.Items[1].Display);

        var bounds = s.Bounds.GetBounds(b);
        reference.Selected = reference.Items[1];
        var e = s.Document.Elements[1];
        Assert.Equal(AnchorReference.Element, e.RelativeTo);
        Assert.Equal("a", e.ReferenceElementId);
        Assert.Equal((200, 200), (e.X, e.Y)); // 300 - 100: the same place, measured from the header's corner
        Assert.Equal(bounds, s.Bounds.GetBounds(e));

        // Now make the header relative to the box: refused, explained, nothing written.
        s.Select(a);
        Row<AnchorRow>(panel, "Prop.Anchor").PickCommand.Execute(AnchorPoint.Center);
        var headerReference = Row<ChoiceRow>(panel, "Prop.Reference");
        headerReference.Selected = headerReference.Items.First(i => Equals(i.Value, "b"));
        Assert.True(headerReference.HasError);
        Assert.Contains("already positioned relative to this object", headerReference.Error);
        Assert.Null(headerReference.Selected!.Value);
        Assert.Equal(AnchorReference.Label, s.Document.Elements[0].RelativeTo);
        Assert.DoesNotContain(ZplGenerator.Generate(s.Document).Issues, i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void EveryNewTextIsInBothLanguages()
    {
        foreach (var lang in new[] { "en", "es" })
        {
            Loc.Instance.SetLanguage(lang);
            foreach (var key in new[] { "Prop.Anchor", "Prop.Anchor.Hint", "Prop.Anchor.Free", "Prop.Anchor.Free.Tip", "Prop.Anchor.Free.Status", "Prop.Reference", "Prop.Reference.Hint",
                         "Prop.Reference.Label", "Prop.Reference.Missing", "Prop.X.Offset", "Prop.Y.Offset", "Prop.Offset.Hint", "Anchor.Cycle", "Menu.AlignToLabel",
                         "Issue." + AnchorResolver.CycleIssue, "Issue." + AnchorResolver.MissingReferenceIssue })
                Assert.True(Loc.Instance.HasKey(key), key + " / " + lang);
            foreach (var point in Enum.GetValues<AnchorPoint>())
                Assert.True(Loc.Instance.HasKey("Enum.AnchorPoint." + point), point + " / " + lang);
        }
        Loc.Instance.SetLanguage("es");
        var issue = new ValidationIssue(ValidationSeverity.Error, AnchorResolver.CycleIssue, "x", "Cabecera");
        Assert.Contains("Cabecera", Loc.FormatIssue(issue));
        Assert.DoesNotContain("Problem found", Loc.FormatIssue(issue));
    }
}
