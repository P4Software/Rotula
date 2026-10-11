using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Validation;

namespace Rotula.Core.Barcodes.QrContent;

/// <summary>
/// The content of a QR Code in one of the formats that phones act on: a web address, Wi-Fi settings, a contact card, an e-mail, a phone
/// call, an SMS, a map position or a calendar event. Each kind checks its own fields and writes the exact text the QR Code must carry.
/// Everything is culture-neutral: numbers and dates are written the same way on every computer.
/// </summary>
public interface IQrContent
{
    /// <summary>A short name of the kind for pickers and logs, for example <c>Wi-Fi</c>.</summary>
    string Kind { get; }

    /// <summary>
    /// Checks the fields. Errors are problems that make the content unusable; warnings are things a phone may reject or display badly.
    /// Message keys are <c>QrContent.*</c>.
    /// </summary>
    IReadOnlyList<ValidationIssue> Validate();

    /// <summary>The text to put in the QR Code.</summary>
    /// <exception cref="InvalidOperationException">The fields have errors (see <see cref="Validate"/>).</exception>
    string Build();
}

/// <summary>Shared pieces of the content builders.</summary>
internal static class QrText
{
    internal const string CrLf = "\r\n";

    /// <summary>
    /// True while a template is being written (see <c>QrContentBuilder.BuildTemplate</c>): fields still hold <c>{{placeholders}}</c>, so nothing
    /// is validated or normalised, and placeholders are kept readable. Thread-local, set only around the building of a template.
    /// </summary>
    [ThreadStatic] internal static bool Lenient;

    private static readonly Regex PlaceholderToken = new(@"\{\{\s*#?[A-Za-z_][A-Za-z0-9_]*\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Percent-encodes <paramref name="value"/> (everything except letters, digits and - . _ ~); in a template the placeholders are left as they are.</summary>
    internal static string Pct(string value)
    {
        if (!Lenient) return Uri.EscapeDataString(value);
        var sb = new StringBuilder();
        int at = 0;
        foreach (Match m in PlaceholderToken.Matches(value))
        {
            sb.Append(Uri.EscapeDataString(value[at..m.Index])).Append(m.Value);
            at = m.Index + m.Length;
        }
        return sb.Append(Uri.EscapeDataString(value[at..])).ToString();
    }

    internal static ValidationIssue Err(string key, params string[] args) => new(ValidationSeverity.Error, key, null, args);

    /// <summary>Throws when <paramref name="content"/> has an error; otherwise returns normally.</summary>
    internal static void ThrowIfInvalid(IQrContent content)
    {
        if (Lenient) return;
        var errors = content.Validate().Where(i => i.Severity == ValidationSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException($"The {content.Kind} content has errors: " + string.Join(", ", errors.Select(e => e.MessageKey)));
    }

    /// <summary>
    /// Escapes a value of a vCard or iCalendar text property: backslash, semicolon and comma get a backslash, and a line break becomes
    /// <c>\n</c> (RFC 2426 section 5 / RFC 5545 section 3.3.11).
    /// </summary>
    internal static string EscapeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Folds a content line at 75 octets: the rest continues on the next line after a single space. Octets are UTF-8 bytes, and a character
    /// is never split across two lines (RFC 2426 section 2.6, RFC 5545 section 3.1).
    /// </summary>
    internal static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75) return line;
        var sb = new StringBuilder();
        int limit = 75, used = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            int size = rune.Utf8SequenceLength;
            if (used + size > limit)
            {
                sb.Append(CrLf).Append(' ');
                // The continuation line starts with the space, which counts toward its 75 octets.
                used = 1;
                limit = 75;
            }
            sb.Append(rune.ToString());
            used += size;
        }
        return sb.ToString();
    }

    internal static string Join(IEnumerable<string> lines) => string.Join(CrLf, lines.Select(Fold));

    internal static bool IsEmail(string s) => Regex.IsMatch(s, @"^[^@\s,;:<>()\[\]\\""]+@[^@\s,;:<>()\[\]\\""]+\.[^@\s,;:<>()\[\]\\""]+$", RegexOptions.CultureInvariant);
}

