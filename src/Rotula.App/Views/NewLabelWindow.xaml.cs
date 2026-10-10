using System.Windows;
using Rotula.App.ViewModels;
using Rotula.Core.Model;

namespace Rotula.App.Views;

/// <summary>The New label dialog.</summary>
public partial class NewLabelWindow : Window
{
    private readonly NewLabelViewModel _vm = new();

    /// <summary>Creates the dialog.</summary>
    public NewLabelWindow()
    {
        DataContext = _vm;
        InitializeComponent();
    }

    /// <summary>The label the user asked for; set when the dialog closed with Create.</summary>
    public LabelDocument? Result { get; private set; }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        // Keep the dialog open and show the problem when the size is not usable.
        Result = _vm.TryCreate();
        if (Result is not null) DialogResult = true;
    }
}
