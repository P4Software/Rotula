using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Files;
using Rotula.Mcp.Hosting;
using Rotula.Mcp.Security;
using Rotula.Mcp.Session;

namespace Rotula.Mcp.Tools;

/// <summary>Tools that add, change, move, copy, reorder and delete label elements.</summary>
[McpServerToolType]
public sealed class ElementTools
{
    private readonly McpContext _ctx;

    /// <summary>Creates the tool set over the shared context.</summary>
    public ElementTools(McpContext ctx) => _ctx = ctx;

    private const string UnitDoc = "Unit of the lengths in this call: \"mm\", \"in\" or \"dots\". Default: the label's own unit.";

    // ---- add -------------------------------------------------------------------------------

    /// <summary>Adds a text element.</summary>
    [McpServerTool(Name = "add_text", Destructive = false, UseStructuredContent = true)]
    [Description("Adds text to the current label. x and y are the top-left corner measured from the label's top-left inside the margins. Text may contain {{variable}} placeholders. Accented letters and the Spanish inverted marks print correctly (UTF-8). Give blockWidth to wrap the text inside a block of that width. Returns the new element id and any problems found with it.")]
    public ChangeResult AddText(
        [Description("The text to print.")] string text,
        [Description("Distance from the left edge.")] double x,
        [Description("Distance from the top edge.")] double y,
        [Description("Character height. Default: the label's default font height.")] double? fontHeight = null,
        [Description("Character width (0 = proportional to the height).")] double? fontWidth = null,
        [Description("Font id: 0 is the scalable font (default); A-Z and 1-9 are printer bitmap fonts.")] string? fontId = null,
        [Description("Rotation: 0, 90, 180 or 270 degrees.")] string? rotation = null,
        [Description("Width of a wrapped text block; leave empty for a single line.")] double? blockWidth = null,
        [Description("Most lines in the block (with blockWidth).")] int? maxLines = null,
        [Description("Block alignment: Left, Center, Right or Justified.")] string? alignment = null,
        [Description("Print in reverse (white on black where it overlaps black).")] bool reverse = false,
        [Description("A friendly name for the element.")] string? name = null,
        [Description(UnitDoc)] string? unit = null)
    {
        var bag = Bag(("fontHeight", fontHeight), ("fontWidth", fontWidth), ("fontId", fontId), ("rotation", rotation),
            ("blockWidth", blockWidth), ("maxLines", maxLines), ("alignment", alignment), ("name", name));
        if (reverse) bag["reverse"] = J(true);
        return Add("add_text", unit, x, y, new TextElement { Text = ElementModel.Clip(text, 4000) }, bag);
    }

    /// <summary>Adds a barcode element.</summary>
    [McpServerTool(Name = "add_barcode", Destructive = false, UseStructuredContent = true)]
    [Description("Adds a barcode of any of the 28 supported types (call list_symbologies for names, data rules, example data, options and preview limits). The data is checked against the symbology's rules (digits only, check digits, lengths) and problems come back in issues. Options that only one type has (Codabar start/stop, MSI check digits, GS1 DataBar variant, Aztec, MaxiCode, ...) go in the options object. For a QR code you can also use add_qr.")]
    public ChangeResult AddBarcode(
        [Description("Symbology name from list_symbologies, for example Code128, Ean13, Ean8, UpcE, Codabar, Msi, DataBar, QrCode, DataMatrix, Pdf417, Aztec or MaxiCode.")] string symbology,
        [Description("The data to encode; may contain {{variable}} placeholders.")] string data,
        [Description("Distance from the left edge.")] double x,
        [Description("Distance from the top edge.")] double y,
        [Description("Bar height (linear codes) or row height (PDF417).")] double? height = null,
        [Description("Narrow bar width in dots, 1-10 (linear codes and PDF417). Default 2.")] int? moduleWidthDots = null,
        [Description("Print the human-readable text (linear codes). Default true.")] bool? humanReadable = null,
        [Description("Human-readable text position: Below or Above.")] string? humanReadablePosition = null,
        [Description("Check digit option where the symbology supports it.")] bool? checkDigit = null,
        [Description("Rotation: 0, 90, 180 or 270 degrees.")] string? rotation = null,
        [Description("Code 128 mode: None, Ucc, Automatic or Ean.")] string? code128Mode = null,
        [Description("QR error correction: Low, Medium, Quartile or High.")] string? qrErrorCorrection = null,
        [Description("QR model 1 or 2.")] int? qrModel = null,
        [Description("QR module size or Data Matrix element size in dots, 1-10.")] int? moduleSize = null,
        [Description("Data Matrix / PDF417 columns (0 = automatic).")] int? columns = null,
        [Description("Data Matrix / PDF417 rows (0 = automatic).")] int? rows = null,
        [Description("PDF417 error correction level 1-8.")] int? pdf417SecurityLevel = null,
        [Description("PDF417 compact variant.")] bool? pdf417Truncate = null,
        [Description("Extra options of this barcode type as an object, for example {\"codabarStart\":\"B\",\"codabarStop\":\"D\"} or {\"dataBarType\":\"Expanded\"}. Only options listed for the type by list_symbologies are accepted; a wrong name or value is refused with the valid ones.")] Dictionary<string, JsonElement>? options = null,
        [Description("A friendly name for the element.")] string? name = null,
        [Description(UnitDoc)] string? unit = null)
    {
        var bag = Bag(("height", height), ("moduleWidthDots", moduleWidthDots), ("humanReadable", humanReadable),
            ("humanReadablePosition", humanReadablePosition), ("checkDigit", checkDigit), ("rotation", rotation),
            ("code128Mode", code128Mode), ("qrErrorCorrection", qrErrorCorrection), ("qrModel", qrModel), ("moduleSize", moduleSize),
            ("columns", columns), ("rows", rows), ("pdf417SecurityLevel", pdf417SecurityLevel), ("pdf417Truncate", pdf417Truncate), ("options", options), ("name", name));
        var element = new BarcodeElement { Symbology = ElementModel.ParseSymbology(symbology), Data = ElementModel.Clip(data, 4000) };
        return Add("add_barcode", unit, x, y, element, bag);
    }

