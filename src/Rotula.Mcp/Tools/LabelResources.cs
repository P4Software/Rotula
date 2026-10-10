using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rotula.Core.Zpl;
using Rotula.Files;
using Rotula.Mcp.Hosting;

namespace Rotula.Mcp.Tools;

/// <summary>The open label offered as readable resources.</summary>
[McpServerResourceType]
public sealed class LabelResources
{
    private readonly McpContext _ctx;

    /// <summary>Creates the resources over the shared context.</summary>
    public LabelResources(McpContext ctx) => _ctx = ctx;

    /// <summary>The ZPL of the open label.</summary>
    [McpServerResource(UriTemplate = "rotula://label/zpl", Name = "current_label_zpl", MimeType = "text/plain")]
    [Description("The ZPL II program of the label that is open now, regenerated each time it is read.")]
    public string CurrentZpl() => ZplGenerator.Generate(_ctx.Session.GetCopy().Document).Zpl;

    /// <summary>The .rotula JSON of the open label.</summary>
    [McpServerResource(UriTemplate = "rotula://label/json", Name = "current_label_json", MimeType = "application/json")]
    [Description("The open label in the .rotula file format (JSON): size, settings and every element with its id.")]
    public string CurrentJson()
    {
        var s = _ctx.Session.GetCopy();
        return RotulaFileSerializer.Serialize(s.Document, s.Merge);
    }
}
