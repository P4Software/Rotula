using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Elements;
using Rotula.Core.Gs1;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Core.Barcodes.QrContent;

/// <summary>A problem found in a content spec, with the field it belongs to (null when it concerns the content as a whole).</summary>
/// <param name="Field">Name of the field, as in <see cref="QrFieldSchema.Name"/>.</param>
/// <param name="Issue">The message key and arguments.</param>
public sealed record QrFieldIssue(string? Field, ValidationIssue Issue);

/// <summary>The payload built from a spec and a record.</summary>
/// <param name="Text">The text for the QR Code; null when there are errors (nothing must be printed from a broken payload).</param>
/// <param name="Issues">Everything found: errors, and warnings such as a payload that is close to the capacity.</param>
public sealed record QrContentResult(string? Text, IReadOnlyList<QrFieldIssue> Issues)
{
    /// <summary>True when at least one issue is an error.</summary>
    public bool HasErrors => Issues.Any(i => i.Issue.Severity == ValidationSeverity.Error);
}

/// <summary>
/// Builds the payload of a QR Code from a <see cref="QrContentSpec"/>. The order matters for safety: the placeholders are replaced with the
/// record's values first, and the format's escaping is applied to the <b>resolved</b> values afterwards. So a customer called
/// <c>Smith; John</c>, an address with commas, or a password holding <c>;S:evil</c> is data inside one field and can never close the field, add
/// a property, or start a new line in the payload.
/// </summary>
public static class QrContentBuilder
{
    /// <summary>
    /// The text of the payload with the placeholders still in it, for the dialog and for <c>BarcodeElement.Data</c>: fixed text is escaped,
    /// placeholders are left as <c>{{field}}</c>. Nothing is validated. It is what older code (which resolves <c>Data</c> as plain text) sees; the
    /// ZPL generator uses <see cref="Resolve"/> instead.
    /// </summary>
    public static string BuildTemplate(QrContentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        bool before = QrText.Lenient;
        QrText.Lenient = true;
        try { return Compose(spec, spec.Get, template: true, new List<QrFieldIssue>()) ?? string.Empty; }
        finally { QrText.Lenient = before; }
    }

