using Microsoft.Extensions.Logging;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Data;
using Rotula.Mcp.Security;
using Rotula.Mcp.Session;
using Rotula.Mcp.Settings;
using Rotula.Printing;

namespace Rotula.Mcp.Hosting;

/// <summary>A validation finding in plain language, as returned to the assistant.</summary>
/// <param name="Severity">"error" (cannot print correctly) or "warning" (may not look as intended).</param>
/// <param name="Message">What is wrong and how to fix it.</param>
/// <param name="Key">Stable message key, for tools that want to react to a specific problem.</param>
/// <param name="ElementId">The element the finding belongs to, or null for the whole label.</param>
public sealed record IssueInfo(string Severity, string Message, string Key, string? ElementId);

/// <summary>
/// Everything the tool classes share: the settings, the path guard, the working label, the print gate and the services they
/// talk to. One instance lives for the whole server run. Tests build their own with fake printers and temporary folders.
/// </summary>
public sealed class McpContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="settings">The effective settings.</param>
    /// <param name="print">The print service (a fake in tests; never used unless printing is allowed).</param>
    /// <param name="connections">Opens the saved-connection store when a database tool needs it.</param>
    /// <param name="clock">Time source for confirmation tokens.</param>
    /// <param name="logger">Where non-secret diagnostics go; may be null.</param>
    public McpContext(McpSettings settings, IPrintService print, Func<IConnectionStore>? connections = null,
        Func<DateTimeOffset>? clock = null, ILogger? logger = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Print = print ?? throw new ArgumentNullException(nameof(print));
        Connections = connections ?? (() => new JsonConnectionStore());
        Guard = new PathGuard(settings);
        Session = new LabelSession(settings.MaxElements, d => LimitChecks.Verify(d, settings));
        Gate = new PrintGate(settings, clock);
        Logger = logger;
    }

    /// <summary>The effective settings.</summary>
    public McpSettings Settings { get; }

    /// <summary>Validates every file path.</summary>
    public PathGuard Guard { get; }

    /// <summary>The working label.</summary>
    public LabelSession Session { get; }

    /// <summary>The print confirmation state machine.</summary>
    public PrintGate Gate { get; }

    /// <summary>Printer access.</summary>
    public IPrintService Print { get; }

    /// <summary>Opens the saved database connections.</summary>
    public Func<IConnectionStore> Connections { get; }

    /// <summary>Diagnostics sink (stderr / log file); never receives label contents unless debug logging is on.</summary>
    public ILogger? Logger { get; }

    /// <summary>Runs the generator and returns its findings in plain language, optionally only those of one element.</summary>
    public static IReadOnlyList<IssueInfo> Validate(LabelDocument doc, string? onlyElementId = null, IReadOnlyDictionary<string, string>? data = null)
    {
        var result = ZplGenerator.Generate(doc, data);
        return result.Issues
            .Where(i => onlyElementId is null || i.ElementId == onlyElementId)
            .Select(ToInfo)
            .ToList();
    }

    /// <summary>Converts a Core validation issue to the plain-language form.</summary>
    public static IssueInfo ToInfo(ValidationIssue i) =>
        new(i.Severity == ValidationSeverity.Error ? "error" : "warning", Messages.Describe(i), i.MessageKey, i.ElementId);
}
