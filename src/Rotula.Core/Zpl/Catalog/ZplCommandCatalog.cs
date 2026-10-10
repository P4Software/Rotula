using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rotula.Core.Zpl.Catalog;

/// <summary>What Rotula does with a ZPL command.</summary>
public enum ZplCommandStatus
{
    /// <summary>The command is understood: it is read into the document model and written back from it.</summary>
    Supported,

    /// <summary>
    /// The command can appear in a label file and affects printing, but is not modelled. It is kept verbatim and the user is
    /// warned (except for harmless comments), so it is never dropped.
    /// </summary>
    Passthrough,

    /// <summary>Not modelled yet, but meant to be. Behaves like <see cref="Passthrough"/> today.</summary>
    Planned,

    /// <summary>
    /// A label designer will never model it (printer administration, queries, network, RFID, kiosk hardware).
    /// Behaves like <see cref="Passthrough"/> when a file contains it; <see cref="ZplCommandInfo.Reason"/> says why.
    /// </summary>
    OutOfScope,
}

/// <summary>How well the on-screen preview shows the effect of a command.</summary>
public enum ZplCommandPreview
{
    /// <summary>The preview draws exactly what the printer prints.</summary>
    Exact,

    /// <summary>The preview is close but not identical (stand-in fonts, a first-label value, a fixed bar pitch).</summary>
    Approximate,

    /// <summary>The preview shows a labelled box because the printer draws the content.</summary>
    Placeholder,

    /// <summary>The command changes the printed label, but the preview ignores it.</summary>
    NotPreviewed,

    /// <summary>The command has no visible effect on the label (a setting, a comment, a structure marker).</summary>
    NotVisual,
}

/// <summary>One ZPL II command and what Rotula does with it.</summary>
public sealed class ZplCommandInfo
{
    /// <summary>Unique id: the first prefix plus the code, for example <c>^BC</c> or <c>~DG</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The command letters without a prefix, for example <c>BC</c>.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary><c>^</c>, <c>~</c> or <c>^~</c> when the same command exists with both prefixes.</summary>
    public string Prefixes { get; init; } = "^";

    /// <summary>Short name in Rotula's own words.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Group, one of <see cref="ZplCommandCatalog.Groups"/>.</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>What Rotula does with the command.</summary>
    public ZplCommandStatus Status { get; init; }

    /// <summary>For <see cref="ZplCommandStatus.OutOfScope"/>: why the command is not modelled; otherwise null.</summary>
    public string? Reason { get; init; }

    /// <summary>True when <see cref="ZplGenerator"/> writes the command under this name.</summary>
    public bool Generator { get; init; }

    /// <summary>True when <see cref="Rotula.Core.Zpl.Import.ZplParser"/> interprets the command into the document model.</summary>
    public bool Parser { get; init; }

    /// <summary>How well the preview shows the command.</summary>
    public ZplCommandPreview Preview { get; init; }

    /// <summary>One or two plain sentences saying what the command does (English). Meant for an in-app reference.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// A small valid snippet that uses the command, written to sit inside <c>^XA ... ^XZ</c>. Tests parse it to check the
    /// status. Empty for commands without a useful example.
    /// </summary>
    public string Example { get; init; } = string.Empty;

    /// <summary>Limits, quirks and how the command round-trips; may be empty.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>The id as users type it, for display: <c>^BC</c>, or <c>^PH / ~PH</c> for a command with both prefixes.</summary>
    public string DisplayId => Prefixes.Length == 2 ? "^" + Code + " / ~" + Code : Id;
}

/// <summary>
/// The catalogue of every ZPL II command with Rotula's status for it. The data lives in <c>commands.json</c> (an embedded
/// resource); tests check it against the real parser and generator, so it cannot drift from the code, and
/// <c>docs/zpl-coverage.md</c> is generated from it.
/// </summary>
public static class ZplCommandCatalog
{
    private static readonly Lazy<Loaded> Data = new(Load);

    private sealed record Loaded(IReadOnlyList<string> Groups, IReadOnlyList<ZplCommandInfo> Commands);

    private sealed class Document
    {
        public List<string> Groups { get; set; } = new();
        public List<ZplCommandInfo> Commands { get; set; } = new();
    }

    /// <summary>Every command, grouped in display order.</summary>
    public static IReadOnlyList<ZplCommandInfo> All => Data.Value.Commands;

    /// <summary>The group names in display order.</summary>
    public static IReadOnlyList<string> Groups => Data.Value.Groups;

    /// <summary>Finds a command by id (<c>^BC</c>) or by code with either prefix (<c>~BC</c> finds a command listed with both prefixes). Null when unknown.</summary>
    public static ZplCommandInfo? Find(string idOrCode)
    {
        if (string.IsNullOrWhiteSpace(idOrCode)) return null;
        string text = idOrCode.Trim().ToUpperInvariant();
        foreach (var c in All)
        {
            if (c.Id == text) return c;
            if (text.Length >= 2 && text[0] is '^' or '~' && c.Code == text[1..] && c.Prefixes.Contains(text[0])) return c;
        }
        return null;
    }

    /// <summary>
    /// Searches the name, code and summary for every word of <paramref name="query"/> (case-insensitive). An empty query
    /// returns everything. Meant to feed a searchable reference.
    /// </summary>
    public static IReadOnlyList<ZplCommandInfo> Search(string? query, ZplCommandStatus? status = null)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return All.Where(c => (status is null || c.Status == status)
            && words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || c.Code.Contains(w.TrimStart('^', '~'), StringComparison.OrdinalIgnoreCase)
                || c.Summary.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static Loaded Load()
    {
        var assembly = typeof(ZplCommandCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("Rotula.Core.Zpl.Catalog.commands.json")
            ?? throw new InvalidOperationException("The ZPL command catalogue is missing from the assembly.");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new JsonStringEnumConverter() },
        };
        var doc = JsonSerializer.Deserialize<Document>(stream, options)
            ?? throw new InvalidOperationException("The ZPL command catalogue is empty.");
        return new Loaded(doc.Groups, doc.Commands);
    }
}
