using Rotula.App.Editing;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Rendering.View;

namespace Rotula.Tests;

[Collection("App")]
public class AppEditorCommandsTests
{
    private static string[] Order(EditorSession s) => s.Document.Elements.Select(e => e.Name ?? "?").ToArray();

    private static (EditorSession S, EditorCommands C) Four()
    {
        var s = AppTestSupport.Session(
            new BoxElement { Name = "A" }, new BoxElement { Name = "B" }, new BoxElement { Name = "C" }, new BoxElement { Name = "D" });
        return (s, new EditorCommands(s));
    }

    private static void Select(EditorSession s, params string[] names) =>
        s.SetSelection(s.Document.Elements.Where(e => names.Contains(e.Name)).Select(e => e.Id));

    // ------------------------------------------------------------------------------- z-order

    [Fact]
    public void BringToFront_And_SendToBack_KeepRelativeOrderOfSelection()
    {
        var (s, c) = Four();
        Select(s, "A", "C");
        c.BringToFront();
        Assert.Equal(new[] { "B", "D", "A", "C" }, Order(s));
        Select(s, "D", "C");
        c.SendToBack();
        Assert.Equal(new[] { "D", "C", "B", "A" }, Order(s));
    }

    [Fact]
    public void BringForward_And_SendBackward_MoveOneStep()
    {
        var (s, c) = Four();
        Select(s, "B");
        c.BringForward();
        Assert.Equal(new[] { "A", "C", "B", "D" }, Order(s));
        c.SendBackward();
        c.SendBackward();
        Assert.Equal(new[] { "B", "A", "C", "D" }, Order(s));
    }

    [Fact]
    public void BringForward_AdjacentSelection_MovesTogether()
    {
        var (s, c) = Four();
        Select(s, "A", "B");
        c.BringForward();
        Assert.Equal(new[] { "C", "A", "B", "D" }, Order(s));
    }