    /// <summary>
    /// Builds the payload for one record: replaces the placeholders with <paramref name="record"/> (a name that is missing gives an empty value and
    /// the warning <c>Variable.Missing</c>), cleans the values, checks every field, escapes, and checks the size against the symbol.
    /// </summary>
    /// <param name="spec">The definition.</param>
    /// <param name="record">The values of one data record by column name (the label's variable names).</param>
    /// <param name="level">Error correction level of the symbol, for the capacity check.</param>
    /// <param name="model">QR model, 1 or 2.</param>
    public static QrContentResult Resolve(QrContentSpec spec, IReadOnlyDictionary<string, string> record, QrErrorCorrection level = QrErrorCorrection.Quartile, int model = 2)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(record);
        var issues = new List<QrFieldIssue>();
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var schema = QrContentSchema.For(spec.Kind);
        foreach (var (name, value) in spec.Fields)
        {
            var missing = new List<string>();
            string text = QrContentSpec.Substitute(value, n => record.TryGetValue(n, out var v) ? v : null, missing);
            foreach (var m in missing.Distinct()) issues.Add(new QrFieldIssue(name, new ValidationIssue(ValidationSeverity.Warning, "Variable.Missing", null, m)));
            bool multiline = schema.Field(name)?.Hint == QrInputHint.MultilineText;
            resolved[name] = Clean(text, multiline);
        }
        return Finish(spec, resolved, issues, level, model);
    }

    /// <summary>
    /// Checks a spec as designed, before any record exists: required fields, and every field without a placeholder against its format (a field with
    /// a placeholder is checked with the sample value of the schema in its place).
    /// </summary>
    public static IReadOnlyList<QrFieldIssue> Validate(QrContentSpec spec, QrErrorCorrection level = QrErrorCorrection.Quartile, int model = 2)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var schema = QrContentSchema.For(spec.Kind);
        var sample = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in spec.Fields)
        {
            sample[name] = value.Contains("{{", StringComparison.Ordinal) ? schema.Field(name)?.Sample ?? "x" : value;
        }
        var result = Resolve(new QrContentSpec(spec.Kind, sample), new Dictionary<string, string>(), level, model);
        // The capacity of a payload built from samples says nothing about the real records, so it is not reported at design time.
        return result.Issues.Where(i => !i.Issue.MessageKey.StartsWith("QrContent.Capacity.", StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// The payload of a barcode element that has a content spec, for one record; null when the element has none (use <c>Data</c> then). Variable
    /// values come from <paramref name="data"/>, then from the declared sample values. Problems are added to <paramref name="issues"/> with the
    /// element's id; when there are errors the returned text is empty, so nothing is printed.
    /// </summary>
    public static string? ResolveForElement(BarcodeElement element, IReadOnlyDictionary<string, string>? data, IEnumerable<LabelVariable> declared, ICollection<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.QrContentSpec.Length == 0 || element.Symbology != BarcodeSymbology.QrCode || !QrContentSpec.TryParse(element.QrContentSpec, out var spec)) return null;
        var variables = declared as IList<LabelVariable> ?? declared.ToList();
        var record = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in spec.Placeholders())
        {
            if (data is not null && data.TryGetValue(name, out var v)) record[name] = v;
            else if (variables.FirstOrDefault(x => x.Name == name) is { } decl) record[name] = decl.SampleValue;
        }
        var result = Resolve(spec, record, element.QrErrorCorrection, element.QrModel);
        foreach (var i in result.Issues)
            issues.Add(new ValidationIssue(i.Issue.Severity, i.Issue.MessageKey, element.Id, i.Issue.Args.Prepend(i.Field ?? string.Empty).ToArray()));
        return result.Text ?? string.Empty;
    }

    // ---- cleaning ---------------------------------------------------------------------------------

    /// <summary>
    /// Removes control characters (and the Unicode line and paragraph separators) from a resolved value. Line breaks are kept as a single LF
    /// where the field is multi-line, and become a space otherwise, so a value can never smuggle a new line into a line-based format.
    /// </summary>
    public static string Clean(string value, bool multiline)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\r') { if (i + 1 < value.Length && value[i + 1] == '\n') i++; c = '\n'; }
            if (c is '\n' or '\u2028' or '\u2029' or '\u0085') { sb.Append(multiline ? '\n' : ' '); continue; }
            if (c == '\t') { sb.Append(' '); continue; }
            if (char.IsControl(c)) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    // ---- building ---------------------------------------------------------------------------------

    private static QrContentResult Finish(QrContentSpec spec, Dictionary<string, string> v, List<QrFieldIssue> issues, QrErrorCorrection level, int model)
    {
        var schema = QrContentSchema.For(spec.Kind);
        foreach (var f in schema.Fields)
        {
            string value = v.TryGetValue(f.Name, out var x) ? x : string.Empty;
            if (f.Required && value.Length == 0) issues.Add(Error(f.Name, "QrContent.Field.Required", f.Name));
            else if (f.MaxLength > 0 && value.Length > f.MaxLength) issues.Add(Error(f.Name, "QrContent.Field.TooLong", f.Name, f.MaxLength.ToString(CultureInfo.InvariantCulture)));
            else if (f.Hint == QrInputHint.Choice && value.Length > 0 && !f.Choices.Contains(value)) issues.Add(Error(f.Name, "QrContent.Field.Choice", f.Name, string.Join(',', f.Choices)));
        }
        if (issues.Any(i => i.Issue.Severity == ValidationSeverity.Error && i.Issue.MessageKey.StartsWith("QrContent.Field.", StringComparison.Ordinal)))
            return new QrContentResult(null, issues);

        string? text = Compose(spec, n => v.TryGetValue(n, out var s) ? s : string.Empty, template: false, issues);
        if (text is null || issues.Any(i => i.Issue.Severity == ValidationSeverity.Error)) return new QrContentResult(null, issues);

        var fit = QrSizing.Analyze(text, level, model);
        string levelName = level.ToString();
        if (!fit.Fits)
        {
            issues.Add(new QrFieldIssue(null, new ValidationIssue(ValidationSeverity.Warning, "QrContent.Capacity.Exceeded", null,
                fit.Units.ToString(CultureInfo.InvariantCulture), fit.MaxUnits.ToString(CultureInfo.InvariantCulture), levelName)));
            // A lower level holds more; say which one would fit, or that the text must be shorter.
            QrErrorCorrection? lower = new[] { QrErrorCorrection.Quartile, QrErrorCorrection.Medium, QrErrorCorrection.Low }
                .Where(l => l < level).Cast<QrErrorCorrection?>().FirstOrDefault(l => QrSizing.Analyze(text, l!.Value, model).Fits);
            if (lower is { } fitting)
                issues.Add(new QrFieldIssue(null, new ValidationIssue(ValidationSeverity.Warning, "QrContent.Capacity.TryLowerLevel", null, fitting.ToString())));
            else
                issues.Add(new QrFieldIssue(null, new ValidationIssue(ValidationSeverity.Warning, "QrContent.Capacity.Shorten")));
        }
        else if (fit.UsedPercent >= 85)
            issues.Add(new QrFieldIssue(null, new ValidationIssue(ValidationSeverity.Warning, "QrContent.Capacity.Dense", null,
                Math.Round(fit.UsedPercent).ToString(CultureInfo.InvariantCulture), fit.Version.ToString(CultureInfo.InvariantCulture))));
        return new QrContentResult(text, issues);
    }

    private static QrFieldIssue Error(string? field, string key, params string[] args) => new(field, new ValidationIssue(ValidationSeverity.Error, key, null, args));

    private static bool Flag(string v) => v.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "y" or "si";

    private static void Map(List<QrFieldIssue> into, IEnumerable<ValidationIssue> found, Func<string, string?> fieldOf)
    {
        foreach (var i in found) into.Add(new QrFieldIssue(fieldOf(i.MessageKey), i));
    }

    /// <summary>Writes the payload for the (resolved or template) field values. Returns null when a number or date field cannot be read.</summary>
    private static string? Compose(QrContentSpec spec, Func<string, string> get, bool template, List<QrFieldIssue> issues)
    {
        var kind = spec.Kind;
        switch (kind)
        {
            case QrContentKind.Text:
                return get("text");

            case QrContentKind.Url:
            {
                string url = get("url").Trim();
                if (!template && Flag(get("stripTracking"))) url = StripTracking(url);
                var c = new QrUrlContent(url);
                if (!template) Map(issues, c.Validate(), _ => "url");
                return template ? url : issues.Any(i => i.Issue.Severity == ValidationSeverity.Error) ? null : c.Build();
            }

            case QrContentKind.Wifi:
            {
                string sec = get("security");
                var security = Enum.TryParse<WifiSecurity>(sec, false, out var s) && Enum.IsDefined(s) ? s : WifiSecurity.Wpa;
                var c = new QrWifiContent(get("ssid"), get("password"), security, Flag(get("hidden")));
                return Build(c, issues, template, k => k.Contains(".Ssid.", StringComparison.Ordinal) ? "ssid" : "password");
            }

            case QrContentKind.VCard:
            {
                var c = new QrVCardContent
                {
                    FirstName = get("firstName"), LastName = get("lastName"), Organization = get("organization"), Title = get("title"), Phone = get("phone"),
                    Mobile = get("mobile"), Email = get("email"), Url = get("url"), Note = get("note"),
                    Address = new QrAddress(get("street"), get("city"), get("region"), get("postalCode"), get("country")),
                };
                return Build(c, issues, template, k => k switch
                {
                    "QrContent.VCard.NameMissing" => "firstName", "QrContent.Email.Address" => "email", "QrContent.Phone.Invalid" => "phone", "QrContent.Url.Invalid" => "url", _ => null,
                });
            }

            case QrContentKind.MeCard:
            {
                var c = new QrMeCardContent
                {
                    FirstName = get("firstName"), LastName = get("lastName"), Phone = get("phone"), Email = get("email"), Url = get("url"), Note = get("note"),
                    Address = new QrAddress(get("street"), get("city"), get("region"), get("postalCode"), get("country")),
                };
                return Build(c, issues, template, k => k switch
                {
                    "QrContent.MeCard.NameMissing" => "firstName", "QrContent.Email.Address" => "email", "QrContent.Phone.Invalid" => "phone", _ => null,
                });
            }

            case QrContentKind.Email:
                return Build(new QrEmailContent(get("to"), get("subject"), get("body")), issues, template, _ => "to");

            case QrContentKind.Phone:
                return Build(new QrPhoneContent(get("number")), issues, template, _ => "number");

            case QrContentKind.Sms:
                return Build(new QrSmsContent(get("number"), get("message")), issues, template, k => k == "QrContent.Sms.Long" ? "message" : "number");

            case QrContentKind.Location:
            {
                if (template)
                {
                    string label0 = get("label");
                    return "geo:" + get("latitude") + "," + get("longitude") + (label0.Length > 0 ? "?q=" + get("latitude") + "," + get("longitude") + "(" + QrText.Pct(label0) + ")" : string.Empty);
                }
                if (!TryNumber(get("latitude"), out double lat)) { issues.Add(Error("latitude", "QrContent.Field.Number", "latitude")); return null; }
                if (!TryNumber(get("longitude"), out double lon)) { issues.Add(Error("longitude", "QrContent.Field.Number", "longitude")); return null; }
                var geo = new QrGeoContent(lat, lon);
                Map(issues, geo.Validate(), k => k.EndsWith("Latitude", StringComparison.Ordinal) ? "latitude" : "longitude");
                if (issues.Any(i => i.Issue.Severity == ValidationSeverity.Error)) return null;
                string label = get("label");
                string basic = geo.Build();
                // Map apps read the label from the "q" parameter (geo:lat,lon?q=lat,lon(Label)); plain geo URIs have no place for it.
                return label.Length == 0 ? basic : basic + "?q=" + basic[4..] + "(" + Uri.EscapeDataString(label) + ")";
            }

            case QrContentKind.CalendarEvent:
                return ComposeEvent(get, template, issues);

            case QrContentKind.GS1DigitalLink:
                return ComposeDigitalLink(get, template, issues);

            default:
                return null;
        }
    }

    private static string? Build(IQrContent content, List<QrFieldIssue> issues, bool template, Func<string, string?> fieldOf)
    {
        if (template) return content.Build();
        Map(issues, content.Validate(), fieldOf);
        return issues.Any(i => i.Issue.Severity == ValidationSeverity.Error) ? null : content.Build();
    }

    private static bool TryNumber(string text, out double value)
    {
        text = text.Trim();
        // A data column may use a decimal comma (8,983333); with a point present, a comma is not a decimal separator.
        if (!text.Contains('.') && text.Count(c => c == ',') == 1) text = text.Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static string StripTracking(string url)
    {
        int q = url.IndexOf('?');
        if (q < 0) return url;
        int hash = url.IndexOf('#', q);
        string fragment = hash < 0 ? string.Empty : url[hash..];
        string query = url[(q + 1)..(hash < 0 ? url.Length : hash)];
        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)).ToList();
        return url[..q] + (kept.Count > 0 ? "?" + string.Join('&', kept) : string.Empty) + fragment;
    }

    private static readonly Regex HasOffset = new(@"(Z|[+-]\d{2}:?\d{2})\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? ComposeEvent(Func<string, string> get, bool template, List<QrFieldIssue> issues)
    {
        bool allDay = Flag(get("allDay"));
        // With no placeholder in the times they can already be written as UTC; otherwise they stay as typed.
        bool timesFixed = !get("start").Contains("{{") && !get("end").Contains("{{") && !get("allDay").Contains("{{");
        if (template && !timesFixed)
        {
            var t = new StringBuilder("BEGIN:VEVENT" + QrText.CrLf + "SUMMARY:" + QrText.EscapeText(get("title")) + QrText.CrLf);
            t.Append("DTSTART:").Append(get("start")).Append(QrText.CrLf).Append("DTEND:").Append(get("end")).Append(QrText.CrLf);
            if (get("location").Length > 0) t.Append("LOCATION:").Append(QrText.EscapeText(get("location"))).Append(QrText.CrLf);
            if (get("description").Length > 0) t.Append("DESCRIPTION:").Append(QrText.EscapeText(get("description"))).Append(QrText.CrLf);
            return t.Append("END:VEVENT").ToString();
        }
        bool Parse(string field, out DateTimeOffset when)
        {
            string text = get(field);
            when = default;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when))
            {
                issues.Add(Error(field, "QrContent.Field.DateTime", field));
                return false;
            }
            // Without an offset the time is taken as UTC; say so, because a local time written that way shifts by the offset.
            if (!allDay && !HasOffset.IsMatch(text))
                issues.Add(new QrFieldIssue(field, new ValidationIssue(ValidationSeverity.Warning, "QrContent.Event.NoOffset", null, field)));
            return true;
        }
        bool okStart = Parse("start", out var start), okEnd = Parse("end", out var end);
        if (!okStart || !okEnd) return null;
        var content = new QrCalendarEventContent
        {
            Summary = get("title"), Start = start, End = end, AllDay = allDay, Location = get("location"), Description = get("description"),
        };
        Map(issues, content.Validate(), k => k == "QrContent.Event.SummaryMissing" ? "title" : "end");
        return issues.Any(i => i.Issue.Severity == ValidationSeverity.Error) ? null : content.Build();
    }

    private static readonly (string Field, string Ai)[] DigitalLinkFields =
    {
        ("gtin", "01"), ("lot", "10"), ("serial", "21"), ("expiry", "17"), ("bestBefore", "15"), ("productionDate", "11"),
    };

    private static string? ComposeDigitalLink(Func<string, string> get, bool template, List<QrFieldIssue> issues)
    {
        string prefix = get("prefix").Trim();
        if (prefix.Length == 0) prefix = Gs1DigitalLink.DefaultPrefix;
        if (template)
        {
            var sb = new StringBuilder(prefix.TrimEnd('/'));
            foreach (var (field, ai) in DigitalLinkFields.Where(f => f.Field is "gtin" or "lot" or "serial"))
                if (get(field).Length > 0) sb.Append('/').Append(ai).Append('/').Append(QrText.Pct(get(field)));
            bool first = true;
            foreach (var (field, ai) in DigitalLinkFields.Where(f => f.Field is "expiry" or "bestBefore" or "productionDate"))
                if (get(field).Length > 0) { sb.Append(first ? '?' : '&').Append(ai).Append('=').Append(QrText.Pct(get(field))); first = false; }
            return sb.ToString();
        }
        var elements = new List<Gs1Element>();
        foreach (var (field, ai) in DigitalLinkFields)
        {
            string value = get(field).Trim();
            if (value.Length == 0) continue;
            // A GTIN of 8, 12 or 13 digits is the same key as its 14-digit form; the dictionary then checks the check digit.
            if (field == "gtin" && value.All(char.IsAsciiDigit) && value.Length is 8 or 12 or 13) value = value.PadLeft(14, '0');
            elements.Add(new Gs1Element(ai, value));
        }
        var uri = Gs1DigitalLink.Build(new Gs1Message(elements), out var found, prefix);
        string? FieldOfAi(string? ai) => DigitalLinkFields.FirstOrDefault(f => f.Ai == ai).Field;
        foreach (var i in found)
            issues.Add(new QrFieldIssue(i.Args.Count > 0 ? FieldOfAi(i.Args[0]) : null, i));
        if (uri is null) { if (!issues.Any(i => i.Issue.Severity == ValidationSeverity.Error)) issues.Add(Error(null, "Gs1.DigitalLink.NoKey")); return null; }
        return issues.Any(i => i.Issue.Severity == ValidationSeverity.Error) ? null : uri;
    }
}
