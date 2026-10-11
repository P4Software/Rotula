using System.Windows;
using System.Windows.Controls;
using Rotula.App.ViewModels;

namespace Rotula.App.Controls;

/// <summary>
/// Lays out the property rows like a form: number fields (X, Y, width, height...) sit two to a line, everything else
/// (headings, text boxes, drop-downs, check boxes) takes the full width. Rows keep their order, so a pair of numbers that follow
/// each other share a line.
/// </summary>
public sealed class PropertyGridPanel : Panel
{
    private const double Gap = 12;

    private static bool IsHalf(UIElement child) =>
        child is FrameworkElement { DataContext: IntRow { FullWidth: false } or DoubleRow };

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width;
        double half = Math.Max(0, (width - Gap) / 2);
        double y = 0, lineHeight = 0;
        int inLine = 0;
        foreach (UIElement child in InternalChildren)
        {
            bool isHalf = IsHalf(child);
            child.Measure(new Size(isHalf ? half : width, double.PositiveInfinity));
            if (!isHalf)
            {
                y += lineHeight;
                lineHeight = 0;
                inLine = 0;
                y += child.DesiredSize.Height;
                continue;
            }
            lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
            if (++inLine == 2)
            {
                y += lineHeight;
                lineHeight = 0;
                inLine = 0;
            }
        }
        y += lineHeight;
        return new Size(width, y);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double half = Math.Max(0, (finalSize.Width - Gap) / 2);
        double y = 0, lineHeight = 0;
        int inLine = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (!IsHalf(child))
            {
                y += lineHeight;
                lineHeight = 0;
                inLine = 0;
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
                continue;
            }
            // Arrange needs the line height, which depends on the partner; a second pass over the pair keeps it simple.
            child.Arrange(new Rect(inLine == 0 ? 0 : half + Gap, y, half, child.DesiredSize.Height));
            lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
            if (++inLine == 2)
            {
                y += lineHeight;
                lineHeight = 0;
                inLine = 0;
            }
        }
        return finalSize;
    }
}
