namespace Rotula.Printing;

/// <summary>
/// What kind of harm a printer-changing command can do. The desktop app words each kind in the user's language; the English
/// <see cref="RiskyCommand.Meaning"/> stays for the MCP server, which speaks English only.
/// </summary>
public enum RiskCategory
{
    /// <summary>Cancels, pauses or restarts print jobs.</summary>
    JobControl,
    /// <summary>Deletes data stored in the printer's memory.</summary>
    DeleteStored,
    /// <summary>Writes fonts, graphics or formats into the printer's memory (flash memory wears out).</summary>
    WriteStored,
    /// <summary>Changes printer settings that outlast this job.</summary>
    Settings,
    /// <summary>Prints diagnostic or configuration output instead of the label.</summary>
    Diagnostics,
    /// <summary>Changes network or wireless settings, which can cut the printer off.</summary>
    Network,
    /// <summary>Starts the printer's built-in BASIC interpreter.</summary>
    Programs,
    /// <summary>Changes the command characters, so the rest of the text cannot be checked.</summary>
    CommandCharacters,
}

/// <summary>A printer-changing ZPL command found in a job.</summary>
/// <param name="Name">The command as ZPL spells it, upper case: <c>~JA</c>, <c>^ID</c>.</param>
/// <param name="Meaning">What it does to the printer, in plain words (English).</param>
/// <param name="Count">How many times it occurs in the job.</param>
/// <param name="Example">The first occurrence with its parameters, cut at the next command or line end (for example <c>^IDE:*.*</c>).</param>
/// <param name="Category">The kind of effect, so a user interface can describe it in the user's language.</param>
public sealed record RiskyCommand(string Name, string Meaning, int Count, string? Example, [property: System.Text.Json.Serialization.JsonIgnore] RiskCategory Category = RiskCategory.Settings);

/// <summary>
/// Finds ZPL commands that do more than print a label: they cancel or erase things, change the printer's settings, or store objects in its
/// memory. A job from an untrusted file (imported ZPL, a downloaded .rotula) can carry such commands, and the person who confirms a print
/// must be told about them by name, not only see the start of the text.
/// <para>
/// The scan reads the <b>entire</b> text that will be sent. It deliberately does not try to skip binary payloads (<c>~DY</c>, <c>~DG</c>,
/// <c>^GF</c>): a scanner that trusts a length written in the payload's own header can be told to skip the very bytes that hold the
/// command. Reading everything can only over-report, which costs one extra acknowledgement; skipping can hide a command.
/// </para>
/// <para>
/// ZPL accepts lower-case command letters, so the match ignores case. The characters that introduce commands can be changed with
/// <c>^CC</c>, <c>~CT</c> and <c>^CD</c>, after which the rest of the text cannot be read reliably, so those commands are flagged themselves.
/// </para>
/// </summary>
public static class PrinterCommandScanner
{
    private const int ExampleLength = 40;

