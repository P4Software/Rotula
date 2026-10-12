using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.App.Editing;

/// <summary>How selected elements are lined up.</summary>
public enum AlignKind
{
    /// <summary>Left edges.</summary>
    Left,
    /// <summary>Horizontal centres.</summary>
    CenterHorizontal,
    /// <summary>Right edges.</summary>
    Right,
    /// <summary>Top edges.</summary>
    Top,
    /// <summary>Vertical centres.</summary>
    CenterVertical,
    /// <summary>Bottom edges.</summary>
    Bottom,
}

/// <summary>How selected elements are spread out.</summary>
public enum DistributeKind
{
    /// <summary>Equal gaps between elements from left to right.</summary>
    Horizontal,
    /// <summary>Equal gaps between elements from top to bottom.</summary>
    Vertical,
}

/// <summary>One of the eight resize handles around a selected element.</summary>
public enum ResizeHandle
{
    /// <summary>Top-left corner.</summary>
    TopLeft,
    /// <summary>Top edge.</summary>
    Top,
    /// <summary>Top-right corner.</summary>
    TopRight,
    /// <summary>Right edge.</summary>
    Right,
    /// <summary>Bottom-right corner.</summary>
    BottomRight,
    /// <summary>Bottom edge.</summary>
    Bottom,
    /// <summary>Bottom-left corner.</summary>
    BottomLeft,
    /// <summary>Left edge.</summary>
    Left,
}

/// <summary>
/// The editing operations of the design canvas, each applied to an <see cref="EditorSession"/> as one undo step.
/// Kept free of WPF so every rule (z-order, align, nudge, resize semantics) is covered by plain unit tests.
/// </summary>
public sealed class EditorCommands
{
    /// <summary>Distance in dots by which duplicates and repeated pastes are offset from the original.</summary>
    public const int PasteOffsetDots = 16;

    private readonly EditorSession _s;
    private int _pasteCount;

    /// <summary>Creates the command set for a session.</summary>
    public EditorCommands(EditorSession session) => _s = session;

    /// <summary>
    /// How many selected objects the last command left alone because they are locked (0 when none). The window uses it to tell the user why
    /// nothing moved, instead of ignoring the key or drag in silence.
    /// </summary>
    public int LockedSkipped { get; private set; }

    /// <summary>
    /// The part of the selection a command that changes position, size or existence may touch: the locked objects are left out and counted
    /// in <see cref="LockedSkipped"/>. Why here and not in the window: the rule must hold for every way in (keys, menus, mouse, panel).
    /// </summary>
    private List<LabelElement> Unlocked(IReadOnlyList<LabelElement> selection)
    {
        var free = selection.Where(e => !e.Locked).ToList();
        LockedSkipped = selection.Count - free.Count;
        return free;
    }

    // ------------------------------------------------------------------------------- add / delete

    /// <summary>Adds an element on top of the others and selects it.</summary>
    /// <param name="element">The new element.</param>
    /// <param name="alsoChange">Optional extra change to the document made in the same undo step (for example declaring a variable).</param>
    public void Add(LabelElement element, Action<LabelDocument>? alsoChange = null)
    {
        _s.Edit("Undo.Add", d =>
        {
            alsoChange?.Invoke(d);
            d.Elements.Add(element);
        }, selectionAfter: () => new[] { element.Id });
    }

    /// <summary>Deletes the selected elements.</summary>
    public void DeleteSelected()
    {
        var ids = Unlocked(_s.SelectedElements).Select(e => e.Id).ToHashSet();
        if (ids.Count == 0) return;
        _s.Edit("Undo.Delete", d => d.Elements.RemoveAll(e => ids.Contains(e.Id)), selectionAfter: () => Array.Empty<string>());
    }

