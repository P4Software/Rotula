using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Mcp.Hosting;
using Rotula.Mcp.Session;

namespace Rotula.Mcp.Tools;

/// <summary>Builds the descriptions of the label and its elements that the tools return.</summary>
internal static class LabelView
{
    public static LabelInfo Describe(McpContext ctx, LabelDocument doc, string name, string? file, bool dirty)
    {
        string unit = Units.OfLabel(doc);
        var dpi = doc.Dpi;
        double M(Measure m) => Units.Express(m, unit, dpi);
        return new LabelInfo(
            name, M(doc.Width), M(doc.Height), unit, (int)dpi, doc.WidthDots, doc.HeightDots,
            doc.Orientation.ToString(),
            new MarginInfo(M(doc.Margins.Left), M(doc.Margins.Top), M(doc.Margins.Right), M(doc.Margins.Bottom)),
            doc.MediaType.ToString(), doc.MediaTracking.ToString(), ElementModel.RotationText(doc.DefaultRotation),
            doc.DefaultFont is { } f ? $"font {f.FontId}, height {f.Height} dots, width {f.Width} dots" : null,
            doc.Encoding.ToString(), doc.Print.Quantity,
            doc.Variables.Select(v => new VariableInfo(v.Name, v.SampleValue)).ToList(),
            doc.Elements.Count, file, dirty, ctx.Session.UndoSteps);
    }

    /// <summary>Describes the current label from the session (copy, so no lock is held while formatting).</summary>
    public static LabelInfo Current(McpContext ctx)
    {
        var s = ctx.Session.GetCopy();
        return Describe(ctx, s.Document, s.Name, s.SourcePath, s.Dirty);
    }

    public static IReadOnlyList<ElementInfo> Elements(LabelDocument doc) =>
        doc.Elements.Select(e => ElementModel.Describe(doc, e)).ToList();

    /// <summary>Builds the reply of a tool that changed the label: a message, the label, the touched elements and their findings.</summary>
    public static ChangeResult Change(McpContext ctx, string message, params string[] elementIds)
    {
        var s = ctx.Session.GetCopy();
        // With no ids the whole element list is returned (open, delete, label settings); otherwise just the touched elements.
        var touched = s.Document.Elements.Where(e => elementIds.Length == 0 || elementIds.Contains(e.Id)).Select(e => ElementModel.Describe(s.Document, e)).ToList();
        var issues = elementIds.Length == 0
            ? McpContext.Validate(s.Document)
            : McpContext.Validate(s.Document).Where(i => i.ElementId is null || elementIds.Contains(i.ElementId)).ToList();
        return new ChangeResult(message, Describe(ctx, s.Document, s.Name, s.SourcePath, s.Dirty), touched, issues);
    }

    public static LabelElement Find(LabelDocument doc, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ModelContextProtocol.McpException("An element id is required. Call get_label to see the ids.");
        return doc.Elements.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), StringComparison.Ordinal))
               ?? throw new ModelContextProtocol.McpException($"There is no element with id \"{id}\". Call get_label to see the ids.");
    }
}