/// <summary>A web address.</summary>
/// <param name="Url">An absolute address such as <c>https://example.com/menu</c>.</param>
public sealed record QrUrlContent(string Url) : IQrContent
{
    /// <inheritdoc />
    public string Kind => "URL";

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        string url = (Url ?? string.Empty).Trim();
        if (url.Length == 0) issues.Add(QrText.Err("QrContent.Url.Empty"));
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is "file" || url.Any(char.IsWhiteSpace))
            issues.Add(QrText.Err("QrContent.Url.Invalid"));
        else if (uri.Scheme is not ("http" or "https"))
            // Phones open other schemes only when an app claims them; a warning, not an error.
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "QrContent.Url.Scheme", null, uri.Scheme));
        return issues;
    }

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        return Url.Trim();
    }
}

/// <summary>The security type of a Wi-Fi network.</summary>
public enum WifiSecurity
{
    /// <summary>WPA, WPA2 or WPA3 personal (written <c>WPA</c>, which phones accept for all of them).</summary>
    Wpa,
    /// <summary>WPA3 with simultaneous authentication of equals (written <c>SAE</c>).</summary>
    Wpa3Sae,
    /// <summary>WEP.</summary>
    Wep,
    /// <summary>An open network without a password (written <c>nopass</c>).</summary>
    None,
}

/// <summary>Wi-Fi settings: <c>WIFI:T:WPA;S:ssid;P:password;H:false;;</c>.</summary>
/// <param name="Ssid">The network name, 1 to 32 bytes.</param>
/// <param name="Password">The password; ignored for open networks.</param>
/// <param name="Security">The security type.</param>
/// <param name="Hidden">True when the network does not broadcast its name.</param>
public sealed record QrWifiContent(string Ssid, string Password = "", WifiSecurity Security = WifiSecurity.Wpa, bool Hidden = false) : IQrContent
{
    /// <inheritdoc />
    public string Kind => "Wi-Fi";

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        string ssid = Ssid ?? string.Empty, pass = Password ?? string.Empty;
        if (ssid.Length == 0) issues.Add(QrText.Err("QrContent.Wifi.Ssid.Empty"));
        else if (Encoding.UTF8.GetByteCount(ssid) > 32) issues.Add(QrText.Err("QrContent.Wifi.Ssid.TooLong", "32"));
        switch (Security)
        {
            case WifiSecurity.None:
                if (pass.Length > 0) issues.Add(new ValidationIssue(ValidationSeverity.Warning, "QrContent.Wifi.Password.Ignored"));
                break;
            case WifiSecurity.Wep:
                if (pass.Length == 0) issues.Add(QrText.Err("QrContent.Wifi.Password.Missing"));
                // WEP keys are 5 or 13 characters, or 10 or 26 hexadecimal digits.
                else if (pass.Length is not (5 or 13) && !(pass.Length is 10 or 26 && pass.All(Uri.IsHexDigit)))
                    issues.Add(QrText.Err("QrContent.Wifi.Password.Length", "5, 13, 10 or 26"));
                break;
            default:
                if (pass.Length == 0) issues.Add(QrText.Err("QrContent.Wifi.Password.Missing"));
                // WPA passphrases are 8 to 63 characters, or exactly 64 hexadecimal digits.
                else if (!(pass.Length is >= 8 and <= 63) && !(pass.Length == 64 && pass.All(Uri.IsHexDigit)))
                    issues.Add(QrText.Err("QrContent.Wifi.Password.Length", "8 to 63"));
                break;
        }
        return issues;
    }

    // Backslash, semicolon, comma, colon and double quote are the characters of the syntax, so a backslash goes in front of each.
    private static string Escape(string v) => Regex.Replace(Regex.Replace(v, @"[\r\n]+", " "), @"([\\;,:""])", "\\$1");

    // A value that is only hexadecimal digits would be read as a hex-encoded name or key, so it is quoted (ZXing wiki, "Barcode Contents").
    internal static string Field(string v) => v.Length > 0 && v.All(Uri.IsHexDigit) ? "\"" + Escape(v) + "\"" : Escape(v);

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        string type = Security switch { WifiSecurity.None => "nopass", WifiSecurity.Wep => "WEP", WifiSecurity.Wpa3Sae => "SAE", _ => "WPA" };
        var sb = new StringBuilder("WIFI:T:").Append(type).Append(";S:").Append(Field(Ssid)).Append(';');
        if (Security != WifiSecurity.None) sb.Append("P:").Append(Field(Password)).Append(';');
        sb.Append("H:").Append(Hidden ? "true" : "false").Append(";;");
        return sb.ToString();
    }
}