    /// <summary>Copies the selection with fresh ids, offset a little so the copy is visible, and selects the copies.</summary>
    public void DuplicateSelected()
    {
        var copies = _s.SelectedElements.Select(e => ElementCloner.Clone(e, newId: true)).ToList();
        if (copies.Count == 0) return;
        foreach (var c in copies)
        {
            c.X += PasteOffsetDots;
            c.Y += PasteOffsetDots;
        }
        _s.Edit("Undo.Duplicate", d => d.Elements.AddRange(copies), selectionAfter: () => copies.Select(c => c.Id).ToList());
    }

    /// <summary>The clipboard text for the current selection, or null when nothing is selected.</summary>
    public string? CopySelected()
    {
        var sel = _s.SelectedElements;
        _pasteCount = 0;
        return sel.Count == 0 ? null : Files.RotulaFileSerializer.SerializeElements(sel);
    }

    /// <summary>Pastes elements from clipboard text. Returns false when the text holds no Rotula elements.</summary>
    public bool Paste(string? clipboardText)
    {
        var items = Files.RotulaFileSerializer.DeserializeElements(clipboardText);
        if (items.Count == 0) return false;
        _pasteCount++;
        var fresh = new List<LabelElement>();
        foreach (var item in items)
        {
            // Pasted elements always get new ids, otherwise pasting twice would create two elements with the same id.
            var c = ElementCloner.Clone(item, newId: true);
            c.X += PasteOffsetDots * _pasteCount;
            c.Y += PasteOffsetDots * _pasteCount;
            fresh.Add(c);
        }
        _s.Edit("Undo.Paste", d => d.Elements.AddRange(fresh), selectionAfter: () => fresh.Select(c => c.Id).ToList());
        return true;
    }

    // ------------------------------------------------------------------------------- z-order

    /// <summary>Moves the selection to the top of the stack, keeping its internal order.</summary>
    public void BringToFront() => Reorder("Undo.BringToFront", (list, sel) =>
    {
        var moved = list.Where(sel.Contains).ToList();
        list.RemoveAll(sel.Contains);
        list.AddRange(moved);
    });

    /// <summary>Moves the selection to the bottom of the stack, keeping its internal order.</summary>
    public void SendToBack() => Reorder("Undo.SendToBack", (list, sel) =>
    {
        var moved = list.Where(sel.Contains).ToList();
        list.RemoveAll(sel.Contains);
        list.InsertRange(0, moved);
    });

    /// <summary>Moves each selected element one step up (towards the front).</summary>
    public void BringForward() => Reorder("Undo.BringForward", (list, sel) =>
    {
        // Walk from the top so adjacent selected elements keep moving together instead of swapping with each other.
        for (int i = list.Count - 2; i >= 0; i--)
            if (sel.Contains(list[i]) && !sel.Contains(list[i + 1]))
                (list[i], list[i + 1]) = (list[i + 1], list[i]);
    });

    /// <summary>Moves each selected element one step down (towards the back).</summary>
    public void SendBackward() => Reorder("Undo.SendBackward", (list, sel) =>
    {
        for (int i = 1; i < list.Count; i++)
            if (sel.Contains(list[i]) && !sel.Contains(list[i - 1]))
                (list[i], list[i - 1]) = (list[i - 1], list[i]);
    });

    private void Reorder(string key, Action<List<LabelElement>, HashSet<LabelElement>> reorder)
    {
        var sel = _s.SelectedElements.ToHashSet();
        if (sel.Count == 0) return;
        var before = _s.Document.Elements.ToList();
        var probe = before.ToList();
        reorder(probe, sel);
        if (probe.SequenceEqual(before)) return; // already at the limit: do not record an empty undo step
        _s.Edit(key, d =>
        {
            var list = d.Elements.ToList();
            reorder(list, d.Elements.Where(e => _s.IsSelected(e)).ToHashSet());
            d.Elements.Clear();
            d.Elements.AddRange(list);
        });
    }

    /// <summary>Moves one layer in the layers list (used by the layer buttons and drag).</summary>
    public void MoveLayer(LabelElement element, int newIndex)
    {
        int old = _s.Document.Elements.IndexOf(element);
        newIndex = Math.Clamp(newIndex, 0, _s.Document.Elements.Count - 1);
        if (old < 0 || old == newIndex) return;
        _s.Edit("Undo.Reorder", d =>
        {
            d.Elements.RemoveAt(old);
            d.Elements.Insert(newIndex, element);
        });
    }

