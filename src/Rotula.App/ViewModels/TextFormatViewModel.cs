using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Zpl;

namespace Rotula.App.ViewModels;

/// <summary>One entry of the font list: a printer font (a ZPL id) or an installed Windows font family.</summary>
public sealed class FontItem
{
    private FontFamily? _preview;

    /// <summary>Creates an entry.</summary>
    public FontItem(FontSource source, string display, string group, char printerId = '0', string family = "", string? storedPath = null, bool isExtra = false)
    {
        Source = source;
        Display = display;
        Group = group;
        PrinterId = printerId;
        Family = family;
        StoredPath = storedPath;
        IsExtra = isExtra;
    }

    /// <summary>Where the font lives.</summary>
    public FontSource Source { get; }

    /// <summary>True for a Windows font.</summary>
    public bool IsWindows => Source == FontSource.Windows;

    /// <summary>The name shown in the list and in the box ("Printer font 0 (scalable)" or "Arial").</summary>
    public string Display { get; }

    /// <summary>Translated name of the group heading the entry is listed under.</summary>
    public string Group { get; }

    /// <summary>The ZPL font id of a printer font.</summary>
    public char PrinterId { get; }

    /// <summary>The family name of a Windows font (empty for a printer font).</summary>
    public string Family { get; }

    /// <summary>The file name of a font stored on the printer (<c>^A@</c>), or null.</summary>
    public string? StoredPath { get; }

    /// <summary>
    /// True for an entry that is only in the list because the label uses it (a Windows font this computer does not have, a font stored
    /// on the printer, an unusual printer font id). It is removed again when the label stops using it.
    /// </summary>
    public bool IsExtra { get; }

    /// <summary>
    /// The font the list draws the entry's name with, so every family is shown in its own typeface. Created on first use: hundreds of
    /// families are listed, but only the few on screen are ever drawn (the list is virtualized).
    /// </summary>
    public FontFamily PreviewFamily => _preview ??= new FontFamily(IsWindows && Family.Length > 0 ? Family : "Segoe UI");

    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>What was found when a text was laid out in its box.</summary>
/// <param name="Overflow">What does not fit.</param>
/// <param name="Estimated">True for a printer font, whose width is only an estimate.</param>
/// <param name="Substituted">True when the computer has no font with the requested family.</param>
/// <param name="ActualFamily">The family really used.</param>
public sealed record TextFit(TextOverflow Overflow, bool Estimated, bool Substituted, string ActualFamily)
{
    /// <summary>Nothing to report.</summary>
    public static readonly TextFit Fine = new(TextOverflow.None, false, false, string.Empty);
}

/// <summary>
/// Everything the text formatting controls show and change for the selected text element: font (printer or Windows), size in points, bold,
/// italic, underline, alignment, word wrap and the overflow warning. One instance serves both the property panel and the toolbar over the
/// canvas, so they always agree. Every change is one undo step (typing a size merges into one).
/// </summary>
/// <remarks>
/// <para>The view-model never keeps the element: it looks it up by id each time, because undo and redo replace the objects in the document.</para>
/// <para>The model rules live in Core (<see cref="FontChoice"/>); this class only maps controls onto them and keeps the size when the
/// user switches between a printer font (dots) and a Windows font (points).</para>
/// </remarks>
public sealed partial class TextFormatViewModel : ObservableObject
{
    /// <summary>The sizes offered in the size list (points), the ones a word processor offers.</summary>
    public static readonly IReadOnlyList<string> SizePresets = new[] { "6", "7", "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "60", "72" };

    // The built-in ZPL fonts with their nominal height in dots (see TextFitEstimator): the bitmap fonts are fixed in shape, 0 is scalable.
    private static readonly (char Id, int Height)[] BitmapFonts = { ('A', 9), ('B', 11), ('C', 18), ('D', 18), ('E', 28), ('F', 26), ('G', 60), ('H', 21) };

    private readonly EditorSession _session;
    private readonly Func<ITextRasterizer?> _rasterizer;
    private readonly List<FontItem> _printerItems = new();
    private List<FontItem> _windowsItems = new();
    private bool _fontsBuilt;
    private bool _refreshing;
    private string? _id;
    private string _search = string.Empty;
    private FontItem? _extra;

    /// <summary>Creates the view-model for a session.</summary>
    /// <param name="session">The editing session (selection, document, undo).</param>
    /// <param name="rasterizer">Gives the rasterizer that lists and measures Windows fonts; it may return null (then no Windows fonts are offered).</param>
    public TextFormatViewModel(EditorSession session, Func<ITextRasterizer?> rasterizer)
    {
        _session = session;
        _rasterizer = rasterizer;
        Fonts = new ObservableCollection<FontItem>();
        // Subscribed first (the panel creates this object before it listens itself), so the panel's rows always see the refreshed state.
        session.SelectionChanged += (_, _) => Refresh();
        session.DocumentChanged += (_, _) => Refresh();
        // The font list's collection view belongs to the thread that made it; a language switch from another thread is handed over to it.
        var owner = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Loc.Instance.SubscribeWeak(this, self =>
        {
            void Rebuild() { self._fontsBuilt = false; self._fitModes = null; self.Refresh(); }
            if (owner.CheckAccess()) Rebuild(); else owner.BeginInvoke(Rebuild);
        });
        Refresh();
    }

    /// <summary>Shows a short message in the status bar (set by the main view-model).</summary>
    public Action<string>? Status { get; set; }

    /// <summary>
    /// The values of the data record shown on the canvas, or null for the sample values; the overflow warning measures the text a record
    /// really gives. Set by the main view-model.
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>?>? DataProvider { get; set; }

    /// <summary>Called when the user dismisses the "printed as a picture" notice for good (the main view-model saves it in settings.json).</summary>
    public Action? RasterNoticeDismissedChanged { get; set; }

    /// <summary>True once the user has dismissed the "printed as a picture" notice.</summary>
    public bool RasterNoticeDismissed { get; set; }

    // ------------------------------------------------------------------------------- selection

    private bool _isActive;

    /// <summary>True when exactly one text element is selected (the controls are shown).</summary>
    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }

