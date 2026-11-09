namespace Rotula.Mcp.Security;

/// <summary>A printer-changing ZPL command found in a job.</summary>
/// <param name="Name">The command as ZPL spells it, upper case: <c>~JA</c>, <c>^ID</c>.</param>
/// <param name="Meaning">What it does to the printer, in plain words.</param>
/// <param name="Count">How many times it occurs in the job.</param>
/// <param name="Example">The first occurrence with its parameters, cut at the next command or line end (for example <c>^IDE:*.*</c>).</param>
public sealed record RiskyCommand(string Name, string Meaning, int Count, string? Example);

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
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        ["~JA"] = "cancels every print job the printer is holding",
        ["~JB"] = "resets the printer's optional memory (erases what is stored in it)",
        ["~JC"] = "starts a media sensor calibration (the printer feeds labels and changes its sensor settings)",
        ["~JD"] = "switches on diagnostics mode (the printer prints the data it receives as text instead of labels)",
        ["~JL"] = "sets the label length by feeding labels",
        ["~JP"] = "pauses the printer and cancels the format being printed",
        ["~JR"] = "restarts the printer (power-on reset)",
        ["~JX"] = "cancels the format the printer is receiving",
        ["~PP"] = "pauses printing",
        ["~PS"] = "starts or resumes printing",
        ["^JJ"] = "changes the printer's job control (automatic cancel, pause and error handling)",
        ["^JU"] = "updates the printer's saved configuration, including a reset to factory settings (^JUF)",
        ["^ID"] = "deletes stored objects (fonts, graphics, formats); a wildcard such as ^IDE:*.* deletes many at once",
        ["~EG"] = "erases all downloaded graphics",
        ["^EG"] = "erases all downloaded graphics",
        ["^TO"] = "copies stored objects from one memory to another",
        ["~DY"] = "downloads a file (font, graphic or firmware) into the printer's memory and can replace what is stored there",
        ["~DB"] = "downloads a bitmap font into the printer's memory",
        ["~DS"] = "downloads a scalable font into the printer's memory",
        ["~DT"] = "downloads a TrueType font into the printer's memory",
        ["~DU"] = "downloads an unbounded TrueType font into the printer's memory",
        ["~DE"] = "downloads an encoding table into the printer's memory",
        ["~DG"] = "stores a graphic in the printer's permanent (flash) memory, which wears out with every write",
        ["^DF"] = "stores a format in the printer instead of printing it, and can replace a stored format of the same name",
        ["^CW"] = "assigns a font letter to a stored font, changing how later text is drawn",
        ["~WC"] = "prints a configuration label",
        ["~RO"] = "resets the printer's odometer and label counters",
        ["^MP"] = "changes which front-panel modes are protected",
        ["^MM"] = "changes the print mode (tear-off, peel, cutter...)",
        ["^MF"] = "changes what the printer does with the media at power-up and when the head is closed",
        ["~SD"] = "sets the print darkness, which stays set for later jobs",
        ["~TA"] = "changes the tear-off position, which stays set for later jobs",
        ["^SS"] = "changes the media sensor settings",
        ["^SZ"] = "switches the printer's command language, after which it may misread later jobs",
        ["^ST"] = "sets the printer's clock",
        ["^KP"] = "changes the printer's password",
        ["^KN"] = "changes the printer's name",
        ["^KL"] = "changes the language of the printer's display",
        ["^NI"] = "changes the printer's network identity",
        ["^NS"] = "changes the printer's network settings",
        ["^ND"] = "changes the printer's network settings",
        ["^NN"] = "changes the printer's SNMP network settings",
        ["^NP"] = "changes the printer's primary network device",
        ["^NT"] = "changes the printer's network (SMTP) settings",
        ["^NW"] = "changes the printer's web authentication settings",
        ["^NB"] = "changes how the printer looks for its wired network at start-up",
        ["~NC"] = "connects the printer to a network",
        ["~NR"] = "makes the network printers transparent to this one",
        ["~NT"] = "makes the connected network printer transparent",
        ["^WA"] = "changes the printer's wireless antenna settings",
        ["^WE"] = "changes the printer's wireless encryption settings",
        ["^WI"] = "changes the printer's wireless network settings",
        ["^WL"] = "changes the printer's wireless authentication (LEAP) settings",
        ["^WP"] = "changes the printer's wireless password settings",
        ["^WR"] = "changes the printer's wireless transmit rate",
        ["^WS"] = "changes the printer's wireless settings",
        // Changing the command characters means the text after it cannot be scanned for commands any more.
        ["^CC"] = "changes the character that starts commands, so later commands in this job cannot be read or checked",
        ["~CC"] = "changes the character that starts commands, so later commands in this job cannot be read or checked",
        ["^CT"] = "changes the character that starts control commands, so later commands in this job cannot be read or checked",
        ["~CT"] = "changes the character that starts control commands, so later commands in this job cannot be read or checked",
        ["^CD"] = "changes the character that separates parameters, so later commands in this job cannot be read or checked",
        ["~CD"] = "changes the character that separates parameters, so later commands in this job cannot be read or checked",
    };

    /// <summary>Flash devices of a Zebra printer; <c>R:</c> (RAM) is lost at power-off and <c>Z:</c> is read-only.</summary>
    private static readonly char[] FlashDevices = ['A', 'B', 'E'];

    /// <summary>Scans the whole text. The result lists each command once, in the order it first appears.</summary>
    /// <param name="zpl">The ZPL that will be sent, or a raw block of it.</param>
    public static IReadOnlyList<RiskyCommand> Scan(string zpl)
    {
        ArgumentNullException.ThrowIfNull(zpl);
        var found = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var order = new List<string>();
        for (int i = 0; i + 2 < zpl.Length; i++)
        {
            char prefix = zpl[i];
            if (prefix != '^' && prefix != '~') continue;
            string key = string.Concat(prefix, char.ToUpperInvariant(zpl[i + 1]), char.ToUpperInvariant(zpl[i + 2]));
            if (!Table.ContainsKey(key)) continue;
            // A graphic stored in RAM (the default device) is routine; only the permanent memory is a concern.
            if (key == "~DG" && !TargetsFlash(zpl, i + 3)) continue;

            if (found.TryGetValue(key, out var seen)) found[key] = (seen.Count + 1, seen.Example);
            else
            {
                found[key] = (1, ExampleAt(zpl, i));
                order.Add(key);
            }
        }
        return order.Select(k => new RiskyCommand(k, Table[k], found[k].Count, found[k].Example)).ToList();
    }

    /// <summary>True when the device letter after a <c>~DG</c> names a flash memory (<c>E:</c>, <c>B:</c>, <c>A:</c>).</summary>
    private static bool TargetsFlash(string zpl, int afterCommand) =>
        afterCommand + 1 < zpl.Length && zpl[afterCommand + 1] == ':' && FlashDevices.Contains(char.ToUpperInvariant(zpl[afterCommand]));

    /// <summary>The command with its parameters, cut at the next command start or line end and at a fixed length.</summary>
    private static string ExampleAt(string zpl, int start)
    {
        int end = start + 3;
        while (end < zpl.Length && end - start < ExampleLength && zpl[end] is not ('^' or '~' or '\r' or '\n')) end++;
        return zpl[start..end];
    }
}