    // ------------------------------------------------------------------------------- move / nudge / rotate

    /// <summary>
    /// Moves the selection. Repeated nudges within a second merge into one undo step.
    /// </summary>
    public void Nudge(int dx, int dy)
    {
        var sel = Unlocked(_s.SelectedElements);
        if (sel.Count == 0 || (dx == 0 && dy == 0)) return;
        var movable = sel.Select(e => e.Id).ToHashSet();
        _s.Edit("Undo.Move", d =>
        {
            foreach (var e in d.Elements.Where(e => movable.Contains(e.Id)))
            {
                e.X += dx;
                e.Y += dy;
            }
        }, mergeKey: "nudge:" + string.Join(',', movable));
    }

    /// <summary>
    /// Rotates the selected elements by 90 degrees about their own centres. Rotation in ZPL only has four steps, so a
    /// free-angle rotation does not exist in this model. Circles ignore rotation and are left alone.
    /// </summary>
    public void Rotate90(bool clockwise = true)
    {
        var targets = Unlocked(_s.SelectedElements).Where(e => e is not (CircleElement or EllipseElement or DiagonalLineElement)).ToList();
        if (targets.Count == 0) return;
        _s.Edit("Undo.Rotate", d =>
        {
            foreach (var e in d.Elements.Where(e => targets.Any(t => t.Id == e.Id)))
            {
                var before = _s.Bounds.GetBounds(e);
                var current = e is ImageElement img ? img.Rotation ?? FieldRotation.Normal : ElementBoundsCalculator.EffectiveRotation(e, d);
                int steps = ((int)current + (clockwise ? 1 : 3)) % 4;
                e.Rotation = (FieldRotation)steps;
                var after = _s.Bounds.GetBounds(e);
                // Keep the visual centre fixed so the element turns in place instead of jumping.
                e.X += (int)Math.Round(before.CenterX - after.CenterX);
                e.Y += (int)Math.Round(before.CenterY - after.CenterY);
            }
        });
    }

    // ------------------------------------------------------------------------------- align / distribute

    /// <summary>
    /// Aligns the selection. With several elements they align to the selection's bounding box; with a single element it
    /// aligns to the label.
    /// </summary>
    public void Align(AlignKind kind)
    {
        var sel = _s.SelectedElements;
        if (sel.Count == 0) return;
        // A locked object still counts for the reference box (it is a fine anchor to line the others up with) but it is never moved.
        RectD reference = sel.Count == 1 ? LabelRect(_s.Document) : _s.Bounds.GetSelectionBounds(sel);
        var movable = Unlocked(sel).Select(e => e.Id).ToHashSet();
        if (movable.Count == 0) return;
        _s.Edit("Undo.Align", d =>
        {
            foreach (var e in d.Elements.Where(e => movable.Contains(e.Id)))
            {
                var b = _s.Bounds.GetBounds(e);
                double dx = 0, dy = 0;
                switch (kind)
                {
                    case AlignKind.Left: dx = reference.X - b.X; break;
                    case AlignKind.CenterHorizontal: dx = reference.CenterX - b.CenterX; break;
                    case AlignKind.Right: dx = reference.Right - b.Right; break;
                    case AlignKind.Top: dy = reference.Y - b.Y; break;
                    case AlignKind.CenterVertical: dy = reference.CenterY - b.CenterY; break;
                    case AlignKind.Bottom: dy = reference.Bottom - b.Bottom; break;
                }
                e.X += (int)Math.Round(dx);
                e.Y += (int)Math.Round(dy);
            }
        });
    }

