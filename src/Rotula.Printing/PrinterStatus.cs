using System.Globalization;
using System.Text;

namespace Rotula.Printing;

/// <summary>A condition a ZPL compatible printer reports in its <c>~HS</c> host status reply.</summary>
public enum PrinterCondition
{
    /// <summary>The printhead is open (string 2, "head up" flag). Blocks printing.</summary>
    HeadOpen,
    /// <summary>The printer is out of labels or the media sensor sees no label (string 1, "paper out" flag). Blocks printing.</summary>
    PaperOut,
    /// <summary>The ribbon is out while the printer is in thermal transfer mode (string 2). Blocks printing.</summary>
    RibbonOut,
    /// <summary>The printhead is too hot (string 1, "over temperature" flag). Blocks printing.</summary>
    HeadTooHot,
    /// <summary>Communications diagnostics mode is on: the printer would print the received data as a dump instead of labels. Blocks printing.</summary>
    DiagnosticMode,
    /// <summary>The receive buffer is full (string 1). Blocks printing until the printer catches up.</summary>
    BufferFull,
    /// <summary>The printer is paused (string 1). Blocks printing until someone presses Pause or Feed.</summary>
    Paused,
    /// <summary>The printhead is too cold (string 1, "under temperature" flag). The printer still prints, possibly lighter.</summary>
    HeadTooCold,
    /// <summary>The printer lost its configuration (string 1, "corrupt RAM" flag) and runs on defaults.</summary>
    CorruptRam,
    /// <summary>A format is only partly received (string 1); another job may be arriving.</summary>
    PartialFormat,
    /// <summary>In peel-off mode a printed label waits to be taken (string 2).</summary>
    LabelWaiting,
}

/// <summary>The print mode a printer reports in string 2 of <c>~HS</c>.</summary>
public enum PrinterPrintMode
{
    /// <summary>A value this version does not know.</summary>
    Unknown,
    /// <summary>0: rewind.</summary>
    Rewind,
    /// <summary>1: peel-off.</summary>
    PeelOff,
    /// <summary>2: tear-off (the most common setting).</summary>
    TearOff,
    /// <summary>3: cutter.</summary>
    Cutter,
    /// <summary>4: applicator.</summary>
    Applicator,
    /// <summary>5: delayed cut.</summary>
    DelayedCut,
    /// <summary>6: linerless peel.</summary>
    LinerlessPeel,
    /// <summary>7: linerless rewind.</summary>
    LinerlessRewind,
    /// <summary>8: partial cutter.</summary>
    PartialCutter,
    /// <summary>9: RFID.</summary>
    Rfid,
    /// <summary>K: kiosk.</summary>
    Kiosk,
    /// <summary>S: stream.</summary>
    Stream,
}

/// <summary>Why a status could not be read. The UI maps the name to a plain-language reason.</summary>
public enum StatusUnavailableReason
{
    /// <summary>The transport cannot read replies (most USB drivers, or not running on Windows).</summary>
    NotSupported,
    /// <summary>Windows reports the queue as offline; nothing was sent so the query does not wait in the queue forever.</summary>
    Offline,
    /// <summary>The printer could not be reached (connection refused, unknown host, queue not found).</summary>
    Unreachable,
    /// <summary>The printer did not answer in time.</summary>
    NoReply,
    /// <summary>Something answered, but not with a host status reply.</summary>
    Unreadable,
}

/// <summary>
/// The decoded answer to <c>~HS</c>, or the reason there is none. Built by <see cref="HostStatusParser"/>; a value object with no
/// user-facing text. Only conditions the printer positively reported are listed, so a partial reply never invents a fault.
/// </summary>
public sealed class PrinterStatus
{
    private PrinterStatus() { }

    /// <summary>True when at least string 1 or string 2 of the reply was decoded.</summary>
    public bool IsAvailable => Reason is null;

    /// <summary>Why the status is unknown, or null when <see cref="IsAvailable"/>.</summary>
    public StatusUnavailableReason? Reason { get; private init; }

    /// <summary>True when both string 1 (media, buffer, temperature) and string 2 (head, ribbon, batch) were decoded.</summary>
    public bool IsComplete { get; private init; }

    /// <summary>Conditions the printer reported, most serious first.</summary>
    public IReadOnlyList<PrinterCondition> Conditions { get; private init; } = Array.Empty<PrinterCondition>();

    /// <summary>Formats received but not yet printed (string 1), or null when string 1 was not read.</summary>
    public int? FormatsInBuffer { get; private init; }

    /// <summary>Labels still to print in the current batch (string 2), or null when string 2 was not read.</summary>
    public long? LabelsRemaining { get; private init; }

    /// <summary>True when a format is only partly received (string 1), or null when unknown.</summary>
    public bool? PartialFormat { get; private init; }