    // Keyed by prefix plus the two command characters, upper case. Only commands that change or erase something on the printer, or
    // that make the rest of the text unreadable to this scan, are listed; ordinary label commands (^FO, ^FD, ^BC, ^PQ...) are not.
    private static readonly Dictionary<string, (RiskCategory Category, string Meaning)> Table = new(StringComparer.Ordinal)
    {
        ["~JA"] = (RiskCategory.JobControl, "cancels every print job the printer is holding"),
        ["~JB"] = (RiskCategory.DeleteStored, "resets the printer's optional memory (erases what is stored in it)"),
        ["~JC"] = (RiskCategory.Settings, "starts a media sensor calibration (the printer feeds labels and changes its sensor settings)"),
        ["~JD"] = (RiskCategory.Diagnostics, "switches on diagnostics mode (the printer prints the data it receives as text instead of labels)"),
        ["~JL"] = (RiskCategory.Settings, "sets the label length by feeding labels"),
        ["~JP"] = (RiskCategory.JobControl, "pauses the printer and cancels the format being printed"),
        ["~JR"] = (RiskCategory.JobControl, "restarts the printer (power-on reset)"),
        ["~JX"] = (RiskCategory.JobControl, "cancels the format the printer is receiving"),
        ["~PP"] = (RiskCategory.JobControl, "pauses printing"),
        ["~PS"] = (RiskCategory.JobControl, "starts or resumes printing"),
        ["^JJ"] = (RiskCategory.JobControl, "changes the printer's job control (automatic cancel, pause and error handling)"),
        ["^JU"] = (RiskCategory.Settings, "updates the printer's saved configuration, including a reset to factory settings (^JUF)"),
        ["^ID"] = (RiskCategory.DeleteStored, "deletes stored objects (fonts, graphics, formats); a wildcard such as ^IDE:*.* deletes many at once"),
        ["~EG"] = (RiskCategory.DeleteStored, "erases all downloaded graphics"),
        ["^EG"] = (RiskCategory.DeleteStored, "erases all downloaded graphics"),
        ["^TO"] = (RiskCategory.WriteStored, "copies stored objects from one memory to another"),
        ["~DY"] = (RiskCategory.WriteStored, "downloads a file (font, graphic or firmware) into the printer's memory and can replace what is stored there"),
        ["~DB"] = (RiskCategory.WriteStored, "downloads a bitmap font into the printer's memory"),
        ["~DS"] = (RiskCategory.WriteStored, "downloads a scalable font into the printer's memory"),
        ["~DT"] = (RiskCategory.WriteStored, "downloads a TrueType font into the printer's memory"),
        ["~DU"] = (RiskCategory.WriteStored, "downloads an unbounded TrueType font into the printer's memory"),
        ["~DE"] = (RiskCategory.WriteStored, "downloads an encoding table into the printer's memory"),
        ["~DG"] = (RiskCategory.WriteStored, "stores a graphic in the printer's permanent (flash) memory, which wears out with every write"),
        ["^DF"] = (RiskCategory.WriteStored, "stores a format in the printer instead of printing it, and can replace a stored format of the same name"),
        ["^CW"] = (RiskCategory.Settings, "assigns a font letter to a stored font, changing how later text is drawn"),
        ["~WC"] = (RiskCategory.Diagnostics, "prints a configuration label"),
        ["~RO"] = (RiskCategory.Settings, "resets the printer's odometer and label counters"),
        ["^MP"] = (RiskCategory.Settings, "changes which front-panel modes are protected"),
        ["^MM"] = (RiskCategory.Settings, "changes the print mode (tear-off, peel, cutter...)"),
        ["^MF"] = (RiskCategory.Settings, "changes what the printer does with the media at power-up and when the head is closed"),
        ["~SD"] = (RiskCategory.Settings, "sets the print darkness, which stays set for later jobs"),
        ["~TA"] = (RiskCategory.Settings, "changes the tear-off position, which stays set for later jobs"),
        ["^SS"] = (RiskCategory.Settings, "changes the media sensor settings"),
        ["^SZ"] = (RiskCategory.Settings, "switches the printer's command language, after which it may misread later jobs"),
        ["^ST"] = (RiskCategory.Settings, "sets the printer's clock"),
        ["^KP"] = (RiskCategory.Settings, "changes the printer's password"),
        ["^KN"] = (RiskCategory.Settings, "changes the printer's name"),
        ["^KL"] = (RiskCategory.Settings, "changes the language of the printer's display"),
        ["^NI"] = (RiskCategory.Network, "changes the printer's network identity"),
        ["^NS"] = (RiskCategory.Network, "changes the printer's network settings"),
        ["^ND"] = (RiskCategory.Network, "changes the printer's network settings"),
        ["^NN"] = (RiskCategory.Network, "changes the printer's SNMP network settings"),
        ["^NP"] = (RiskCategory.Network, "changes the printer's primary network device"),
        ["^NT"] = (RiskCategory.Network, "changes the printer's network (SMTP) settings"),
        ["^NW"] = (RiskCategory.Network, "changes the printer's web authentication settings"),
        ["^NB"] = (RiskCategory.Network, "changes how the printer looks for its wired network at start-up"),
        ["~NC"] = (RiskCategory.Network, "connects the printer to a network"),
        ["~NR"] = (RiskCategory.Network, "makes the network printers transparent to this one"),
        ["~NT"] = (RiskCategory.Network, "makes the connected network printer transparent"),
        ["^WA"] = (RiskCategory.Network, "changes the printer's wireless antenna settings"),
        ["^WE"] = (RiskCategory.Network, "changes the printer's wireless encryption settings"),
        ["^WI"] = (RiskCategory.Network, "changes the printer's wireless network settings"),
        ["^WL"] = (RiskCategory.Network, "changes the printer's wireless authentication (LEAP) settings"),
        ["^WP"] = (RiskCategory.Network, "changes the printer's wireless password settings"),
        ["^WR"] = (RiskCategory.Network, "changes the printer's wireless transmit rate"),
        ["^WS"] = (RiskCategory.Network, "changes the printer's wireless settings"),
        ["^CM"] = (RiskCategory.Settings, "remaps the memory letters (R:, E:, B:, A:), so a later store to RAM can land in permanent memory"),
        ["^IS"] = (RiskCategory.WriteStored, "saves an image into the printer's memory"),
        ["^JI"] = (RiskCategory.Programs, "starts the printer's built-in BASIC interpreter (ZBI), which can run programs"),
        ["~JI"] = (RiskCategory.Programs, "starts the printer's built-in BASIC interpreter (ZBI), which can run programs"),
        ["^SX"] = (RiskCategory.Settings, "changes where the printer sends its alert messages"),
        ["^SC"] = (RiskCategory.Settings, "changes the printer's serial port settings, which can cut off communication with it"),
        ["^ZZ"] = (RiskCategory.Settings, "puts the printer to sleep or changes its idle time-out"),
        ["~WR"] = (RiskCategory.Network, "resets the printer's wireless card"),
        ["^XS"] = (RiskCategory.Settings, "is a printer set-up command that can change how the printer behaves for later jobs"),
        // Changing the command characters means the text after it cannot be scanned for commands any more.
        ["^CC"] = (RiskCategory.CommandCharacters, "changes the character that starts commands, so later commands in this job cannot be read or checked"),
        ["~CC"] = (RiskCategory.CommandCharacters, "changes the character that starts commands, so later commands in this job cannot be read or checked"),
        ["^CT"] = (RiskCategory.CommandCharacters, "changes the character that starts control commands, so later commands in this job cannot be read or checked"),
        ["~CT"] = (RiskCategory.CommandCharacters, "changes the character that starts control commands, so later commands in this job cannot be read or checked"),
        ["^CD"] = (RiskCategory.CommandCharacters, "changes the character that separates parameters, so later commands in this job cannot be read or checked"),
        ["~CD"] = (RiskCategory.CommandCharacters, "changes the character that separates parameters, so later commands in this job cannot be read or checked"),
    };

