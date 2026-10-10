using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.App.Editing;

/// <summary>
/// Deep copies of Core model objects. Used for undo snapshots, duplicate and paste. A hand-written copy is used instead
/// of a JSON round trip because snapshots are taken on every edit and image pixels must be shared, not re-encoded.
/// </summary>
public static class ElementCloner
{
    /// <summary>Copies an element. With <paramref name="newId"/> the copy gets a fresh id (duplicate/paste); otherwise the id is kept (undo).</summary>
    public static LabelElement Clone(LabelElement e, bool newId = false)
    {
        LabelElement c;
        switch (e)
        {
            case DataFieldElement f:
                c = CopyText(new DataFieldElement { VariableName = f.VariableName }, f);
                break;
            case TextElement t:
                c = CopyText(new TextElement(), t);
                break;
            case BarcodeElement b:
                var bc = new BarcodeElement
                {
                    Symbology = b.Symbology, Data = b.Data, Height = b.Height, ModuleWidth = b.ModuleWidth, WideBarRatio = b.WideBarRatio,
                    HumanReadable = b.HumanReadable, HumanReadablePosition = b.HumanReadablePosition, CheckDigit = b.CheckDigit,
                    Code128Mode = b.Code128Mode, QrErrorCorrection = b.QrErrorCorrection, QrModel = b.QrModel, ModuleSize = b.ModuleSize,
                    Columns = b.Columns, Rows = b.Rows, Pdf417SecurityLevel = b.Pdf417SecurityLevel, Pdf417Truncate = b.Pdf417Truncate,
                };
                // The options of the later symbologies travel through Core's own export/import so a new option can never be forgotten here.
                bc.ImportExtraOptions(b.ExportExtraOptions());
                c = bc;
                break;
            case DiagonalLineElement dg:
                c = new DiagonalLineElement { WidthDots = dg.WidthDots, HeightDots = dg.HeightDots, ThicknessDots = dg.ThicknessDots, Color = dg.Color, Lean = dg.Lean };
                break;
            case EllipseElement el:
                c = new EllipseElement { WidthDots = el.WidthDots, HeightDots = el.HeightDots, ThicknessDots = el.ThicknessDots, Color = el.Color };
                break;
            case SymbolElement sy:
                c = new SymbolElement { Symbol = sy.Symbol, HeightDots = sy.HeightDots, WidthDots = sy.WidthDots };
                break;
            case BoxElement x:
                c = new BoxElement { WidthDots = x.WidthDots, HeightDots = x.HeightDots, ThicknessDots = x.ThicknessDots, Color = x.Color, CornerRounding = x.CornerRounding };
                break;
            case LineElement l:
                c = new LineElement { LengthDots = l.LengthDots, ThicknessDots = l.ThicknessDots, Direction = l.Direction, Color = l.Color };
                break;
            case CircleElement ci:
                c = new CircleElement { DiameterDots = ci.DiameterDots, ThicknessDots = ci.ThicknessDots, Color = ci.Color };
                break;
            case ImageElement im:
                // GrayscaleBitmap is immutable, so the pixels can be shared between copies.
                c = new ImageElement { Source = im.Source, WidthDots = im.WidthDots, HeightDots = im.HeightDots, Dither = im.Dither, Threshold = im.Threshold, Invert = im.Invert };
                break;
            case RawZplElement raw:
                // Imported ZPL that the designer cannot interpret; it must survive copies and undo snapshots untouched.
                c = new RawZplElement { Zpl = raw.Zpl, IsVisual = raw.IsVisual };
                break;
            case RecallPlaceholderElement rp:
                c = new RecallPlaceholderElement
                {
                    Command = rp.Command, Reference = rp.Reference, Zpl = rp.Zpl, EmitAsField = rp.EmitAsField, WidthDots = rp.WidthDots, HeightDots = rp.HeightDots,
                };
                break;
            default:
                throw new NotSupportedException($"Element type {e.GetType().Name} is not supported.");
        }
        if (e is FieldDataElement fromField && c is FieldDataElement toField) CopyFieldData(fromField, toField);
        if (!newId) c.Id = e.Id;
        c.Name = e.Name;
        c.X = e.X;
        c.Y = e.Y;
        c.OriginKind = e.OriginKind;
        c.Rotation = e.Rotation;
        c.ReverseField = e.ReverseField;
        return c;
    }