    /// <summary>Label length the printer measured or was told, in dots (string 1), or null.</summary>
    public int? LabelLengthDots { get; private init; }

    /// <summary>True for thermal transfer (ribbon), false for direct thermal (string 2), or null.</summary>
    public bool? ThermalTransfer { get; private init; }

    /// <summary>Print mode (string 2), or null.</summary>
    public PrinterPrintMode? PrintMode { get; private init; }

    /// <summary>Number of graphic images stored in printer memory (string 2), or null.</summary>
    public int? GraphicsStored { get; private init; }

    /// <summary>The conditions that stop printing: head open, paper out, ribbon out, head too hot, diagnostics mode, buffer full, paused.</summary>
    public IReadOnlyList<PrinterCondition> Blocking => Conditions.Where(IsBlockingCondition).ToList();

    /// <summary>The reported conditions that do not stop printing (head too cold, lost configuration, partial format, label waiting).</summary>
    public IReadOnlyList<PrinterCondition> Warnings => Conditions.Where(c => !IsBlockingCondition(c)).ToList();

    /// <summary>True when the printer reported at least one condition that stops printing.</summary>
    public bool IsBlocked => Conditions.Any(IsBlockingCondition);

    /// <summary>
    /// True when the printer reports it has nothing left to do: no format in the buffer, no label left in the batch and no partial format.
    /// False when any of those is unknown, so an incomplete reply never counts as "finished".
    /// </summary>
    public bool IsIdle => FormatsInBuffer == 0 && LabelsRemaining == 0 && PartialFormat == false;

    /// <summary>True for the conditions that stop a printer from printing a new job.</summary>
    public static bool IsBlockingCondition(PrinterCondition condition) => condition <= PrinterCondition.Paused;

    /// <summary>A status that could not be read.</summary>
    public static PrinterStatus Unavailable(StatusUnavailableReason reason) => new() { Reason = reason };

    internal static PrinterStatus Decoded(string[]? s1, string[]? s2)
    {
        var found = new SortedSet<PrinterCondition>();
        int? formats = null, length = null, graphics = null;
        long? remaining = null;
        bool? partial = null, transfer = null;
        PrinterPrintMode? mode = null;

        if (s1 is not null)
        {
            // String 1: aaa,b,c,dddd,eee,f,g,h,iii,j,k,l
            if (Flag(s1[1])) found.Add(PrinterCondition.PaperOut);
            if (Flag(s1[2])) found.Add(PrinterCondition.Paused);
            length = Int(s1[3]);
            formats = Int(s1[4]);
            if (Flag(s1[5])) found.Add(PrinterCondition.BufferFull);
            if (Flag(s1[6])) found.Add(PrinterCondition.DiagnosticMode);
            partial = Flag(s1[7]);
            if (partial == true) found.Add(PrinterCondition.PartialFormat);
            if (Flag(s1[9])) found.Add(PrinterCondition.CorruptRam);
            if (Flag(s1[10])) found.Add(PrinterCondition.HeadTooCold);
            if (Flag(s1[11])) found.Add(PrinterCondition.HeadTooHot);
        }
        if (s2 is not null)
        {
            // String 2: mmm,n,o,p,q,r,s,t,uuuuuuuu,v,www
            if (Flag(s2[2])) found.Add(PrinterCondition.HeadOpen);
            transfer = Flag(s2[4]);
            // The ribbon sensor means nothing in direct thermal mode, so "ribbon out" only counts when a ribbon is in use.
            if (Flag(s2[3]) && transfer == true) found.Add(PrinterCondition.RibbonOut);
            mode = Mode(s2[5]);
            if (Flag(s2[7])) found.Add(PrinterCondition.LabelWaiting);
            remaining = long.TryParse(s2[8], NumberStyles.None, CultureInfo.InvariantCulture, out long r) ? r : null;
            graphics = Int(s2[10]);
        }
        return new PrinterStatus
        {
            IsComplete = s1 is not null && s2 is not null,
            Conditions = found.ToList(),
            FormatsInBuffer = formats,
            LabelsRemaining = remaining,
            PartialFormat = partial,
            LabelLengthDots = length,
            ThermalTransfer = transfer,
            PrintMode = mode,
            GraphicsStored = graphics,
        };
    }

    private static bool Flag(string field) => field == "1";

