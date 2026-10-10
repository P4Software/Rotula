using System.Globalization;
using System.Numerics;

namespace Rotula.Core.Merge;

/// <summary>Kind of serial number produced by a <see cref="SerialCounter"/>.</summary>
public enum CounterKind
{
    /// <summary>Decimal number: Start + index * Step, zero-padded to Padding digits.</summary>
    Numeric,
    /// <summary>
    /// Class-preserving odometer: every character of Start keeps its class (digit 0-9, upper-case letter A-Z,
    /// lower-case letter a-z) and counts within it, with carry to the left. So <c>A9</c> + 1 = <c>B0</c>,
    /// <c>AZ</c> + 1 = <c>BA</c>, <c>Z9</c> + 1 overflows.
    /// </summary>
    Alphanumeric,
}

/// <summary>What happens when a counter runs past the capacity of its format.</summary>
public enum CounterOverflow
{
    /// <summary>Wrap around to the start of the range (like an odometer rolling over).</summary>
    Wrap,
    /// <summary>Report the record as invalid (<c>Merge.Counter.Overflow</c>) instead of printing a duplicate serial.</summary>
    Error,
}

/// <summary>
/// A deterministic serial-number generator exposed to labels as the virtual column <c>#Name</c>
/// (placeholder <c>{{#Name}}</c>). The value for record <c>i</c> (0-based) depends only on the settings and <c>i</c>,
/// so previews, batches and re-runs always agree.
/// </summary>
/// <remarks>
/// Capacity: Numeric with Padding &gt; 0 holds 10^Padding values (0..10^Padding-1); Numeric with Padding 0 is
/// unbounded except for the 64-bit range. Alphanumeric holds the product of the position radices (10 per digit,
/// 26 per letter). Values outside the capacity wrap or fail according to <see cref="Overflow"/>.
/// Prefix and suffix are added outside the counted part and never affect the capacity.
/// </remarks>
public sealed class SerialCounter
{
    /// <summary>Counter name; the placeholder is <c>{{#Name}}</c>. Letters, digits and underscore.</summary>
    public string Name { get; set; } = "Counter";

    /// <summary>Numeric or alphanumeric.</summary>
    public CounterKind Kind { get; set; } = CounterKind.Numeric;

    /// <summary>First value: a decimal integer for Numeric, a string of [0-9A-Za-z] for Alphanumeric.</summary>
    public string Start { get; set; } = "1";

    /// <summary>Increment per record. May be negative or zero (a constant); alphanumeric counts the same way.</summary>
    public int Step { get; set; } = 1;

    /// <summary>Minimum number of digits for Numeric (zero-padded). Ignored by Alphanumeric, where Start fixes the width.</summary>
    public int Padding { get; set; }

    /// <summary>Text placed before the counted part.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Text placed after the counted part.</summary>
    public string Suffix { get; set; } = string.Empty;

    /// <summary>Overflow behaviour; wraps by default.</summary>
    public CounterOverflow Overflow { get; set; } = CounterOverflow.Wrap;

    /// <summary>The key under which the value appears in merge data: <c>#Name</c>.</summary>
    public string VirtualColumn => "#" + Name;

    /// <summary>
    /// Checks the settings. Returns a stable message key (<c>Merge.Counter.Name</c>, <c>.Start</c>, <c>.Padding</c>)
    /// or null when valid.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrEmpty(Name) || !(char.IsAsciiLetter(Name[0]) || Name[0] == '_') || !Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return "Merge.Counter.Name";
        if (Padding < 0 || Padding > 18) return "Merge.Counter.Padding";
        if (Kind == CounterKind.Numeric)
            return long.TryParse(Start, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? null : "Merge.Counter.Start";
        return Start.Length > 0 && Start.All(char.IsAsciiLetterOrDigit) ? null : "Merge.Counter.Start";
    }

    /// <summary>
    /// Value for the record at zero-based <paramref name="index"/>. Returns false only when the value is out of
    /// range and <see cref="Overflow"/> is <see cref="CounterOverflow.Error"/> (or an unpadded numeric counter leaves the 64-bit range).
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings are invalid (see <see cref="Validate"/>).</exception>
    public bool TryGetValue(long index, out string value)
    {
        if (Validate() is { } bad) throw new InvalidOperationException("Invalid counter settings: " + bad);
        value = string.Empty;
        BigInteger position = ParseStart() + (BigInteger)index * Step;

        BigInteger capacity = Capacity();
        if (capacity > 0)
        {
            if (position < 0 || position >= capacity)
            {
                if (Overflow == CounterOverflow.Error) return false;
                // Euclidean modulo so negative steps wrap to the top of the range instead of going negative.
                position = ((position % capacity) + capacity) % capacity;
            }
        }
        else if (position < long.MinValue || position > long.MaxValue)
        {
            // No meaningful wrap point without padding, so the 64-bit limit always fails regardless of mode.
            return false;
        }

        value = Prefix + Format(position) + Suffix;
        return true;
    }

    /// <summary>Value for <paramref name="index"/>, throwing <see cref="OverflowException"/> on error-mode overflow.</summary>
    public string ValueAt(long index) =>
        TryGetValue(index, out var v) ? v : throw new OverflowException("Counter '" + Name + "' overflowed at record index " + index);

    // Number of distinct values, or 0 for "unbounded" (Numeric without padding).
    private BigInteger Capacity()
    {
        if (Kind == CounterKind.Numeric) return Padding == 0 ? BigInteger.Zero : BigInteger.Pow(10, Padding);
        BigInteger cap = BigInteger.One;
        foreach (char c in Start) cap *= Radix(c);
        return cap;
    }

    private static int Radix(char c) => char.IsAsciiDigit(c) ? 10 : 26;

    private BigInteger ParseStart()
    {
        if (Kind == CounterKind.Numeric) return long.Parse(Start, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        BigInteger v = BigInteger.Zero;
        foreach (char c in Start)
        {
            int digit = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiLetterUpper(c) ? c - 'A' : c - 'a';
            v = v * Radix(c) + digit;
        }
        return v;
    }

    private string Format(BigInteger position)
    {
        if (Kind == CounterKind.Numeric)
        {
            string digits = BigInteger.Abs(position).ToString(CultureInfo.InvariantCulture).PadLeft(Padding, '0');
            return position.Sign < 0 ? "-" + digits : digits;
        }
        var chars = new char[Start.Length];
        for (int i = Start.Length - 1; i >= 0; i--)
        {
            char template = Start[i];
            int radix = Radix(template);
            int d = (int)(position % radix);
            position /= radix;
            chars[i] = char.IsAsciiDigit(template) ? (char)('0' + d) : char.IsAsciiLetterUpper(template) ? (char)('A' + d) : (char)('a' + d);
        }
        return new string(chars);
    }
}