    /// <summary>Copies the options every data field has (field number, embedding, clock, printer serialization).</summary>
    private static void CopyFieldData(FieldDataElement from, FieldDataElement to)
    {
        to.FieldNumber = from.FieldNumber;
        to.FieldPrompt = from.FieldPrompt;
        to.EmbedCharacter = from.EmbedCharacter;
        to.Clock = from.Clock is null ? null : new FieldClockOptions { Primary = from.Clock.Primary, Secondary = from.Clock.Secondary, Third = from.Clock.Third };
        to.Serial = from.Serial is null ? null : new PrinterSerialOptions
        {
            Kind = from.Serial.Kind, Increment = from.Serial.Increment, LeadingZeros = from.Serial.LeadingZeros,
            Mask = from.Serial.Mask, IncrementText = from.Serial.IncrementText,
        };
    }

    private static TextElement CopyText(TextElement target, TextElement s)
    {
        target.TextBox = s.TextBox is null ? null : new TextBoxOptions { WidthDots = s.TextBox.WidthDots, HeightDots = s.TextBox.HeightDots };
        target.Direction = s.Direction;
        target.InterCharacterGap = s.InterCharacterGap;
        target.Text = s.Text;
        target.FontId = s.FontId;
        target.FontHeight = s.FontHeight;
        target.FontWidth = s.FontWidth;
        target.Block = s.Block is null ? null : new TextBlockOptions
        {
            WidthDots = s.Block.WidthDots, MaxLines = s.Block.MaxLines, LineSpacing = s.Block.LineSpacing,
            Alignment = s.Block.Alignment, HangingIndent = s.Block.HangingIndent,
        };
        return target;
    }

    /// <summary>Copies the label-level settings (everything except the element list) from one document to another.</summary>
    public static void CopySettings(LabelDocument from, LabelDocument to)
    {
        to.Width = from.Width;
        to.Height = from.Height;
        to.Dpi = from.Dpi;
        to.Orientation = from.Orientation;
        to.Margins = new LabelMargins { Left = from.Margins.Left, Top = from.Margins.Top, Right = from.Margins.Right, Bottom = from.Margins.Bottom };
        to.MediaType = from.MediaType;
        to.MediaTracking = from.MediaTracking;
        to.DefaultRotation = from.DefaultRotation;
        to.DefaultFont = from.DefaultFont is null ? null : new FontSpec(from.DefaultFont.FontId, from.DefaultFont.Height, from.DefaultFont.Width);
        to.Encoding = from.Encoding;
        to.Print = new PrintOptions
        {
            Quantity = from.Print.Quantity, PauseEvery = from.Print.PauseEvery, Replicates = from.Print.Replicates, OverridePause = from.Print.OverridePause,
        };
        to.StockId = from.StockId;
        to.LabelShift = from.LabelShift;
        to.LabelTop = from.LabelTop;
        to.PrintMode = from.PrintMode;
        to.PrePeel = from.PrePeel;
        to.MirrorImage = from.MirrorImage;
        to.PrintRate = from.PrintRate is null ? null : new PrintRateOptions { Speed = from.PrintRate.Speed, SlewSpeed = from.PrintRate.SlewSpeed, BackfeedSpeed = from.PrintRate.BackfeedSpeed };
        to.MediaDarkness = from.MediaDarkness;
        to.FontAliases.Clear();
        foreach (var a in from.FontAliases) to.FontAliases.Add(new FontAlias(a.Letter, a.Path));
        // StoredGraphic is immutable (its data is never changed in place), so the same objects can be shared between snapshots.
        to.StoredGraphics.Clear();
        to.StoredGraphics.AddRange(from.StoredGraphics);
        to.Variables.Clear();
        foreach (var v in from.Variables) to.Variables.Add(new LabelVariable(v.Name, v.SampleValue));
    }

    /// <summary>Deep copy of a whole document.</summary>
    public static LabelDocument CloneDocument(LabelDocument d)
    {
        var copy = new LabelDocument();
        CopySettings(d, copy);
        foreach (var e in d.Elements) copy.Elements.Add(Clone(e));
        return copy;
    }
}
