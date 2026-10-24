namespace Rotula.Cli;

/// <summary>
/// The parsed command line: the command, its positional arguments, options with values (<c>--printer NAME</c> or <c>--printer=NAME</c>,
/// repeatable ones such as <c>--set</c> keep every value) and switches (<c>--json</c>). Hand-written so the program needs no parser package.
/// </summary>
internal sealed class CommandLine
{
    /// <summary>Options that take a value, for every command.</summary>
    internal static readonly IReadOnlySet<string> ValueOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "printer", "host", "port", "copies", "data", "sheet", "set", "answer", "dpi", "darkness", "speed", "output", "reference", "lang",
        "settings", "counter-file", "urls", "label-folder",
    };

    /// <summary>Switches, for every command.</summary>
    internal static readonly IReadOnlySet<string> Switches = new HashSet<string>(StringComparer.Ordinal)
    {
        "json", "dry-run", "no-status", "no-verify", "help", "version", "init",
    };

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _switches = new(StringComparer.Ordinal);

    /// <summary>The command (first word), lower case; empty when none was given.</summary>
    public string Command { get; private set; } = string.Empty;

    /// <summary>Words after the command that are not options.</summary>
    public List<string> Positionals { get; } = new();

    /// <summary>The last value of an option, or null.</summary>
    public string? Value(string name) => _values.TryGetValue(name, out var list) && list.Count > 0 ? list[^1] : null;

    /// <summary>Every value of a repeatable option.</summary>
    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var list) ? list : [];

    /// <summary>True when the switch was given.</summary>
    public bool Has(string name) => _switches.Contains(name);

    /// <summary>
    /// Parses <paramref name="args"/>. Returns null on success, or the message key and argument of the problem (an unknown option, an option
    /// without its value).
    /// </summary>
    public static (CommandLine Line, string? ErrorKey, string? ErrorArg) Parse(IReadOnlyList<string> args)
    {
        var line = new CommandLine();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                string name = a[2..], value;
                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                    if (!ValueOptions.Contains(name)) return (line, "Engine.Cli.UnknownOption", "--" + name);
                }
                else if (Switches.Contains(name))
                {
                    line._switches.Add(name);
                    continue;
                }
                else if (ValueOptions.Contains(name))
                {
                    if (i + 1 >= args.Count) return (line, "Engine.Cli.MissingValue", a);
                    value = args[++i];
                }
                else
                {
                    return (line, "Engine.Cli.UnknownOption", a);
                }
                if (!line._values.TryGetValue(name, out var list)) line._values[name] = list = new List<string>();
                list.Add(value);
            }
            else if (a is "-h" or "/?")
            {
                line._switches.Add("help");
            }
            else if (line.Command.Length == 0)
            {
                line.Command = a.ToLowerInvariant();
            }
            else
            {
                line.Positionals.Add(a);
            }
        }
        return (line, null, null);
    }

    /// <summary>
    /// Reads repeatable <c>name=value</c> pairs (<c>--set</c>, <c>--answer</c>). Returns null and the bad pair when one has no name.
    /// </summary>
    public static Dictionary<string, string>? Pairs(IReadOnlyList<string> raw, out string? bad)
    {
        bad = null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in raw)
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) { bad = pair; return null; }
            map[pair[..eq].Trim()] = pair[(eq + 1)..];
        }
        return map;
    }
}
