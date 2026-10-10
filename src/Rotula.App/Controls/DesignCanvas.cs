using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rotula.App.Editing;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Rendering.View;

namespace Rotula.App.Controls;

/// <summary>
/// The design surface. The label itself is drawn by Rotula.Rendering (cached while the design is unchanged); everything
/// interactive - grid, selection, handles, guides - is drawn on top by WPF, so moving the pointer never re-renders the label.
/// </summary>
public sealed class DesignCanvas : FrameworkElement
{
    /// <summary>Empty border around the label inside the scroll area, in device-independent pixels.</summary>
    public const double Gutter = 48;

    private const double HandleSize = 9;
    private const double DragStartPixels = 3;
    private const double SnapPixels = 7;

    private enum Mode { None, Move, Resize, Rotate, Marquee }

    private static readonly Brush PaperBrush = Brushes.White;
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x42, 0x0F));
    private static readonly Brush MarqueeFill = new SolidColorBrush(Color.FromArgb(32, 0xC8, 0x42, 0x0F));
    private static readonly Brush GuideBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0x2F, 0x8B));
    private static readonly Brush GridDotBrush = new SolidColorBrush(Color.FromArgb(120, 110, 100, 90));
    private static readonly Pen MarginPen = CreatePen(Color.FromArgb(150, 200, 120, 0), 1, new DashStyle(new double[] { 3, 3 }, 0));

    private MainViewModel? _vm;
    private WriteableBitmap? _bitmap;
    private long _bitmapRevision = -1;
    private double _bitmapScale;
    private string? _previewError;

    private Mode _mode;
    private Point _downPixel;
    private bool _dragStarted;
    private GestureToken? _gesture;
    private Dictionary<string, (int X, int Y)> _origins = new();
    private LabelElement? _resizeOriginal;
    private ResizeHandle _handle;
    private RectD _startBounds;
    private RectD _marquee;
    private bool _marqueeAdditive;
    private List<double> _vGuides = new();
    private List<double> _hGuides = new();
    private (double X, double Y) _downDots;

    /// <summary>Creates the canvas.</summary>
    public DesignCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        DataContextChanged += OnDataContextChanged;
        // The bitmap is already at screen resolution; nearest-neighbour keeps dots crisp when WPF has to resample.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    private static Pen CreatePen(Color c, double thickness, DashStyle? dash)
    {
        var pen = new Pen(new SolidColorBrush(c), thickness);
        if (dash is not null) pen.DashStyle = dash;
        pen.Freeze();
        return pen;
    }

    // ------------------------------------------------------------------------------- wiring

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Session.LiveChanged -= OnLive;
            _vm.Session.DocumentChanged -= OnDocumentChanged;
            _vm.Session.SelectionChanged -= OnLive;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null)
        {
            _vm.Session.LiveChanged += OnLive;
            _vm.Session.DocumentChanged += OnDocumentChanged;
            _vm.Session.SelectionChanged += OnLive;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnLive(object? sender, EventArgs e) => InvalidateVisual();

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Zoom))
            InvalidateMeasure();
        if (e.PropertyName is nameof(MainViewModel.Zoom) or nameof(MainViewModel.GridVisible) or nameof(MainViewModel.Unit))
            InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _bitmap = null; // pixel size depends on the monitor scale
        InvalidateVisual();
    }

    // ------------------------------------------------------------------------------- geometry

    private LabelDocument Doc => _vm!.Session.Document;
    private int Dpi => (int)Doc.Dpi;
    private double Ppd => _vm!.Zoom.PixelsPerDot(Dpi);
    private double HomeX => Doc.Margins.Left.ToDots(Doc.Dpi);
    private double HomeY => Doc.Margins.Top.ToDots(Doc.Dpi);

    /// <summary>Pixels per dot at the current zoom (used by the rulers).</summary>
    public double PixelsPerDot => _vm is null ? 1 : Ppd;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_vm is null) return new Size(0, 0);
        return new Size(Doc.WidthDots * Ppd + 2 * Gutter, Doc.HeightDots * Ppd + 2 * Gutter);
    }

    private Point ToPixel(double xDots, double yDots) => new(Gutter + (xDots + HomeX) * Ppd, Gutter + (yDots + HomeY) * Ppd);

    private (double X, double Y) ToDots(Point p) => ((p.X - Gutter) / Ppd - HomeX, (p.Y - Gutter) / Ppd - HomeY);

    private Rect ToPixelRect(RectD r)
    {
        var tl = ToPixel(r.X, r.Y);
        return new Rect(tl.X, tl.Y, r.Width * Ppd, r.Height * Ppd);
    }

    // ------------------------------------------------------------------------------- drawing

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_vm is null) return;
        double ppd = Ppd;
        var labelRect = new Rect(Gutter, Gutter, Doc.WidthDots * ppd, Doc.HeightDots * ppd);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        // A soft drop shadow: a few translucent layers growing outwards read as blur without an expensive bitmap effect.
        for (int i = 4; i >= 1; i--)
        {
            var layer = new SolidColorBrush(Color.FromArgb((byte)(10 + (5 - i) * 4), 40, 30, 20));
            dc.DrawRoundedRectangle(layer, null, new Rect(labelRect.X - i + 1, labelRect.Y - i + 4, labelRect.Width + 2 * i - 2, labelRect.Height + 2 * i - 2), 2 + i, 2 + i);
        }
        dc.DrawRectangle(PaperBrush, null, labelRect);

        DrawPreview(dc, labelRect);
        if (_vm.GridVisible) DrawGrid(dc, labelRect);
        DrawPrintableArea(dc);
        DrawSelection(dc);
        DrawGuides(dc, labelRect);
        if (_mode == Mode.Marquee && _dragStarted)
        {
            var r = ToPixelRect(_marquee);
            dc.DrawRectangle(MarqueeFill, new Pen(AccentBrush, 1), r);
        }
    }

    private void DrawPreview(DrawingContext dc, Rect labelRect)
    {
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double scale = Ppd * dpiScale;
        // The cache only re-renders when the design or the scale changed; a repaint caused by the pointer is nearly free.
        var result = _vm!.Preview.GetBitmap(scale, out _previewError);
        if (result is null)
        {
            DrawCentered(dc, labelRect, Loc(_previewError ?? "Preview.InvalidSize"));
            return;
        }
        var bmp = result.Bitmap;
        if (_bitmap is null || _bitmap.PixelWidth != bmp.Width || _bitmap.PixelHeight != bmp.Height || Math.Abs(_bitmap.DpiX - 96 * dpiScale) > 0.01)
        {
            _bitmap = new WriteableBitmap(bmp.Width, bmp.Height, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32, null);
            _bitmapRevision = -1;
        }
        if (_bitmapRevision != _vm.Session.Revision || Math.Abs(_bitmapScale - scale) > 1e-9)
        {
            _bitmap.WritePixels(new Int32Rect(0, 0, bmp.Width, bmp.Height), bmp.GetPixels(), bmp.RowBytes * bmp.Height, bmp.RowBytes);
            _bitmapRevision = _vm.Session.Revision;
            _bitmapScale = scale;
        }
        dc.DrawImage(_bitmap, new Rect(labelRect.X, labelRect.Y, bmp.Width / dpiScale, bmp.Height / dpiScale));
    }

    private static string Loc(string key) => Localization.Loc.Get(key);

    private void DrawCentered(DrawingContext dc, Rect area, string text)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13,
            Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(60, area.Width - 20), TextAlignment = System.Windows.TextAlignment.Center };
        dc.DrawText(ft, new Point(area.X + 10, area.Y + Math.Max(10, (area.Height - ft.Height) / 2)));
    }

    private void DrawGrid(DrawingContext dc, Rect labelRect)
    {
        double step = _vm!.Grid.SpacingDots(Dpi) * Ppd;
        // Lines closer than 5 px only make a grey smear; skip them like design tools do.
        if (step < 8) return;
        // A dot grid (a dot at every crossing) stays out of the way of the design far better than lines do.
        for (double x = labelRect.X; x <= labelRect.Right + 0.01; x += step)
            for (double y = labelRect.Y; y <= labelRect.Bottom + 0.01; y += step)
                dc.DrawRectangle(GridDotBrush, null, new Rect(x - 0.5, y - 0.5, 1.5, 1.5));
    }

    private void DrawPrintableArea(DrawingContext dc)
    {
        var m = Doc.Margins;
        if (HomeX == 0 && HomeY == 0 && m.Right.Value == 0 && m.Bottom.Value == 0) return;
        double right = Doc.WidthDots - m.Right.ToDots(Doc.Dpi), bottom = Doc.HeightDots - m.Bottom.ToDots(Doc.Dpi);
        var tl = new Point(Gutter + HomeX * Ppd, Gutter + HomeY * Ppd);
        var br = new Point(Gutter + right * Ppd, Gutter + bottom * Ppd);
        dc.DrawRectangle(null, MarginPen, new Rect(tl, br));
    }

    private void DrawSelection(DrawingContext dc)
    {
        var selected = _vm!.Session.SelectedElements;
        if (selected.Count == 0) return;
        var pen = new Pen(AccentBrush, 1);
        foreach (var e in selected)
            dc.DrawRectangle(null, pen, ToPixelRect(_vm.Session.Bounds.GetBounds(e)));

        var union = _vm.Session.Bounds.GetSelectionBounds(selected);
        if (selected.Count > 1) dc.DrawRectangle(null, new Pen(AccentBrush, 1), ToPixelRect(union));

        if (selected.Count != 1) return;
        var r = ToPixelRect(union);
        foreach (var h in Enum.GetValues<ResizeHandle>())
        {
            var (hx, hy) = HitTesting.HandlePosition(union, h);
            var p = ToPixel(hx, hy);
            dc.DrawRectangle(Brushes.White, new Pen(AccentBrush, 1), new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
        }
        // Rotate handle: a circle above the top edge, joined to it by a thin stem.
        var top = new Point(r.X + r.Width / 2, r.Y);
        var rot = new Point(top.X, top.Y - HitTesting.RotateHandleOffsetPixels);
        dc.DrawLine(new Pen(AccentBrush, 1), top, rot);
        dc.DrawEllipse(Brushes.White, new Pen(AccentBrush, 1.5), rot, HandleSize / 2 + 1, HandleSize / 2 + 1);
    }

    private void DrawGuides(DrawingContext dc, Rect labelRect)
    {
        var pen = new Pen(GuideBrush, 1);
        foreach (double x in _vGuides) { var p = ToPixel(x, 0); dc.DrawLine(pen, new Point(p.X, labelRect.Y - 8), new Point(p.X, labelRect.Bottom + 8)); }
        foreach (double y in _hGuides) { var p = ToPixel(0, y); dc.DrawLine(pen, new Point(labelRect.X - 8, p.Y), new Point(labelRect.Right + 8, p.Y)); }
    }

    // ------------------------------------------------------------------------------- mouse

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_vm is null) return;
        Focus();
        var pixel = e.GetPosition(this);
        var (x, y) = ToDots(pixel);
        _downPixel = pixel;
        _downDots = (x, y);
        _dragStarted = false;
        var session = _vm.Session;
        double tol = Snapping.ThresholdDots(4, Ppd);
        double handleTol = Snapping.ThresholdDots(HandleSize, Ppd);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // 1. A handle of the single selected element.
        var selected = session.SelectedElements;
        if (selected.Count == 1)
        {
            var b = session.Bounds.GetBounds(selected[0]);
            if (HitTesting.HitRotateHandle(b, x, y, Snapping.ThresholdDots(HitTesting.RotateHandleOffsetPixels, Ppd), handleTol))
            {
                _mode = Mode.Rotate;
                _startBounds = b;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            if (HitTesting.HitHandle(b, x, y, handleTol) is { } handle)
            {
                _mode = Mode.Resize;
                _handle = handle;
                _startBounds = b;
                _resizeOriginal = ElementCloner.Clone(selected[0]);
                _gesture = session.BeginGesture();
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // 2. An element.
        var hit = HitTesting.HitElement(session, x, y, tol);
        if (hit is not null)
        {
            if (ctrl) { session.ToggleSelection(hit); _mode = Mode.None; e.Handled = true; return; }
            if (shift && !session.IsSelected(hit)) session.SetSelection(session.SelectedIds.Append(hit.Id));
            else if (!session.IsSelected(hit)) session.Select(hit);
            _mode = Mode.Move;
            _gesture = session.BeginGesture();
            _origins = session.SelectedElements.ToDictionary(el => el.Id, el => (el.X, el.Y));
            _startBounds = session.Bounds.GetSelectionBounds(session.SelectedElements);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 3. Empty space: rubber band.
        _marqueeAdditive = ctrl || shift;
        if (!_marqueeAdditive) session.SetSelection(Array.Empty<string>());
        _mode = Mode.Marquee;
        _marquee = new RectD(x, y, 0, 0);
        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_vm is null) return;
        var pixel = e.GetPosition(this);
        var (x, y) = ToDots(pixel);
        bool inside = x >= -HomeX && y >= -HomeY && x <= Doc.WidthDots - HomeX && y <= Doc.HeightDots - HomeY;
        _vm.SetCursorDots(inside ? x + HomeX : null, inside ? y + HomeY : null);

        if (_mode == Mode.None || e.LeftButton != MouseButtonState.Pressed)
        {
            UpdateCursor(x, y);
            return;
        }
        if (!_dragStarted && (Math.Abs(pixel.X - _downPixel.X) > DragStartPixels || Math.Abs(pixel.Y - _downPixel.Y) > DragStartPixels))
            _dragStarted = true;
        if (!_dragStarted) return;

        switch (_mode)
        {
            case Mode.Move: DoMove(x, y); break;
            case Mode.Resize: DoResize(x, y); break;
            case Mode.Marquee:
                _marquee = new RectD(Math.Min(_downDots.X, x), Math.Min(_downDots.Y, y), Math.Abs(x - _downDots.X), Math.Abs(y - _downDots.Y));
                InvalidateVisual();
                break;
        }
    }

    private void DoMove(double x, double y)
    {
        var session = _vm!.Session;
        double dx = x - _downDots.X, dy = y - _downDots.Y;
        bool noSnap = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var moving = new RectD(_startBounds.X + dx, _startBounds.Y + dy, _startBounds.Width, _startBounds.Height);
        _vGuides.Clear();
        _hGuides.Clear();
        if (!noSnap)
        {
            var others = session.Document.Elements.Where(el => !_origins.ContainsKey(el.Id)).Select(el => session.Bounds.GetBounds(el)).ToList();
            var snap = DragSnapper.Snap(moving, others, EditorCommands.LabelRect(Doc), _vm.Grid, Dpi,
                Snapping.ThresholdDots(SnapPixels, Ppd), _vm.SnapToGuides);
            dx += snap.Dx; dy += snap.Dy;
            _vGuides.AddRange(snap.VerticalGuides);
            _hGuides.AddRange(snap.HorizontalGuides);
        }
        int idx = (int)Math.Round(dx), idy = (int)Math.Round(dy);
        foreach (var el in session.Document.Elements)
            if (_origins.TryGetValue(el.Id, out var o))
            {
                el.X = o.X + idx;
                el.Y = o.Y + idy;
            }
        session.Touch();
    }

    private void DoResize(double x, double y)
    {
        var session = _vm!.Session;
        var original = _resizeOriginal!;
        var pointer = (x, y);
        if (_vm.SnapToGrid && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            double step = _vm.Grid.SpacingDots(Dpi);
            pointer = (Snapping.SnapToGrid(x + HomeX, step) - HomeX, Snapping.SnapToGrid(y + HomeY, step) - HomeY);
        }
        var target = HitTesting.ResizeRect(_startBounds, _handle, pointer.Item1, pointer.Item2, 4);
        // Start every update from the untouched original, so scaling is never compounded by rounding.
        int index = session.Document.Elements.FindIndex(el => el.Id == original.Id);
        if (index < 0) return;
        var fresh = ElementCloner.Clone(original);
        session.Document.Elements[index] = fresh;
        _vm.Commands.ApplyResize(fresh, _startBounds, _handle, target.Width, target.Height);
        session.Touch();
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_vm is null || _mode == Mode.None) return;
        var session = _vm.Session;
        var mode = _mode;
        _mode = Mode.None;
        ReleaseMouseCapture();
        _vGuides.Clear();
        _hGuides.Clear();
        var (x, y) = ToDots(e.GetPosition(this));

        switch (mode)
        {
            case Mode.Move:
                if (_dragStarted && _gesture is not null) session.EndGesture(_gesture, "Undo.Move");
                else if (!_dragStarted && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
                {
                    // A plain click on one of several selected elements narrows the selection to it.
                    var hit = HitTesting.HitElement(session, x, y, Snapping.ThresholdDots(4, Ppd));
                    if (hit is not null) session.Select(hit);
                }
                break;
            case Mode.Resize:
                if (_dragStarted && _gesture is not null) session.EndGesture(_gesture, "Undo.Resize");
                break;
            case Mode.Rotate:
            {
                int turns = _dragStarted ? HitTesting.QuarterTurns(_startBounds, x, y) : 1;
                for (int i = 0; i < turns; i++) _vm.Commands.Rotate90(true);
                break;
            }
            case Mode.Marquee:
                if (_dragStarted)
                {
                    var ids = HitTesting.ElementsIn(session, _marquee).Select(el => el.Id);
                    session.SetSelection(_marqueeAdditive ? session.SelectedIds.Concat(ids) : ids);
                }
                break;
        }
        _gesture = null;
        _resizeOriginal = null;
        _dragStarted = false;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture lost without a mouse-up (Alt+Tab, dialog): put the document back instead of leaving a half-finished drag.
        if (_mode != Mode.None && _gesture is not null && _vm is not null)
        {
            _vm.Session.CancelGesture(_gesture);
            _gesture = null;
        }
        _mode = Mode.None;
        _vGuides.Clear();
        _hGuides.Clear();
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _vm?.SetCursorDots(null, null);
    }

    private void UpdateCursor(double x, double y)
    {
        var cursor = Cursors.Arrow;
        if (_vm is not null)
        {
            var sel = _vm.Session.SelectedElements;
            if (sel.Count == 1)
            {
                var b = _vm.Session.Bounds.GetBounds(sel[0]);
                double handleTol = Snapping.ThresholdDots(HandleSize, Ppd);
                if (HitTesting.HitRotateHandle(b, x, y, Snapping.ThresholdDots(HitTesting.RotateHandleOffsetPixels, Ppd), handleTol)) cursor = Cursors.Hand;
                else if (HitTesting.HitHandle(b, x, y, handleTol) is { } h)
                    cursor = h switch
                    {
                        ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
                        ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
                        ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
                        _ => Cursors.SizeWE,
                    };
                else if (HitTesting.HitElement(_vm.Session, x, y, Snapping.ThresholdDots(4, Ppd)) is not null) cursor = Cursors.SizeAll;
            }
            else if (HitTesting.HitElement(_vm.Session, x, y, Snapping.ThresholdDots(4, Ppd)) is not null) cursor = Cursors.SizeAll;
        }
        Cursor = cursor;
    }

    // ------------------------------------------------------------------------------- keyboard

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null) return;
        bool large = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Left: _vm.NudgeBy(-1, 0, large); e.Handled = true; break;
            case Key.Right: _vm.NudgeBy(1, 0, large); e.Handled = true; break;
            case Key.Up: _vm.NudgeBy(0, -1, large); e.Handled = true; break;
            case Key.Down: _vm.NudgeBy(0, 1, large); e.Handled = true; break;
            case Key.Delete: _vm.DeleteCommand.Execute(null); e.Handled = true; break;
            case Key.Escape:
                if (_mode != Mode.None && _gesture is not null)
                {
                    _vm.Session.CancelGesture(_gesture);
                    _gesture = null;
                    _mode = Mode.None;
                    ReleaseMouseCapture();
                    InvalidateVisual();
                }
                else _vm.SelectNoneCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_vm is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        // Ctrl + wheel zooms around the pointer; the host scroll viewer is adjusted by the DesignArea.
        ZoomRequested?.Invoke(this, new ZoomRequestEventArgs(Math.Pow(1.1, e.Delta / 120.0), e.GetPosition(this)));
        e.Handled = true;
    }

    /// <summary>Raised when the user zooms with Ctrl + wheel.</summary>
    public event EventHandler<ZoomRequestEventArgs>? ZoomRequested;

    /// <inheritdoc />
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }
}

/// <summary>A request to change the zoom by a factor around a point of the canvas.</summary>
public sealed class ZoomRequestEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public ZoomRequestEventArgs(double ratio, Point anchor)
    {
        Ratio = ratio;
        Anchor = anchor;
    }

    /// <summary>Multiplicative zoom change.</summary>
    public double Ratio { get; }

    /// <summary>Pointer position on the canvas.</summary>
    public Point Anchor { get; }
}
