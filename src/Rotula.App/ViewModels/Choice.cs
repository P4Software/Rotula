using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Localization;

namespace Rotula.App.ViewModels;

/// <summary>
/// One entry of a drop-down list: a value plus the resource key of its name. The name is looked up again by
/// <see cref="Refresh"/> when the language changes, so the list keeps its items (and the selection) instead of being rebuilt.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class Choice<T> : ObservableObject
{
    private readonly string _nameKey;

    /// <summary>Creates a choice.</summary>
    /// <param name="value">The value this entry stands for.</param>
    /// <param name="nameKey">Resource key of the display name.</param>
    public Choice(T value, string nameKey)
    {
        Value = value;
        _nameKey = nameKey;
    }

    /// <summary>The value this entry stands for.</summary>
    public T Value { get; }

    /// <summary>Display name in the current language.</summary>
    public string Name => Loc.Instance[_nameKey];

    /// <summary>Tells the bindings to read <see cref="Name"/> again.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Name));

    /// <inheritdoc />
    public override string ToString() => Name;
}
