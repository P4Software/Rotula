using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Prompts;

namespace Rotula.App.ViewModels;

/// <summary>One question of the print-time prompt dialog: a label, an input (text box or choice list), help text and the reason an answer was refused.</summary>
public sealed partial class PromptFieldViewModel : ObservableObject
{
    /// <summary>Creates the field for one question of a <see cref="PromptSet"/>.</summary>
    public PromptFieldViewModel(PromptItem item)
    {
        Item = item;
        _value = item.InitialValue;
    }

    /// <summary>The question as the label defined it.</summary>
    public PromptItem Item { get; }

    /// <summary>The variable name; the key of the answer.</summary>
    public string Name => Item.Name;

    /// <summary>Caption above the input; a required question ends with an asterisk.</summary>
    public string Caption => Item.Required ? Item.Label + " *" : Item.Label;

    /// <summary>Help shown under the input: the label's own text, or the expected shape for dates and numbers.</summary>
    public string Help => Item.HelpText.Length > 0 ? Item.HelpText : Item.InputType switch
    {
        PromptInputType.Date => Loc.Get("Prompt.Hint.Date"),
        PromptInputType.Number => Loc.Get("Prompt.Hint.Number"),
        _ => string.Empty,
    };

    /// <summary>True when there is help text to show.</summary>
    public bool HasHelp => Help.Length > 0;

    /// <summary>True for a question with a fixed list of answers (drawn as a list instead of a text box).</summary>
    public bool IsChoice => Item.InputType == PromptInputType.Choice && Item.Choices.Count > 0;

    /// <summary>True for a free-text question.</summary>
    public bool IsText => !IsChoice;

    /// <summary>The allowed answers of a choice question.</summary>
    public IReadOnlyList<string> Choices => Item.Choices;

    /// <summary>The most characters the answer may have.</summary>
    public int MaxLength => Item.MaxLength;

    /// <summary>What the operator typed or picked.</summary>
    [ObservableProperty]
    private string _value;

    /// <summary>Why the answer was refused, in plain language; empty while it is fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    /// <summary>True when <see cref="Error"/> has text.</summary>
    public bool HasError => Error.Length > 0;

    partial void OnValueChanged(string value) => Error = string.Empty;
}

/// <summary>
/// The questions a label asks just before it prints (variables of kind Prompt). The dialog stays open until every answer passes the
/// label's own rules or the operator cancels, so a refused answer is explained next to its field and nothing is printed with a wrong value.
/// </summary>
public sealed partial class PromptDialogViewModel : ObservableObject
{
    private readonly PromptSet _set;

    /// <summary>Creates the dialog for <paramref name="set"/>.</summary>
    public PromptDialogViewModel(PromptSet set)
    {
        _set = set;
        Fields = set.Items.Select(i => new PromptFieldViewModel(i)).ToList();
    }

    /// <summary>One entry per question, in the order the label declares them.</summary>
    public IReadOnlyList<PromptFieldViewModel> Fields { get; }

    /// <summary>True once the operator pressed the main button and every answer was accepted.</summary>
    public bool Accepted { get; private set; }

    /// <summary>The checked answers by variable name; valid only when <see cref="Accepted"/> is true.</summary>
    public IReadOnlyDictionary<string, string> Answers { get; private set; } = new Dictionary<string, string>();

    /// <summary>Raised when the window should close; the argument is <see cref="Accepted"/>.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Checks the answers; closes the dialog when all are fine, otherwise shows what to fix next to each field.</summary>
    [RelayCommand]
    private void Accept()
    {
        var typed = Fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.OrdinalIgnoreCase);
        var result = _set.Validate(typed);
        foreach (var f in Fields) f.Error = string.Empty;
        foreach (var issue in result.Issues)
        {
            // The first argument of every prompt issue is the variable name.
            var field = Fields.FirstOrDefault(f => issue.Args.Count > 0 && string.Equals(f.Name, issue.Args[0], StringComparison.OrdinalIgnoreCase));
            if (field is null) continue;
            field.Error = field.Error.Length == 0 ? Loc.FormatIssue(issue) : field.Error + " " + Loc.FormatIssue(issue);
        }
        if (!result.IsValid) return;
        Answers = result.Values;
        Accepted = true;
        CloseRequested?.Invoke(this, true);
    }

    /// <summary>Closes the dialog without printing.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
