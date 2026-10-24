using Rotula.Core.Sheets;

namespace Rotula.Files;

/// <summary>
/// The sheet layout remembered with a label (the root <c>sheet</c> object). It is optional and only a preference for the "Label sheets"
/// export, so it does not raise the format version: an older build ignores the unknown property and opens the label as before.
/// </summary>
public sealed class SheetDto
{
    /// <summary>Id of the built-in template, or null for a layout the user typed.</summary>
    public string? Template { get; set; }
    /// <summary>Paper size name: <c>Letter</c>, <c>A4</c>, <c>Legal</c> or <c>Custom</c>.</summary>
    public string Page { get; set; } = "Letter";
    /// <summary>Paper width in millimetres.</summary>
    public double PageWidthMm { get; set; }
    /// <summary>Paper height in millimetres.</summary>
    public double PageHeightMm { get; set; }
    /// <summary>Top margin in millimetres.</summary>
    public double TopMm { get; set; }
    /// <summary>Left margin in millimetres.</summary>
    public double LeftMm { get; set; }
    /// <summary>Label width in millimetres.</summary>
    public double LabelWidthMm { get; set; }
    /// <summary>Label height in millimetres.</summary>
    public double LabelHeightMm { get; set; }
    /// <summary>Labels across.</summary>
    public int Columns { get; set; } = 1;
    /// <summary>Labels down.</summary>
    public int Rows { get; set; } = 1;
    /// <summary>Distance between the left edges of two columns, in millimetres.</summary>
    public double HorizontalPitchMm { get; set; }
    /// <summary>Distance between the top edges of two rows, in millimetres.</summary>
    public double VerticalPitchMm { get; set; }
    /// <summary>Fill order name; null is across then down.</summary>
    public string? Order { get; set; }
    /// <summary>Guides name; null is none.</summary>
    public string? Guides { get; set; }
}

/// <summary>Converts <see cref="SheetSettings"/> to and from the file object. The file is untrusted: a section that makes no sense is dropped, never trusted.</summary>
internal static class SheetSettingsMapper
{
    public static SheetDto ToDto(SheetSettings s) => new()
    {
        Template = s.TemplateId, Page = s.Layout.PageKind.ToString(), PageWidthMm = s.Layout.PageWidthMm, PageHeightMm = s.Layout.PageHeightMm,
        TopMm = s.Layout.TopMarginMm, LeftMm = s.Layout.LeftMarginMm, LabelWidthMm = s.Layout.LabelWidthMm, LabelHeightMm = s.Layout.LabelHeightMm,
        Columns = s.Layout.Columns, Rows = s.Layout.Rows, HorizontalPitchMm = s.Layout.HorizontalPitchMm, VerticalPitchMm = s.Layout.VerticalPitchMm,
        Order = s.Order == SheetOrder.AcrossThenDown ? null : s.Order.ToString(),
        Guides = s.Guides == SheetGuides.None ? null : s.Guides.ToString(),
    };

    /// <summary>The settings in the file, or null when the section is missing or its layout has errors (the dialog then starts from its defaults).</summary>
    public static SheetSettings? FromDto(SheetDto? d)
    {
        if (d is null) return null;
        var page = Enum.TryParse<SheetPageKind>(d.Page, ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : SheetPageKind.Custom;
        var layout = new SheetLayout
        {
            PageKind = page, PageWidthMm = d.PageWidthMm, PageHeightMm = d.PageHeightMm, TopMarginMm = d.TopMm, LeftMarginMm = d.LeftMm,
            LabelWidthMm = d.LabelWidthMm, LabelHeightMm = d.LabelHeightMm, Columns = d.Columns, Rows = d.Rows,
            HorizontalPitchMm = d.HorizontalPitchMm, VerticalPitchMm = d.VerticalPitchMm,
        };
        // A named paper keeps its real size even when the numbers in the file were edited by hand.
        if (page != SheetPageKind.Custom) layout = layout.WithPage(page);
        if (!layout.IsValid) return null;
        string? template = d.Template is { Length: > 0 and <= 64 } t && SheetTemplates.Find(t) is { } found ? found.Id : null;
        return new SheetSettings
        {
            TemplateId = template,
            Layout = layout,
            Order = Enum.TryParse<SheetOrder>(d.Order, ignoreCase: true, out var o) && Enum.IsDefined(o) ? o : SheetOrder.AcrossThenDown,
            Guides = Enum.TryParse<SheetGuides>(d.Guides, ignoreCase: true, out var g) && Enum.IsDefined(g) ? g : SheetGuides.None,
        };
    }
}