    /// <summary>Spreads three or more elements so the gaps between them are equal; the outermost two stay put.</summary>
    public void Distribute(DistributeKind kind)
    {
        var sel = _s.SelectedElements;
        if (sel.Count < 3) return;
        bool horizontal = kind == DistributeKind.Horizontal;
        var ordered = sel.Select(e => (Element: e, Bounds: _s.Bounds.GetBounds(e)))
            .OrderBy(t => horizontal ? t.Bounds.X : t.Bounds.Y).ToList();
        double first = horizontal ? ordered[0].Bounds.X : ordered[0].Bounds.Y;
        double last = horizontal ? ordered[^1].Bounds.Right : ordered[^1].Bounds.Bottom;
        double total = ordered.Sum(t => horizontal ? t.Bounds.Width : t.Bounds.Height);
        double gap = (last - first - total) / (ordered.Count - 1);
        var targets = new Dictionary<string, double>();
        double pos = first;
        Unlocked(sel); // only to count the locked ones: they keep their place but still take part in the spacing
        foreach (var t in ordered)
        {
            if (!t.Element.Locked) targets[t.Element.Id] = pos - (horizontal ? t.Bounds.X : t.Bounds.Y);
            pos += (horizontal ? t.Bounds.Width : t.Bounds.Height) + gap;
        }
        if (targets.Count == 0) return;
        _s.Edit("Undo.Distribute", d =>
        {
            foreach (var e in d.Elements.Where(e => targets.ContainsKey(e.Id)))
            {
                int delta = (int)Math.Round(targets[e.Id]);
                if (horizontal) e.X += delta; else e.Y += delta;
            }
        });
    }

    /// <summary>The label rectangle in element coordinates (element X/Y are relative to the label home).</summary>
    public static RectD LabelRect(LabelDocument doc)
    {
        int hx = doc.Margins.Left.ToDots(doc.Dpi), hy = doc.Margins.Top.ToDots(doc.Dpi);
        return new RectD(-hx, -hy, doc.WidthDots, doc.HeightDots);
    }

    // ------------------------------------------------------------------------------- resize

    /// <summary>
    /// Resizes one element from a handle drag. What "size" means depends on the element, because the printer model has no
    /// free scaling: boxes and images change width and height, lines change length, circles change diameter, barcodes
    /// change bar height (module size for 2D codes) and text changes font height (block width for wrapped text).
    /// The edge opposite to the handle stays where it was.
    /// </summary>
    /// <param name="element">The element, already reset to its state at the start of the drag.</param>
    /// <param name="start">Its bounds at the start of the drag.</param>
    /// <param name="handle">The handle being dragged.</param>
    /// <param name="newWidth">New bounding-box width in dots.</param>
    /// <param name="newHeight">New bounding-box height in dots.</param>
    public void ApplyResize(LabelElement element, RectD start, ResizeHandle handle, double newWidth, double newHeight)
    {
        if (element.Locked) { LockedSkipped = 1; return; }
        bool affectsX = handle is not (ResizeHandle.Top or ResizeHandle.Bottom);
        bool affectsY = handle is not (ResizeHandle.Left or ResizeHandle.Right);
        double sx = affectsX && start.Width > 0 ? Math.Max(0.01, newWidth) / start.Width : 1;
        double sy = affectsY && start.Height > 0 ? Math.Max(0.01, newHeight) / start.Height : 1;
        ElementResizer.Scale(element, _s.Document, sx, sy);

        // Pin the edge opposite to the dragged handle.
        var now = _s.Bounds.GetBounds(element);
        bool leftHandle = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        bool topHandle = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        if (affectsX) element.X += (int)Math.Round(leftHandle ? start.Right - now.Right : start.X - now.X);
        if (affectsY) element.Y += (int)Math.Round(topHandle ? start.Bottom - now.Bottom : start.Y - now.Y);
    }
}

/// <summary>Element-specific meaning of resizing (see <see cref="EditorCommands.ApplyResize"/>).</summary>
public static class ElementResizer
{
    /// <summary>Smallest font height the printer accepts.</summary>
    public const int MinFontHeight = 10;

