using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Core.Zpl;

/// <summary>Result of escaping text for a ^FD field.</summary>
/// <param name="Text">The text to place after ^FD.</param>
/// <param name="NeedsHexEscape">True when <see cref="Text"/> contains <c>_XX</c> escapes and ^FH must precede ^FD.</param>
/// <param name="Unmappable">Characters the legacy code page cannot represent (replaced by '?').</param>
public readonly record struct ZplEscapedText(string Text, bool NeedsHexEscape, IReadOnlyList<string> Unmappable);

/// <summary>Converts arbitrary text into ZPL-safe ^FD content using ^FH hex escapes.</summary>
public static class ZplText
{
    /// <summary>
    /// Escapes <paramref name="text"/>. Everything outside printable ASCII, plus the ZPL control
    /// characters <c>^ ~</c> and the escape character <c>_</c>, becomes <c>_XX</c> per encoded byte, so the
    /// generated ZPL is pure ASCII regardless of how the file or socket is later encoded.
    /// </summary>
    /// <param name="text">Text to escape.</param>
    /// <param name="encoding">Label encoding that decides which bytes are produced.</param>
    /// <param name="allowLineBreaks">When true, newlines become the ^FB line break <c>\&amp;</c>; otherwise they become spaces.</param>
    public static ZplEscapedText Escape(string text, ZplEncoding encoding, bool allowLineBreaks)
    {
        var enc = encoding.GetEncoding();
        var sb = new StringBuilder(text.Length + 8);
        bool needsHex = false;
        List<string>? unmappable = null;

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        for (int i = 0; i < normalized.Length;)
        {
            int runeLen = char.IsHighSurrogate(normalized[i]) && i + 1 < normalized.Length ? 2 : 1;
            string piece = normalized.Substring(i, runeLen);
            i += runeLen;

            if (piece == "\n")
            {
                // The ^FB line break is the literal two characters \& and must not be hex escaped.
                sb.Append(allowLineBreaks ? "\\&" : " ");
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = enc.GetBytes(piece);
            }
            catch (EncoderFallbackException)
            {
                (unmappable ??= new List<string>()).Add(piece);
                bytes = new[] { (byte)'?' };
            }

            foreach (byte b in bytes)
            {
                if (b is >= 0x20 and <= 0x7E && b is not ((byte)'^' or (byte)'~' or (byte)'_'))
                {
                    sb.Append((char)b);
                }
                else
                {
                    needsHex = true;
                    sb.Append('_').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
        }
        return new ZplEscapedText(sb.ToString(), needsHex, (IReadOnlyList<string>?)unmappable ?? Array.Empty<string>());
    }
}

/// <summary>Substitutes <c>{{name}}</c> placeholders with merge data or sample values. A leading <c>#</c> (for example <c>{{#Counter}}</c>) marks a virtual serial-number column.</summary>
public static class VariableResolver
{
    private static readonly Regex Token = new(@"\{\{\s*(#?[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Names of all placeholders in <paramref name="template"/>, in order of appearance (duplicates kept).</summary>
    public static IReadOnlyList<string> FindVariables(string template) =>
        Token.Matches(template).Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// Replaces every placeholder. Lookup order: merge data, then the declared sample value. An unknown
    /// name becomes an empty string and adds a <c>Variable.Missing</c> warning.
    /// </summary>
    public static string Resolve(string template, IReadOnlyDictionary<string, string>? data, IEnumerable<LabelVariable> declared, string? elementId, ICollection<ValidationIssue> issues) =>
        Resolve(template, data, declared, elementId, issues, null);

    /// <summary>
    /// Like the other overload, but <paramref name="literal"/> may supply the replacement text of a name before any lookup happens (null means "not mine").
    /// The generator uses it to write printer clock codes for date variables.
    /// </summary>
    public static string Resolve(string template, IReadOnlyDictionary<string, string>? data, IEnumerable<LabelVariable> declared, string? elementId, ICollection<ValidationIssue> issues, Func<string, string?>? literal)
    {
        if (template.IndexOf("{{", StringComparison.Ordinal) < 0) return template;
        var declaredList = declared as IList<LabelVariable> ?? declared.ToList();
        return Token.Replace(template, m =>
        {
            string name = m.Groups[1].Value;
            if (literal?.Invoke(name) is { } forced) return forced;
            if (TryLookup(name, data, declaredList, out var value)) return value;
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Variable.Missing", elementId, name));
            return string.Empty;
        });
    }

    /// <summary>
    /// Looks one name up the way a placeholder is resolved: the record data first, then the declared variable's sample value.
    /// Shared by placeholder substitution and by print conditions, so both always see the same value for a name.
    /// </summary>
    /// <param name="name">The field or variable name (no braces).</param>
    /// <param name="data">Record values, or null.</param>
    /// <param name="declared">Declared variables.</param>
    /// <param name="value">The value, or an empty string when the name is found nowhere.</param>
    /// <returns>True when the name was found.</returns>
    public static bool TryLookup(string name, IReadOnlyDictionary<string, string>? data, IEnumerable<LabelVariable> declared, out string value)
    {
        if (data is not null && data.TryGetValue(name, out var found))
        {
            value = found;
            return true;
        }
        var decl = declared.FirstOrDefault(v => v.Name == name);
        if (decl is not null)
        {
            value = decl.SampleValue;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
