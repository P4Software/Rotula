using Rotula.Rendering;
using Rotula.Rendering.Input;

namespace Rotula.App.Editing;

/// <summary>
/// Keeps the render input and the rasterised preview of the label while the design is unchanged.
/// Converting the document into a <see cref="RenderLabel"/> and drawing it are the expensive steps, so both are redone
/// only when the session revision changes (and, for the bitmap, when the scale changes). Panning, selecting and
/// moving the mouse therefore never re-render the label.
/// </summary>
public sealed class PreviewCache : IDisposable
{
    private readonly EditorSession _session;
    private RenderLabel? _label;
    private long _labelRevision = -1;
    private RenderResult? _bitmap;
    private long _bitmapRevision = -1;
    private double _bitmapScale;
    private long _failedRevision = -1;
    private double _failedScale;
    private static int _failureReported;

    /// <summary>Raised once per process when the preview cannot be drawn for a reason other than size (for example the bitmap cannot be allocated).</summary>
    public static event Action<Exception>? RenderFailed;

    /// <summary>Creates a cache for a session.</summary>
    public PreviewCache(EditorSession session) => _session = session;

    /// <summary>
    /// Values of the data-merge record shown on the canvas, or null to draw the sample values. Changing it must be followed
    /// by <see cref="EditorSession.Touch"/> so the cached render is rebuilt.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Data { get; set; }

    /// <summary>How many times the document was converted to render input (exposed for tests).</summary>
    public int LabelBuildCount { get; private set; }

    /// <summary>How many times the label was rasterised (exposed for tests).</summary>
    public int BitmapBuildCount { get; private set; }

    /// <summary>The render input for the current revision, rebuilt only when the document changed.</summary>
    public RenderLabel GetLabel()
    {
        if (_label is null || _labelRevision != _session.Revision)
        {
            _label = LabelDocumentAdapter.ToRenderLabel(_session.Document, Data);
            _labelRevision = _session.Revision;
            LabelBuildCount++;
        }
        return _label;
    }

    /// <summary>
    /// The render input for the design canvas: the same as <see cref="GetLabel"/>, plus the objects that are not printed for the shown record,
    /// drawn dimmed so they can still be seen and selected. When no object is switched off it is exactly <see cref="GetLabel"/>.
    /// </summary>
    public RenderLabel GetDesignLabel()
    {
        if (!Core.Elements.ElementPrinting.AnyConditional(_session.Document)) return GetLabel();
        if (_designLabel is null || _designLabelRevision != _session.Revision)
        {
            _designLabel = LabelDocumentAdapter.ToRenderLabel(_session.Document, Data, includeNonPrinting: true);
            _designLabelRevision = _session.Revision;
        }
        return _designLabel;
    }

    /// <summary>
    /// Draws the label exactly as it prints (objects that are not printed for the shown record are left out). The caller owns and must dispose the
    /// result. Returns null and sets <paramref name="errorKey"/> when the label cannot be drawn.
    /// </summary>
    public RenderResult? RenderPrinted(double scale, out string? errorKey)
    {
        errorKey = null;
        var label = GetLabel();
        if (label.WidthDots <= 0 || label.HeightDots <= 0) { errorKey = "Preview.InvalidSize"; return null; }
        try { return LabelRenderer.RenderBitmap(label, new RenderOptions { Scale = scale }); }
        catch (ArgumentOutOfRangeException) { errorKey = "Preview.TooLarge"; return null; }
        catch (Exception) { errorKey = "Preview.Unavailable"; return null; }
    }

    /// <summary>
    /// True when the element has a condition that is false for the record shown now (so it is left out of the print). Objects marked "do not print"
    /// are a different state and report false here. Cached per revision and record, because the answer needs the variables resolved.
    /// </summary>
    public bool IsHiddenByCondition(Core.Elements.LabelElement element)
    {
        if (element.NotPrintable || element.PrintCondition is null) return false;
        if (_notPrinted is null || _notPrintedRevision != _session.Revision || !ReferenceEquals(_notPrintedData, Data))
        {
            _notPrinted = LabelDocumentAdapter.FindNotPrinted(_session.Document, Data);
            _notPrintedRevision = _session.Revision;
            _notPrintedData = Data;
        }
        return _notPrinted.Contains(element.Id);
    }

    private HashSet<string>? _notPrinted;
    private long _notPrintedRevision = -1;
    private IReadOnlyDictionary<string, string>? _notPrintedData;

    private RenderLabel? _designLabel;
    private long _designLabelRevision = -1;

    /// <summary>
    /// The rasterised label at <paramref name="scale"/> pixels per dot. The returned object is owned by the cache and stays
    /// valid until the next call that has to re-render. Returns null and sets <paramref name="errorKey"/> when the label
    /// cannot be drawn (zero size, or too large for the current zoom).
    /// </summary>
    public RenderResult? GetBitmap(double scale, out string? errorKey)
    {
        errorKey = null;
        // A render that failed for this very state is not tried again on every repaint of the pointer.
        if (_failedRevision == _session.Revision && Math.Abs(_failedScale - scale) < 1e-9) { errorKey = "Preview.Unavailable"; return null; }
        if (_bitmap is not null && _bitmapRevision == _session.Revision && Math.Abs(_bitmapScale - scale) < 1e-9)
            return _bitmap;

        var label = GetDesignLabel();
        _bitmap?.Dispose();
        _bitmap = null;
        if (label.WidthDots <= 0 || label.HeightDots <= 0)
        {
            errorKey = "Preview.InvalidSize";
            return null;
        }
        try
        {
            _bitmap = LabelRenderer.RenderBitmap(label, new RenderOptions { Scale = scale });
            _bitmapRevision = _session.Revision;
            _bitmapScale = scale;
            BitmapBuildCount++;
            return _bitmap;
        }
        catch (ArgumentOutOfRangeException)
        {
            errorKey = "Preview.TooLarge";
            return null;
        }
        catch (Exception ex)
        {
            // "Unable to allocate pixels", a font that cannot be loaded...: show "preview unavailable" instead of the unexpected-error dialog.
            _failedRevision = _session.Revision;
            _failedScale = scale;
            errorKey = "Preview.Unavailable";
            if (System.Threading.Interlocked.Exchange(ref _failureReported, 1) == 0) RenderFailed?.Invoke(ex);
            return null;
        }
    }

    /// <summary>Releases the cached bitmap.</summary>
    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
