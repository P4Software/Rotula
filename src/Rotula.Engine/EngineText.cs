using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Rotula.Core.Validation;
using Rotula.Printing;

namespace Rotula.Engine;

/// <summary>
/// The engine's sentences in one of the shipped languages. The texts are the app's own resource files, embedded at build time, so a message
/// from the command line or the REST API reads exactly as the same message in the app. A key missing in a translation falls back to English.
/// </summary>
public sealed class EngineText
{
    /// <summary>The languages the engine speaks, in the canonical spelling of the app (English first).</summary>
    public static readonly IReadOnlyList<string> Languages = new[] { "en", "es", "pt-BR", "fr-CA" };

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tables = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, EngineText> Instances = new(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, string> _table;
    private readonly IReadOnlyDictionary<string, string> _english;

    private EngineText(string language)
    {
        Language = language;
        _table = Table(language);
        _english = Table("en");
    }

    /// <summary>The language of this instance (one of <see cref="Languages"/>).</summary>
    public string Language { get; }

    /// <summary>The English texts.</summary>
    public static EngineText English => For("en");

    /// <summary>
    /// The texts for a language name such as <c>es</c>, <c>es-MX</c>, <c>pt</c> or an HTTP <c>Accept-Language</c> value. Anything not shipped
    /// gives English, so a caller can never make the engine fail by asking for a language.
    /// </summary>
    public static EngineText For(string? language) => Instances.GetOrAdd(Resolve(language), l => new EngineText(l));

    /// <summary>Maps a requested language to a shipped one: exact match first, then the same base language, then English.</summary>
    public static string Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "en";
        // Accept-Language may list several ("es-MX,es;q=0.9,en;q=0.8"); the first one the engine speaks wins.
        foreach (var part in language.Split(','))
        {
            string tag = part.Split(';')[0].Trim().Replace('_', '-');
            if (tag.Length == 0) continue;
            var exact = Languages.FirstOrDefault(l => string.Equals(l, tag, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            string baseTag = tag.Split('-')[0];
            var family = Languages.FirstOrDefault(l => string.Equals(l.Split('-')[0], baseTag, StringComparison.OrdinalIgnoreCase));
            if (family is not null) return family;
        }
        return "en";
    }

    /// <summary>True when the key has a text (in this language or in English).</summary>
    public bool Has(string key) => _table.ContainsKey(key) || _english.ContainsKey(key);

    /// <summary>The text of a key formatted with <paramref name="args"/>; an unknown key comes back as the key itself so nothing is lost.</summary>
    public string Get(string key, params object[] args)
    {
        if (!_table.TryGetValue(key, out var text) && !_english.TryGetValue(key, out text)) return key;
        if (args.Length == 0) return text;
        try { return string.Format(CultureInfo.InvariantCulture, text, args); }
        catch (FormatException) { return text; }
    }

    /// <summary>Describes a validation issue or print notice the way the app does (<c>Issue.&lt;key&gt;</c>, keyword arguments translated).</summary>
    public string Describe(ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        string? arg = issue.MessageKey switch
        {
            PrinterStatusKeys.Warning or PrinterStatusKeys.FaultAfterJob => Conditions(issue.Args),
            PrinterStatusKeys.NotChecked or PrinterStatusKeys.NotVerified => Reason(issue.Args.FirstOrDefault()),
            PrinterStatusKeys.Verified => string.Empty,
            _ => null,
        };
        if (arg is not null) return Get("Issue." + issue.MessageKey, arg);
        string key = "Issue." + issue.MessageKey;
        // Rotula.Data and the file reader raise some notes under their own key, without the Issue. prefix.
        if (!Has(key) && Has(issue.MessageKey)) key = issue.MessageKey;
        if (!Has(key)) return Get("Issue.Unknown", issue.MessageKey);
        return Get(key, issue.Args.Select(a => (object)(Has("Issue.Arg." + a) ? Get("Issue.Arg." + a) : a)).ToArray());
    }

    /// <summary>Describes a print failure: what happened and what to do next (same wording as the app's print dialog).</summary>
    public string Describe(PrintError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        object[] args = error.Args.Cast<object>().ToArray();
        switch (error.Kind)
        {
            case PrintErrorKind.InvalidProfile:
                return Get(error.Key) + " " + string.Join(" ", error.Args.Where(Has).Select(k => Get(k)));
            case PrintErrorKind.Unknown:
                args = new object[] { string.Join(", ", error.Args.Where(a => a.Length > 0)) };
                break;
            case PrintErrorKind.HeadOpen or PrintErrorKind.PaperOut or PrintErrorKind.RibbonOut or PrintErrorKind.HeadTooHot
                or PrintErrorKind.DiagnosticMode or PrintErrorKind.BufferFull or PrintErrorKind.Paused:
            {
                // Arguments: the printer, then every condition it reported. The message names the worst one; the others follow.
                string main = Get(error.Key, error.Args.Count > 0 ? error.Args[0] : string.Empty, string.Empty);
                var others = error.Args.Skip(1).Where(c => !string.Equals(c, error.Kind.ToString(), StringComparison.Ordinal)).ToList();
                return others.Count == 0 ? main : main + " " + Get("Print.Status.AlsoReports", Conditions(others));
            }
        }
        if (args.Length < 2) args = args.Concat(Enumerable.Repeat((object)string.Empty, 2 - args.Length)).ToArray();
        return Has(error.Key) ? Get(error.Key, args) : Get("Print.Error.Unknown", string.Join(", ", error.Args));
    }

    /// <summary>Describes a decoded printer status in one or more sentences (ready, or what stops it, plus what it is doing).</summary>
    public string Describe(PrinterStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.IsAvailable) return Get("Print.Status.Unavailable", Reason(status.Reason!.Value.ToString()));
        var lines = new List<string>
        {
            status.IsBlocked ? Get("Print.Status.Blocked", Conditions(status.Blocking.Select(c => c.ToString()))) : Get("Print.Status.Ready"),
        };
        if (status.Warnings.Count > 0) lines.Add(Get("Print.Status.Warnings", Conditions(status.Warnings.Select(c => c.ToString()))));
        if (status.LabelsRemaining is > 0 || status.FormatsInBuffer is > 0)
            lines.Add(Get("Print.Status.Busy", status.LabelsRemaining ?? 0, status.FormatsInBuffer ?? 0));
        if (!status.IsComplete) lines.Add(Get("Print.Status.Partial"));
        return string.Join(" ", lines);
    }

    /// <summary>Joins condition names as a phrase ("out of labels, paused"); a name this version does not know is shown as it is.</summary>
    public string Conditions(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => Has("Enum.PrinterCondition." + n) ? Get("Enum.PrinterCondition." + n) : n));

    private string Reason(string? name) =>
        name is not null && Has("Print.Status.Reason." + name) ? Get("Print.Status.Reason." + name) : Get("Print.Status.Reason.Unknown");

    private static IReadOnlyDictionary<string, string> Table(string language) => Tables.GetOrAdd(language, Load);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Rotula.Engine.Strings." + language + ".xml");
            if (stream is null) return map;
            foreach (var data in XDocument.Load(stream).Descendants("data"))
            {
                string? name = (string?)data.Attribute("name");
                string? value = (string?)data.Element("value");
                if (name is not null && value is not null) map[name] = value;
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            // Without texts the engine still prints; messages then show their keys.
        }
        return map;
    }
}