    [Fact]
    public void ZOrder_AtTheLimit_RecordsNoUndoStep()
    {
        var (s, c) = Four();
        Select(s, "D");
        c.BringToFront();
        c.BringForward();
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void ZOrder_IsUndoable()
    {
        var (s, c) = Four();
        Select(s, "A");
        c.BringToFront();
        s.PerformUndo();
        Assert.Equal(new[] { "A", "B", "C", "D" }, Order(s));
    }

    [Fact]
    public void MoveLayer_ReordersByIndex()
    {
        var (s, c) = Four();
        c.MoveLayer(s.Document.Elements[0], 3);
        Assert.Equal(new[] { "B", "C", "D", "A" }, Order(s));
    }

    // ------------------------------------------------------------------------------- duplicate / copy / paste / delete

    [Fact]
    public void Duplicate_CreatesNewIdsAtOffset_AndSelectsCopies()
    {
        var box = AppTestSupport.Box(100, 100);
        var s = AppTestSupport.Session(box);
        var c = new EditorCommands(s);
        s.Select(box);
        c.DuplicateSelected();
        Assert.Equal(2, s.Document.Elements.Count);
        var copy = s.Document.Elements[1];
        Assert.NotEqual(box.Id, copy.Id);
        Assert.Equal(100 + EditorCommands.PasteOffsetDots, copy.X);
        Assert.Equal(new[] { copy.Id }, s.SelectedIds);
    }

    [Fact]
    public void CopyPaste_RoundTripsThroughText_AndStaggersRepeatedPastes()
    {
        var box = AppTestSupport.Box(10, 10);
        var s = AppTestSupport.Session(box);
        var c = new EditorCommands(s);
        s.Select(box);
        string text = c.CopySelected()!;
        Assert.True(c.Paste(text));
        Assert.True(c.Paste(text));
        Assert.Equal(3, s.Document.Elements.Count);
        Assert.Equal(3, s.Document.Elements.Select(e => e.Id).Distinct().Count());
        Assert.Equal(10 + 2 * EditorCommands.PasteOffsetDots, s.Document.Elements[2].X);
    }

    [Fact]
    public void Paste_OfForeignText_DoesNothing()
    {
        var s = AppTestSupport.Session();
        Assert.False(new EditorCommands(s).Paste("hello"));
        Assert.False(s.Undo.CanUndo);
    }

    [Fact]
    public void CopySelected_WithNothingSelected_ReturnsNull()
    {
        Assert.Null(new EditorCommands(AppTestSupport.Session(AppTestSupport.Box(0, 0))).CopySelected());
    }

    [Fact]
    public void Delete_RemovesOnlySelected_AndClearsSelection()
    {
        var (s, c) = Four();
        Select(s, "B", "C");
        c.DeleteSelected();
        Assert.Equal(new[] { "A", "D" }, Order(s));
        Assert.Empty(s.SelectedIds);
    }

    // ------------------------------------------------------------------------------- nudge / rotate

    [Fact]
    public void Nudge_MovesAllSelected()
    {
        var a = AppTestSupport.Box(0, 0);
        var b = AppTestSupport.Box(50, 50);
        var s = AppTestSupport.Session(a, b);
        s.SelectAll();
        new EditorCommands(s).Nudge(3, -2);
        Assert.Equal((3, -2), (s.Document.Elements[0].X, s.Document.Elements[0].Y));
        Assert.Equal((53, 48), (s.Document.Elements[1].X, s.Document.Elements[1].Y));
    }

    [Fact]
    public void Rotate90_CyclesRotation_AndKeepsTheCentre()
    {
        var box = AppTestSupport.Box(100, 100, 200, 100);
        var s = AppTestSupport.Session(box);
        s.Select(box);
        var c = new EditorCommands(s);
        var before = s.Bounds.GetBounds(s.Document.Elements[0]);
        c.Rotate90(true);
        var el = s.Document.Elements[0];
        Assert.Equal(FieldRotation.Rotated90, el.Rotation);
        var after = s.Bounds.GetBounds(el);
        Assert.Equal(100, after.Width);
        Assert.Equal(200, after.Height);
        Assert.Equal(before.CenterX, after.CenterX, 1);
        Assert.Equal(before.CenterY, after.CenterY, 1);
        c.Rotate90(false);
        Assert.Equal(FieldRotation.Normal, s.Document.Elements[0].Rotation);
    }

    [Fact]
    public void Rotate90_FourTimes_IsTheIdentity()
    {
        var box = AppTestSupport.Box(100, 100, 200, 100);
        var s = AppTestSupport.Session(box);
        s.Select(box);
        var c = new EditorCommands(s);
        for (int i = 0; i < 4; i++) c.Rotate90(true);
        var el = s.Document.Elements[0];
        Assert.Equal((100, 100), (el.X, el.Y));
        Assert.Equal(FieldRotation.Normal, el.Rotation);
    }

    [Fact]
    public void Rotate90_SkipsCircles()
    {
        var circle = new CircleElement();
        var s = AppTestSupport.Session(circle);
        s.Select(circle);
        new EditorCommands(s).Rotate90();
        Assert.False(s.Undo.CanUndo);
    }

    // ------------------------------------------------------------------------------- align / distribute

    [Fact]
    public void Align_MultipleElements_UsesSelectionBounds()
    {
        var a = AppTestSupport.Box(10, 10, 100, 50);
        var b = AppTestSupport.Box(200, 300, 40, 20);
        var s = AppTestSupport.Session(a, b);
        s.SelectAll();
        var c = new EditorCommands(s);
        c.Align(AlignKind.Left);
        Assert.Equal(10, s.Document.Elements[1].X);
        c.Align(AlignKind.Bottom);
        Assert.Equal(320, s.Bounds.GetBounds(s.Document.Elements[0]).Bottom);
        Assert.Equal(s.Bounds.GetBounds(s.Document.Elements[0]).Bottom, s.Bounds.GetBounds(s.Document.Elements[1]).Bottom);
    }

    [Fact]
    public void Align_CenterHorizontally_CentresOnTheSelection()
    {
        var a = AppTestSupport.Box(0, 0, 100, 10);
        var b = AppTestSupport.Box(300, 50, 100, 10);
        var s = AppTestSupport.Session(a, b);
        s.SelectAll();
        new EditorCommands(s).Align(AlignKind.CenterHorizontal);
        Assert.Equal(s.Bounds.GetBounds(s.Document.Elements[0]).CenterX, s.Bounds.GetBounds(s.Document.Elements[1]).CenterX);
        Assert.Equal(200, s.Bounds.GetBounds(s.Document.Elements[0]).CenterX);
    }

    [Fact]
    public void Align_SingleElement_AlignsToTheLabel()
    {
        var box = AppTestSupport.Box(50, 50, 100, 100);
        var s = AppTestSupport.Session(box);
        s.Select(box);
        var c = new EditorCommands(s);
        c.Align(AlignKind.Right);
        Assert.Equal(700, s.Document.Elements[0].X);
        c.Align(AlignKind.CenterVertical);
        Assert.Equal(550, s.Document.Elements[0].Y);
    }

    [Fact]
    public void Distribute_Horizontal_MakesGapsEqual_AndKeepsTheOutermostPut()
    {
        var a = AppTestSupport.Box(0, 0, 50, 10);
        var b = AppTestSupport.Box(70, 0, 50, 10);
        var c3 = AppTestSupport.Box(400, 0, 50, 10);
        var s = AppTestSupport.Session(a, b, c3);
        s.SelectAll();
        new EditorCommands(s).Distribute(DistributeKind.Horizontal);
        var xs = s.Document.Elements.Select(e => e.X).ToArray();
        Assert.Equal(0, xs[0]);
        Assert.Equal(400, xs[2]);
        Assert.Equal(200, xs[1]); // gap = (450 - 150) / 2 = 150 -> 50 + 150
    }

    [Fact]
    public void Distribute_NeedsThreeElements()
    {
        var s = AppTestSupport.Session(AppTestSupport.Box(0, 0), AppTestSupport.Box(100, 0));
        s.SelectAll();
        new EditorCommands(s).Distribute(DistributeKind.Horizontal);
        Assert.False(s.Undo.CanUndo);
    }

    // ------------------------------------------------------------------------------- bounds

    [Fact]
    public void Bounds_BoxLineCircleImage_AreModelDerived()
    {
        var s = AppTestSupport.Session(
            new BoxElement { X = 10, Y = 20, WidthDots = 100, HeightDots = 40 },
            new LineElement { X = 0, Y = 0, LengthDots = 90, ThicknessDots = 4, Direction = LineDirection.Vertical },
            new CircleElement { X = 5, Y = 6, DiameterDots = 70 },
            new ImageElement { X = 1, Y = 2, Source = AppTestSupport.SmallBitmap(), WidthDots = 32 });
        var b = s.Document.Elements.Select(e => s.Bounds.GetBounds(e)).ToArray();
        Assert.Equal(new RectD(10, 20, 100, 40), b[0]);
        Assert.Equal(new RectD(0, 0, 4, 90), b[1]);
        Assert.Equal(new RectD(5, 6, 70, 70), b[2]);
        Assert.Equal(new RectD(1, 2, 32, 32), b[3]);
        Assert.Equal(0, s.Bounds.ProbeCount);
    }

    [Fact]
    public void Bounds_RotatedBox_SwapsSides_AndTypesetBoxExtendsUpwards()
    {
        var rotated = new BoxElement { X = 10, Y = 10, WidthDots = 100, HeightDots = 40, Rotation = FieldRotation.Rotated90 };
        var typeset = new BoxElement { X = 100, Y = 100, WidthDots = 50, HeightDots = 30, OriginKind = FieldOriginKind.Typeset };
        var s = AppTestSupport.Session(rotated, typeset);
        Assert.Equal(new RectD(10, 10, 40, 100), s.Bounds.GetBounds(rotated));
        Assert.Equal(new RectD(100, 70, 50, 30), s.Bounds.GetBounds(typeset));
    }

    [Fact]
    public void Bounds_Text_IsMeasuredByTheRenderer_AndCachedWhileOnlyMoving()
    {
        var text = new TextElement { Text = "HELLO WORLD", X = 40, Y = 50, FontHeight = 40 };
        var s = AppTestSupport.Session(text);
        var b = s.Bounds.GetBounds(text);
        Assert.True(b.Width > 80 && b.Width < 600, $"width {b.Width}");
        Assert.True(b.Height > 15 && b.Height < 80, $"height {b.Height}");
        Assert.True(b.X >= 40 - 2 && b.X < 80);
        Assert.Equal(1, s.Bounds.ProbeCount);

        text.X = 300; text.Y = 400; // moving must not re-measure
        var moved = s.Bounds.GetBounds(text);
        Assert.Equal(1, s.Bounds.ProbeCount);
        Assert.Equal(b.Width, moved.Width);
        Assert.Equal(b.X - 40 + 300, moved.X);

        text.Text = "A MUCH LONGER TEXT THAN BEFORE";
        Assert.True(s.Bounds.GetBounds(text).Width > b.Width);
        Assert.Equal(2, s.Bounds.ProbeCount);
    }

    [Fact]
    public void Bounds_Barcode_HasPlausibleSize()
    {
        var bc = new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "12345678", Height = 80, X = 20, Y = 30 };
        var s = AppTestSupport.Session(bc);
        var b = s.Bounds.GetBounds(bc);
        Assert.True(b.Height >= 80, $"height {b.Height}");
        Assert.True(b.Width > 60);
    }

