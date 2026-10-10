using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Rotula.App.Views;

/// <summary>The property panel (rows come from <see cref="ViewModels.PropertyPanelViewModel"/>).</summary>
public partial class PropertiesPanel : UserControl
{
    /// <summary>Moves the keyboard focus into the first text field of the panel (after the rows were rebuilt), selecting its content.</summary>
    public void FocusFirstField()
    {
        // The rows are rebuilt by the selection change that triggered this call; wait until they exist in the visual tree.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var box = FindFirstTextBox(this);
            if (box is null) return;
            box.Focus();
            box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static TextBox? FindFirstTextBox(System.Windows.DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox { IsEnabled: true } tb) return tb;
            if (FindFirstTextBox(child) is { } found) return found;
        }
        return null;
    }

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