    /// <summary>Scales an element by independent factors along the screen axes of its bounding box.</summary>
    public static void Scale(LabelElement e, LabelDocument doc, double sx, double sy)
    {
        var rot = ElementBoundsCalculator.EffectiveRotation(e, doc);
        bool swap = rot is FieldRotation.Rotated90 or FieldRotation.Rotated270;
        // Factors along the element's own (unrotated) axes.
        double ux = swap ? sy : sx, uy = swap ? sx : sy;
        switch (e)
        {
            case BoxElement b:
                b.WidthDots = Dots(b.WidthDots * ux);
                b.HeightDots = Dots(b.HeightDots * uy);
                break;
            case LineElement l:
            {
                bool horizontal = l.Direction == LineDirection.Horizontal;
                l.LengthDots = Dots(l.LengthDots * (horizontal ? ux : uy));
                break;
            }
            case CircleElement c:
                c.DiameterDots = Math.Max(3, (int)Math.Round(c.DiameterDots * Math.Max(sx, sy)));
                break;
            case EllipseElement el:
                el.WidthDots = Math.Max(3, (int)Math.Round(el.WidthDots * sx));
                el.HeightDots = Math.Max(3, (int)Math.Round(el.HeightDots * sy));
                break;
            case DiagonalLineElement dl:
                dl.WidthDots = Math.Max(3, (int)Math.Round(dl.WidthDots * sx));
                dl.HeightDots = Math.Max(3, (int)Math.Round(dl.HeightDots * sy));
                break;
            case SymbolElement sym:
                sym.HeightDots = Math.Clamp((int)Math.Round(sym.HeightDots * uy), 1, 32000);
                if (sym.WidthDots > 0) sym.WidthDots = Math.Clamp((int)Math.Round(sym.WidthDots * uy), 1, 32000);
                break;
            case ImageElement i when i.Source is not null:
            {
                var (w, h) = Imaging(i);
                bool iswap = (i.Rotation ?? FieldRotation.Normal) is FieldRotation.Rotated90 or FieldRotation.Rotated270;
                i.WidthDots = Dots(w * (iswap ? sy : sx));
                i.HeightDots = Dots(h * (iswap ? sx : sy));
                break;
            }
            case BarcodeElement bc:
                ScaleBarcode(bc, ux, uy);
                break;
            case TextElement t:
                if (t.Block is not null)
                    t.Block.WidthDots = Math.Clamp((int)Math.Round(t.Block.WidthDots * ux), 1, 9999);
                else
                {
                    int current = t.FontHeight ?? doc.DefaultFont?.Height ?? 30;
                    t.FontHeight = Math.Clamp((int)Math.Round(current * uy), MinFontHeight, 32000);
                    // Keep proportional width fonts proportional: an explicit width scales with the height.
                    if (t.FontWidth is > 0) t.FontWidth = Math.Clamp((int)Math.Round(t.FontWidth.Value * uy), 1, 32000);
                }
                break;
        }
    }

    private static (int W, int H) Imaging(ImageElement i) => Core.Imaging.ImageProcessor.ResolveSize(i);

    private static void ScaleBarcode(BarcodeElement b, double ux, double uy)
    {
        var info = Core.Barcodes.BarcodeCatalog.Get(b.Symbology);
        // Symbologies sized by their own magnification (QR, Data Matrix, Aztec, DataBar, ...) have a fixed aspect ratio,
        // so the module size is the knob (1-10). Stacked ones with a bar height (DataBar) keep it in step.
        if (info.UsesModuleSize)
            b.ModuleSize = Math.Clamp((int)Math.Round(b.ModuleSize * Math.Max(ux, uy)), 1, 10);
        if (info.Supports(Core.Barcodes.BarcodeOption.Height))
            b.Height = Math.Clamp((int)Math.Round(b.Height * uy), 1, 32000);
        // Width follows the module width, so dragging horizontally changes it in whole dots.
        if (info.Supports(Core.Barcodes.BarcodeOption.ModuleWidth))
            b.ModuleWidth = Math.Clamp((int)Math.Round(b.ModuleWidth * ux), 1, 10);
    }

    private static int Dots(double v) => Math.Max(1, (int)Math.Round(v));
}
