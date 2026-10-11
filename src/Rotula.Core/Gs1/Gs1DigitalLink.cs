using System.Text;
using Rotula.Core.Validation;

namespace Rotula.Core.Gs1;

/// <summary>A GS1 Digital Link URI taken apart: where it points, the GS1 data in it, and any other query parameters.</summary>
/// <param name="Prefix">Scheme, host and any path in front of the key, for example <c>https://id.gs1.org</c>.</param>
/// <param name="Message">The key, its qualifiers and the data attributes, as GS1 elements (key first, then qualifiers in URI order, then attributes).</param>
/// <param name="OtherParameters">Query parameters that are not GS1 identifiers (for example a campaign tag), kept in order and decoded.</param>
public sealed record Gs1DigitalLinkUri(string Prefix, Gs1Message Message, IReadOnlyList<KeyValuePair<string, string>> OtherParameters);

/// <summary>
/// Builds and reads GS1 Digital Link URIs in the uncompressed form with numeric identifiers:
/// <c>https://id.gs1.org/01/09506000134352/10/ABC%2F1?17=251231</c>. The primary key and its qualifiers form the path (in the order the
/// dictionary prescribes), data attributes go into the query string, and every value is percent-encoded. Compressed URIs and the
/// alphabetic short names of the standard are not supported.
/// </summary>
public static class Gs1DigitalLink
{
    /// <summary>The resolver GS1 itself runs; any other domain you control works as well.</summary>
    public const string DefaultPrefix = "https://id.gs1.org";

    /// <summary>
    /// Writes <paramref name="message"/> as a Digital Link URI. The message needs exactly one primary key (GTIN, SSCC, GLN, ...); its
    /// qualifiers (lot, serial, ...) must be ones the dictionary allows for that key, and every other element must be allowed as a data
    /// attribute. Returns null and fills <paramref name="issues"/> when that is not the case.
    /// </summary>
    /// <param name="message">The GS1 data.</param>
    /// <param name="issues">The problems found; empty on success.</param>
    /// <param name="prefix">Scheme, host and optional path; no trailing slash needed.</param>
    public static string? Build(Gs1Message message, out IReadOnlyList<ValidationIssue> issues, string prefix = DefaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(message);
        var found = new List<ValidationIssue>();
        issues = found;
        found.AddRange(Gs1Validator.Validate(message));

        var keys = message.Elements.Where(e => Gs1Dictionary.Find(e.Ai)?.IsDigitalLinkKey == true).ToList();
        if (keys.Count != 1)
        {
            if (keys.Count == 0) found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.NoKey"));
            else found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.ManyKeys"));
            return null;
        }
        var key = keys[0];
        var keyDef = Gs1Dictionary.Get(key.Ai);
        var others = message.Elements.Where(e => e != key).ToList();

        // A qualifier is an identifier that some allowed sequence of this key lists; everything else must be a data attribute.
        var allQualifiers = keyDef.DigitalLinkQualifiers!.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);
        var qualifiers = others.Where(e => allQualifiers.Contains(e.Ai)).ToList();
        var attributes = others.Where(e => !allQualifiers.Contains(e.Ai)).ToList();
        foreach (var a in attributes.Where(a => Gs1Dictionary.Find(a.Ai)?.IsDigitalLinkAttribute != true))
            found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.NotAllowed", null, a.Ai));