/// <summary>A postal address of a contact.</summary>
/// <param name="Street">Street and number.</param>
/// <param name="City">City.</param>
/// <param name="Region">State, province or region.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="Country">Country.</param>
public sealed record QrAddress(string Street = "", string City = "", string Region = "", string PostalCode = "", string Country = "")
{
    /// <summary>True when every part is empty.</summary>
    public bool IsEmpty => Street.Length + City.Length + Region.Length + PostalCode.Length + Country.Length == 0;
}

/// <summary>A contact card in vCard 3.0 format (RFC 2426), with CRLF line ends and lines folded at 75 octets.</summary>
public sealed record QrVCardContent : IQrContent
{
    /// <summary>First name.</summary>
    public string FirstName { get; init; } = string.Empty;
    /// <summary>Last name.</summary>
    public string LastName { get; init; } = string.Empty;
    /// <summary>Company or organisation.</summary>
    public string Organization { get; init; } = string.Empty;
    /// <summary>Job title.</summary>
    public string Title { get; init; } = string.Empty;
    /// <summary>Work phone.</summary>
    public string Phone { get; init; } = string.Empty;
    /// <summary>Mobile phone.</summary>
    public string Mobile { get; init; } = string.Empty;
    /// <summary>E-mail address.</summary>
    public string Email { get; init; } = string.Empty;
    /// <summary>Web site.</summary>
    public string Url { get; init; } = string.Empty;
    /// <summary>Work address.</summary>
    public QrAddress Address { get; init; } = new();
    /// <summary>Free note.</summary>
    public string Note { get; init; } = string.Empty;

    /// <inheritdoc />
    public string Kind => "vCard";

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        // FN (formatted name) is mandatory in vCard 3.0; it is built from the name, or from the organisation for a company card.
        if (FormattedName().Length == 0) issues.Add(QrText.Err("QrContent.VCard.NameMissing"));
        if (Email.Length > 0 && !QrText.IsEmail(Email)) issues.Add(QrText.Err("QrContent.Email.Address"));
        foreach (var number in new[] { Phone, Mobile })
            if (number.Length > 0 && !QrPhoneContent.TryNormalize(number, out _)) issues.Add(QrText.Err("QrContent.Phone.Invalid"));
        if (Url.Length > 0 && (!Uri.TryCreate(Url, UriKind.Absolute, out _) || Url.Any(char.IsWhiteSpace))) issues.Add(QrText.Err("QrContent.Url.Invalid"));
        return issues;
    }

    private string FormattedName() => string.Join(' ', new[] { FirstName, LastName }.Where(s => s.Length > 0)) is { Length: > 0 } n ? n : Organization;

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        var lines = new List<string> { "BEGIN:VCARD", "VERSION:3.0" };
        // N is family;given;additional;prefix;suffix.
        lines.Add("N:" + QrText.EscapeText(LastName) + ";" + QrText.EscapeText(FirstName) + ";;;");
        lines.Add("FN:" + QrText.EscapeText(FormattedName()));
        if (Organization.Length > 0) lines.Add("ORG:" + QrText.EscapeText(Organization));
        if (Title.Length > 0) lines.Add("TITLE:" + QrText.EscapeText(Title));
        if (Phone.Length > 0 && QrPhoneContent.TryNormalize(Phone, out var work)) lines.Add("TEL;TYPE=WORK,VOICE:" + work);
        if (Mobile.Length > 0 && QrPhoneContent.TryNormalize(Mobile, out var cell)) lines.Add("TEL;TYPE=CELL,VOICE:" + cell);
        if (Email.Length > 0) lines.Add("EMAIL;TYPE=INTERNET:" + Email);
        if (Url.Length > 0) lines.Add("URL:" + Url);
        if (!Address.IsEmpty)
            // ADR is post office box;extended;street;locality;region;postal code;country.
            lines.Add("ADR;TYPE=WORK:;;" + string.Join(';', new[] { Address.Street, Address.City, Address.Region, Address.PostalCode, Address.Country }.Select(QrText.EscapeText)));
        if (Note.Length > 0) lines.Add("NOTE:" + QrText.EscapeText(Note));
        lines.Add("END:VCARD");
        return QrText.Join(lines);
    }
}

