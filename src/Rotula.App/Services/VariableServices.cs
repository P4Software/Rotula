using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Prompts;
using Rotula.Printing;

namespace Rotula.App.Services;

/// <summary>
/// What printing needs to work out a label's variables besides the label: where serial numbers are remembered, where the last answers are
/// kept, and a way to ask the operator the label's questions. Behind an interface so the view-models run in tests without a file or a window.
/// </summary>
public interface IVariableServices
{
    /// <summary>
    /// Where counters that remember their value keep their position, or null when there is no such place (a counter that remembers then
    /// refuses to print, rather than starting again and repeating numbers).
    /// </summary>
    ICounterStore? CounterStore { get; }

    /// <summary>Remembers the last answer of prompts that ask for it.</summary>
    IPromptMemory PromptMemory { get; }

    /// <summary>
    /// Draws the text of Windows fonts when a label is printed, saved or checked. Null (the default) makes such a text an error, so a label is never
    /// printed with a substitute font. The application registers the SkiaSharp rasterizer here.
    /// </summary>
    Rotula.Core.Text.ITextRasterizer? TextRasterizer => null;

    /// <summary>
    /// Asks the operator the questions in <paramref name="set"/>. Returns the checked answers (empty when there is nothing to ask), or null when
    /// the operator cancelled, in which case nothing may be printed.
    /// </summary>
    IReadOnlyDictionary<string, string>? Ask(PromptSet set);
}

/// <summary>Default <see cref="IVariableServices"/>: a counter store given by the caller and the prompt dialog of <see cref="IToolDialogService"/>.</summary>
public sealed class VariableServices : IVariableServices
{
    private readonly IToolDialogService? _dialogs;

    /// <summary>Creates the service.</summary>
    /// <param name="counterStore">Where serial numbers are remembered; null for none.</param>
    /// <param name="dialogs">Shows the prompt dialog; null makes every question count as cancelled (tests, headless use).</param>
    /// <param name="promptMemory">Where last answers are kept; null keeps them for this session only.</param>
    /// <param name="textRasterizer">Draws the text of Windows fonts; null leaves such texts unprintable.</param>
    public VariableServices(ICounterStore? counterStore, IToolDialogService? dialogs, IPromptMemory? promptMemory = null,
        Rotula.Core.Text.ITextRasterizer? textRasterizer = null)
    {
        TextRasterizer = textRasterizer;
        CounterStore = counterStore;
        _dialogs = dialogs;
        PromptMemory = promptMemory ?? new MemoryPromptMemory();
    }

    /// <inheritdoc />
    public ICounterStore? CounterStore { get; }

    /// <inheritdoc />
    public IPromptMemory PromptMemory { get; }

    /// <inheritdoc />
    public Rotula.Core.Text.ITextRasterizer? TextRasterizer { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? Ask(PromptSet set)
    {
        if (set.IsEmpty) return new Dictionary<string, string>();
        if (_dialogs is null) return null;
        var vm = new ViewModels.PromptDialogViewModel(set);
        if (!_dialogs.ShowPromptDialog(vm) || !vm.Accepted) return null;
        set.Remember(vm.Answers, PromptMemory);
        return vm.Answers;
    }

    /// <summary>The options a print command passes to the print service: the counter store and the answers. Dates use the computer clock.</summary>
    public static PrintVariableOptions Options(IVariableServices services, IReadOnlyDictionary<string, string>? answers) =>
        new() { CounterStore = services.CounterStore, PromptAnswers = answers, TextRasterizer = services.TextRasterizer };
}
