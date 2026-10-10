using System.Windows.Data;
using System.Windows.Markup;

namespace Rotula.App.Localization;

/// <summary>
/// XAML markup extension for translated text: <c>Header="{loc:T Menu.File}"</c>. It binds to the indexer of
/// <see cref="Loc"/>, so the text changes immediately when the user switches language.
/// Works for any property, including <c>AutomationProperties.Name</c> and tool tips.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    /// <summary>Creates the extension (needed by the XAML parser).</summary>
    public TExtension() { }

    /// <summary>Creates the extension for a key.</summary>
    public TExtension(string key) => Key = key;

    /// <summary>The resource key.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding("[" + Key + "]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
