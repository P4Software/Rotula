using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.Core.Model;
using Rotula.Rendering;

namespace Rotula.App.ViewModels;

/// <summary>
/// The preview window: draws the label as the printer puts it on the media ("as printed", turned for ^POI and mirrored for ^PMY) or
/// the way it is edited on the canvas ("as designed"), and says in words which one is shown so an upside-down picture is never a surprise.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private readonly PreviewCache _preview;

    /// <summary>Creates the view model for the label held by <paramref name="preview"/>.</summary>
    /// <param name="preview">The preview cache of the open label (its current record data is used; its print orientation (^PO) and mirror (^PM) settings decide the view).</param>
    public PreviewViewModel(PreviewCache preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _preview = preview;
        // The preview is a snapshot of the label when the window opens, so the settings are read once.
        _inverted = preview.Document.Orientation == LabelOrientation.Inverted;
        _mirrored = preview.Document.MirrorImage;
        ChangesWhenPrinted = _inverted || _mirrored;
        // The preview's job is to show what the printer draws, so it opens on "as printed".
        _asPrinted = true;
    }

    private readonly bool _inverted;
    private readonly bool _mirrored;

    /// <summary>Raised when the picture must be drawn again (the view switched between "as printed" and "as designed").</summary>
    public event EventHandler? PictureChanged;

    /// <summary>
    /// True when the printer turns or mirrors this label, so "as printed" and "as designed" differ and the switch is shown.
    /// False for a normal label, where both views are the same picture.
    /// </summary>
    public bool ChangesWhenPrinted { get; }

    /// <summary>True to show the label as it comes out of the printer; false to show it as designed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AsDesigned), nameof(ModeNote))]
    private bool _asPrinted;

    /// <summary>
    /// The opposite of <see cref="AsPrinted"/>, for the second option of the switch. Only a write of true acts: a radio button that
    /// is unchecked by its sibling writes false back, and reacting to that would flip the choice again.
    /// </summary>
    public bool AsDesigned
    {
        get => !AsPrinted;
        set { if (value) AsPrinted = false; }
    }

    /// <summary>
    /// One sentence on which view is shown and why it looks the way it does, or empty for a label the printer does not turn or mirror.
    /// </summary>
    public string ModeNote
    {
        get
        {
            if (!ChangesWhenPrinted) return string.Empty;
            if (!AsPrinted) return Loc.Get("Preview.Note.Designed");
            return Loc.Get(_inverted && _mirrored ? "Preview.Note.PrintedBoth" : _inverted ? "Preview.Note.PrintedInverted" : "Preview.Note.PrintedMirrored");
        }
    }

    /// <summary>
    /// Draws the label at one pixel per dot in the chosen view. The caller owns and must dispose the result. Returns null and sets
    /// <paramref name="errorKey"/> when the label cannot be drawn.
    /// </summary>
    public RenderResult? Render(out string? errorKey) => _preview.RenderPrinted(1.0, out errorKey, asPrinted: AsPrinted);

    partial void OnAsPrintedChanged(bool value) => PictureChanged?.Invoke(this, EventArgs.Empty);
}
