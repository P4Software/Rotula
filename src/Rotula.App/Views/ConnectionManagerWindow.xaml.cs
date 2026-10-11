using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Rotula.App.ViewModels;

namespace Rotula.App.Views;

/// <summary>
/// The connection manager dialog. A <see cref="PasswordBox"/> cannot be bound (by design, so a password does not sit in a
/// bindable string property longer than needed), so this class copies it into the view-model on every change and empties it
/// whenever the view-model asks.
/// </summary>
public partial class ConnectionManagerWindow : DialogWindow
{
    private readonly ConnectionManagerViewModel _vm;

    /// <summary>Creates the window.</summary>
    public ConnectionManagerWindow(ConnectionManagerViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PasswordCleared += (_, _) => PasswordInput?.Clear();
        viewModel.PasswordFocusRequested += (_, _) => PasswordInput?.Focus();
    }

    private void OnBrowseField(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OptionFieldViewModel field }) field.Browse();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _vm.Password = PasswordInput.Password;

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _vm.CancelOperation();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Do not leave the typed password in memory longer than the dialog lives.
        PasswordInput?.Clear();
        _vm.Password = string.Empty;
    }
}
