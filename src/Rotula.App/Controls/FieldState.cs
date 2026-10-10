using System.Windows;

namespace Rotula.App.Controls;

/// <summary>
/// Attached state for form fields. The property panel rows keep their own <c>HasError</c>/<c>Error</c> properties (they validate while
/// the user types and never put invalid values into the document), so WPF's <c>Validation.HasError</c> is never set. This attached
/// property lets a field bind that flag and lets the TextBox template draw the red error border from it.
/// </summary>
public static class FieldState
{
    /// <summary>True when the field currently holds a value that was rejected; drives the red border.</summary>
    public static readonly DependencyProperty HasErrorProperty =
        DependencyProperty.RegisterAttached("HasError", typeof(bool), typeof(FieldState), new FrameworkPropertyMetadata(false));

    /// <summary>Gets <see cref="HasErrorProperty"/>.</summary>
    public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);

    /// <summary>Sets <see cref="HasErrorProperty"/>.</summary>
    public static void SetHasError(DependencyObject d, bool value) => d.SetValue(HasErrorProperty, value);
}