    /// <summary>Flash devices of a Zebra printer; <c>R:</c> (RAM) is lost at power-off and <c>Z:</c> is read-only.</summary>
    private static readonly char[] FlashDevices = ['A', 'B', 'E'];

    /// <summary>Scans the whole text. The result lists each command once, in the order it first appears.</summary>
    /// <param name="zpl">The ZPL that will be sent, or a raw block of it.</param>
    public static IReadOnlyList<RiskyCommand> Scan(string zpl)
    {
        ArgumentNullException.ThrowIfNull(zpl);
        // Printers ignore carriage returns and line feeds in the command stream, so "~J" + line break + "A" is a ~JA. Reading the text
        // with them removed catches that; the price is a possible extra report for two commands that merely meet at a line end.
        if (zpl.Contains('\r') || zpl.Contains('\n')) zpl = zpl.Replace("\r", "").Replace("\n", "");
        // ^CM can make "R:" mean a flash drive, so once it appears no device letter can be trusted.
        bool memoryRemapped = zpl.Contains("^CM", StringComparison.OrdinalIgnoreCase) || zpl.Contains("~CM", StringComparison.OrdinalIgnoreCase);
        var found = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var order = new List<string>();
        for (int i = 0; i + 2 < zpl.Length; i++)
        {
            char prefix = zpl[i];
            if (prefix != '^' && prefix != '~') continue;
            string key = string.Concat(prefix, char.ToUpperInvariant(zpl[i + 1]), char.ToUpperInvariant(zpl[i + 2]));
            if (!Table.ContainsKey(key)) continue;
            // A graphic stored in RAM (the default device) is routine; only the permanent memory is a concern.
            if (key == "~DG" && !memoryRemapped && !TargetsFlash(zpl, i + 3)) continue;

            if (found.TryGetValue(key, out var seen)) found[key] = (seen.Count + 1, seen.Example);
            else
            {
                found[key] = (1, ExampleAt(zpl, i));
                order.Add(key);
            }
        }
        return order.Select(k => new RiskyCommand(k, Table[k].Meaning, found[k].Count, found[k].Example, Table[k].Category)).ToList();
    }

    /// <summary>True when the device letter after a <c>~DG</c> names a flash memory (<c>E:</c>, <c>B:</c>, <c>A:</c>).</summary>
    private static bool TargetsFlash(string zpl, int afterCommand) =>
        afterCommand + 1 < zpl.Length && zpl[afterCommand + 1] == ':' && FlashDevices.Contains(char.ToUpperInvariant(zpl[afterCommand]));

    /// <summary>The command with its parameters, cut at the next command start or line end and at a fixed length.</summary>
    private static string ExampleAt(string zpl, int start)
    {
        int end = start + 3;
        while (end < zpl.Length && end - start < ExampleLength && zpl[end] is not ('^' or '~')) end++;
        return zpl[start..end];
    }
}
