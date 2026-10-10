using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;

namespace Rotula.App.ViewModels;

/// <summary>
/// Base for windows that run slow work (printing, reading files, checking records): one operation at a time, a busy
/// indicator, a Cancel button that stops it, and a result line that says what happened. The work always runs off the UI
/// thread (the callers pass async delegates) and receives a <see cref="CancellationToken"/>.
/// </summary>
public abstract partial class OperationViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    /// <summary>True while an operation runs; buttons that start work are disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>The opposite of <see cref="IsBusy"/>, for binding to IsEnabled.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>What the running operation is doing, for example "Sending to the printer...".</summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>Progress 0-100 for operations that can tell; negative means unknown (indeterminate bar).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressKnown), nameof(IsProgressUnknown))]
    private double _progress = -1;

    /// <summary>True when <see cref="Progress"/> holds a real percentage.</summary>
    public bool IsProgressKnown => Progress >= 0;

    /// <summary>True while an operation runs without a known percentage (shows the moving bar).</summary>
    public bool IsProgressUnknown => !IsProgressKnown;

    /// <summary>The outcome of the last operation, in plain language.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(HasError), nameof(HasSuccess))]
    private string _resultText = string.Empty;

    /// <summary>True when <see cref="ResultText"/> reports a problem (shown in the error colour).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(HasSuccess))]
    private bool _resultIsError;

    /// <summary>True when there is a result line to show.</summary>
    public bool HasResult => ResultText.Length > 0;

    /// <summary>True when the result line reports a problem (drives the red banner).</summary>
    public bool HasError => HasResult && ResultIsError;

    /// <summary>True when the result line reports success or information (drives the green banner).</summary>
    public bool HasSuccess => HasResult && !ResultIsError;

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Asks the window to close; a running operation is cancelled first.</summary>
    protected void RequestClose()
    {
        CancelOperation();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the running operation (if any). Also called from the window's Closing handler.</summary>
    [RelayCommand]
    public void CancelOperation() => _cts?.Cancel();

    /// <summary>Shows a result line.</summary>
    protected void SetResult(string text, bool isError)
    {
        ResultText = text;
        ResultIsError = isError;
    }

    /// <summary>
    /// Runs <paramref name="work"/> as the single active operation. Cancellation is reported as a calm message rather than
    /// an error; an unexpected exception is shown as a generic failure (a defect, but the window stays usable).
    /// </summary>
    /// <returns>True when the work ran to the end.</returns>
    protected async Task<bool> RunAsync(string busyText, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return false;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        BusyText = busyText;
        Progress = -1;
        ResultText = string.Empty;
        try
        {
            await work(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetResult(Loc.Get("Common.OperationCancelled"), false);
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetResult(Loc.Get("Common.OperationFailed", ex.Message), true);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            Progress = -1;
            _cts = null;
        }
    }
}
