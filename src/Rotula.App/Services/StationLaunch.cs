namespace Rotula.App.Services;

/// <summary>What the program was started for.</summary>
public enum LaunchMode
{
    /// <summary>The designer (optionally opening a label given as the only argument).</summary>
    Designer,
    /// <summary>The print station (<c>--station [folder-or-file]</c>).</summary>
    Station,
    /// <summary>The tool that makes a station exit PIN (<c>--station-pin</c>).</summary>
    StationPin,
}

/// <summary>The command line, read once at start-up.</summary>
/// <param name="Mode">What to open.</param>
/// <param name="Path">The label (designer) or the station folder, configuration or label (station); null when none was given.</param>
public sealed record StationLaunch(LaunchMode Mode, string? Path)
{
    /// <summary>The option that starts the print station.</summary>
    public const string StationOption = "--station";

    /// <summary>The option that starts the PIN tool.</summary>
    public const string PinOption = "--station-pin";

    /// <summary>
    /// Reads the arguments. <c>--station</c> may be followed by a folder, a station.json or a .rotula file (without one, the shared policy file is used);
    /// <c>--station-pin</c> opens the PIN tool; anything else is the designer with the first argument as the label to open. Options ignore case and may
    /// also be written <c>/station</c>, as Windows shortcuts often do, or <c>--station=C:\Labels</c>.
    /// </summary>
    public static StationLaunch Parse(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i].Trim();
            if (IsOption(a, PinOption)) return new StationLaunch(LaunchMode.StationPin, null);
            if (IsOption(a, StationOption))
            {
                string? path = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
                return new StationLaunch(LaunchMode.Station, string.IsNullOrWhiteSpace(path) ? null : path.Trim());
            }
            if (a.StartsWith(StationOption + "=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("/station=", StringComparison.OrdinalIgnoreCase))
            {
                string path = a[(a.IndexOf('=') + 1)..].Trim().Trim('"');
                return new StationLaunch(LaunchMode.Station, path.Length == 0 ? null : path);
            }
        }
        return new StationLaunch(LaunchMode.Designer, args.Count > 0 ? args[0] : null);
    }

    private static bool IsOption(string arg, string option) =>
        string.Equals(arg, option, StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "/" + option[2..], StringComparison.OrdinalIgnoreCase);
}
