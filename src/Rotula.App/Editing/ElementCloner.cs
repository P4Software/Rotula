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
                c = new BarcodeElement
                {
                    Symbology = b.Symbology, Data = b.Data, Height = b.Height, ModuleWidth = b.ModuleWidth, WideBarRatio = b.WideBarRatio,
                    HumanReadable = b.HumanReadable, HumanReadablePosition = b.HumanReadablePosition, CheckDigit = b.CheckDigit,
                    Code128Mode = b.Code128Mode, QrErrorCorrection = b.QrErrorCorrection, QrModel = b.QrModel, ModuleSize = b.ModuleSize,
                    Columns = b.Columns, Rows = b.Rows, Pdf417SecurityLevel = b.Pdf417SecurityLevel, Pdf417Truncate = b.Pdf417Truncate,
                };
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
            default:
                throw new NotSupportedException($"Element type {e.GetType().Name} is not supported.");
        }
        if (!newId) c.Id = e.Id;
        c.Name = e.Name;
        c.X = e.X;
        c.Y = e.Y;
        c.OriginKind = e.OriginKind;
        c.Rotation = e.Rotation;
        c.ReverseField = e.ReverseField;
        return c;
    }

    private static TextElement CopyText(TextElement target, TextElement s)
    {
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
