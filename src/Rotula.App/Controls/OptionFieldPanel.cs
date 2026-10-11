using System.Windows;
using System.Windows.Controls;
using Rotula.App.ViewModels;

namespace Rotula.App.Controls;

/// <summary>
/// Lays out the fields of a connection form: every field takes the full width, except a short number box (the port), which sits to
/// the right of the field before it, as in the original form. Order is kept.
/// </summary>
public sealed class OptionFieldPanel : Panel
{
    private const double Gap = 12;
    private const double ShortWidth = 140;

    private static bool IsShort(UIElement child) => child is FrameworkElement { DataContext: OptionFieldViewModel { IsShort: true } };

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        double y = 0;
        UIElement? previous = null;
        bool previousHasPartner = false;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            bool pair = IsShort(child) && previous is not null && !IsShort(previous) && !previousHasPartner;
            if (pair)
            {
                previous!.Measure(new Size(Math.Max(0, width - ShortWidth - Gap), double.PositiveInfinity));
                child.Measure(new Size(ShortWidth, double.PositiveInfinity));
                double rowHeight = Math.Max(previous.DesiredSize.Height, child.DesiredSize.Height);
                y += rowHeight - previous.DesiredSize.Height;
                previousHasPartner = true;
            }
            else
            {
                child.Measure(new Size(width, double.PositiveInfinity));
                y += child.DesiredSize.Height;
                previousHasPartner = false;
            }
            previous = child;
        }
        return new Size(width, y);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        UIElement? previous = null;
        bool previousHasPartner = false;
        double previousTop = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) { child.Arrange(new Rect(0, 0, 0, 0)); continue; }
            bool pair = IsShort(child) && previous is not null && !IsShort(previous) && !previousHasPartner;
            if (pair)
            {
                double leftWidth = Math.Max(0, finalSize.Width - ShortWidth - Gap);
                double rowHeight = Math.Max(previous!.DesiredSize.Height, child.DesiredSize.Height);
                previous.Arrange(new Rect(0, previousTop, leftWidth, rowHeight));
                child.Arrange(new Rect(leftWidth + Gap, previousTop, ShortWidth, rowHeight));
                y = previousTop + rowHeight;
                previousHasPartner = true;
            }
            else
            {
                previousTop = y;
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
                previousHasPartner = false;
            }
            previous = child;
        }
        return finalSize;
    }
}
