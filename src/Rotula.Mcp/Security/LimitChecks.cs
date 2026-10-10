using ModelContextProtocol;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Mcp.Settings;

namespace Rotula.Mcp.Security;

/// <summary>
/// Size limits for label content that did not come from a trusted tool call: a file opened from disk, imported ZPL, or a
/// string an assistant sent. They keep a crafted label from exhausting memory when it is rendered or turned into ZPL.
/// </summary>
public static class LimitChecks
{
    /// <summary>Most printed pixels one image element may produce (about 4000 x 4000 dots).</summary>
    public const long MaxImagePixels = 16_000_000;

    /// <summary>Most characters in one text or barcode content string.</summary>
    public const int MaxContentChars = 20_000;

    /// <summary>Most declared variables.</summary>
    public const int MaxVariables = 500;

    /// <summary>Throws a plain-language error when the label breaks a limit.</summary>
    public static void Verify(LabelDocument doc, McpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Elements.Count > settings.MaxElements)
            throw new McpException($"That label has {doc.Elements.Count} elements; the limit is {settings.MaxElements} (maxElements in rotula-mcp.settings.json).");
        if (doc.Variables.Count > MaxVariables)
            throw new McpException($"That label declares {doc.Variables.Count} variables; the limit is {MaxVariables}.");
        foreach (var e in doc.Elements)
        {
            switch (e)
            {
                case ImageElement { Source: not null } img:
                    var (w, h) = ImageProcessor.ResolveSize(img);
                    if ((long)w * h > MaxImagePixels)
                        throw new McpException($"The image \"{img.Id}\" would print {w} x {h} dots, which is more than the limit of {MaxImagePixels:N0} dots.");
                    break;
                case TextElement t when t.Text.Length > MaxContentChars:
                    throw new McpException($"The text of \"{t.Id}\" is longer than {MaxContentChars:N0} characters.");
                case BarcodeElement b when b.Data.Length > MaxContentChars:
                    throw new McpException($"The barcode data of \"{b.Id}\" is longer than {MaxContentChars:N0} characters.");
                case RawZplElement r when r.Zpl.Length > 2_000_000:
                    throw new McpException($"The raw ZPL block \"{r.Id}\" is larger than 2,000,000 characters.");
            }
        }
    }
}
