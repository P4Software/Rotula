using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.Data.Packs;

namespace Rotula.App.ViewModels;

/// <summary>Turns the stable keys of the pack loader into plain-language sentences in the current language.</summary>
public static class PackMessages
{
    /// <summary>
    /// Describes a pack warning from its <c>Data.Pack.*</c> key and arguments. When the key has no text, or the text expects more
    /// arguments than the loader supplied, the English sentence that travels with the warning is used, so the user never sees a key.
    /// </summary>
    public static string Describe(PackWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        if (Loc.Instance.HasKey(warning.Key))
        {
            try
            {
                var args = warning.Args.Count > 0 ? warning.Args.Cast<object>().ToArray() : new object[] { warning.PackId };
                return Loc.Get(warning.Key, args);
            }
            catch (FormatException)
            {
                // The text asks for more arguments than were supplied: fall through to the English sentence.
            }
        }
        return warning.EnglishText;
    }

    /// <summary>The plain-language approval warning for a pack in the user's folder (it is a program that runs with the user's rights).</summary>
    public static string DescribeTrust(PackTrustPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        string name = prompt.Candidate.Manifest?.Name ?? prompt.Candidate.DisplayId;
        if (!Loc.Instance.HasKey(prompt.WarningKey)) return prompt.EnglishWarning;
        try { return Loc.Get(prompt.WarningKey, name, prompt.Candidate.Folder); }
        catch (FormatException) { return prompt.EnglishWarning; }
    }
}

/// <summary>One line of the add-on details list.</summary>
public sealed class PackWarningItem : ObservableObject
{
    /// <summary>Creates an item for a warning; <paramref name="pending"/> is set when the add-on waits for the user's approval.</summary>
    public PackWarningItem(PackWarning warning, PackCandidate? pending)
    {
        Warning = warning;
        Pending = pending;
    }

    /// <summary>The loader's warning.</summary>
    public PackWarning Warning { get; }

    /// <summary>The add-on waiting for approval, or null.</summary>
    public PackCandidate? Pending { get; }

    /// <summary>True when the user can review and approve this add-on.</summary>
    public bool CanReview => Pending is not null;

    /// <summary>The sentence in the current language.</summary>
    public string Text => PackMessages.Describe(Warning);

    /// <summary>Technical detail (exception type and message), or empty.</summary>
    public string Detail => Warning.Detail ?? string.Empty;

    /// <summary>True when there is a technical detail to show.</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>Accessible name of the review button.</summary>
    public string ReviewName => Loc.Get("Pack.Details.ReviewName", Warning.PackId);

    /// <summary>Re-reads the translated text after a language switch.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(ReviewName));
    }
}

/// <summary>What the dialogs of the add-on notice need from the window layer (replaced by a fake in tests).</summary>
public interface IPackDialogService
{
    /// <summary>Shows the list of add-on problems modally.</summary>
    void ShowDetails(PackNoticeViewModel viewModel);

    /// <summary>Shows the approval prompt; true when the user approved, false when they skipped it.</summary>
    bool ShowTrustPrompt(PackTrustPrompt prompt);
}

/// <summary>
/// The small non-modal notice shown after start-up when some data source add-ons could not be loaded or wait for approval,
/// plus the details list behind it. Holds the loading options so an approval can load that add-on straight away.
/// </summary>
public sealed partial class PackNoticeViewModel : ObservableObject
{
    private readonly PackLoadOptions _options;
    private readonly IPackDialogService _dialogs;

    /// <summary>Creates the notice from a finished loading run.</summary>
    public PackNoticeViewModel(PackLoadReport report, PackLoadOptions options, IPackDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(report);
        _options = options;
        _dialogs = dialogs;
        foreach (var w in report.Warnings)
        {
            var pending = report.PendingApproval.FirstOrDefault(c => string.Equals(c.Manifest?.Id, w.PackId, StringComparison.OrdinalIgnoreCase));
            Items.Add(new PackWarningItem(w, pending));
        }
        IsVisible = Items.Count > 0;
        Loc.Instance.SubscribeWeak(this, static self =>
        {
            foreach (var i in self.Items) i.Refresh();
            self.OnPropertyChanged(nameof(NoticeText));
        });
    }

    /// <summary>The problems, one per add-on.</summary>
    public ObservableCollection<PackWarningItem> Items { get; } = new();

    /// <summary>True while the notice is shown.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Message after an approval ("the add-on was approved and is now available"), or empty.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>The one-line notice: something could not be loaded, or something waits for approval.</summary>
    public string NoticeText => Items.Any(i => !i.CanReview) ? Loc.Get("Pack.Notice.Warnings") : Loc.Get("Pack.Notice.Pending");

    /// <summary>Opens the details dialog.</summary>
    [RelayCommand]
    private void ShowDetails() => _dialogs.ShowDetails(this);

    /// <summary>Hides the notice (the problems stay in the details until the next start).</summary>
    [RelayCommand]
    private void Dismiss() => IsVisible = false;

    /// <summary>
    /// Shows the approval prompt for an add-on waiting in the user's folder. Only an explicit approval records the fingerprint and
    /// loads the add-on; skipping changes nothing, so the question comes back at the next start.
    /// </summary>
    [RelayCommand]
    private void Review(PackWarningItem? item)
    {
        if (item?.Pending is not { } candidate) return;
        PackTrustPrompt prompt;
        try { prompt = PackLoader.CreateTrustPrompt(candidate); }
        catch (InvalidOperationException) { return; }
        if (!_dialogs.ShowTrustPrompt(prompt)) return;

        PackLoader.Approve(prompt, _options.TrustStore);
        var report = new PackLoadReport();
        PackLoader.LoadOne(candidate, _options, report);
        Items.Remove(item);
        foreach (var w in report.Warnings) Items.Add(new PackWarningItem(w, null));
        StatusText = report.Loaded.Count > 0 ? Loc.Get("Pack.Details.Loaded", item.Warning.PackId) : string.Empty;
        OnPropertyChanged(nameof(NoticeText));
        if (Items.Count == 0) IsVisible = false;
    }
}

/// <summary>
/// Loads the add-ons once at start-up without blocking the window. <see cref="Ready"/> completes when loading is over, so screens
/// that list data sources (the connection manager) can wait for it instead of showing an add-on connection as missing too early.
/// </summary>
public static class PackStartup
{
    /// <summary>Completes when the add-on loading run is over. A completed task until <see cref="Begin"/> is called.</summary>
    public static Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts loading on a background thread and returns the report. A failure of the loader itself is turned into an empty report
    /// (the application always starts); <paramref name="log"/> receives the exception.
    /// </summary>
    public static Task<PackLoadReport> Begin(PackLoadOptions options, Action<Exception>? log = null, Func<PackLoadOptions, PackLoadReport>? loader = null)
    {
        loader ??= PackLoader.LoadAll;
        var task = Task.Run(() =>
        {
            try { return loader(options); }
            catch (Exception ex)
            {
                // PackLoader never throws for a pack problem; anything that gets here is a bug, and must not stop the program.
                log?.Invoke(ex);
                return new PackLoadReport();
            }
        });
        Ready = task;
        return task;
    }
}
