using System.Reflection;
using System.Xml.Linq;
using Rotula.Core.Validation;
using Rotula.Printing;

namespace Rotula.Mcp.Hosting;

/// <summary>
/// Turns the message keys that Rotula's libraries return (validation issues, print errors, import warnings) into plain
/// English sentences. The texts are the app's own English resource file, embedded at build time so both stay in sync; the
/// server therefore needs no resource assemblies and no WPF.
/// </summary>
public static class Messages
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Table = new(Load);

    /// <summary>Number of texts available (0 means the resource could not be read; keys are then shown as they are).</summary>
    public static int Count => Table.Value.Count;

    /// <summary>Looks a text up by resource key, or null.</summary>
    public static string? Lookup(string key) => Table.Value.TryGetValue(key, out var v) ? v : null;

    /// <summary>Formats the sentence for a validation issue, translating keyword arguments such as <c>odd</c> and <c>even</c>.</summary>
    public static string Describe(ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return Format("Issue." + issue.MessageKey, issue.MessageKey, issue.Args, translateArgs: true);
    }

    /// <summary>Formats a key with positional arguments; unknown keys fall back to the key plus its arguments.</summary>
    public static string Format(string key, string fallbackName, IReadOnlyList<string> args, bool translateArgs = false)
    {
        var values = args.Select(a => (object)(translateArgs && Lookup("Issue.Arg." + a) is { } t ? t : a)).ToArray();
        if (Lookup(key) is { } text)
        {
            try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, text, values); }
            catch (FormatException) { return text; }
        }
        return args.Count == 0 ? fallbackName : $"{fallbackName} ({string.Join(", ", args)})";
    }

    /// <summary>Describes a print failure in plain language.</summary>
    public static string Describe(PrintError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Format(error.Key, error.Kind.ToString(), error.Args);
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Rotula.Mcp.Strings.en.xml");
            if (stream is null) return map;
            var doc = XDocument.Load(stream);
            foreach (var data in doc.Descendants("data"))
            {
                string? name = (string?)data.Attribute("name");
                string? value = (string?)data.Element("value");
                if (name is not null && value is not null) map[name] = value;
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            // Without texts the server still works; messages then show their keys.
        }
        return map;
    }
}
