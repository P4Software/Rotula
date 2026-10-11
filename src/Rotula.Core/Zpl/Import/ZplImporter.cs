using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Text;

namespace Rotula.Core.Zpl.Import;

/// <summary>One tokenized command: prefix (^ or ~), upper-case name, raw parameter text and its extent in the input.</summary>
internal readonly record struct ZplCommand(char Prefix, string Name, string Params, int Start, int End)
{
    public string Display => Prefix + Name;
}

/// <summary>
/// The interpreter behind <see cref="ZplParser"/>. It walks the command stream once, keeping the same sticky state a
/// printer keeps (^BY, ^CF, ^FW, ^CI, ^MU) and collecting one field at a time between ^FO/^FT and ^FS.
/// </summary>
internal sealed partial class ZplImporter
{
    private static readonly Regex QrHeader = new(@"^([HQML])([AM]),(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Everything collected for the field currently open between ^FO/^FT and ^FS.</summary>
    private sealed class Field
    {
        public bool Open;
        public int StartOffset, EndOffset, StartPosition;
        public FieldOriginKind Kind;
        public int X, Y;
        public bool Reverse;
        public char HexIndicator;
        public TextBlockOptions? Block;
        public char? FontId;
        public string? StoredFontPath;
        public int? FontHeight, FontWidth;
        public char? RotationChar;
        public string? BarcodeCommand;
        public string[] BarcodeParams = Array.Empty<string>();
        public string? GraphicCommand;
        public string[] GraphicParams = Array.Empty<string>();
        public string GraphicRawParams = string.Empty;
        public string? Data;
        public string? RecallCommand;
        public string RecallParams = string.Empty;
        public bool Unsupported;
        // Options of data fields. They are applied to the element that is built, or the field is kept raw when it cannot hold them.
        public int? FieldNumber;
        public string? FieldPrompt;
        public char? EmbedChar;
        public FieldClockOptions? Clock;
        public FieldDirection Direction;
        public int Gap;
        public bool HasDirection;
        public TextBoxOptions? TextBox;
        public PrinterSerialOptions? Serial;
        public string? SerialValue;
        /// <summary>True when ^LRY was in force as the field started: the field prints reversed as if it had ^FR.</summary>
        public bool LabelReverse;
        /// <summary>The first option command that only some fields understand, for the "not applicable" warning.</summary>
        public string? OptionCommand;
    }

    /// <summary>Per-label bookkeeping next to the <see cref="LabelDocument"/> being filled.</summary>
    private sealed class DocCtx
    {
        public DocCtx(LabelDocument doc) { Doc = doc; }
        public LabelDocument Doc { get; }
        public bool HasStart, Ended, SeenWidth, SeenLength, ExplicitStart;
        public int Visual;
        /// <summary>Offset where the label ended (its ^XZ, or the end of the input); downloads before it belong to it.</summary>
        public int EndPosition = int.MaxValue;
        public Dictionary<string, int> Positions { get; } = new();
    }

    private readonly string _s;
    private readonly bool _bytes;
    private readonly ZplParseOptions _options;
    private readonly List<ZplImportWarning> _warnings = new();
    private readonly List<DocCtx> _docs = new();
    private readonly List<int> _lineStarts = new() { 0 };

    private DocCtx? _cur;
    private Field _f = new();
    private int _lastX, _lastY;

    // Sticky printer state, in the order the printer would hold it.
    private double _unitFactor = 1.0;
    private FontSpec _cf = new('A', 9, 5); // power-up default of the printer
    private FieldRotation _fw = FieldRotation.Normal;
    private int _byWidth = 2, _byHeight = 10;
    private double _byRatio = 3.0;
    private ZplEncoding? _enc;
    private bool _fieldDecodeFailed;
    private bool _labelReverse; // ^LRY: every following field prints reversed, until ^LRN
    private readonly List<(int Position, StoredGraphic Graphic)> _stored = new(); // ~DG / ~DY downloads in file order
    private long _storedBytes;
    private const long MaxStoredBytes = 16L * 1024 * 1024;

    public ZplImporter(string text, bool bytesMode, ZplParseOptions options, bool utf8Bom)
    {
        _s = text;
        _bytes = bytesMode;
        _options = options;
        if (utf8Bom) _enc = ZplEncoding.Utf8;
        for (int i = 0; i < _s.Length; i++)
            if (_s[i] == '\n') _lineStarts.Add(i + 1);
    }

    /// <summary>Builds the result for a file that could not even be read.</summary>
    public static ZplParseResult Failed(string key, params string[] args)
    {
        var imp = new ZplImporter(string.Empty, false, new ZplParseOptions(), false);
        imp.Warn(key, -1, null, null, args);
        var doc = imp.NewDoc(null, implicitStart: true).Doc;
        return new ZplParseResult(new[] { doc }, imp._warnings, Array.Empty<BarPatternMatch>());
    }

    // ---- driver ----------------------------------------------------------------------------

    public ZplParseResult Run()
    {
        try
        {
            foreach (var command in Tokenize())
            {
                try
                {
                    Handle(command);
                    if (_f.Open) _f.EndOffset = command.End;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A defect in one command must never lose the rest of the file.
                    Warn("Import.Internal.Error", command.Start, command.Display, null, ex.GetType().Name);
                }
            }
            FlushField(null, implicitEnd: true);
            if (_cur is { Ended: false }) EndDoc(_cur, closedByXz: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Warn("Import.Internal.Error", -1, null, null, ex.GetType().Name);
        }

        // A trailing implicit label created only by stray text after the last ^XZ carries nothing worth keeping.
        if (_docs.Count > 1 && _docs[^1] is { ExplicitStart: false, Doc.Elements.Count: 0 }) _docs.RemoveAt(_docs.Count - 1);
        // NewDoc registers the document itself; adding its result again would list the same label twice.
        if (_docs.Count == 0) NewDoc(null, implicitStart: true);
        if (_docs.Count > 1) Warn("Import.Label.Multiple", -1, null, null, _docs.Count.ToString(CultureInfo.InvariantCulture));

        AssignStoredGraphics();
        var documents = _docs.Select(d => d.Doc).ToList();
        var patterns = BarPatternRecognizer.Detect(documents[0]);
        foreach (var pattern in patterns)
            Warn("Import.BarPattern.Detected", _docs[0].Positions.GetValueOrDefault(pattern.Bars[0].Id, -1), null, pattern.Bars[0].Id,
                pattern.Bars.Count.ToString(CultureInfo.InvariantCulture), pattern.DecodedValue ?? string.Empty);
        return new ZplParseResult(documents, _warnings.OrderBy(w => w.Position < 0 ? int.MaxValue : w.Position).ToList(), patterns);
    }

    /// <summary>
    /// A download stays on the printer, so it belongs to the label it precedes and to every later label. A download after the
    /// last label still goes to the last label rather than being lost.
    /// </summary>
    private void AssignStoredGraphics()
    {
        foreach (var (position, graphic) in _stored)
        {
            bool placed = false;
            foreach (var d in _docs)
            {
                if (position >= d.EndPosition) continue;
                d.Doc.StoredGraphics.Add(graphic);
                placed = true;
            }
            if (!placed && _docs.Count > 0) _docs[^1].Doc.StoredGraphics.Add(graphic);
        }
    }

    private IEnumerable<ZplCommand> Tokenize()
    {
        int n = _s.Length, i = 0;
        while (i < n)
        {
            if (_s[i] is not ('^' or '~'))
            {
                int j = i;
                while (j < n && _s[j] is not ('^' or '~')) j++;
                if (!string.IsNullOrWhiteSpace(_s[i..j]))
                    Warn("Import.Text.Stray", i, null, null, Shorten(_s[i..j].Trim()));
                i = j;
                continue;
            }

            int start = i;
            char prefix = _s[i++];
            string name;
            if (prefix == '^' && i < n && (_s[i] == 'A' || _s[i] == 'a'))
            {
                // ^A is one letter; the font id that follows is a parameter, not part of the name.
                name = "A";
                i++;
            }
            else
            {
                int nameStart = i;
                while (i < n && i - nameStart < 2 && _s[i] is not ('^' or '~')) i++;
                name = _s[nameStart..i].ToUpperInvariant();
            }

            int p = i;
            while (i < n && _s[i] is not ('^' or '~')) i++;
            string parameters = _s[p..i].TrimEnd('\r', '\n');
            yield return new ZplCommand(prefix, name, parameters, start, i);
        }
    }

    // ---- warnings --------------------------------------------------------------------------

    private void Warn(string key, int position, string? command, string? elementId, params string[] args)
    {
        int line = 0, column = 0;
        if (position >= 0)
        {
            int idx = _lineStarts.BinarySearch(position);
            if (idx < 0) idx = ~idx - 1;
            line = idx + 1;
            column = position - _lineStarts[idx] + 1;
        }
        _warnings.Add(new ZplImportWarning(key, position, line, column, command, elementId, args));
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "...";

    // ---- label lifecycle -------------------------------------------------------------------

    private DocCtx NewDoc(DocCtx? previous, bool implicitStart)
    {
        var doc = new LabelDocument { Dpi = _options.Dpi, DefaultFont = null };
        var ctx = new DocCtx(doc) { ExplicitStart = !implicitStart };
        if (previous is not null)
        {
            // The printer keeps its settings between labels, so a second ^XA starts from the first label's state.
            var p = previous.Doc;
            doc.Width = p.Width;
            doc.Height = p.Height;
            doc.Orientation = p.Orientation;
            doc.Margins = new LabelMargins { Left = p.Margins.Left, Top = p.Margins.Top, Right = p.Margins.Right, Bottom = p.Margins.Bottom };
            doc.MediaType = p.MediaType;
            doc.MediaTracking = p.MediaTracking;
            doc.DefaultRotation = p.DefaultRotation;
            doc.DefaultFont = p.DefaultFont is { } f ? new FontSpec(f.FontId, f.Height, f.Width) : null;
            doc.Encoding = p.Encoding;
            doc.LabelShift = p.LabelShift;
            doc.LabelTop = p.LabelTop;
            doc.PrintMode = p.PrintMode;
            doc.PrePeel = p.PrePeel;
            doc.MirrorImage = p.MirrorImage;
            doc.PrintRate = p.PrintRate is { } pr ? new PrintRateOptions { Speed = pr.Speed, SlewSpeed = pr.SlewSpeed, BackfeedSpeed = pr.BackfeedSpeed } : null;
            doc.MediaDarkness = p.MediaDarkness;
            foreach (var alias in p.FontAliases) doc.FontAliases.Add(new FontAlias(alias.Letter, alias.Path));
            ctx.SeenWidth = previous.SeenWidth;
            ctx.SeenLength = previous.SeenLength;
        }
        _docs.Add(ctx);
        _cur = ctx;
        return ctx;
    }

    private DocCtx EnsureDoc(ZplCommand c)
    {
        if (_cur is null) return NewDoc(null, implicitStart: true);
        if (_cur.Ended) return NewDoc(_cur, implicitStart: true);
        return _cur;
    }

    private void StartLabel(ZplCommand c)
    {
        FlushField(null, implicitEnd: true);
        if (_cur is null || _cur.Ended)
        {
            var ctx = NewDoc(_cur, implicitStart: false);
            ctx.HasStart = true;
            return;
        }
        if (_cur.HasStart)
            Warn("Import.Label.NestedStart", c.Start, "^XA", null);
        _cur.HasStart = true;
        _cur.ExplicitStart = true;
    }

    private void EndLabel(ZplCommand c)
    {
        FlushField(null, implicitEnd: true);
        if (_cur is null || _cur.Ended)
        {
            Warn("Import.Label.StrayEnd", c.Start, "^XZ", null);
            return;
        }
        EndDoc(_cur, closedByXz: true, c.Start);
    }

    private void EndDoc(DocCtx d, bool closedByXz, int position = 0)
    {
        if (!d.HasStart) Warn("Import.Label.NoStart", 0, null, null);
        else if (!closedByXz) Warn("Import.Label.NoEnd", _s.Length, null, null);
        if (!d.SeenWidth) Warn("Import.Label.NoWidth", -1, "^PW", null);
        if (!d.SeenLength) Warn("Import.Label.NoLength", -1, "^LL", null);
        d.Ended = true;
        d.EndPosition = closedByXz ? position : _s.Length + 1;
    }

    // ---- command dispatch ------------------------------------------------------------------

    private void Handle(ZplCommand c)
    {
        if (c.Prefix == '~')
        {
            if (c.Name is "DG" or "DY" && TryStoreDownload(c)) return;
            // Other tilde commands are immediate printer actions (status, feed, fonts); none is a label element.
            AddRawStandalone(_cur ?? NewDoc(null, implicitStart: true), c, "Import.Command.Unknown", c.Display);
            return;
        }

        switch (c.Name)
        {
            case "XA": StartLabel(c); return;
            case "XZ": EndLabel(c); return;
        }

        var d = EnsureDoc(c);
        string[] p = SplitParams(c.Params);

        switch (c.Name)
        {
            // ---- label settings
            case "PW":
                if (Int(p, 0, c, scale: true) is { } pw && pw is >= 1 and <= 9999) { d.Doc.Width = Measure.Dots(pw); d.SeenWidth = true; }
                else if (Opt(p, 0) is not null) Warn("Import.Parameter.OutOfRange", c.Start, "^PW", null, "^PW", Opt(p, 0)!);
                break;
            case "LL":
                if (Int(p, 0, c, scale: true) is { } ll && ll is >= 1 and <= 32000) { d.Doc.Height = Measure.Dots(ll); d.SeenLength = true; }
                else if (Opt(p, 0) is not null) Warn("Import.Parameter.OutOfRange", c.Start, "^LL", null, "^LL", Opt(p, 0)!);
                break;
            case "LH":
                if (d.Visual > 0) Warn("Import.Label.HomeLate", c.Start, "^LH", null);
                d.Doc.Margins.Left = Measure.Dots(Int(p, 0, c, scale: true) ?? 0);
                d.Doc.Margins.Top = Measure.Dots(Int(p, 1, c, scale: true) ?? 0);
                break;
            case "PO":
                d.Doc.Orientation = UpperChar(p, 0) == 'I' ? LabelOrientation.Inverted : LabelOrientation.Normal;
                if (Opt(p, 0) is { } po && po.ToUpperInvariant() is not ("N" or "I")) Warn("Import.Parameter.Invalid", c.Start, "^PO", null, "^PO", po);
                break;
            case "MN":
                switch (UpperChar(p, 0))
                {
                    case 'N': d.Doc.MediaTracking = MediaTracking.Continuous; break;
                    case 'M': d.Doc.MediaTracking = MediaTracking.Mark; break;
                    case 'Y': case 'W': d.Doc.MediaTracking = MediaTracking.Web; break;
                    case 'A':
                        d.Doc.MediaTracking = MediaTracking.Web;
                        Warn("Import.Setting.Approximated", c.Start, "^MN", null, "^MNA", "^MNY");
                        break;
                    default: Warn("Import.Parameter.Invalid", c.Start, "^MN", null, "^MN", Opt(p, 0) ?? string.Empty); break;
                }
                break;
            case "MT":
                d.Doc.MediaType = UpperChar(p, 0) == 'D' ? MediaType.DirectThermal : MediaType.ThermalTransfer;
                break;
            case "MU": SetUnits(p, c); break;
            case "PM":
                switch (UpperChar(p, 0))
                {
                    case 'Y': d.Doc.MirrorImage = true; break;
                    case 'N': d.Doc.MirrorImage = false; break;
                    default: Warn("Import.Parameter.Invalid", c.Start, "^PM", null, "^PM", Opt(p, 0) ?? string.Empty); break;
                }
                break;
            case "LS":
                if (Opt(p, 0) is not null)
                {
                    if (Int(p, 0, c, scale: true) is { } shift && shift is >= -9999 and <= 9999) d.Doc.LabelShift = shift;
                    else Warn("Import.Parameter.OutOfRange", c.Start, "^LS", null, "^LS", Opt(p, 0)!);
                }
                break;
            case "LT":
                if (Opt(p, 0) is not null)
                {
                    if (Int(p, 0, c, scale: true) is { } top && top is >= -240 and <= 240) d.Doc.LabelTop = top;
                    else Warn("Import.Parameter.OutOfRange", c.Start, "^LT", null, "^LT", Opt(p, 0)!);
                }
                break;
            case "LR":
                // ^LRY is the same as an ^FR in every following field, so it becomes a per-field flag and stays exact mid-label.
                switch (UpperChar(p, 0))
                {
                    case 'Y': _labelReverse = true; break;
                    case 'N': case '\0': _labelReverse = false; break;
                    default: Warn("Import.Parameter.Invalid", c.Start, "^LR", null, "^LR", Opt(p, 0)!); break;
                }
                break;
            case "MM": SetPrintMode(d, p, c); break;
            case "PR": SetPrintRate(d, p, c); break;
            case "MD":
                if (Opt(p, 0) is { } md)
                {
                    if (decimal.TryParse(md, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal level) && level is >= -30m and <= 30m) d.Doc.MediaDarkness = level;
                    else Warn("Import.Parameter.OutOfRange", c.Start, "^MD", null, "^MD", md);
                }
                break;
            case "CW": SetFontAlias(d, p, c); break;
            case "PQ":
                d.Doc.Print.Quantity = Math.Max(1, Int(p, 0, c) ?? 1);
                d.Doc.Print.PauseEvery = Math.Max(0, Int(p, 1, c) ?? 0);
                // ZPL's replicate default is 0 ("none"); the model uses 1 for that.
                d.Doc.Print.Replicates = Math.Max(1, Int(p, 2, c) ?? 1);
                d.Doc.Print.OverridePause = UpperChar(p, 3) == 'Y';
                break;
            case "CI": SetEncoding(d, p, c); break;
            case "CF":
                SetDefaultFont(d, p, c);
                break;
            case "FW":
                if (RotationFrom(UpperChar(p, 0)) is { } fw)
                {
                    _fw = fw;
                    if (d.Visual == 0) d.Doc.DefaultRotation = fw;
                }
                else if (Opt(p, 0) is { } bad) Warn("Import.Parameter.Invalid", c.Start, "^FW", null, "^FW", bad);
                break;
            case "BY":
                if (Int(p, 0, c) is { } bw) _byWidth = Math.Clamp(bw, 1, 10);
                if (Opt(p, 1) is { } ratio && double.TryParse(ratio, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && double.IsFinite(r))
                    _byRatio = Math.Clamp(r, 2.0, 3.0);
                if (Int(p, 2, c, scale: true) is { } bh) _byHeight = Math.Clamp(bh, 1, 32000);
                break;

            // ---- field structure
            case "FO":
            case "FT":
                OpenField(d, c, p);
                break;
            case "FS":
                FlushField(c, implicitEnd: false);
                break;
            case "FR":
                RequireField(d, c);
                _f.Reverse = true;
                break;
            case "FH":
                RequireField(d, c);
                _f.HexIndicator = Opt(p, 0) is { Length: > 0 } hi ? hi[0] : '_';
                break;
            case "FB":
                RequireField(d, c);
                _f.Block = new TextBlockOptions
                {
                    WidthDots = Math.Clamp(Int(p, 0, c, scale: true) ?? 1, 1, 9999),
                    MaxLines = Math.Clamp(Int(p, 1, c) ?? 1, 1, 9999),
                    LineSpacing = Int(p, 2, c, scale: true) ?? 0,
                    Alignment = UpperChar(p, 3) switch { 'C' => TextAlignment.Center, 'R' => TextAlignment.Right, 'J' => TextAlignment.Justified, _ => TextAlignment.Left },
                    HangingIndent = Int(p, 4, c, scale: true) ?? 0,
                };
                break;
            case "A": SetFieldFont(d, p, c); break;
            case "FP":
                RequireField(d, c);
                _f.HasDirection = true;
                _f.OptionCommand ??= "^FP";
                switch (UpperChar(p, 0))
                {
                    case 'V': _f.Direction = FieldDirection.Vertical; break;
                    case 'R': _f.Direction = FieldDirection.Reverse; break;
                    case 'H': case '\0': _f.Direction = FieldDirection.Horizontal; break;
                    default: Warn("Import.Parameter.Invalid", c.Start, "^FP", null, "^FP", Opt(p, 0)!); break;
                }
                _f.Gap = Math.Clamp(Int(p, 1, c, scale: true) ?? 0, 0, 9999);
                break;
            case "TB":
                RequireField(d, c);
                _f.OptionCommand ??= "^TB";
                if (RotationFrom(UpperChar(p, 0)) is not null) _f.RotationChar = UpperChar(p, 0);
                _f.TextBox = new TextBoxOptions
                {
                    WidthDots = Math.Clamp(Int(p, 1, c, scale: true) ?? 1, 1, 32000),
                    HeightDots = Math.Clamp(Int(p, 2, c, scale: true) ?? 1, 1, 32000),
                };
                break;
            case "FE":
                RequireField(d, c);
                // The character may be a space or a comma, so it is read from the raw text rather than the split parameters.
                _f.EmbedChar = c.Params.Length > 0 ? c.Params[0] : '#';
                if (_f.EmbedChar is < ' ' or > '~')
                {
                    // Only printable ASCII can be written back safely.
                    _f.Unsupported = true;
                    Warn("Import.Parameter.Invalid", c.Start, "^FE", null, "^FE", c.Params.Trim());
                }
                break;
            case "FC":
                RequireField(d, c);
                SetClock(p, c);
                break;
            case "FN":
                RequireField(d, c);
                SetFieldNumber(c);
                break;
            case "SN":
                RequireField(d, c);
                SetSerialNumber(p, c);
                break;
            case "SF":
                RequireField(d, c);
                SetSerialMask(p, c);
                break;
            case "FD":
            case "FV":
                RequireField(d, c);
                _f.Data = (_f.Data ?? string.Empty) + DecodeData(c.Params, _f.HexIndicator, c.Start);
                break;
            case "FX":
                // A comment: harmless, but kept so the author's notes survive a round trip.
                AddElement(d, new RawZplElement { Zpl = _s[c.Start..c.End].Trim() + "^FS" }, c.Start);
                break;

            // ---- graphics
            case "GB":
            case "GC":
            case "GD":
            case "GE":
            case "GS":
                RequireField(d, c);
                _f.GraphicCommand = c.Name;
                _f.GraphicParams = p;
                break;
            case "GF":
                RequireField(d, c);
                _f.GraphicCommand = "GF";
                _f.GraphicRawParams = c.Params;
                break;

            // ---- barcodes
            case "BC": case "B3": case "BA": case "BE": case "BU": case "B2": case "BQ": case "BX": case "B7":
            case "B1": case "B4": case "B5": case "B8": case "B9": case "BB": case "BD": case "BF": case "BI": case "BJ":
            case "BK": case "BL": case "BM": case "BO": case "BP": case "BR": case "BS": case "BT": case "BZ":
            case "B0": // the guide documents Aztec under both ^BO (letter) and ^B0 (zero); it is one symbology
                RequireField(d, c);
                _f.BarcodeCommand = c.Name == "B0" ? "BO" : c.Name;
                _f.BarcodeParams = p;
                break;

            // ---- stored objects
            case "XG":
            case "IM":
                RequireField(d, c);
                _f.RecallCommand = c.Name;
                _f.RecallParams = c.Params.Trim();
                break;
            case "IL":
            case "XF":
                AddRecall(d, c);
                break;
            case "IS":
                Warn("Import.ImageSave.Unsupported", c.Start, "^IS", null, FirstParam(p));
                AddRawStandalone(d, c, null, null, visual: false);
                break;
            case "DF":
                Warn("Import.StoredFormat.Unsupported", c.Start, "^DF", null, FirstParam(p));
                AddRawStandalone(d, c, null, null, visual: false);
                break;

            default:
                if (_f.Open)
                {
                    // The whole field is kept verbatim: a half-interpreted field would print differently from the original.
                    _f.Unsupported = true;
                    Warn("Import.Command.Unknown", c.Start, c.Display, null, c.Display);
                }
                else AddRawStandalone(d, c, "Import.Command.Unknown", c.Display);
                break;
        }
    }

    // ---- small helpers ---------------------------------------------------------------------

    private static string[] SplitParams(string text) => text.Split(',');

    private static string? Opt(string[] p, int i) => i < p.Length && p[i].Trim() is { Length: > 0 } s ? s : null;

    private static char UpperChar(string[] p, int i) => Opt(p, i) is { } s ? char.ToUpperInvariant(s[0]) : '\0';

    private static string FirstParam(string[] p) => Opt(p, 0) ?? string.Empty;

    private static bool YesNo(string[] p, int i, bool fallback) => UpperChar(p, i) switch { 'Y' => true, 'N' => false, _ => fallback };

    private static FieldRotation? RotationFrom(char ch) => ch switch
    {
        'N' => FieldRotation.Normal,
        'R' => FieldRotation.Rotated90,
        'I' => FieldRotation.Inverted180,
        'B' => FieldRotation.Rotated270,
        _ => null,
    };

    /// <summary>Reads an integer parameter; with <paramref name="scale"/> the current ^MU unit is applied. Null when absent or invalid.</summary>
    private int? Int(string[] p, int i, ZplCommand c, bool scale = false)
    {
        if (Opt(p, i) is not { } s) return null;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            Warn("Import.Parameter.Invalid", c.Start, c.Display, null, c.Display, s);
            return null;
        }
        if (scale) v *= _unitFactor;
        // Clamped so later arithmetic on positions and sizes can never overflow.
        return (int)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), -1_000_000, 1_000_000);
    }

    private void RequireField(DocCtx d, ZplCommand c)
    {
        if (_f.Open) return;
        // Field data without a preceding ^FO: ZPL continues from the last position.
        _f = new Field { Open = true, StartOffset = c.Start, EndOffset = c.End, StartPosition = c.Start, X = _lastX, Y = _lastY, LabelReverse = _labelReverse };
    }

    private void AddRawStandalone(DocCtx d, ZplCommand c, string? warningKey, string? warningArg, bool visual = false)
    {
        var raw = new RawZplElement { Zpl = _s[c.Start..c.End].Trim(), IsVisual = visual, Name = null };
        AddElement(d, raw, c.Start);
        WarnIfNonAscii(raw, c.Start);
        if (warningKey is not null) Warn(warningKey, c.Start, c.Display, raw.Id, warningArg ?? c.Display);
    }

    private void AddElement(DocCtx d, LabelElement e, int position)
    {
        d.Doc.Elements.Add(e);
        d.Positions[e.Id] = position;
        if (e is not RawZplElement { IsVisual: false }) d.Visual++;
    }

    // ---- settings --------------------------------------------------------------------------

    private void SetUnits(string[] p, ZplCommand c)
    {
        char kind = UpperChar(p, 0);
        if (kind is not ('D' or 'I' or 'M'))
        {
            Warn("Import.Parameter.Invalid", c.Start, "^MU", null, "^MU", Opt(p, 0) ?? string.Empty);
            return;
        }
        double dpi = (int)_options.Dpi;
        double conversion = 1.0;
        if (Int(p, 1, c) is { } b && Int(p, 2, c) is { } t && b > 0 && t > 0) conversion = (double)t / b;
        _unitFactor = kind switch { 'I' => dpi * conversion, 'M' => dpi / 25.4 * conversion, _ => conversion };
        if (Math.Abs(_unitFactor - 1.0) > 1e-9)
            Warn("Import.Units.Converted", c.Start, "^MU", null, kind.ToString(), _unitFactor.ToString("0.####", CultureInfo.InvariantCulture));
    }

    private void SetEncoding(DocCtx d, string[] p, ZplCommand c)
    {
        if (Int(p, 0, c) is not { } code) return;
        if (p.Length > 1 && p.Skip(1).Any(s => s.Trim().Length > 0))
            Warn("Import.Encoding.RemapIgnored", c.Start, "^CI", null);

        ZplEncoding encoding;
        if (ZplEncoding.TryFromCiCode(code, out var known, out bool approx))
        {
            encoding = known;
            if (approx) Warn("Import.Encoding.NationalSet", c.Start, "^CI", null, code.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            encoding = ZplEncoding.Utf8;
            Warn("Import.Encoding.Unsupported", c.Start, "^CI", null, code.ToString(CultureInfo.InvariantCulture));
        }

        if (d.Visual == 0) d.Doc.Encoding = encoding;
        else if (!encoding.Equals(d.Doc.Encoding))
            Warn("Import.Encoding.Changed", c.Start, "^CI", null, code.ToString(CultureInfo.InvariantCulture));
        _enc = encoding;
    }

    private void SetDefaultFont(DocCtx d, string[] p, ZplCommand c)
    {
        char id = Opt(p, 0) is { } s ? char.ToUpperInvariant(s[0]) : _cf.FontId;
        int h = Int(p, 1, c, scale: true) ?? _cf.Height;
        int w = Int(p, 2, c, scale: true) ?? _cf.Width;
        _cf = new FontSpec(id, h, w);
        // Only the first ^CF before any field becomes the document default; later ones are written into the elements they affect.
        if (d.Visual == 0) d.Doc.DefaultFont = new FontSpec(id, h, w);
    }

    private void SetFieldFont(DocCtx d, string[] p, ZplCommand c)
    {
        RequireField(d, c);
        string head = Opt(p, 0) ?? string.Empty;
        if (head.Length > 0)
        {
            char id = char.ToUpperInvariant(head[0]);
            // ^A@ names a font file stored on the printer in its fourth parameter; keep it so the text prints with that font again.
            if (id == '@' && Opt(p, 3) is { } storedPath && StoredFonts.IsValidPath(storedPath.Trim()))
            {
                _f.StoredFontPath = storedPath.Trim();
                _f.FontId = null;
            }
            else
            {
                if (id == '@') Warn("Import.Font.Custom", c.Start, "^A", null);
                _f.FontId = id;
            }
            if (head.Length > 1)
            {
                _f.RotationChar = char.ToUpperInvariant(head[1]);
                if (RotationFrom(_f.RotationChar.Value) is null) _f.RotationChar = null;
            }
        }
        _f.FontHeight = Int(p, 1, c, scale: true);
        _f.FontWidth = Int(p, 2, c, scale: true);
    }

    private static readonly Regex FontAliasPath = new(@"^(?:[REBAreba]:)?[A-Za-z0-9_\-]{1,8}\.(?:FNT|TTF|TTE|fnt|ttf|tte)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FieldNumberParams = new(@"^\s*(\d{1,4})?\s*(?:""([A-Za-z0-9 ]{0,255})"")?\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private void SetPrintMode(DocCtx d, string[] p, ZplCommand c)
    {
        PrintMode? mode = UpperChar(p, 0) switch
        {
            'T' => PrintMode.Tear, 'P' => PrintMode.Peel, 'R' => PrintMode.Rewind, 'A' => PrintMode.Applicator,
            'C' => PrintMode.Cutter, 'D' => PrintMode.DelayedCutter, 'F' => PrintMode.Rfid, 'K' => PrintMode.Kiosk,
            _ => null,
        };
        if (mode is null)
        {
            // Modes the guide marks as reserved (L, U) and anything unknown cannot be modelled; the printer decides what they do.
            if (Opt(p, 0) is null) Warn("Import.Parameter.Invalid", c.Start, "^MM", null, "^MM", string.Empty);
            else AddRawStandalone(d, c, "Import.Setting.Unsupported", c.Display);
            return;
        }
        d.Doc.PrintMode = mode;
        // The pre-peel switch defaults to no, so leaving it out switches it off again.
        d.Doc.PrePeel = UpperChar(p, 1) == 'Y';
    }

    /// <summary>Reads one ^PR speed: a number 1-14, or the letters A-E that stand for 2, 3, 4, 6 and 8 inches per second.</summary>
    private int? ReadSpeed(string[] p, int i, ZplCommand c)
    {
        if (Opt(p, i) is not { } s) return null;
        if (s.Length == 1 && char.ToUpperInvariant(s[0]) is >= 'A' and <= 'E')
            return char.ToUpperInvariant(s[0]) switch { 'A' => 2, 'B' => 3, 'C' => 4, 'D' => 6, _ => 8 };
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v is >= 1 and <= 14) return v;
        Warn("Import.Parameter.Invalid", c.Start, "^PR", null, "^PR", s);
        return null;
    }

    private void SetPrintRate(DocCtx d, string[] p, ZplCommand c)
    {
        int? speed = ReadSpeed(p, 0, c), slew = ReadSpeed(p, 1, c), back = ReadSpeed(p, 2, c);
        if (speed is null && slew is null && back is null) return;
        // The printer keeps the speeds it was not told about, so only the given ones change.
        var rate = d.Doc.PrintRate ??= new PrintRateOptions();
        rate.Speed = speed ?? rate.Speed;
        rate.SlewSpeed = slew ?? rate.SlewSpeed;
        rate.BackfeedSpeed = back ?? rate.BackfeedSpeed;
    }

    private void SetFontAlias(DocCtx d, string[] p, ZplCommand c)
    {
        string? letter = Opt(p, 0), path = Opt(p, 1);
        if (letter is null || letter.Length != 1 || !char.IsAsciiLetterOrDigit(letter[0]) || path is null || !FontAliasPath.IsMatch(path))
        {
            AddRawStandalone(d, c, null, null);
            Warn("Import.Parameter.Invalid", c.Start, "^CW", null, "^CW", c.Params.Trim());
            return;
        }
        if (d.Visual > 0)
        {
            // Fields before this command still use the old font, which a document-wide list cannot express.
            AddRawStandalone(d, c, "Import.Font.AliasLate", c.Display);
            return;
        }
        d.Doc.FontAliases.RemoveAll(a => a.Letter == char.ToUpperInvariant(letter[0]));
        d.Doc.FontAliases.Add(new FontAlias(letter[0], path.ToUpperInvariant()));
    }

    private void SetClock(string[] p, ZplCommand c)
    {
        char? Mark(int i) => Opt(p, i) is { } s ? s[0] : null;
        var clock = new FieldClockOptions { Primary = Mark(0) ?? '%', Secondary = Mark(1), Third = Mark(2) };
        var marks = new List<char> { clock.Primary };
        if (clock.Secondary is { } second) marks.Add(second);
        if (clock.Third is { } third) marks.Add(third);
        if (marks.Distinct().Count() != marks.Count || marks.Any(m => m < ' ' || m > '~'))
        {
            // The printer requires three different indicators; a clash cannot be written back the same way.
            _f.Unsupported = true;
            Warn("Import.Parameter.Invalid", c.Start, "^FC", null, "^FC", c.Params.Trim());
            return;
        }
        _f.Clock = clock;
    }

    private void SetFieldNumber(ZplCommand c)
    {
        var m = FieldNumberParams.Match(c.Params);
        if (!m.Success)
        {
            _f.Unsupported = true;
            Warn("Import.Parameter.Invalid", c.Start, "^FN", null, "^FN", c.Params.Trim());
            return;
        }
        _f.FieldNumber = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        _f.FieldPrompt = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : null;
    }

    private void SetSerialNumber(string[] p, ZplCommand c)
    {
        long increment = 1;
        if (Opt(p, 1) is { } inc && !long.TryParse(inc, NumberStyles.Integer, CultureInfo.InvariantCulture, out increment))
        {
            Warn("Import.Parameter.Invalid", c.Start, "^SN", null, "^SN", inc);
            increment = 1;
        }
        string value = Opt(p, 0) ?? string.Empty;
        if (!PrinterSerialOptions.IsValidStartValue(value))
        {
            // A start value the generator would refuse cannot be modelled; the field is kept as it was written.
            _f.Unsupported = true;
            Warn("Import.Parameter.Invalid", c.Start, "^SN", null, "^SN", value);
            return;
        }
        _f.Serial = new PrinterSerialOptions { Kind = PrinterSerialKind.Number, Increment = increment, LeadingZeros = YesNo(p, 2, false) };
        _f.SerialValue = value;
    }

    private void SetSerialMask(string[] p, ZplCommand c)
    {
        string mask = Opt(p, 0) ?? string.Empty;
        string increment = Opt(p, 1) ?? string.Empty;
        if (!PrinterSerialOptions.IsValidMask(mask) || !PrinterSerialOptions.IsValidIncrementText(increment))
        {
            _f.Unsupported = true;
            Warn("Import.Parameter.Invalid", c.Start, "^SF", null, "^SF", mask + "," + increment);
            return;
        }
        _f.Serial = new PrinterSerialOptions { Kind = PrinterSerialKind.Mask, Mask = mask, IncrementText = increment };
    }

    /// <summary>
    /// Records a graphic downloaded with <c>~DG</c>, or with <c>~DY</c> when its data is plain ASCII bitmap data. Returns false when
    /// the command is something else (a font, a PNG, binary data) so the caller keeps it verbatim.
    /// </summary>
    private bool TryStoreDownload(ZplCommand c)
    {
        string[] head;
        string data;
        string path;
        int total, perRow;
        if (c.Name == "DG")
        {
            // ~DGd:o.x,t,w,data: the data may itself contain commas (compression), so only the first three are separators.
            head = c.Params.Split(',', 4);
            if (head.Length < 4) { Warn("Import.Graphic.Invalid", c.Start, c.Display, null, "0"); return false; }
            path = head[0].Trim();
            data = head[3];
            if (!int.TryParse(head[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out total)
                || !int.TryParse(head[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out perRow))
            {
                Warn("Import.Graphic.Invalid", c.Start, c.Display, null, head[1].Trim());
                return false;
            }
        }
        else
        {
            // ~DYd:f,b,x,t,w,data with b = A (hex or Z64 text) and x = G (.GRF bitmap) is the same download as ~DG.
            head = c.Params.Split(',', 6);
            if (head.Length < 6 || head[1].Trim().ToUpperInvariant() != "A" || head[2].Trim().ToUpperInvariant() is not ("G" or "B")) return false;
            path = head[0].Trim();
            data = head[5];
            if (!int.TryParse(head[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out total)
                || !int.TryParse(head[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out perRow))
                return false;
            // ~DY names carry no extension in their file part; the x parameter says what it is.
            if (!path.Contains('.')) path += ".GRF";
        }

        if (!StoredGraphic.IsValidPath(StoredGraphic.NormalizePath(path)))
        {
            // A name the printer would not accept cannot be written back, so the download is kept verbatim instead.
            Warn("Import.Parameter.Invalid", c.Start, c.Display, null, c.Display, path);
            return false;
        }
        // Each download is kept in memory until the file is processed; a limit stops a hostile file from using all of it.
        if (total < 1 || _storedBytes + (long)total > MaxStoredBytes)
        {
            Warn("Import.Graphic.Invalid", c.Start, c.Display, null, total.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        if (!ZplGraphicData.TryDecode(data, total, perRow, out byte[] packed, out bool truncated) || total % perRow != 0)
        {
            Warn("Import.Graphic.Invalid", c.Start, c.Display, null, total.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        if (truncated) Warn("Import.Graphic.Truncated", c.Start, c.Display, null);
        _storedBytes += packed.Length;
        _stored.Add((c.Start, new StoredGraphic(path, perRow, packed)));
        return true;
    }

    // ---- fields ----------------------------------------------------------------------------

    private void OpenField(DocCtx d, ZplCommand c, string[] p)
    {
        FlushField(null, implicitEnd: true);
        var kind = c.Name == "FT" ? FieldOriginKind.Typeset : FieldOriginKind.Origin;
        int x = Int(p, 0, c, scale: true) ?? _lastX;
        int y = Int(p, 1, c, scale: true) ?? _lastY;
        _f = new Field { Open = true, Kind = kind, X = x, Y = y, StartOffset = c.Start, EndOffset = c.End, StartPosition = c.Start, LabelReverse = _labelReverse };
    }

    /// <summary>
    /// Turns the collected field into an element. <paramref name="terminator"/> is the ^FS that ended it; null means the
    /// field ran into the next command or the end of the label without one.
    /// </summary>
    private void FlushField(ZplCommand? terminator, bool implicitEnd)
    {
        var f = _f;
        if (!f.Open) return;
        _f = new Field();
        _lastX = f.X;
        _lastY = f.Y;
        var d = _cur;
        if (d is null) return;

        bool hasContent = f.Unsupported || f.GraphicCommand is not null || f.BarcodeCommand is not null || f.RecallCommand is not null || f.Data is not null
            || f.FieldNumber is not null || f.Serial is not null;
        if (!hasContent) return;
        if (implicitEnd && terminator is null)
            Warn("Import.Field.MissingEnd", f.StartPosition, null, null);

        _fieldDecodeFailed = false;
        // A barcode command with a parameter the model cannot hold (a composite DataBar, Intelligent Mail ...) is kept whole.
        if (!f.Unsupported && f.BarcodeCommand is not null && RejectBarcode(f) is { } rejection)
        {
            f.Unsupported = true;
            Warn(rejection.Key, f.StartPosition, "^" + f.BarcodeCommand, null, rejection.Args);
        }
        if (!f.Unsupported && f.Serial is { Kind: PrinterSerialKind.Number })
        {
            // ^SN supplies the data itself; a ^FD next to it would make the printed value ambiguous.
            if (f.Data is not null)
            {
                f.Unsupported = true;
                Warn("Import.Serial.Conflict", f.StartPosition, "^SN", null);
            }
            else f.Data = f.SerialValue;
        }
        LabelElement? element = f.Unsupported ? BuildRawField(f, terminator) : BuildElement(d, f);
        if (element is null) return;
        if (element is not RawZplElement && !ApplyFieldOptions(f, element))
        {
            element = BuildRawField(f, terminator);
        }
        element.X = f.X;
        element.Y = f.Y;
        element.OriginKind = f.Kind;
        element.ReverseField = f.Reverse || f.LabelReverse;
        AddElement(d, element, f.StartPosition);
    }

    /// <summary>
    /// Copies the options collected for the field onto the element. Returns false (after warning) when the element cannot hold
    /// one of them, for example a field number on a box: the caller then keeps the whole field verbatim.
    /// </summary>
    private bool ApplyFieldOptions(Field f, LabelElement element)
    {
        bool any = f.FieldNumber is not null || f.EmbedChar is not null || f.Clock is not null || f.Serial is not null;
        if (element is TextElement text)
        {
            if (f.TextBox is not null && text.Block is not null)
            {
                Warn("Import.Field.OptionNotApplicable", f.StartPosition, "^TB", null, "^TB");
                return false;
            }
            text.TextBox = f.TextBox;
            if (f.HasDirection) { text.Direction = f.Direction; text.InterCharacterGap = f.Gap; }
            if (f.TextBox is not null && text.Text.Contains('<'))
            {
                // Inside a ^TB box "<<>" is a literal '<'; any other '<' opens an escape sequence the model cannot hold.
                string unescaped = text.Text.Replace("<<>", "\u0001", StringComparison.Ordinal);
                if (unescaped.Contains('<'))
                {
                    Warn("Import.Field.OptionNotApplicable", f.StartPosition, "^TB", null, "^TB");
                    return false;
                }
                text.Text = unescaped.Replace("\u0001", "<", StringComparison.Ordinal);
            }
        }
        else if (f.TextBox is not null || f.HasDirection)
        {
            Warn("Import.Field.OptionNotApplicable", f.StartPosition, f.OptionCommand ?? "^TB", null, f.OptionCommand ?? "^TB");
            return false;
        }

        if (element is BarcodeElement symbol && f.Serial is { Kind: PrinterSerialKind.Number }
            && ZplBarcodeEmitter.Command(symbol, string.Empty, "N", out string dataPrefix).Length > 0 && dataPrefix.Length > 0)
        {
            // Symbols whose data starts with a fixed prefix (a QR Code's "QA,") cannot take a counting start value.
            Warn("Import.Field.OptionNotApplicable", f.StartPosition, "^SN", null, "^SN");
            return false;
        }

        if (element is FieldDataElement data)
        {
            data.FieldNumber = f.FieldNumber;
            data.FieldPrompt = f.FieldPrompt;
            data.EmbedCharacter = f.EmbedChar;
            data.Clock = f.Clock;
            data.Serial = f.Serial;
        }
        else if (any)
        {
            string cmd = f.FieldNumber is not null ? "^FN" : f.EmbedChar is not null ? "^FE" : f.Clock is not null ? "^FC" : f.Serial?.Kind == PrinterSerialKind.Mask ? "^SF" : "^SN";
            Warn("Import.Field.OptionNotApplicable", f.StartPosition, cmd, null, cmd);
            return false;
        }
        return true;
    }

    private LabelElement BuildRawField(Field f, ZplCommand? terminator)
    {
        int end = terminator?.End ?? f.EndOffset;
        string text = _s[f.StartOffset..Math.Max(f.StartOffset, Math.Min(end, _s.Length))].Trim();
        if (terminator is null && !text.EndsWith("^FS", StringComparison.OrdinalIgnoreCase)) text += "^FS";
        if (f.LabelReverse && !f.Reverse)
        {
            // The kept text must still print reversed: put ^FR right behind the first command (normally ^FO or ^FT).
            int next = text.IndexOf('^', 1);
            if (next > 0) text = text.Insert(next, "^FR");
        }
        var raw = new RawZplElement { Zpl = text, IsVisual = true };
        WarnIfNonAscii(raw, f.StartPosition);
        return raw;
    }

    /// <summary>
    /// Raw blocks keep their text verbatim, so bytes above 0x7F survive only as Latin-1 characters. Saying so keeps the
    /// risk visible: whoever sends the regenerated ZPL must write those characters back as single bytes.
    /// </summary>
    private void WarnIfNonAscii(RawZplElement raw, int position)
    {
        if (_bytes && raw.Zpl.Any(ch => ch > 0x7F))
            Warn("Import.Raw.NonAscii", position, null, raw.Id);
    }

    private LabelElement? BuildElement(DocCtx d, Field f)
    {
        if (f.GraphicCommand is not null) return BuildGraphic(d, f);
        if (f.BarcodeCommand is not null) return BuildBarcode(d, f);
        if (f.RecallCommand is not null)
        {
            var placeholder = MakeRecall(f.RecallCommand, f.RecallParams, asField: true);
            // A graphic downloaded earlier in the same file can be drawn, so only a recall of something unseen is reported.
            bool known = StoredGraphic.Find(_stored.Where(x => x.Position < f.StartPosition).Select(x => x.Graphic), placeholder.Reference) is not null;
            if (!known) Warn("Import.Recall.Graphic.Unavailable", f.StartPosition, "^" + f.RecallCommand, placeholder.Id, placeholder.Reference);
            return placeholder;
        }
        return BuildText(d, f);
    }

    private FieldRotation? ResolveRotation(DocCtx d, char explicitChar)
    {
        if (RotationFrom(explicitChar) is { } r) return r;
        // Inheriting the ^FW in force is only the same thing as "null" when it is the document default.
        return _fw == d.Doc.DefaultRotation ? null : _fw;
    }

    private TextElement BuildText(DocCtx d, Field f)
    {
        var def = d.Doc.DefaultFont;
        string text = f.Data ?? string.Empty;
        if (f.Block is not null) text = text.Replace("\\&", "\n", StringComparison.Ordinal);
        return new TextElement
        {
            Text = text,
            // A value given by ^A stays explicit; an inherited one is only left null when it equals the document default.
            StoredFontPath = f.StoredFontPath,
            FontId = f.StoredFontPath is not null ? null : f.FontId ?? (def is not null && def.FontId == _cf.FontId ? null : _cf.FontId),
            FontHeight = f.FontHeight ?? (def is not null && def.Height == _cf.Height ? null : _cf.Height),
            FontWidth = f.FontWidth ?? (def is not null && def.Width == _cf.Width ? null : _cf.Width),
            Rotation = ResolveRotation(d, f.RotationChar ?? '\0'),
            Block = f.Block,
        };
    }

    // ---- graphics --------------------------------------------------------------------------

    private LabelElement? BuildGraphic(DocCtx d, Field f)
    {
        var c = new ZplCommand('^', f.GraphicCommand!, string.Empty, f.StartPosition, f.StartPosition);
        string[] p = f.GraphicParams;
        switch (f.GraphicCommand)
        {
            case "GB":
            {
                int t = Math.Clamp(Int(p, 2, c, scale: true) ?? 1, 1, 32000);
                // Omitted or too small sizes become the thickness, which is how a bar is drawn as ^GB,216,14.
                int w = Math.Clamp(Math.Max(Int(p, 0, c, scale: true) ?? t, t), 1, 32000);
                int h = Math.Clamp(Math.Max(Int(p, 1, c, scale: true) ?? t, t), 1, 32000);
                var color = UpperChar(p, 3) == 'W' ? GraphicColor.White : GraphicColor.Black;
                // The generator always writes the rounding parameter for boxes and never for lines, so its presence tells them apart.
                if (Opt(p, 4) is not null || (w != t && h != t))
                {
                    return new BoxElement
                    {
                        WidthDots = w, HeightDots = h, ThicknessDots = t, Color = color,
                        CornerRounding = Math.Clamp(Int(p, 4, c) ?? 0, 0, 8),
                    };
                }
                bool horizontal = h == t;
                return new LineElement
                {
                    Direction = horizontal ? LineDirection.Horizontal : LineDirection.Vertical,
                    LengthDots = horizontal ? w : h,
                    ThicknessDots = t,
                    Color = color,
                };
            }
            case "GC":
                return new CircleElement
                {
                    DiameterDots = Math.Clamp(Int(p, 0, c, scale: true) ?? 3, 3, 4095),
                    ThicknessDots = Math.Clamp(Int(p, 1, c, scale: true) ?? 1, 1, 4095),
                    Color = UpperChar(p, 2) == 'W' ? GraphicColor.White : GraphicColor.Black,
                };
            case "GE":
            {
                int t = Math.Clamp(Int(p, 2, c, scale: true) ?? 1, 1, 4095);
                // Omitted sizes become the thickness, as the guide's defaults say.
                return new EllipseElement
                {
                    WidthDots = Math.Clamp(Int(p, 0, c, scale: true) ?? t, 3, 4095),
                    HeightDots = Math.Clamp(Int(p, 1, c, scale: true) ?? t, 3, 4095),
                    ThicknessDots = t,
                    Color = UpperChar(p, 3) == 'W' ? GraphicColor.White : GraphicColor.Black,
                };
            }
            case "GD":
            {
                int t = Math.Clamp(Int(p, 2, c, scale: true) ?? 1, 1, 32000);
                string lean = Opt(p, 4) ?? string.Empty;
                // R or / lean right (rise to the right), L or \ lean left; right is the default.
                return new DiagonalLineElement
                {
                    WidthDots = Math.Clamp(Int(p, 0, c, scale: true) ?? t, 3, 32000),
                    HeightDots = Math.Clamp(Int(p, 1, c, scale: true) ?? t, 3, 32000),
                    ThicknessDots = t,
                    Color = UpperChar(p, 3) == 'W' ? GraphicColor.White : GraphicColor.Black,
                    Lean = lean.Length > 0 && (char.ToUpperInvariant(lean[0]) == 'L' || lean[0] == '\\') ? DiagonalLean.Left : DiagonalLean.Right,
                };
            }
            case "GS":
            {
                string symbol = (f.Data ?? string.Empty).Trim();
                char letter = symbol.Length > 0 ? char.ToUpperInvariant(symbol[0]) : '\0';
                if (letter is < 'A' or > 'E' || symbol.Length > 1)
                {
                    Warn("Import.Parameter.Invalid", f.StartPosition, "^GS", null, "^GS", symbol);
                    return BuildRawField(f, null);
                }
                return new SymbolElement
                {
                    Symbol = (GraphicSymbol)(letter - 'A'),
                    // The guide's default for both sizes is the last ^CF value.
                    HeightDots = Math.Clamp(Int(p, 1, c, scale: true) ?? _cf.Height, 0, 32000),
                    WidthDots = Math.Clamp(Int(p, 2, c, scale: true) ?? _cf.Width, 0, 32000),
                    Rotation = ResolveRotation(d, UpperChar(p, 0)),
                };
            }
            default:
                return BuildImage(d, f);
        }
    }

    private LabelElement BuildImage(DocCtx d, Field f)
    {
        // ^GFa,b,c,d,data: the data itself may contain commas, so only the first four are separators.
        string[] parts = f.GraphicRawParams.Split(',', 5);
        string[] head = parts.Take(4).ToArray();
        char format = UpperChar(head, 0);
        var c = new ZplCommand('^', "GF", string.Empty, f.StartPosition, f.StartPosition);

        void Fail(string key, string arg) => Warn(key, f.StartPosition, "^GF", null, arg);

        if (format != 'A')
        {
            Fail("Import.Graphic.Format.Unsupported", format == '\0' ? string.Empty : format.ToString());
            return BuildRawField(f, null);
        }

        int total = Int(head, 2, c) ?? 0, perRow = Int(head, 3, c) ?? 0;
        string data = parts.Length > 4 ? parts[4] : string.Empty;
        if (!ZplGraphicData.TryDecode(data, total, perRow, out byte[] packed, out bool truncated))
        {
            Fail("Import.Graphic.Invalid", total.ToString(CultureInfo.InvariantCulture));
            return BuildRawField(f, null);
        }
        if (truncated) Warn("Import.Graphic.Truncated", f.StartPosition, "^GF", null);

        int width = perRow * 8, rows = total / perRow;
        var pixels = new byte[width * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = (packed[y * perRow + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
        return new ImageElement
        {
            Source = new GrayscaleBitmap(width, rows, pixels),
            // Pure black/white pixels at their native size: the threshold reduction reproduces the bitmap exactly.
            Dither = ImageDitherMode.Threshold,
            Threshold = 128,
        };
    }

    // ---- stored object recalls -------------------------------------------------------------

    private RecallPlaceholderElement MakeRecall(string command, string parameters, bool asField)
    {
        string reference = parameters.Split(',')[0].Trim();
        return new RecallPlaceholderElement
        {
            Command = command,
            Reference = reference,
            Zpl = "^" + command + parameters,
            EmitAsField = asField,
        };
    }

    private void AddRecall(DocCtx d, ZplCommand c)
    {
        FlushField(null, implicitEnd: true);
        var placeholder = MakeRecall(c.Name, c.Params.Trim(), asField: false);
        placeholder.X = _lastX;
        placeholder.Y = _lastY;
        AddElement(d, placeholder, c.Start);
        Warn(c.Name == "IL" ? "Import.Recall.Image.Unavailable" : "Import.Recall.Format.Unavailable", c.Start, c.Display, placeholder.Id, placeholder.Reference);
    }

    // Barcode commands are interpreted in ZplImporter.Barcodes.cs.

    /// <summary>
    /// Code 128 data may start with a ZPL invocation (<c>&gt;:</c> B, <c>&gt;;</c> C, <c>&gt;9</c> A) that picks the start subset. The
    /// generator reserves <c>&gt;</c>, so that one leading code is folded into automatic mode and anything else is warned about.
    /// </summary>
    private string NormalizeCode128(BarcodeElement b, string data, Field f)
    {
        if (data.Length >= 2 && data[0] == '>' && data[1] is ':' or ';' or '9')
        {
            data = data[2..];
            if (b.Code128Mode == Code128Mode.None) b.Code128Mode = Code128Mode.Automatic;
        }
        if (data.Contains('>'))
            Warn("Import.Barcode.Code128.Invocation", f.StartPosition, "^BC", b.Id);
        return data;
    }

    // ---- field data ------------------------------------------------------------------------

    /// <summary>Resolves <c>^FH</c> escapes and, for byte input, decodes the field bytes with the active ^CI.</summary>
    private string DecodeData(string raw, char hex, int position)
    {
        var sb = new StringBuilder(raw.Length);
        var pending = new List<byte>();

        void Flush()
        {
            if (pending.Count == 0) return;
            sb.Append(DecodeBytes(pending.ToArray(), position));
            pending.Clear();
        }

        for (int i = 0; i < raw.Length; i++)
        {
            char ch = raw[i];
            if (hex != '\0' && ch == hex && i + 2 < raw.Length && Uri.IsHexDigit(raw[i + 1]) && Uri.IsHexDigit(raw[i + 2]))
            {
                pending.Add(Convert.ToByte(raw.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (_bytes && ch <= 0xFF)
            {
                pending.Add((byte)ch);
            }
            else
            {
                Flush();
                sb.Append(ch);
            }
        }
        Flush();
        return sb.ToString();
    }

    private string DecodeBytes(byte[] bytes, int position)
    {
        try
        {
            if (_enc is null)
            {
                // The printer powers up in ^CI0 (Zebra code page 850), so files that never say otherwise and hold
                // legacy bytes mean code page 850. Valid UTF-8 is far more likely to be deliberate, so it is tried first.
                try { return new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { return ZplEncoding.Cp850.GetEncoding().GetString(bytes); }
            }
            if (_enc.IsUtf8)
                return new UTF8Encoding(false, true).GetString(bytes);
            return _enc.GetEncoding().GetString(bytes);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            if (!_fieldDecodeFailed)
            {
                Warn("Import.Text.DecodeFailed", position, null, null, _enc?.ToString() ?? "^CI28");
                _fieldDecodeFailed = true;
            }
            // Latin-1 never fails, so the user sees every byte instead of losing the text.
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
