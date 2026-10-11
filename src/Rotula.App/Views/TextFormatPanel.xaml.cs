using System.Windows.Controls;

namespace Rotula.App.Views;

/// <summary>The formatting block of a selected text in the Properties panel (font, size, styles, alignment, warnings). Bound to a <c>TextFormatViewModel</c>.</summary>
public partial class TextFormatPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public TextFormatPanel() => InitializeComponent();
}
