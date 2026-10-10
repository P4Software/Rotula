using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Rotula.App.Controls;
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
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.ZoomToFitRequested -= OnFitRequested;
            _vm.Session.DocumentChanged -= OnDocChanged;
        }
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.ZoomToFitRequested += OnFitRequested;
            _vm.Session.DocumentChanged += OnDocChanged;
            UpdateRulers();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Zoom) or nameof(MainViewModel.Unit)) UpdateRulers();
    }

    private void OnDocChanged(object? sender, EventArgs e) => UpdateRulers();

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
        HRuler.ScrollOffset = Scroller.HorizontalOffset;
        VRuler.ScrollOffset = Scroller.VerticalOffset;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
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