    /// <summary>Adds a QR code.</summary>
    [McpServerTool(Name = "add_qr", Destructive = false, UseStructuredContent = true)]
    [Description("Adds a QR code. The printed size is set by moduleSize (dots per square, 1-10): a typical label code uses 4 to 6. Error correction defaults to Quartile.")]
    public ChangeResult AddQr(
        [Description("The text or URL to encode; may contain {{variable}} placeholders.")] string data,
        [Description("Distance from the left edge.")] double x,
        [Description("Distance from the top edge.")] double y,
        [Description("Dots per module, 1-10. Default 4.")] int? moduleSize = null,
        [Description("Low, Medium, Quartile (default) or High.")] string? errorCorrection = null,
        [Description("Rotation: 0, 90, 180 or 270 degrees.")] string? rotation = null,
        [Description("A friendly name for the element.")] string? name = null,
        [Description(UnitDoc)] string? unit = null)
    {
        var bag = Bag(("moduleSize", moduleSize), ("qrErrorCorrection", errorCorrection), ("rotation", rotation), ("name", name));
        return Add("add_qr", unit, x, y, new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = ElementModel.Clip(data, 4000) }, bag);
    }

    /// <summary>Adds a box.</summary>
    [McpServerTool(Name = "add_box", Destructive = false, UseStructuredContent = true)]
    [Description("Adds a rectangle outline. Make a filled block by giving a thickness of at least half the smaller side.")]
    public ChangeResult AddBox(
        [Description("Distance from the left edge.")] double x,
        [Description("Distance from the top edge.")] double y,
        [Description("Box width.")] double width,
        [Description("Box height.")] double height,
        [Description("Border thickness. Default 3 dots.")] double? thickness = null,
        [Description("Black (default) or White.")] string? color = null,
        [Description("Corner rounding 0 (square) to 8.")] int? cornerRounding = null,
        [Description("A friendly name for the element.")] string? name = null,
        [Description(UnitDoc)] string? unit = null)
    {
        var bag = Bag(("width", width), ("height", height), ("thickness", thickness), ("color", color), ("cornerRounding", cornerRounding), ("name", name));
        return Add("add_box", unit, x, y, new BoxElement(), bag);
    }

    /// <summary>Adds a line.</summary>
    [McpServerTool(Name = "add_line", Destructive = false, UseStructuredContent = true)]
    [Description("Adds a straight horizontal or vertical line starting at x, y.")]
    public ChangeResult AddLine(
        [Description("Start distance from the left edge.")] double x,
        [Description("Start distance from the top edge.")] double y,
        [Description("Line length.")] double length,
        [Description("Line thickness. Default 3 dots.")] double? thickness = null,
        [Description("Horizontal (default) or Vertical.")] string? direction = null,
        [Description("Black (default) or White.")] string? color = null,
        [Description("A friendly name for the element.")] string? name = null,
        [Description(UnitDoc)] string? unit = null)
    {
        var bag = Bag(("length", length), ("thickness", thickness), ("direction", direction), ("color", color), ("name", name));
        return Add("add_line", unit, x, y, new LineElement(), bag);
    }

    /// <summary>Adds a circle.</summary>
    [McpServerTool(Name = "add_circle", Destructive = false, UseStructuredContent = true)]
    [Description("Adds a circle outline. x and y are the top-left corner of the circle's bounding square.")]
    public ChangeResult AddCircle(
        [Description("Distance from the left edge to the bounding square.")] double x,
        [Description("Distance from the top edge to the bounding square.")] double y,
        [Description("Outer diameter (3 to 4095 dots).")] double diameter,
        [Description("Border thickness. Default 3 dots.")] double? thickness = null,
        [Description("Black (default) or White.")] string? color = null,
        [Description("A friendly name for the element.")] string? name = null,
        [Description(UnitDoc)] string? unit = null)
    {
        var bag = Bag(("diameter", diameter), ("thickness", thickness), ("color", color), ("name", name));
        return Add("add_circle", unit, x, y, new CircleElement(), bag);
    }

    /// <summary>Adds an image.</summary>
    [McpServerTool(Name = "add_image", Destructive = false, UseStructuredContent = true)]
    [Description("Adds a PNG image from an allowed folder (PNG only, at most 4096 x 4096 pixels). Thermal printers print black or white only, so the image is converted to black and white: use dither FloydSteinberg for photos, Threshold for logos and line art, Ordered for a steady pattern. Give width and/or height (the other keeps the aspect ratio); with neither, one pixel becomes one dot.")]
    public ChangeResult AddImage(
        [Description("Path of the .png file.")] string path,
        [Description("Distance from the left edge.")] double x,
        [Description("Distance from the top edge.")] double y,
        [Description("Printed width.")] double? width = null,
        [Description("Printed height.")] double? height = null,
        [Description("Threshold, FloydSteinberg (default) or Ordered.")] string? dither = null,
        [Description("Brightness cut-off 0-255 (darker prints black). Default 128.")] int? threshold = null,
        [Description("Swap black and white.")] bool? invert = null,
        [Description("Rotation: 0, 90, 180 or 270 degrees.")] string? rotation = null,
        [Description("A friendly name for the element.")] string? name = null,
        [Description(UnitDoc)] string? unit = null)
    {
        string full = _ctx.Guard.Resolve(path, [".png"], mustExist: true);
        var source = SafeFiles.LoadPng(full);
        var bag = Bag(("width", width), ("height", height), ("dither", dither), ("threshold", threshold), ("invert", invert), ("rotation", rotation), ("name", name));
        return Add("add_image", unit, x, y, new ImageElement { Source = source }, bag);
    }

    /// <summary>Adds a data field bound to a variable.</summary>
    [McpServerTool(Name = "add_data_field", Destructive = false, UseStructuredContent = true)]
    [Description("Adds a text field whose content comes from a variable, for data merge: each record of a CSV, Excel or database source fills the field. The variable is declared if it does not exist yet; sampleValue is what shows when no data is merged. Use merge_preview to see a record in place.")]
    public ChangeResult AddDataField(
        [Description("Variable name: letters, digits and underscore. Matches a column name of the data source.")] string variableName,
        [Description("Distance from the left edge.")] double x,
        [Description("Distance from the top edge.")] double y,
        [Description("Value shown when no data is merged. Default: the variable name.")] string? sampleValue = null,
        [Description("Character height.")] double? fontHeight = null,
        [Description("Character width (0 = proportional).")] double? fontWidth = null,
        [Description("Font id (0 is the scalable font).")] string? fontId = null,
        [Description("Rotation: 0, 90, 180 or 270 degrees.")] string? rotation = null,
        [Description("Width of a wrapped text block; leave empty for a single line.")] double? blockWidth = null,
        [Description("Most lines in the block.")] int? maxLines = null,
        [Description("Block alignment: Left, Center, Right or Justified.")] string? alignment = null,
        [Description("A friendly name for the element.")] string? name = null,
        [Description(UnitDoc)] string? unit = null)
    {
        ElementModel.RequireVariableName(variableName);
        var bag = Bag(("fontHeight", fontHeight), ("fontWidth", fontWidth), ("fontId", fontId), ("rotation", rotation),
            ("blockWidth", blockWidth), ("maxLines", maxLines), ("alignment", alignment), ("name", name));
        return Add("add_data_field", unit, x, y, new DataFieldElement { VariableName = variableName }, bag, doc =>
        {
            var existing = doc.Variables.FirstOrDefault(v => string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase));
            if (existing is null) doc.Variables.Add(new LabelVariable(variableName, ElementModel.Clip(sampleValue ?? variableName, 1000)));
            else if (sampleValue is not null) existing.SampleValue = ElementModel.Clip(sampleValue, 1000);
        });
    }

    // ---- change ----------------------------------------------------------------------------

    /// <summary>Changes properties of an element.</summary>
    [McpServerTool(Name = "update_element", Destructive = false, UseStructuredContent = true)]
    [Description("Changes one or more properties of an existing element by id. properties is an object such as {\"text\":\"Hello\",\"fontHeight\":8} or {\"data\":\"12345\",\"height\":15}. Every element accepts x, y, name, rotation, reverse and originKind; the rest depends on its type (text: text, fontId, fontHeight, fontWidth, blockWidth, maxLines, lineSpacing, hangingIndent, alignment; barcode: symbology, data, height, moduleWidthDots, the other options listed by list_symbologies, and options (an object with the extra options of the type, such as {\"msiCheckDigit\":\"Mod10\"}); box: width, height, thickness, color, cornerRounding; line: length, thickness, direction, color; circle: diameter, thickness, color; image: width, height, dither, threshold, invert). Lengths use the unit argument. An unknown name is refused with the valid names. Nothing changes if any value is rejected.")]
    public ChangeResult UpdateElement(
        [Description("Element id from get_label.")] string id,
        [Description("Property names and new values.")] Dictionary<string, JsonElement> properties,
        [Description(UnitDoc)] string? unit = null)
    {
        if (properties is null || properties.Count == 0)
            throw new McpException("properties is empty. Pass the values to change, for example {\"text\":\"New text\"}.");
        if (properties.Count > 40) throw new McpException("Too many properties in one call (at most 40).");
        _ctx.Session.Mutate("update_element " + id, doc =>
        {
            var e = LabelView.Find(doc, id);
            ElementModel.Apply(doc, e, properties, Units.Normalize(unit, doc));
            return 0;
        });
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, $"Updated {id}.", id);
    }

    /// <summary>Moves an element.</summary>
    [McpServerTool(Name = "move_element", Destructive = false, UseStructuredContent = true)]
    [Description("Moves an element, either to an absolute position (x, y) or by a distance (deltaX, deltaY). Give any of the four; x and deltaX together are not allowed.")]
    public ChangeResult MoveElement(
        [Description("Element id.")] string id,
        [Description("New distance from the left edge.")] double? x = null,
        [Description("New distance from the top edge.")] double? y = null,
        [Description("Move right by this much (negative = left).")] double? deltaX = null,
        [Description("Move down by this much (negative = up).")] double? deltaY = null,
        [Description(UnitDoc)] string? unit = null)
    {
        if (x is null && y is null && deltaX is null && deltaY is null)
            throw new McpException("Give x/y for an absolute position or deltaX/deltaY to move by a distance.");
        if ((x is not null && deltaX is not null) || (y is not null && deltaY is not null))
            throw new McpException("Use either x or deltaX (and either y or deltaY), not both.");
        _ctx.Session.Mutate("move_element " + id, doc =>
        {
            var e = LabelView.Find(doc, id);
            string u = Units.Normalize(unit, doc);
            if (x is not null) e.X = ElementModel.CheckRange("x", Units.ToDots(x.Value, u, doc.Dpi), -32000, 32000);
            if (y is not null) e.Y = ElementModel.CheckRange("y", Units.ToDots(y.Value, u, doc.Dpi), -32000, 32000);
            if (deltaX is not null) e.X = ElementModel.CheckRange("x", e.X + Units.ToDots(deltaX.Value, u, doc.Dpi), -32000, 32000);
            if (deltaY is not null) e.Y = ElementModel.CheckRange("y", e.Y + Units.ToDots(deltaY.Value, u, doc.Dpi), -32000, 32000);
            return 0;
        });
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, $"Moved {id}.", id);
    }

    /// <summary>Deletes an element.</summary>
    [McpServerTool(Name = "delete_element", Destructive = true, UseStructuredContent = true)]
    [Description("Removes an element from the label. undo_last brings it back.")]
    public ChangeResult DeleteElement([Description("Element id.")] string id)
    {
        _ctx.Session.Mutate("delete_element " + id, doc =>
        {
            var e = LabelView.Find(doc, id);
            doc.Elements.Remove(e);
            return 0;
        });
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, $"Deleted {id}.");
    }

    /// <summary>Copies an element.</summary>
    [McpServerTool(Name = "duplicate_element", Destructive = false, UseStructuredContent = true)]
    [Description("Makes a copy of an element, placed slightly offset (10 dots right and down unless offsetX / offsetY are given) and on top of the others. Returns the new id.")]
    public ChangeResult DuplicateElement(
        [Description("Element id to copy.")] string id,
        [Description("Horizontal offset of the copy.")] double? offsetX = null,
        [Description("Vertical offset of the copy.")] double? offsetY = null,
        [Description(UnitDoc)] string? unit = null)
    {
        string newId = _ctx.Session.Mutate("duplicate_element " + id, doc =>
        {
            var source = LabelView.Find(doc, id);
            var copy = RotulaFileSerializer.DeserializeElements(RotulaFileSerializer.SerializeElements(new[] { source })).FirstOrDefault()
                       ?? throw new McpException("That element cannot be copied.");
            string u = Units.Normalize(unit, doc);
            copy.Id = LabelSession.NextId(doc, ElementModel.IdPrefix(source));
            copy.X = ElementModel.CheckRange("x", source.X + (offsetX is null ? 10 : Units.ToDots(offsetX.Value, u, doc.Dpi)), -32000, 32000);
            copy.Y = ElementModel.CheckRange("y", source.Y + (offsetY is null ? 10 : Units.ToDots(offsetY.Value, u, doc.Dpi)), -32000, 32000);
            doc.Elements.Add(copy);
            return copy.Id;
        });
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, $"Copied {id} to {newId}.", newId);
    }

    /// <summary>Changes the drawing order.</summary>
    [McpServerTool(Name = "reorder_element", Destructive = false, UseStructuredContent = true)]
    [Description("Changes where an element sits in the drawing order. Later elements are drawn on top of earlier ones. Use to=\"front\", \"back\", \"forward\" or \"backward\", or index for an exact position (0 is the very back).")]
    public ChangeResult ReorderElement(
        [Description("Element id.")] string id,
        [Description("\"front\", \"back\", \"forward\" (one step toward the front) or \"backward\".")] string? to = null,
        [Description("Exact position in the drawing order, 0 = back.")] int? index = null)
    {
        if ((to is null) == (index is null))
            throw new McpException("Give exactly one of to (front, back, forward, backward) or index.");
        _ctx.Session.Mutate("reorder_element " + id, doc =>
        {
            var e = LabelView.Find(doc, id);
            int from = doc.Elements.IndexOf(e);
            int target = index ?? to!.Trim().ToLowerInvariant() switch
            {
                "front" or "top" => doc.Elements.Count - 1,
                "back" or "bottom" => 0,
                "forward" or "up" => Math.Min(from + 1, doc.Elements.Count - 1),
                "backward" or "down" => Math.Max(from - 1, 0),
                _ => throw new McpException($"Unknown position \"{to}\". Use front, back, forward or backward."),
            };
            if (target < 0 || target >= doc.Elements.Count)
                throw new McpException($"The index must be between 0 and {doc.Elements.Count - 1}.");
            doc.Elements.RemoveAt(from);
            doc.Elements.Insert(target, e);
            return 0;
        });
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, $"Reordered {id}.", id);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private ChangeResult Add(string tool, string? unit, double x, double y, LabelElement element,
        Dictionary<string, JsonElement> bag, Action<LabelDocument>? extra = null)
    {
        string id = _ctx.Session.Mutate(tool, doc =>
        {
            string u = Units.Normalize(unit, doc);
            element.Id = LabelSession.NextId(doc, ElementModel.IdPrefix(element));
            element.X = ElementModel.CheckRange("x", Units.ToDots(x, u, doc.Dpi), -32000, 32000);
            element.Y = ElementModel.CheckRange("y", Units.ToDots(y, u, doc.Dpi), -32000, 32000);
            ElementModel.Apply(doc, element, bag, u);
            extra?.Invoke(doc);
            doc.Elements.Add(element);
            return element.Id;
        });
        _ctx.Gate.Forget();
        return LabelView.Change(_ctx, $"Added {ElementModel.TypeName(element)} {id}.", id);
    }

    /// <summary>Collects the arguments that were actually given into a property bag.</summary>
    private static Dictionary<string, JsonElement> Bag(params (string Name, object? Value)[] values)
    {
        var bag = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
            if (value is not null) bag[name] = J(value);
        return bag;
    }

    private static JsonElement J(object value)
    {
        // JSON cannot carry NaN or infinity; say so in words instead of failing inside the serializer.
        if (value is double d && !double.IsFinite(d)) throw new McpException("A length must be a finite number.");
        return JsonSerializer.SerializeToElement(value);
    }
}