        var order = ChooseSequence(keyDef.DigitalLinkQualifiers!, qualifiers.Select(q => q.Ai).ToList());
        if (order is null)
        {
            found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.QualifierCombination", null, key.Ai,
                string.Join(',', qualifiers.Select(q => q.Ai))));
            return null;
        }
        if (found.Any(i => i.Severity == ValidationSeverity.Error)) return null;

        var sb = new StringBuilder(prefix.TrimEnd('/'));
        sb.Append('/').Append(key.Ai).Append('/').Append(Uri.EscapeDataString(key.Value));
        foreach (var ai in order)
            foreach (var q in qualifiers.Where(q => q.Ai == ai))
                sb.Append('/').Append(q.Ai).Append('/').Append(Uri.EscapeDataString(q.Value));
        for (int i = 0; i < attributes.Count; i++)
            sb.Append(i == 0 ? '?' : '&').Append(attributes[i].Ai).Append('=').Append(Uri.EscapeDataString(attributes[i].Value));
        return sb.ToString();
    }

    /// <summary>
    /// Picks the qualifier sequence that holds every present qualifier and returns those in the sequence's order; an empty list when there are
    /// none; null when no allowed sequence contains them all (the standard forbids mixing sequences and reordering).
    /// </summary>
    private static List<string>? ChooseSequence(IReadOnlyList<IReadOnlyList<string>> sequences, List<string> present)
    {
        if (present.Count == 0) return new List<string>();
        foreach (var seq in sequences)
            if (present.All(seq.Contains)) return seq.Where(present.Contains).ToList();
        return null;
    }

    /// <summary>
    /// Reads a Digital Link URI. Returns null and fills <paramref name="issues"/> when it is not an http(s) URI with a primary key in the
    /// path. The qualifiers must follow the key in the order the dictionary allows. A GTIN of 8, 12 or 13 digits is padded to 14.
    /// </summary>
    public static Gs1DigitalLinkUri? Parse(string uriText, out IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(uriText);
        var found = new List<ValidationIssue>();
        issues = found;
        if (!Uri.TryCreate(uriText.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.Uri"));
            return null;
        }

        // Splitting the escaped path keeps an encoded slash (%2F) inside its value.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int keyIndex = -1;
        for (int i = 0; i + 1 < segments.Length; i++)
            if (Gs1Dictionary.Find(segments[i])?.IsDigitalLinkKey == true) { keyIndex = i; break; }
        if (keyIndex < 0)
        {
            found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.NoKey"));
            return null;
        }
        if ((segments.Length - keyIndex) % 2 != 0)
        {
            found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.OddSegments"));
            return null;
        }

        var elements = new List<Gs1Element>();
        for (int i = keyIndex; i < segments.Length; i += 2)
        {
            string ai = segments[i], value = Uri.UnescapeDataString(segments[i + 1]);
            if (!Gs1Dictionary.IsKnown(ai)) { found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Ai.Unknown", null, ai)); return null; }
            if (ai == "01" && value.Length is 8 or 12 or 13 && value.All(char.IsAsciiDigit)) value = value.PadLeft(14, '0');
            elements.Add(new Gs1Element(ai, value));
        }

        var keyDef = Gs1Dictionary.Get(elements[0].Ai);
        var qualifierAis = elements.Skip(1).Select(e => e.Ai).ToList();
        // The qualifiers must be a subsequence of one allowed sequence, in that order (dictionary note on dlpkey).
        if (qualifierAis.Count > 0 && !keyDef.DigitalLinkQualifiers!.Any(seq => IsSubsequence(qualifierAis, seq)))
            found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.QualifierOrder", null, keyDef.Ai, string.Join(',', qualifierAis)));

        var other = new List<KeyValuePair<string, string>>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            var def = name.All(char.IsAsciiDigit) ? Gs1Dictionary.Find(name) : null;
            if (def is null) { other.Add(new KeyValuePair<string, string>(name, value)); continue; }
            if (!def.IsDigitalLinkAttribute) found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.DigitalLink.NotAllowed", null, def.Ai));
            elements.Add(new Gs1Element(def.Ai, value));
        }

        var message = new Gs1Message(elements);
        found.AddRange(Gs1Validator.Validate(message));
        string prefix = uri.GetLeftPart(UriPartial.Authority) + (keyIndex > 0 ? "/" + string.Join('/', segments.Take(keyIndex)) : string.Empty);
        return new Gs1DigitalLinkUri(prefix, message, other);
    }

    private static bool IsSubsequence(List<string> sub, IReadOnlyList<string> seq)
    {
        int at = 0;
        foreach (var s in sub)
        {
            while (at < seq.Count && seq[at] != s) at++;
            if (at >= seq.Count) return false;
            at++;
        }
        return true;
    }
}