/// <summary>A contact card in the compact MeCard format of NTT DoCoMo: <c>MECARD:N:Last,First;TEL:...;;</c>.</summary>
public sealed record QrMeCardContent : IQrContent
{
    /// <summary>First name.</summary>
    public string FirstName { get; init; } = string.Empty;
    /// <summary>Last name.</summary>
    public string LastName { get; init; } = string.Empty;
    /// <summary>Phone number.</summary>
    public string Phone { get; init; } = string.Empty;
    /// <summary>E-mail address.</summary>
    public string Email { get; init; } = string.Empty;
    /// <summary>Web site.</summary>
    public string Url { get; init; } = string.Empty;
    /// <summary>Address.</summary>
    public QrAddress Address { get; init; } = new();
    /// <summary>Free note.</summary>
    public string Note { get; init; } = string.Empty;

    /// <inheritdoc />
    public string Kind => "MeCard";

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        if (FirstName.Length + LastName.Length == 0) issues.Add(QrText.Err("QrContent.MeCard.NameMissing"));
        if (Email.Length > 0 && !QrText.IsEmail(Email)) issues.Add(QrText.Err("QrContent.Email.Address"));
        if (Phone.Length > 0 && !QrPhoneContent.TryNormalize(Phone, out _)) issues.Add(QrText.Err("QrContent.Phone.Invalid"));
        return issues;
    }

    // MeCard escapes backslash, semicolon, comma and colon with a backslash.
    // MeCard has no escape for a line break, so a break would start a new line of the payload: it becomes a space.
    private static string Escape(string v) => Regex.Replace(Regex.Replace(v, @"[\r\n]+", " "), @"([\\;,:])", "\\$1");

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        var sb = new StringBuilder("MECARD:");
        sb.Append("N:").Append(Escape(LastName));
        if (FirstName.Length > 0) sb.Append(LastName.Length > 0 ? "," : string.Empty).Append(Escape(FirstName));
        sb.Append(';');
        if (Phone.Length > 0 && QrPhoneContent.TryNormalize(Phone, out var tel)) sb.Append("TEL:").Append(tel).Append(';');
        if (Email.Length > 0) sb.Append("EMAIL:").Append(Escape(Email)).Append(';');
        if (Url.Length > 0) sb.Append("URL:").Append(Escape(Url)).Append(';');
        // ADR is post office box, room, house, city, prefecture, zip, country; only the parts Rotula has are filled.
        if (!Address.IsEmpty)
            sb.Append("ADR:,,").Append(string.Join(',', new[] { Address.Street, Address.City, Address.Region, Address.PostalCode, Address.Country }.Select(Escape))).Append(';');
        if (Note.Length > 0) sb.Append("NOTE:").Append(Escape(Note)).Append(';');
        return sb.Append(';').ToString();
    }
}