    [Fact]
    public void Bounds_EmptyText_FallsBackToASelectableBox()
    {
        var text = new TextElement { Text = "", X = 10, Y = 10 };
        var s = AppTestSupport.Session(text);
        var b = s.Bounds.GetBounds(text);
        Assert.True(b.Width > 0 && b.Height > 0);
    }

    // ------------------------------------------------------------------------------- resize semantics

    [Fact]
    public void Resize_Box_ChangesWidthAndHeight_PinningTheOppositeCorner()
    {
        var box = AppTestSupport.Box(100, 100, 100, 50);
        var s = AppTestSupport.Session(box);
        var c = new EditorCommands(s);
        var start = s.Bounds.GetBounds(box);
        c.ApplyResize(box, start, ResizeHandle.TopLeft, 150, 100);
        Assert.Equal((150, 100), (box.WidthDots, box.HeightDots));
        Assert.Equal((50, 50), (box.X, box.Y)); // bottom-right corner (200,150) stays
    }

    [Fact]
    public void Resize_Line_ChangesLengthOnly_Circle_ChangesDiameter()
    {
        var line = new LineElement { LengthDots = 100, ThicknessDots = 4 };
        var circle = new CircleElement { DiameterDots = 100 };
        var s = AppTestSupport.Session(line, circle);
        var c = new EditorCommands(s);
        c.ApplyResize(line, s.Bounds.GetBounds(line), ResizeHandle.Right, 250, 4);
        Assert.Equal((250, 4), (line.LengthDots, line.ThicknessDots));
        c.ApplyResize(circle, s.Bounds.GetBounds(circle), ResizeHandle.BottomRight, 40, 40);
        Assert.Equal(40, circle.DiameterDots);
    }

