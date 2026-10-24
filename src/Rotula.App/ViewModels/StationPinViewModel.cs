using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Files.Station;

namespace Rotula.App.ViewModels;

/// <summary>
/// The small tool that turns a new exit PIN into the line for station.json. The PIN itself is never shown or stored: only its salted hash is
/// put on the screen for the person setting up the station to copy.
/// </summary>
public sealed partial class StationPinViewModel : ObservableObject
{
    private readonly IClipboardService? _clipboard;
    private readonly int _iterations;

    /// <summary>Creates the tool.</summary>
    /// <param name="clipboard">Puts the line on the clipboard; null copies nothing (tests).</param>
    /// <param name="iterations">PBKDF2 iterations; tests pass a smaller number.</param>
    public StationPinViewModel(IClipboardService? clipboard, int iterations = StationPin.DefaultIterations)
    {
        _clipboard = clipboard;
        _iterations = iterations;
    }

    /// <summary>The new PIN (copied from the password box; never shown).</summary>
    public string Pin { get; set; } = string.Empty;

    /// <summary>The PIN typed a second time.</summary>
    public string Confirm { get; set; } = string.Empty;

    /// <summary>The line to paste into station.json, once made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultLine = string.Empty;

    /// <summary>True when <see cref="ResultLine"/> is ready.</summary>
    public bool HasResult => ResultLine.Length > 0;

    /// <summary>Why no line was made, or "Copied."; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    /// <summary>True when <see cref="Message"/> has text.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>Makes the hash line from the two PIN boxes.</summary>
    [RelayCommand]
    private void Create()
    {
        ResultLine = string.Empty;
        if (!StationPin.IsValidPin(Pin)) { Message = Loc.Get("Station.NewPin.Invalid", StationPin.MinLength, StationPin.MaxLength); return; }
        if (!string.Equals(Pin.Trim(), Confirm.Trim(), StringComparison.Ordinal)) { Message = Loc.Get("Station.NewPin.Mismatch"); return; }
        Message = string.Empty;
        ResultLine = "\"exitPinHash\": \"" + StationPin.Hash(Pin, _iterations) + "\"";
    }

    /// <summary>Copies the line to the clipboard.</summary>
    [RelayCommand]
    private void Copy()
    {
        if (!HasResult) return;
        _clipboard?.SetText(ResultLine);
        Message = Loc.Get("Station.NewPin.Copied");
    }
}
