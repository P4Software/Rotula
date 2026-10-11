using System.Text;
using Rotula.Core.Gs1;

namespace Rotula.Core.Zpl;

/// <summary>
/// How a <see cref="Gs1Message"/> is written into the field data of the ZPL commands that carry GS1 data, and how that data is read
/// back. Rotula stores GS1 data as the element string written with parentheses (<c>(01)09506000134352(10)ABC</c>); each command wants
/// its own spelling of the FNC1 characters.
/// </summary>
internal static class Gs1ZplData
{
    /// <summary>
    /// The escape character written in parameter g of ^BX when a GS1 DataMatrix is printed (the guide: "any character"). The defaults are
    /// not usable here: the tilde starts a ZPL control command, so it cannot stand in a parameter or in raw field data, and the underscore
    /// is the hex indicator of ^FH, which Rotula uses whenever a value holds an underscore, so "_1" would be read as a hex escape.
    /// The number sign is not in the GS1 character set, so it can never be part of a value and needs no doubling.
    /// </summary>
    public const char DataMatrixEscape = '#';

    // ---- Code 128, ^BC mode D ----------------------------------------------------------------------

    /// <summary>
    /// Field data for <c>^BC...,D</c> (UCC/EAN mode, the GS1-128 of the ZPL II Programming Guide): the printer starts the symbol with FNC1,
    /// accepts the parentheses (and prints them in the interpretation line), and expects <c>&gt;8</c> for the FNC1 between a
    /// variable-length element and the next one, as in the guide's sample <c>(91)0005886&gt;8(10)0000410549&gt;8(99)05</c>.
    /// A greater-than sign inside a value is written <c>&gt;&lt;</c>, the invocation code for that character.
    /// </summary>
    public static string ToCode128ModeD(Gs1Message message)
    {
        var sb = new StringBuilder();
        var els = message.Elements;
        for (int i = 0; i < els.Count; i++)
        {
            sb.Append('(').Append(els[i].Ai).Append(')').Append(els[i].Value.Replace(">", "><", StringComparison.Ordinal));
            if (i < els.Count - 1 && Gs1Message.NeedsSeparator(els[i].Ai)) sb.Append(">8");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads field data written for <c>^BC...,D</c> back into a message. Fails (false) unless the data is exactly what
    /// <see cref="ToCode128ModeD"/> would have written for the message it describes, so that nothing the printer would encode differently is
    /// silently normalised.
    /// </summary>
    public static bool TryFromCode128ModeD(string data, out Gs1Message? message)
    {
        message = null;
        var elements = new List<Gs1Element>();
        int i = 0;
        while (i < data.Length)
        {
            if (data[i] != '(') return false;
            int close = data.IndexOf(')', i);
            if (close < 0) return false;
            string ai = data.Substring(i + 1, close - i - 1);
            if (ai.Length is < 2 or > 4 || ai.Any(c => c is < '0' or > '9')) return false;
            var value = new StringBuilder();
            int j = close + 1;
            while (j < data.Length && data[j] != '(')
            {
                if (data[j] == '>')
                {
                    if (j + 1 >= data.Length) return false;
                    if (data[j + 1] == '<') { value.Append('>'); j += 2; continue; }
                    if (data[j + 1] == '8') { j += 2; if (j < data.Length && data[j] != '(') return false; break; }
                    return false;
                }
                value.Append(data[j++]);
            }
            elements.Add(new Gs1Element(ai, value.ToString()));
            i = j;
        }
        if (elements.Count == 0) return false;
        var parsed = new Gs1Message(elements);
        // The printer drops spaces and parentheses in mode D, so only characters of the GS1 set may stay.
        if (elements.Any(e => !e.Value.All(Gs1Linters.IsCset82))) return false;
        if (ToCode128ModeD(parsed) != data) return false;
        message = parsed;
        return true;
    }

    // ---- Data Matrix, ^BX quality 200 ---------------------------------------------------------------

    /// <summary>
    /// Field data for a GS1 DataMatrix with <c>^BX...,200,,,,#</c>: the escape sequence <c>#1</c> is FNC1 (ZPL II Programming Guide, ^BX,
    /// "_1 to _3 for FNC characters 1 to 3", with the escape character chosen in parameter g). One FNC1 starts the data, which marks the
    /// symbol as GS1, and another follows each element of variable length that is not the last.
    /// </summary>
    public static string ToDataMatrix(Gs1Message message)
    {
        string esc = DataMatrixEscape + "1";
        var sb = new StringBuilder(esc);
        var els = message.Elements;
        for (int i = 0; i < els.Count; i++)
        {
            sb.Append(els[i].Ai).Append(els[i].Value);
            if (i < els.Count - 1 && Gs1Message.NeedsSeparator(els[i].Ai)) sb.Append(esc);
        }
        return sb.ToString();
    }

    /// <summary>Reads GS1 DataMatrix field data (after ^FH was applied) back into a message; false when it is not exactly what <see cref="ToDataMatrix"/> writes.</summary>
    /// <param name="data">The field data.</param>
    /// <param name="escape">The escape character of parameter g of ^BX (the default is the tilde).</param>
    /// <param name="message">The message when the result is true.</param>
    public static bool TryFromDataMatrix(string data, char escape, out Gs1Message? message)
    {
        message = null;
        if (data.Length < 3 || data[0] != escape || data[1] != '1') return false;
        var sb = new StringBuilder();
        for (int i = 2; i < data.Length; i++)
        {
            if (data[i] != escape) { sb.Append(data[i]); continue; }
            if (i + 1 >= data.Length || data[i + 1] != '1') return false;
            sb.Append(Gs1Message.GroupSeparator);
            i++;
        }
        var parsed = Gs1Message.ParseElementString(sb.ToString(), out _);
        if (parsed is null) return false;
        // The escape character itself may differ from the tilde we write; compare in our spelling.
        if (ToDataMatrix(parsed).Replace(DataMatrixEscape, escape) != data) return false;
        message = parsed;
        return true;
    }
}