    [Fact]
    public void Resize_Barcode_ChangesBarHeight_QrChangesModuleSize()
    {
        var bc = new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "12345", Height = 100, ModuleWidth = 2 };
        var qr = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "x", ModuleSize = 4 };
        var s = AppTestSupport.Session(bc, qr);
        var c = new EditorCommands(s);
        var b0 = s.Bounds.GetBounds(bc);
        c.ApplyResize(bc, b0, ResizeHandle.Bottom, b0.Width, b0.Height * 1.5);
        Assert.InRange(bc.Height, 140, 160);
        var q0 = s.Bounds.GetBounds(qr);
        c.ApplyResize(qr, q0, ResizeHandle.BottomRight, q0.Width * 2, q0.Height * 2);
        Assert.Equal(8, qr.ModuleSize);
        c.ApplyResize(qr, s.Bounds.GetBounds(qr), ResizeHandle.BottomRight, 1000, 1000);
        Assert.Equal(10, qr.ModuleSize); // clamped to the printer's maximum
    }

    [Fact]
    public void Resize_Text_ChangesFontHeight_Block_ChangesBlockWidth()
    {
        var plain = new TextElement { Text = "ABC", FontHeight = 40 };
        var block = new TextElement { Text = "ABC DEF", Block = new TextBlockOptions { WidthDots = 300 } };
        var s = AppTestSupport.Session(plain, block);
        var c = new EditorCommands(s);
        var b = s.Bounds.GetBounds(plain);
        c.ApplyResize(plain, b, ResizeHandle.BottomRight, b.Width * 2, b.Height * 2);
        Assert.InRange(plain.FontHeight!.Value, 78, 82);
        var bb = s.Bounds.GetBounds(block);
        c.ApplyResize(block, bb, ResizeHandle.Right, bb.Width * 0.5, bb.Height);
        Assert.Equal(150, block.Block!.WidthDots);
        c.ApplyResize(plain, s.Bounds.GetBounds(plain), ResizeHandle.BottomRight, 1, 1);
        Assert.Equal(ElementResizer.MinFontHeight, plain.FontHeight);
    }

    [Fact]
    public void Resize_RotatedBox_AppliesScaleAlongItsOwnAxes()
    {
        var box = new BoxElement { WidthDots = 100, HeightDots = 50, Rotation = FieldRotation.Rotated90 };
        var s = AppTestSupport.Session(box);
        var start = s.Bounds.GetBounds(box); // 50 wide, 100 tall on screen
        new EditorCommands(s).ApplyResize(box, start, ResizeHandle.Bottom, 50, 200);
        Assert.Equal((200, 50), (box.WidthDots, box.HeightDots)); // screen-vertical = the element's width axis
    }

    // ------------------------------------------------------------------------------- hit testing & snapping

    [Fact]
    public void HitTest_PicksTheFrontMostElement_AndToleratesThinLines()
    {
        var back = AppTestSupport.Box(0, 0, 200, 200);
        var front = AppTestSupport.Box(50, 50, 50, 50);
        var line = new LineElement { X = 300, Y = 300, LengthDots = 100, ThicknessDots = 1 };
        var s = AppTestSupport.Session(back, front, line);
        Assert.Same(front, HitTesting.HitElement(s, 60, 60, 2));
        Assert.Same(back, HitTesting.HitElement(s, 10, 10, 2));
        Assert.Null(HitTesting.HitElement(s, 500, 500, 2));
        Assert.Null(HitTesting.HitElement(s, 350, 306, 2));
        Assert.Same(line, HitTesting.HitElement(s, 350, 302, 3));
    }

    [Fact]
    public void ResizeRect_DraggedEdgesFollow_OppositeStay_MinimumSizeHolds()
    {
        var r = new RectD(100, 100, 100, 100);
        Assert.Equal(new RectD(80, 100, 120, 100), HitTesting.ResizeRect(r, ResizeHandle.Left, 80, 999, 4));
        Assert.Equal(new RectD(100, 100, 150, 50), HitTesting.ResizeRect(r, ResizeHandle.BottomRight, 250, 150, 4));
        var tiny = HitTesting.ResizeRect(r, ResizeHandle.Right, 10, 0, 4);
        Assert.Equal(4, tiny.Width);
        Assert.Equal(100, tiny.X);
    }

    [Fact]
    public void HitHandle_FindsCornersAndEdges()
    {
        var r = new RectD(100, 100, 200, 100);
        Assert.Equal(ResizeHandle.TopLeft, HitTesting.HitHandle(r, 101, 99, 5));
        Assert.Equal(ResizeHandle.Right, HitTesting.HitHandle(r, 301, 150, 5));
        Assert.Null(HitTesting.HitHandle(r, 200, 150, 5));
    }

    [Fact]
    public void QuarterTurns_FollowsThePointerAroundTheCentre()
    {
        var r = new RectD(0, 0, 100, 100);
        Assert.Equal(0, HitTesting.QuarterTurns(r, 50, -30));
        Assert.Equal(1, HitTesting.QuarterTurns(r, 140, 50));
        Assert.Equal(2, HitTesting.QuarterTurns(r, 50, 140));
        Assert.Equal(3, HitTesting.QuarterTurns(r, -40, 50));
    }

    [Fact]
    public void Snap_ToOtherElementEdge_WinsOverGrid_AndReportsGuide()
    {
        var label = new RectD(0, 0, 800, 1200);
        var other = new RectD(300, 0, 100, 100);
        var moving = new RectD(403, 500, 50, 50); // left edge is 3 dots from the other's right edge (400)
        var grid = new GridSettings(LengthUnit.Millimeters, 5, true, true);
        var snap = DragSnapper.Snap(moving, new[] { other }, label, grid, 203, 6);
        Assert.Equal(-3, snap.Dx, 6);
        Assert.Contains(400.0, snap.VerticalGuides);
    }

    [Fact]
    public void Snap_FallsBackToGrid_AndCanBeSwitchedOff()
    {
        var label = new RectD(0, 0, 800, 1200);
        var moving = new RectD(203.4, 100.7, 50, 50);
        var grid = new GridSettings(LengthUnit.Dots, 10, true, true);
        var snap = DragSnapper.Snap(moving, Array.Empty<RectD>(), label, grid, 203, 2);
        Assert.Equal(200, moving.X + snap.Dx, 6);
        Assert.Equal(100, moving.Y + snap.Dy, 6);
        var off = DragSnapper.Snap(moving, Array.Empty<RectD>(), label, grid with { SnapEnabled = false }, 203, 2, useGuides: false);
        Assert.Equal((0, 0), (off.Dx, off.Dy));
    }

    [Fact]
    public void Snap_ToLabelCentre_Works()
    {
        var label = new RectD(0, 0, 800, 1200);
        var moving = new RectD(375, 10, 50, 50); // centre x = 400 = label centre
        var snap = DragSnapper.Snap(moving, Array.Empty<RectD>(), label, new GridSettings(SnapEnabled: false), 203, 6);
        Assert.Equal(0, snap.Dx);
        Assert.Contains(400.0, snap.VerticalGuides);
    }
}