    private static int? Int(string field) => int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static PrinterPrintMode Mode(string field) => field.ToUpperInvariant() switch
    {
        "0" => PrinterPrintMode.Rewind,
        "1" => PrinterPrintMode.PeelOff,
        "2" => PrinterPrintMode.TearOff,
        "3" => PrinterPrintMode.Cutter,
        "4" => PrinterPrintMode.Applicator,
        "5" => PrinterPrintMode.DelayedCut,
        "6" => PrinterPrintMode.LinerlessPeel,
        "7" => PrinterPrintMode.LinerlessRewind,
        "8" => PrinterPrintMode.PartialCutter,
        "9" => PrinterPrintMode.Rfid,
        "K" => PrinterPrintMode.Kiosk,
        "S" => PrinterPrintMode.Stream,
        _ => PrinterPrintMode.Unknown,
    };
}

/// <summary>
/// Decodes the reply to the ZPL <c>~HS</c> (host status) command. The printer answers with three strings, each framed by STX (0x02) and
/// ETX (0x03) and followed by CR LF:
/// <code>
/// &lt;STX&gt;aaa,b,c,dddd,eee,f,g,h,iii,j,k,l&lt;ETX&gt;        string 1: interface, paper out, pause, label length, formats in buffer,
///                                                buffer full, diagnostics mode, partial format, unused, corrupt RAM,
///                                                under temperature, over temperature
/// &lt;STX&gt;mmm,n,o,p,q,r,s,t,uuuuuuuu,v,www&lt;ETX&gt;     string 2: function settings, unused, head up, ribbon out, thermal transfer,
///                                                print mode, print width mode, label waiting, labels remaining,
///                                                format while printing, graphics stored
/// &lt;STX&gt;xxxx,y&lt;ETX&gt;                               string 3: password, static RAM installed (not used by Rotula)
/// </code>
/// </summary>
/// <remarks>
/// Robustness: frames are recognised by their number of fields, so junk between frames, a missing string or strings in another order
/// do not matter. A frame without its ETX (a cut-off reply) or with a value that is not a number is ignored rather than guessed.
/// When the reply holds no STX at all (a gateway that strips control characters), each line is tried as a frame. When several
/// replies are in the buffer, the last complete one of each string wins.
/// </remarks>
public static class HostStatusParser
{
    /// <summary>The command that asks for the reply, as sent to the printer.</summary>
    public const string Command = "~HS";

    private const char Stx = '\u0002';
    private const char Etx = '\u0003';

    /// <summary>Decodes raw reply bytes (ASCII). Never throws.</summary>
    public static PrinterStatus Parse(ReadOnlySpan<byte> reply) => Parse(Encoding.Latin1.GetString(reply));

    /// <summary>Decodes a reply already read as text. Never throws; returns <see cref="StatusUnavailableReason.Unreadable"/> when nothing could be decoded.</summary>
    public static PrinterStatus Parse(string? reply)
    {
        if (string.IsNullOrEmpty(reply)) return PrinterStatus.Unavailable(StatusUnavailableReason.NoReply);
        string[]? s1 = null, s2 = null;
        foreach (var frame in Frames(reply))
        {
            var fields = frame.Split(',').Select(f => f.Trim()).ToArray();
            if (fields.Length == 12 && IsString1(fields)) s1 = fields;
            else if (fields.Length == 11 && IsString2(fields)) s2 = fields;
        }
        return s1 is null && s2 is null ? PrinterStatus.Unavailable(StatusUnavailableReason.Unreadable) : PrinterStatus.Decoded(s1, s2);
    }

    /// <summary>Counts the complete frames (STX..ETX) in a buffer, so a reader knows when all three strings have arrived.</summary>
    public static int CompleteFrames(ReadOnlySpan<byte> buffer)
    {
        int count = 0;
        bool open = false;
        foreach (byte b in buffer)
        {
            if (b == 0x02) open = true;
            else if (b == 0x03 && open) { count++; open = false; }
        }
        return count;
    }

    private static IEnumerable<string> Frames(string reply)
    {
        if (reply.IndexOf(Stx) < 0)
        {
            // No framing at all: try every line on its own.
            foreach (var line in reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                yield return new string(line.Where(c => !char.IsControl(c)).ToArray());
            yield break;
        }
        int start = -1;
        for (int i = 0; i < reply.Length; i++)
        {
            if (reply[i] == Stx) start = i + 1; // a second STX before an ETX restarts the frame: the first one was cut off
            else if (reply[i] == Etx && start >= 0)
            {
                yield return reply[start..i];
                start = -1;
            }
        }
    }

    private static bool IsString1(string[] f) => f.All(IsDigits);

    // The print mode (field 6) may be a letter (K, S); everything else is digits. The labels-remaining field is always eight digits, which
    // also keeps a damaged string 1 that lost a field (11 numbers too) from being read as string 2.
    private static bool IsString2(string[] f) =>
        f[8].Length == 8 && f.Select((v, i) => i == 5 ? v.Length == 1 && char.IsAsciiLetterOrDigit(v[0]) : IsDigits(v)).All(ok => ok);

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
