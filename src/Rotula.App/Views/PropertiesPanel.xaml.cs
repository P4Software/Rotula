using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Rotula.App.Views;

/// <summary>The property panel (rows come from <see cref="ViewModels.PropertyPanelViewModel"/>).</summary>
public partial class PropertiesPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public PropertiesPanel()
    {
        InitializeComponent();
        // Number boxes commit on losing focus; Enter should commit too, like in any form.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.OriginalSource is TextBox { AcceptsReturn: false } tb)
                BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateSource();
        };
    }
}
