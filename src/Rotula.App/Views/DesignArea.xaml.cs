using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Rotula.App.Controls;
using Rotula.App.Editing;
using Rotula.App.ViewModels;
using Rotula.Rendering.View;

namespace Rotula.App.Views;

/// <summary>Rulers, scroll area and design canvas, with zoom handling (wheel, fit).</summary>
public partial class DesignArea : UserControl
{
    private MainViewModel? _vm;

    /// <summary>Creates the area.</summary>
    public DesignArea()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Canvas.InteractionChanged += (_, _) => ScheduleBarUpdate();
        BarLayer.SizeChanged += (_, _) => ScheduleBarUpdate();
        FormatBar.SizeChanged += (_, _) => ScheduleBarUpdate();
    }

    private bool _barUpdatePending;

    /// <summary>Places the formatting bar after layout has settled (zoom and scrolling change the canvas' size first).</summary>
    private void ScheduleBarUpdate()
    {
        if (_barUpdatePending) return;
        _barUpdatePending = true;
        Dispatcher.BeginInvoke(new Action(() => { _barUpdatePending = false; UpdateFormatBar(); }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Floats the formatting bar above the selected text (below it when there is no room above), keeps it inside the visible area, and hides it
    /// while the user drags, when no single text is selected or when the text is scrolled out of view.
    /// </summary>
    private void UpdateFormatBar()
    {
        if (_vm is null) return;
        var rect = _vm.Format.IsActive && !Canvas.IsInteracting ? Canvas.SelectionPixelRect() : null;
        if (rect is not { } r) { FormatBar.Visibility = Visibility.Collapsed; return; }
        var topLeft = Canvas.TranslatePoint(r.TopLeft, BarLayer);
        double layerW = BarLayer.ActualWidth, layerH = BarLayer.ActualHeight;
        if (layerW <= 0 || layerH <= 0 || topLeft.Y + r.Height < 0 || topLeft.Y > layerH || topLeft.X + r.Width < 0 || topLeft.X > layerW)
        {
            FormatBar.Visibility = Visibility.Collapsed;
            return;
        }
        FormatBar.Visibility = Visibility.Visible;
        FormatBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = FormatBar.DesiredSize.Width, h = FormatBar.DesiredSize.Height;
        // Above the rotate handle, which sits a fixed distance over the top edge; below the text when the top of the area is too close.
        double y = topLeft.Y - HitTesting.RotateHandleOffsetPixels - h - 10;
        if (y < 4) y = topLeft.Y + r.Height + 12;
        y = Math.Clamp(y, 4, Math.Max(4, layerH - h - 4));
        double x = Math.Clamp(topLeft.X, 4, Math.Max(4, layerW - w - 4));
        System.Windows.Controls.Canvas.SetLeft(FormatBar, x);
        System.Windows.Controls.Canvas.SetTop(FormatBar, y);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.ZoomToFitRequested -= OnFitRequested;
            _vm.Session.DocumentChanged -= OnDocChanged;
            _vm.Session.SelectionChanged -= OnBarTrigger;
            _vm.Session.LiveChanged -= OnBarTrigger;
            _vm.Format.PropertyChanged -= OnFormatChanged;
        }
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.ZoomToFitRequested += OnFitRequested;
            _vm.Session.DocumentChanged += OnDocChanged;
            _vm.Session.SelectionChanged += OnBarTrigger;
            _vm.Session.LiveChanged += OnBarTrigger;
            _vm.Format.PropertyChanged += OnFormatChanged;
            UpdateRulers();
            ScheduleBarUpdate();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Zoom) or nameof(MainViewModel.Unit)) UpdateRulers();
        if (e.PropertyName is nameof(MainViewModel.Zoom)) ScheduleBarUpdate();
    }

    private void OnDocChanged(object? sender, EventArgs e) { UpdateRulers(); ScheduleBarUpdate(); }

    private void OnBarTrigger(object? sender, EventArgs e) => ScheduleBarUpdate();

    private void OnFormatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TextFormatViewModel.IsActive) or "") ScheduleBarUpdate();
    }

    private void UpdateRulers()
    {
        if (_vm is null) return;
        int dpi = (int)_vm.Session.Document.Dpi;
        double ppd = _vm.Zoom.PixelsPerDot(dpi);
        foreach (var r in new[] { HRuler, VRuler })
        {
            r.Unit = _vm.Unit;
            r.Dpi = dpi;
            r.PixelsPerDot = ppd;
        }
        HRuler.LabelLengthDots = _vm.Session.Document.WidthDots;
        VRuler.LabelLengthDots = _vm.Session.Document.HeightDots;
        HRuler.ScrollOffset = Scroller.HorizontalOffset;
        VRuler.ScrollOffset = Scroller.VerticalOffset;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ScheduleBarUpdate();
        HRuler.ScrollOffset = Scroller.HorizontalOffset;
        VRuler.ScrollOffset = Scroller.VerticalOffset;
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRulers();

    private void OnFitRequested(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        // Layout may not have run yet when a file is opened at startup, so fit once the viewport has a size.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var doc = _vm.Session.Document;
            if (Scroller.ViewportWidth <= 0 || Scroller.ViewportHeight <= 0) return;
            _vm.Zoom = ZoomModel.FitToViewport(Scroller.ViewportWidth, Scroller.ViewportHeight, doc.WidthDots, doc.HeightDots, (int)doc.Dpi,
                DesignCanvas.Gutter);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnZoomRequested(object? sender, ZoomRequestEventArgs e)
    {
        if (_vm is null) return;
        int dpi = (int)_vm.Session.Document.Dpi;
        double oldPpd = _vm.Zoom.PixelsPerDot(dpi);
        var anchorInViewport = e.Anchor - new Vector(Scroller.HorizontalOffset, Scroller.VerticalOffset);
        _vm.Zoom = _vm.Zoom.Multiply(e.Ratio);
        double newPpd = _vm.Zoom.PixelsPerDot(dpi);
        // Keep the label point under the pointer where it was: re-derive the offsets once layout has the new size.
        double cx = (Scroller.HorizontalOffset + anchorInViewport.X - DesignCanvas.Gutter) / oldPpd;
        double cy = (Scroller.VerticalOffset + anchorInViewport.Y - DesignCanvas.Gutter) / oldPpd;
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(DesignCanvas.Gutter + cx * newPpd - anchorInViewport.X);
        Scroller.ScrollToVerticalOffset(DesignCanvas.Gutter + cy * newPpd - anchorInViewport.Y);
    }

    /// <summary>Moves keyboard focus to the canvas.</summary>
    public void FocusCanvas() => Canvas.Focus();
}
