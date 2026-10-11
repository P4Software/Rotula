using System.Windows;
using System.Windows.Markup;

namespace Rotula.App.Controls;

/// <summary>
/// The look of one field in the field lists (the Insert field flyout and the autocomplete): its name in the monospace face, a small tag
/// for where it comes from, and the value of the record shown on the canvas. Built from XAML text so both lists, which are created in code,
/// share exactly one definition and use the theme's brushes.
/// </summary>
internal static class FieldTemplates
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private static DataTemplate? _item;
    private static Style? _container, _focusableContainer;

    /// <summary>The template of one field row.</summary>
    public static DataTemplate ItemTemplate() => _item ??= (DataTemplate)XamlReader.Parse("""
        <DataTemplate @NS@>
          <Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
              <TextBlock Text="{Binding Token}" FontFamily="{DynamicResource MonoFont}" FontSize="12.5" FontWeight="SemiBold" VerticalAlignment="Center" />
              <Border CornerRadius="4" Background="{DynamicResource HoverBrush}" Padding="5,1" Margin="8,0,0,0" VerticalAlignment="Center">
                <TextBlock Text="{Binding KindText}" FontSize="10.5" Foreground="{DynamicResource MutedBrush}" />
              </Border>
            </StackPanel>
            <TextBlock Grid.Column="1" Text="{Binding SampleText}" Foreground="{DynamicResource MutedBrush}" FontSize="12" Margin="14,0,0,0"
                       HorizontalAlignment="Right" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
          </Grid>
        </DataTemplate>
        """.Replace("@NS@", Ns));

    /// <summary>
    /// A compact row style: the theme's list rows are tall because they hold two lines. The autocomplete list uses rows that cannot take the
    /// keyboard focus (the user keeps typing in the text box); the flyout uses focusable rows so the keyboard works inside it.
    /// </summary>
    public static Style ItemContainerStyle(bool focusable = false) => focusable
        ? _focusableContainer ??= Build(true)
        : _container ??= Build(false);

    private static Style Build(bool focusable) => (Style)XamlReader.Parse("""
        <Style @NS@ TargetType="ListBoxItem" BasedOn="{StaticResource {x:Type ListBoxItem}}">
          <Setter Property="MinHeight" Value="32" />
          <Setter Property="Margin" Value="4,1" />
          <Setter Property="Padding" Value="10,0" />
          <Setter Property="Focusable" Value="@FOCUS@" />
        </Style>
        """.Replace("@NS@", Ns).Replace("@FOCUS@", focusable ? "True" : "False"));
}
