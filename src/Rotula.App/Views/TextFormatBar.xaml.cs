using System.Windows.Controls;

namespace Rotula.App.Views;

/// <summary>The compact formatting bar over the selected text on the canvas. Bound to a <c>TextFormatViewModel</c>.</summary>
public partial class TextFormatBar : UserControl
{
    /// <summary>Creates the bar.</summary>
    public TextFormatBar() => InitializeComponent();
}