/// <summary>An e-mail: <c>mailto:address?subject=...&amp;body=...</c> (RFC 6068).</summary>
/// <param name="To">One or more addresses separated by commas.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Body">Message text; line breaks are kept.</param>
public sealed record QrEmailContent(string To, string Subject = "", string Body = "") : IQrContent
{
    /// <inheritdoc />
    public string Kind => "E-mail";

    private IEnumerable<string> Recipients() => (To ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        var list = Recipients().ToList();
        if (list.Count == 0) issues.Add(QrText.Err("QrContent.Email.Empty"));
        else if (list.Any(a => !QrText.IsEmail(a))) issues.Add(QrText.Err("QrContent.Email.Address"));
        return issues;
    }

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        // The address part keeps @ and the dot readable; everything else is percent-encoded. Subject and body use %20 for a space, never +,
        // because mail programs read + as a plus sign inside a mailto link.
        string addresses = string.Join(',', Recipients().Select(a =>
        {
            int at = a.IndexOf('@');
            return at < 0 ? QrText.Pct(a) : QrText.Pct(a[..at]) + "@" + QrText.Pct(a[(at + 1)..]);
        }));
        var query = new List<string>();
        if (!string.IsNullOrEmpty(Subject)) query.Add("subject=" + QrText.Pct(Subject));
        if (!string.IsNullOrEmpty(Body)) query.Add("body=" + QrText.Pct(Body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)));
        return "mailto:" + addresses + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
    }
}

/// <summary>A phone number: <c>tel:+50761234567</c> (RFC 3966).</summary>
/// <param name="Number">The number; spaces, dots, dashes and parentheses are removed. Use the international form with <c>+</c> so it works anywhere.</param>
public sealed record QrPhoneContent(string Number) : IQrContent
{
    /// <inheritdoc />
    public string Kind => "Phone";

    /// <summary>Removes the separators people type and checks what is left: an optional leading +, then 3 to 15 digits.</summary>
    internal static bool TryNormalize(string number, out string normalized)
    {
        var sb = new StringBuilder();
        foreach (char c in number ?? string.Empty)
        {
            if (c is ' ' or '-' or '.' or '(' or ')' or ' ') continue;
            if (c is >= '0' and <= '9' || (c == '+' && sb.Length == 0)) sb.Append(c);
            else
            {
                // A template still holds placeholders instead of digits: keep the text as typed.
                normalized = QrText.Lenient ? (number ?? string.Empty).Trim() : string.Empty;
                return QrText.Lenient;
            }
        }
        normalized = sb.ToString();
        int digits = normalized.Count(char.IsAsciiDigit);
        return digits is >= 3 and <= 15;
    }

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate() =>
        TryNormalize(Number, out _) ? Array.Empty<ValidationIssue>() : new[] { QrText.Err("QrContent.Phone.Invalid") };

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        TryNormalize(Number, out var n);
        return "tel:" + n;
    }
}

/// <summary>A text message to a number: <c>SMSTO:+50761234567:message</c>, the form phones and ZXing read.</summary>
/// <param name="Number">The recipient's number.</param>
/// <param name="Message">The text to pre-fill.</param>
public sealed record QrSmsContent(string Number, string Message = "") : IQrContent
{
    /// <inheritdoc />
    public string Kind => "SMS";

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        if (!QrPhoneContent.TryNormalize(Number, out _)) issues.Add(QrText.Err("QrContent.Phone.Invalid"));
        // One SMS holds 160 characters of the basic alphabet (70 with accents or symbols); longer texts are sent as several messages.
        if ((Message ?? string.Empty).Length > 160) issues.Add(new ValidationIssue(ValidationSeverity.Warning, "QrContent.Sms.Long", null, "160"));
        return issues;
    }

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        QrPhoneContent.TryNormalize(Number, out var n);
        return "SMSTO:" + n + ":" + (Message ?? string.Empty);
    }
}