    private TextElement? Current => _id is null ? null : _session.Document.Elements.FirstOrDefault(e => e.Id == _id) as TextElement;

    private char DefaultFontId => _session.Document.DefaultFont?.FontId ?? '0';

    private int Dpi => (int)_session.Document.Dpi;

    // ------------------------------------------------------------------------------- font list

    /// <summary>Every font the list offers: the printer fonts, then the installed Windows fonts (plus the label's own font when it is not among them).</summary>
    public ObservableCollection<FontItem> Fonts { get; }

    /// <summary>The list as the drop-down shows it: grouped ("Printer fonts", "Windows fonts") and filtered by what the user types.</summary>
    public ICollectionView FontsView => _view ??= CreateView();

    private ICollectionView? _view;

    // Made on first use, by the control that shows the list: a collection view belongs to the thread that creates it, and a view-model that
    // is never shown (tests, headless use) must be free to change the list from any thread.
    private ICollectionView CreateView()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Fonts);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FontItem.Group)));
        // The current font always stays in the list: if a filter removed it, the editable box would clear the text the user is typing.
        view.Filter = o => _search.Length == 0 || ReferenceEquals(o, _selectedFont) || ((FontItem)o).Display.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
        return view;
    }

    private FontItem? _selectedFont;

    /// <summary>
    /// The font of the selected text. Setting it (the user picked another font) changes the element; a null is ignored, because a
    /// filtered list drops the selection while the user types a search.
    /// </summary>
    public FontItem? SelectedFont
    {
        get => _selectedFont;
        set
        {
            if (value is null) return;
            if (ReferenceEquals(value, _selectedFont)) return;
            _selectedFont = value;
            OnPropertyChanged();
            if (!_refreshing) ApplyFont(value);
        }
    }

    /// <summary>What the user typed into the font box to search the list ("ari" lists Arial and Arial Narrow). Empty shows the whole list.</summary>
    public string FontSearch
    {
        get => _search;
        set
        {
            value ??= string.Empty;
            if (value == _search) return;
            _search = value;
            _view?.Refresh();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Applies what was typed in the font box: the font whose name matches exactly (ignoring capitals), or nothing when there is no such font.
    /// Returns true when the text named a font. Called on Enter and when the box loses the focus, so a half-typed name never changes the label.
    /// </summary>
    public bool CommitFontText(string? text)
    {
        FontSearch = string.Empty;
        string t = (text ?? string.Empty).Trim();
        var item = Fonts.FirstOrDefault(f => string.Equals(f.Display, t, StringComparison.CurrentCultureIgnoreCase)
            || (f.IsWindows && string.Equals(f.Family, t, StringComparison.CurrentCultureIgnoreCase)));
        if (item is null)
        {
            // Put the real font name back in the box.
            OnPropertyChanged(nameof(SelectedFont));
            return false;
        }
        if (ReferenceEquals(item, _selectedFont)) OnPropertyChanged(nameof(SelectedFont));
        else SelectedFont = item;
        return true;
    }

    private void EnsureFonts()
    {
        if (_fontsBuilt) return;
        _fontsBuilt = true;
        string printerGroup = Loc.Get("Panel.Format.Group.Printer"), windowsGroup = Loc.Get("Panel.Format.Group.Windows");
        _printerItems.Clear();
        _printerItems.Add(new FontItem(FontSource.Printer, Loc.Get("Panel.Format.Font.PrinterScalable", "0"), printerGroup, '0'));
        foreach (var (id, _) in BitmapFonts)
            _printerItems.Add(new FontItem(FontSource.Printer, Loc.Get("Panel.Format.Font.PrinterBitmap", id), printerGroup, id));
        IReadOnlyList<string> families;
        try { families = _rasterizer()?.GetFontFamilies() ?? Array.Empty<string>(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { families = Array.Empty<string>(); }
        _windowsItems = families.Select(f => new FontItem(FontSource.Windows, f, windowsGroup, family: f)).ToList();
        _extra = null;
        Fonts.Clear();
        foreach (var i in _printerItems.Concat(_windowsItems)) Fonts.Add(i);
    }

    /// <summary>Finds the list entry for the element's font, adding one when the label uses a font the list does not have.</summary>
    private FontItem ItemFor(TextElement t, FontChoice font)
    {
        FontItem? found = null;
        if (t.StoredFontPath is null)
            found = font.IsWindows
                ? _windowsItems.FirstOrDefault(i => string.Equals(i.Family, font.FamilyName, StringComparison.OrdinalIgnoreCase))
                : _printerItems.FirstOrDefault(i => char.ToUpperInvariant(i.PrinterId) == char.ToUpperInvariant(font.PrinterFontId));
        if (found is not null)
        {
            DropExtra();
            return found;
        }
        // The label's own font, shown even though the list does not have it, so the box never lies about what the label uses.
        FontItem extra;
        if (t.StoredFontPath is { } stored)
            extra = new FontItem(FontSource.Printer, Loc.Get("Panel.Format.Font.Stored", stored), Loc.Get("Panel.Format.Group.Printer"), font.PrinterFontId, storedPath: stored, isExtra: true);
        else if (font.IsWindows)
            extra = new FontItem(FontSource.Windows, Loc.Get("Panel.Format.Font.Missing", font.FamilyName), Loc.Get("Panel.Format.Group.Windows"), family: font.FamilyName, isExtra: true);
        else
            extra = new FontItem(FontSource.Printer, Loc.Get("Panel.Format.Font.PrinterOther", font.PrinterFontId), Loc.Get("Panel.Format.Group.Printer"), font.PrinterFontId, isExtra: true);
        if (_extra is not null && _extra.Display == extra.Display && _extra.Source == extra.Source && Fonts.Contains(_extra)) return _extra;
        DropExtra();
        _extra = extra;
        // Printer entries come first so the extra one is listed in its own group.
        Fonts.Insert(extra.IsWindows ? _printerItems.Count : 0, extra);
        return extra;
    }

    private void DropExtra()
    {
        if (_extra is null) return;
        Fonts.Remove(_extra);
        _extra = null;
    }

    // ------------------------------------------------------------------------------- state read from the element

    private FontChoice _font = FontChoice.ForPrinter('0');

    /// <summary>True when the selected text uses a Windows font (printed as a picture).</summary>
    public bool IsWindowsFont => _font.IsWindows;

    /// <summary>True when bold, italic and underline can be used (a Windows font).</summary>
    public bool StylesEnabled => _font.SupportsStyles;

    /// <summary>Bold. Setting it on a printer font changes nothing and says why in the status bar.</summary>
    public bool IsBold { get => _font.IsWindows && _font.Bold; set => SetStyle(value, (f, v) => f.Bold = v, nameof(IsBold)); }

    /// <summary>Italic. Setting it on a printer font changes nothing and says why in the status bar.</summary>
    public bool IsItalic { get => _font.IsWindows && _font.Italic; set => SetStyle(value, (f, v) => f.Italic = v, nameof(IsItalic)); }

    /// <summary>Underline. Setting it on a printer font changes nothing and says why in the status bar.</summary>
    public bool IsUnderline { get => _font.IsWindows && _font.Underline; set => SetStyle(value, (f, v) => f.Underline = v, nameof(IsUnderline)); }

    /// <summary>Tool tip of the Bold button: the shortcut, or why the button is off.</summary>
    public string BoldTip => StylesEnabled ? Loc.Get("Panel.Format.Tip.Bold") : Loc.Get("Panel.Format.Tip.NeedsWindowsFont");

    /// <summary>Tool tip of the Italic button: the shortcut, or why the button is off.</summary>
    public string ItalicTip => StylesEnabled ? Loc.Get("Panel.Format.Tip.Italic") : Loc.Get("Panel.Format.Tip.NeedsWindowsFont");

    /// <summary>Tool tip of the Underline button: the shortcut, or why the button is off.</summary>
    public string UnderlineTip => StylesEnabled ? Loc.Get("Panel.Format.Tip.Underline") : Loc.Get("Panel.Format.Tip.NeedsWindowsFont");

    private void SetStyle(bool value, Action<FontChoice, bool> set, string property)
    {
        if (_refreshing) return;
        if (!_font.SupportsStyles)
        {
            // A printer cannot do this; say so instead of silently ignoring the click or the shortcut.
            Status?.Invoke(Loc.Get("Status.Format.NeedsWindowsFont"));
            OnPropertyChanged(property);
            return;
        }
        Edit("Undo.Format.Style", (t, _) =>
        {
            // A fresh copy, never the shared object: the undo snapshots hold their own and must stay untouched.
            var f = FontChoice.FromElement(t, DefaultFontId).Clone();
            set(f, value);
            f.ApplyTo(t);
        });
    }

    /// <summary>Toggles bold on the selected text (Ctrl+B).</summary>
    public void ToggleBold() { if (IsActive) IsBold = !IsBold; }

    /// <summary>Toggles italic on the selected text (Ctrl+I).</summary>
    public void ToggleItalic() { if (IsActive) IsItalic = !IsItalic; }

    /// <summary>Toggles underline on the selected text (Ctrl+U).</summary>
    public void ToggleUnderline() { if (IsActive) IsUnderline = !IsUnderline; }

    // ------------------------------------------------------------------------------- font change

    private void ApplyFont(FontItem item)
    {
        Edit("Undo.Format.Font", (t, d) =>
        {
            var current = FontChoice.FromElement(t, DefaultFontId);
            int dpi = (int)d.Dpi;
            if (item.IsWindows)
            {
                // Keep the size the text had: the printer font's height in dots becomes the same physical size in points.
                double points = current.IsWindows ? current.PointSize : Math.Round(FontChoice.DotsToPoints(PrinterHeight(t, d), dpi), 1);
                points = Math.Clamp(points, 1, FontChoice.MaxPointSize);
                FontChoice.ForWindows(item.Family, points, current.Bold, current.Italic, current.Underline).ApplyTo(t);
                t.StoredFontPath = null;
            }
            else
            {
                if (current.IsWindows)
                {
                    // The other way round: keep the physical size. Height and width are set together because a missing width would fall back
                    // to the label default and squash the letters.
                    int dots = Math.Clamp((int)Math.Round(FontChoice.PointsToDots(current.PointSize, dpi)), 10, 32000);
                    t.FontHeight = dots;
                    t.FontWidth = dots;
                }
                FontChoice.ForPrinter(item.PrinterId).ApplyTo(t);
                t.StoredFontPath = item.StoredPath;
            }
        });
    }

    private static int PrinterHeight(TextElement t, LabelDocument d) => t.FontHeight ?? d.DefaultFont?.Height ?? 30;

    // ------------------------------------------------------------------------------- size

    private string _sizeText = string.Empty;
    private string _sizeError = string.Empty;

    /// <summary>The size in points as typed in the size box. Typing changes the label as soon as the number is valid; the typing is one undo step.</summary>
    public string SizeText
    {
        get => _sizeText;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _sizeText, value) || _refreshing) return;
            ApplySizeText(value);
        }
    }

    /// <summary>Plain-language problem with the typed size; empty when fine.</summary>
    public string SizeError { get => _sizeError; private set { if (SetProperty(ref _sizeError, value)) OnPropertyChanged(nameof(HasSizeError)); } }

    /// <summary>True when <see cref="SizeError"/> is set.</summary>
    public bool HasSizeError => SizeError.Length > 0;

    private double CurrentPoints(TextElement t, LabelDocument d) =>
        t.Font is { IsWindows: true } w ? w.PointSize : FontChoice.DotsToPoints(PrinterHeight(t, d), (int)d.Dpi);

    private static string FormatPoints(double pt) => pt.ToString("0.##", CultureInfo.CurrentCulture);

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private (double Min, double Max) PointRange()
    {
        if (_font.IsWindows) return (1, FontChoice.MaxPointSize);
        // A printer font's height is limited in dots (10 to 32000), so its limits in points depend on the resolution.
        return (Math.Ceiling(FontChoice.DotsToPoints(10, Dpi) * 10) / 10, Math.Floor(FontChoice.DotsToPoints(32000, Dpi) * 10) / 10);
    }

    private void ApplySizeText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) { SizeError = Loc.Get("Validation.Required"); return; }
        if (!TryParseNumber(text, out double pt) || double.IsNaN(pt) || double.IsInfinity(pt)) { SizeError = Loc.Get("Validation.NotNumber"); return; }
        var (min, max) = PointRange();
        if (pt < min || pt > max)
        {
            SizeError = Loc.Get("Validation.Range", min.ToString("0.##", CultureInfo.CurrentCulture), max.ToString("0.##", CultureInfo.CurrentCulture));
            return;
        }
        SizeError = string.Empty;
        SetPoints(Math.Round(pt, 2));
    }

    /// <summary>Sets the size of the selected text in points (a printer font gets the same physical height in dots).</summary>
    public void SetPoints(double points)
    {
        string? id = _id;
        if (id is null || Current is not { } now || Math.Abs(points - CurrentPoints(now, _session.Document)) < 0.005) return;
        // Typing "1", "12", "120" is one undo step: the steps share a key, and a new key per element keeps different texts apart.
        Edit("Undo.Format.Size", (t, d) => ApplyPoints(t, d, points), mergeKey: $"format:{id}:size");
    }

    private static void ApplyPoints(TextElement t, LabelDocument d, double points)
    {
        if (t.Font is { IsWindows: true } w)
        {
            var f = w.Clone();
            f.PointSize = Math.Clamp(points, 1, FontChoice.MaxPointSize);
            f.ApplyTo(t);
            return;
        }
        int old = PrinterHeight(t, d);
        int dots = Math.Clamp((int)Math.Round(FontChoice.PointsToDots(points, (int)d.Dpi)), 10, 32000);
        // An explicit width follows the height, as the resize handles do, so the letters keep their shape.
        if (t.FontWidth is > 0 && old > 0) t.FontWidth = Math.Clamp((int)Math.Round(t.FontWidth.Value * (double)dots / old), 1, 32000);
        t.FontHeight = dots;
    }

    // ------------------------------------------------------------------------------- alignment and wrap

    /// <summary>True when the text wraps in a box (a <c>^FB</c> block); alignment only means something then.</summary>
    public bool IsWrap => Current?.Block is not null;

    /// <summary>Turns word wrap on or off. On sizes the box from the room left on the label (<see cref="PropertyPanelViewModel.DefaultBlockFor"/>).</summary>
    public void SetWrap(bool on)
    {
        if (Current is not { } now || (now.Block is not null) == on) return;
        Edit("Undo.Format.Wrap", (t, d) => t.Block = on ? PropertyPanelViewModel.DefaultBlockFor(d, t) : null);
    }

    private string AlignTip(string name) => IsWrap ? Loc.Get("Panel.Format.Align." + name) : Loc.Get("Panel.Format.Tip.NeedsWrap");

    /// <summary>Tool tip of the Left alignment button: its name, or why it is off.</summary>
    public string LeftTip => AlignTip("Left");

    /// <summary>Tool tip of the Centre alignment button: its name, or why it is off.</summary>
    public string CenterTip => AlignTip("Center");

    /// <summary>Tool tip of the Right alignment button: its name, or why it is off.</summary>
    public string RightTip => AlignTip("Right");

    /// <summary>Tool tip of the Justify button: its name, or why it is off.</summary>
    public string JustifiedTip => AlignTip("Justified");

    /// <summary>Left alignment is on. Setting it to true aligns the text; false is ignored (one alignment is always on).</summary>
    public bool AlignLeft { get => Alignment == TextAlignment.Left; set => SetAlignmentFlag(value, TextAlignment.Left, nameof(AlignLeft)); }

    /// <summary>Centred alignment is on.</summary>
    public bool AlignCenter { get => Alignment == TextAlignment.Center; set => SetAlignmentFlag(value, TextAlignment.Center, nameof(AlignCenter)); }

    /// <summary>Right alignment is on.</summary>
    public bool AlignRight { get => Alignment == TextAlignment.Right; set => SetAlignmentFlag(value, TextAlignment.Right, nameof(AlignRight)); }

    /// <summary>Justified alignment is on.</summary>
    public bool AlignJustified { get => Alignment == TextAlignment.Justified; set => SetAlignmentFlag(value, TextAlignment.Justified, nameof(AlignJustified)); }

    /// <summary>The alignment of the selected text inside its box (left when there is no box).</summary>
    public TextAlignment Alignment => Current?.Block?.Alignment ?? TextAlignment.Left;

    private void SetAlignmentFlag(bool on, TextAlignment alignment, string property)
    {
        if (_refreshing) return;
        if (!on) { OnPropertyChanged(property); return; } // clicking the button that is already on keeps it on
        SetAlignment(alignment);
        OnPropertyChanged(property);
    }

    /// <summary>Aligns the selected text inside its box. Does nothing without a box (word wrap off).</summary>
    public void SetAlignment(TextAlignment alignment)
    {
        if (Current is not { Block: { } block } || block.Alignment == alignment) return;
        Edit("Undo.Format.Align", (t, _) => { if (t.Block is not null) t.Block.Alignment = alignment; });
    }

    // ------------------------------------------------------------------------------- warnings

    private TextFit _fit = TextFit.Fine;

    /// <summary>True when the text does not fit its box.</summary>
    public bool HasOverflow => _fit.Overflow != TextOverflow.None;

    /// <summary>The plain-language overflow message; "may not fit" for a printer font, which is only an estimate.</summary>
    public string OverflowText => !HasOverflow ? string.Empty : Loc.Get(_fit.Estimated ? "Issue.Text.Overflow.Estimated" : "Issue.Text.Overflow");

    /// <summary>True when the box can be made bigger from the warning (the text wraps in a block).</summary>
    public bool CanEnlargeBox => IsWrap;

    /// <summary>The warning that this computer has no such font, or empty.</summary>
    public string MissingFontText => _fit.Substituted && Current is { Font: { IsWindows: true } w }
        ? Loc.Get("Panel.Format.Font.Missing.Note", w.FamilyName, _fit.ActualFamily) : string.Empty;

    /// <summary>True when <see cref="MissingFontText"/> is set.</summary>
    public bool HasMissingFont => MissingFontText.Length > 0;

    /// <summary>True when the "printed as a picture" notice should be shown (a Windows font, and the user has not dismissed it).</summary>
    public bool ShowRasterNotice => IsActive && IsWindowsFont && !RasterNoticeDismissed;

    /// <summary>Hides the "printed as a picture" notice for good.</summary>
    [RelayCommand]
    private void DismissRasterNotice()
    {
        RasterNoticeDismissed = true;
        OnPropertyChanged(nameof(ShowRasterNotice));
        RasterNoticeDismissedChanged?.Invoke();
    }

    /// <summary>The text as the canvas shows it: the shown record's values, or the label's sample values.</summary>
    private string ResolveText(LabelDocument doc, TextElement t)
    {
        var data = DataProvider?.Invoke();
        try
        {
            var record = VariableEngine.HasDefinitions(doc) ? VariableEngine.BuildPreviewData(doc, new VariableContext { Record = data }) : data;
            return VariableResolver.Resolve(t.GetTemplate(), record, doc.Variables, t.Id, new List<ValidationIssue>());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return t.GetTemplate();
        }
    }

    /// <summary>
    /// Lays the text out in its box and reports what does not fit. A Windows font is measured exactly with the rasterizer (the same layout
    /// the preview and the printed picture use); a printer font can only be estimated.
    /// </summary>
    public TextFit Evaluate(LabelDocument doc, TextElement t)
    {
        string text = ResolveText(doc, t);
        bool boxed = t.Block is not null || t.TextBox is not null;
        var font = FontChoice.FromElement(t, doc.DefaultFont?.FontId ?? '0');
        // The text fit decides the size and the lines the label will really print with; the warning is about that result, not about the
        // designed values, so "shrink to fit" does not warn while the text fits at the size it picks.
        var rasterizer = _rasterizer();
        var fitted = TextFitResolver.Resolve(doc, t, text, rasterizer);
        bool ignore = t.Block?.Fit is { IgnoreOverflowWarning: true };
        if (!font.IsWindows)
        {
            if (!boxed || text.Length == 0) return TextFit.Fine;
            var estimate = fitted?.Overflow ?? TextFitEstimator.Estimate(t, text, doc.DefaultFont);
            return new TextFit(ignore ? TextOverflow.None : estimate, true, false, string.Empty);
        }

        if (rasterizer is null || font.Validate() is not null) return TextFit.Fine;
        if (fitted?.PointSize is { } fittedSize)
        {
            font = font.Clone();
            font.PointSize = fittedSize;
        }
        // Without text there is nothing to lay out, but a missing font should still be reported: measure a sample word.
        string measured = text.Length > 0 ? text : "Ag";
        if (!boxed || text.Length == 0)
        {
            var plain = rasterizer.Measure(new TextRasterRequest(measured.Replace('\n', ' ').Replace('\r', ' '), font, (int)doc.Dpi));
            return new TextFit(TextOverflow.None, false, plain.FontSubstituted, plain.ActualFamily);
        }
        var m = rasterizer.Measure(new TextRasterRequest(text, font, (int)doc.Dpi, t.Block?.WidthDots ?? t.TextBox?.WidthDots ?? 0, t.TextBox?.HeightDots ?? 0,
            fitted?.MaxLines ?? t.Block?.MaxLines ?? 0, t.Block?.LineSpacing ?? 0, t.Block?.Alignment ?? Rotula.Core.Model.TextAlignment.Left, t.Block?.HangingIndent ?? 0));
        return new TextFit(ignore ? TextOverflow.None : m.Overflow, false, m.FontSubstituted, m.ActualFamily);
    }

    /// <summary>Makes the box big enough for the text: wider when a word sticks out, more lines otherwise. One undo step.</summary>
    [RelayCommand]
    private void EnlargeBox() => Fit(shrink: false);

    /// <summary>Makes the text smaller until it fits its box. One undo step.</summary>
    [RelayCommand]
    private void ShrinkText() => Fit(shrink: true);

    private void Fit(bool shrink)
    {
        if (_id is null) return;
        bool fits = false;
        Edit("Undo.Format.Fit", (t, d) => fits = FitInto(d, t, shrink));
        Status?.Invoke(Loc.Get(fits ? "Status.Format.Fixed" : "Status.Format.NotFixed"));
    }

    /// <summary>The loops are bounded: every pass changes a number towards its limit, so the worst case is a few hundred measurements.</summary>
    private bool FitInto(LabelDocument d, TextElement t, bool shrink)
    {
        for (int pass = 0; pass < 400; pass++)
        {
            var fit = Evaluate(d, t);
            if (fit.Overflow == TextOverflow.None) return true;
            if (shrink)
            {
                double points = CurrentPoints(t, d);
                double next = Math.Floor(points * 0.9 * 2) / 2; // steps of half a point, and always at least that
                if (next >= points) next = points - 0.5;
                if (next < (t.Font is { IsWindows: true } ? 4 : FontChoice.DotsToPoints(10, (int)d.Dpi))) return false;
                ApplyPoints(t, d, next);
            }
            else if (t.Block is { } block)
            {
                if ((fit.Overflow & TextOverflow.Width) != 0)
                {
                    if (block.WidthDots >= 9999) return false;
                    block.WidthDots = Math.Min(9999, block.WidthDots + Math.Max(1, block.WidthDots / 10));
                }
                else
                {
                    if (block.MaxLines >= 9999) return false;
                    block.MaxLines++;
                }
            }
            else return false;
        }
        return false;
    }

    // ------------------------------------------------------------------------------- text fit

    private string[]? _fitModes;
    private string _fitMinText = string.Empty, _fitMaxText = string.Empty, _fitCapText = string.Empty, _fitError = string.Empty;

    private TextFitOptions? CurrentFit => Current?.Block?.Fit;

    /// <summary>The modes in the order of <see cref="TextFitMode"/>, translated, for the mode list.</summary>
    public IReadOnlyList<string> FitModes => _fitModes ??= new[]
    {
        Loc.Get("Panel.Format.Fit.Mode.None"), Loc.Get("Panel.Format.Fit.Mode.Shrink"), Loc.Get("Panel.Format.Fit.Mode.Grow"),
    };

    /// <summary>True when the text fit section is shown: it needs a wrapped block (a single line has no box to fit).</summary>
    public bool ShowFit => IsActive && IsWrap;

    /// <summary>Index of the chosen mode in <see cref="FitModes"/>. Setting it changes the block as one undo step; a negative index (the list is being rebuilt) is ignored.</summary>
    public int FitModeIndex
    {
        get => (int)(CurrentFit?.Mode ?? TextFitMode.None);
        set
        {
            if (_refreshing || value < 0 || value > (int)TextFitMode.Grow || value == FitModeIndex) return;
            SetFitMode((TextFitMode)value);
        }
    }

    /// <summary>True while shrink mode is chosen (the size limits are shown).</summary>
    public bool FitIsShrink => CurrentFit?.Mode == TextFitMode.Shrink;

    /// <summary>True while grow mode is chosen (the line cap is shown).</summary>
    public bool FitIsGrow => CurrentFit?.Mode == TextFitMode.Grow;

    /// <summary>The plain-language explanation of the chosen mode.</summary>
    public string FitHint => Loc.Get("Panel.Format.Fit.Hint." + (CurrentFit?.Mode ?? TextFitMode.None));

    /// <summary>Smallest size (points) as typed. Typing changes the label as soon as the number is valid.</summary>
    public string FitMinText { get => _fitMinText; set { if (SetProperty(ref _fitMinText, value ?? string.Empty) && !_refreshing) ApplyFitLimit(isMin: true, _fitMinText); } }

    /// <summary>Largest size (points) as typed.</summary>
    public string FitMaxText { get => _fitMaxText; set { if (SetProperty(ref _fitMaxText, value ?? string.Empty) && !_refreshing) ApplyFitLimit(isMin: false, _fitMaxText); } }

    /// <summary>The most lines grow mode may give the block, as typed.</summary>
    public string FitCapText { get => _fitCapText; set { if (SetProperty(ref _fitCapText, value ?? string.Empty) && !_refreshing) ApplyFitCap(_fitCapText); } }

    /// <summary>Do not warn when the text does not fit.</summary>
    public bool FitIgnoreWarning
    {
        get => CurrentFit?.IgnoreOverflowWarning ?? false;
        set
        {
            if (_refreshing || value == FitIgnoreWarning || Current?.Block is null) return;
            EditFit("Undo.Format.Fit.Mode", fit => fit.IgnoreOverflowWarning = value);
        }
    }

    /// <summary>Plain-language problem with a typed limit; empty when fine.</summary>
    public string FitError { get => _fitError; private set { if (SetProperty(ref _fitError, value)) OnPropertyChanged(nameof(HasFitError)); } }

    /// <summary>True when <see cref="FitError"/> is set.</summary>
    public bool HasFitError => FitError.Length > 0;

    private void SetFitMode(TextFitMode mode)
    {
        EditFit("Undo.Format.Fit.Mode", (fit, d, t) =>
        {
            bool wasPlain = fit.Mode == TextFitMode.None;
            fit.Mode = mode;
            if (mode == TextFitMode.Shrink && wasPlain)
            {
                // Start from the size the text has now: the largest it may be is today's size, the smallest is about half of it.
                double now = Math.Round(CurrentPoints(t, d) * 2) / 2;
                fit.MaxSize = Math.Clamp(now, 2, FontChoice.MaxPointSize);
                fit.MinSize = Math.Clamp(Math.Max(1, Math.Round(now / 2)), 1, fit.MaxSize);
            }
        });
        FitError = string.Empty;
    }

    private void ApplyFitLimit(bool isMin, string text)
    {
        text = text.Trim();
        if (!TryParseNumber(text, out double pt) || double.IsNaN(pt) || double.IsInfinity(pt)) { FitError = Loc.Get(text.Length == 0 ? "Validation.Required" : "Validation.NotNumber"); return; }
        // A printer font cannot be smaller than 10 dots, so its limit in points depends on the resolution; round up so the shown value is allowed.
        double lowest = _font.IsWindows ? TextFitOptions.LowestSize : Math.Max(TextFitOptions.LowestSize, Math.Ceiling(FontChoice.DotsToPoints(10, Dpi) * 10) / 10);
        if (pt < lowest || pt > FontChoice.MaxPointSize)
        {
            FitError = Loc.Get("Validation.Range", lowest.ToString("0.##", CultureInfo.CurrentCulture), FontChoice.MaxPointSize.ToString("0.##", CultureInfo.CurrentCulture));
            return;
        }
        var fit = CurrentFit;
        if (fit is null) return;
        pt = Math.Round(pt, 2);
        if ((isMin && pt > fit.MaxSize) || (!isMin && pt < fit.MinSize)) { FitError = Loc.Get("Panel.Format.Fit.Error.Order"); return; }
        FitError = string.Empty;
        string key = isMin ? "min" : "max";
        EditFit("Undo.Format.Fit.Limit", f => { if (isMin) f.MinSize = pt; else f.MaxSize = pt; }, mergeKey: $"format:{_id}:fit{key}");
    }

    private void ApplyFitCap(string text)
    {
        text = text.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int cap)) { FitError = Loc.Get(text.Length == 0 ? "Validation.Required" : "Validation.NotNumber"); return; }
        if (cap < 1 || cap > 9999) { FitError = Loc.Get("Validation.Range", "1", "9999"); return; }
        FitError = string.Empty;
        EditFit("Undo.Format.Fit.Limit", f => f.MaxLinesCap = cap, mergeKey: $"format:{_id}:fitcap");
    }

    private void EditFit(string undoKey, Action<TextFitOptions> change, string? mergeKey = null) => EditFit(undoKey, (f, _, _) => change(f), mergeKey);

    /// <summary>
    /// Changes the text fit of the selected block as one undo step. A fresh copy is edited, never the shared object (undo snapshots hold their own),
    /// and a fit that ends up changing nothing is removed so the file stays as it was before the feature.
    /// </summary>
    private void EditFit(string undoKey, Action<TextFitOptions, LabelDocument, TextElement> change, string? mergeKey = null)
    {
        Edit(undoKey, (t, d) =>
        {
            if (t.Block is not { } block) return;
            var fit = block.Fit?.Clone() ?? new TextFitOptions();
            change(fit, d, t);
            block.Fit = fit.IsDefault ? null : fit;
        }, mergeKey);
    }

    /// <summary>Puts the element's fit limits into the text boxes, without overwriting what the user is typing while it still means the same number.</summary>
    private void RefreshFitTexts(TextFitOptions? fit)
    {
        string Keep(string typed, double value) => TryParseNumber(typed.Trim(), out double d) && Math.Abs(d - value) < 0.005 ? typed : FormatPoints(value);
        var shown = fit ?? new TextFitOptions();
        _fitMinText = Keep(_fitMinText, shown.MinSize);
        _fitMaxText = Keep(_fitMaxText, shown.MaxSize);
        _fitCapText = shown.MaxLinesCap.ToString(CultureInfo.CurrentCulture);
        _fitError = string.Empty;
    }

    // ------------------------------------------------------------------------------- refresh

    /// <summary>Re-reads everything from the selected element. Called on every selection and document change (including undo and redo).</summary>
    public void Refresh()
    {
        var sel = _session.SelectedElements;
        var t = sel.Count == 1 ? sel[0] as TextElement : null;
        _refreshing = true;
        try
        {
            _id = t?.Id;
            IsActive = t is not null;
            if (t is null)
            {
                _fit = TextFit.Fine;
                _font = FontChoice.ForPrinter('0');
            }
            else
            {
                EnsureFonts();
                var doc = _session.Document;
                _font = FontChoice.FromElement(t, DefaultFontId);
                _selectedFont = ItemFor(t, _font);
                // Do not overwrite what the user is typing while it still means the same size.
                double pt = CurrentPoints(t, doc);
                bool sameTyped = TryParseNumber(_sizeText.Trim(), out double typed) && Math.Abs(typed - pt) < 0.005;
                if (!sameTyped) _sizeText = FormatPoints(pt);
                _sizeError = string.Empty;
                _fit = Evaluate(doc, t);
                RefreshFitTexts(t.Block?.Fit);
            }
        }
        finally { _refreshing = false; }
        // Everything is re-read at once; the controls are few and cheap.
        OnPropertyChanged(string.Empty);
    }

    // ------------------------------------------------------------------------------- editing

    /// <summary>Runs one change on the selected text as a single undo step. Look-up by id, because undo replaces the objects.</summary>
    private void Edit(string undoKey, Action<TextElement, LabelDocument> change, string? mergeKey = null)
    {
        string? id = _id;
        if (id is null) return;
        _session.Edit(undoKey, d =>
        {
            if (d.Elements.FirstOrDefault(e => e.Id == id) is TextElement t) change(t, d);
        }, mergeKey);
    }
}