/// <summary>A map position: <c>geo:8.983333,-79.516667</c> (RFC 5870).</summary>
/// <param name="Latitude">Degrees north, -90 to 90.</param>
/// <param name="Longitude">Degrees east, -180 to 180.</param>
/// <param name="Altitude">Metres above sea level, or null for none.</param>
public sealed record QrGeoContent(double Latitude, double Longitude, double? Altitude = null) : IQrContent
{
    /// <inheritdoc />
    public string Kind => "Location";

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        if (!double.IsFinite(Latitude) || Latitude is < -90 or > 90) issues.Add(QrText.Err("QrContent.Geo.Latitude"));
        if (!double.IsFinite(Longitude) || Longitude is < -180 or > 180) issues.Add(QrText.Err("QrContent.Geo.Longitude"));
        if (Altitude is { } a && !double.IsFinite(a)) issues.Add(QrText.Err("QrContent.Geo.Altitude"));
        return issues;
    }

    // Seven decimals are about a centimetre; always a point as decimal separator, whatever the computer's language.
    private static string Format(double v) => (Math.Round(v, 7) + 0.0).ToString("0.#######", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        return "geo:" + Format(Latitude) + "," + Format(Longitude) + (Altitude is { } a ? "," + Format(a) : string.Empty);
    }
}

/// <summary>A calendar event (iCalendar VEVENT, RFC 5545), with times in UTC.</summary>
public sealed record QrCalendarEventContent : IQrContent
{
    /// <summary>Title of the event.</summary>
    public string Summary { get; init; } = string.Empty;
    /// <summary>Start. An offset other than zero is converted to UTC, so the event happens at the same instant everywhere.</summary>
    public DateTimeOffset Start { get; init; }
    /// <summary>End; for an all-day event the last day (inclusive).</summary>
    public DateTimeOffset End { get; init; }
    /// <summary>True for an event without times: only the dates of <see cref="Start"/> and <see cref="End"/> (as given, not converted) are used.</summary>
    public bool AllDay { get; init; }
    /// <summary>Place.</summary>
    public string Location { get; init; } = string.Empty;
    /// <summary>Longer text.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>True to wrap the event in <c>BEGIN:VCALENDAR</c> ... <c>END:VCALENDAR</c>, which some phones need; it makes the code larger.</summary>
    public bool WrapInCalendar { get; init; }

    /// <inheritdoc />
    public string Kind => "Calendar event";

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        if (Summary.Length == 0) issues.Add(QrText.Err("QrContent.Event.SummaryMissing"));
        if (End < Start) issues.Add(QrText.Err("QrContent.Event.EndBeforeStart"));
        return issues;
    }

    /// <inheritdoc />
    public string Build()
    {
        QrText.ThrowIfInvalid(this);
        var lines = new List<string>();
        if (WrapInCalendar) { lines.Add("BEGIN:VCALENDAR"); lines.Add("VERSION:2.0"); }
        lines.Add("BEGIN:VEVENT");
        lines.Add("SUMMARY:" + QrText.EscapeText(Summary));
        if (AllDay)
        {
            // DTEND of a date event is exclusive (the day after the last day), RFC 5545 section 3.6.1.
            lines.Add("DTSTART;VALUE=DATE:" + Start.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            lines.Add("DTEND;VALUE=DATE:" + End.Date.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }
        else
        {
            lines.Add("DTSTART:" + Start.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            lines.Add("DTEND:" + End.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        }
        if (Location.Length > 0) lines.Add("LOCATION:" + QrText.EscapeText(Location));
        if (Description.Length > 0) lines.Add("DESCRIPTION:" + QrText.EscapeText(Description));
        lines.Add("END:VEVENT");
        if (WrapInCalendar) lines.Add("END:VCALENDAR");
        return QrText.Join(lines);
    }
}
