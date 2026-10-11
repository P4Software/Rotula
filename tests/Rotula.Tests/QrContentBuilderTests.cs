using System.Text;
using Rotula.Core.Barcodes;
using Rotula.Core.Barcodes.QrContent;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using ZXing;
using ZXing.Client.Result;
using static Rotula.Tests.CoreTestSupport;
using static Rotula.Tests.Gs1DecodeSupport;

namespace Rotula.Tests;

/// <summary>
/// The content builder: structured spec, template for the dialog, per-record payloads with escaping applied after substitution, injection
/// safety for every format, non-ASCII text through ZPL and the preview, capacity warnings and the schema the dialog is built from.
/// </summary>
public class QrContentBuilderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoRecord = new Dictionary<string, string>();

    private static QrContentResult Resolve(QrContentSpec spec, params (string Name, string Value)[] record) =>
        QrContentBuilder.Resolve(spec, record.ToDictionary(r => r.Name, r => r.Value));

    private static string Text(QrContentResult r)
    {
        Assert.False(r.HasErrors, string.Join(", ", r.Issues.Select(i => i.Issue.MessageKey + "(" + i.Field + ")")));
        return r.Text!;
    }

    // ---- spec, schema -------------------------------------------------------------------------------

    [Fact]
    public void Spec_RoundTripsThroughJson_AndHostileJsonIsIgnored()
    {
        var spec = QrContentSpec.Create(QrContentKind.Wifi, ("ssid", "{{ssid}}"), ("password", "pa\"ss"), ("security", "Wpa"), ("empty", ""));
        string json = spec.ToJson();
        Assert.True(QrContentSpec.TryParse(json, out var back));
        Assert.Equal(QrContentKind.Wifi, back.Kind);
        Assert.Equal("{{ssid}}", back.Get("ssid"));
        Assert.Equal("pa\"ss", back.Get("password"));
        Assert.Equal(new[] { "ssid" }, back.Placeholders());
        Assert.False(back.Fields.ContainsKey("empty"));          // empty values are not stored

        foreach (var bad in new[] { "", "nope", "[]", "{}", "{\"kind\":\"Evil\"}", "{\"kind\":\"7\"}", "{\"kind\":3}", "{\"kind\":\"Wifi\",\"fields\":" })
            Assert.False(QrContentSpec.TryParse(bad, out _), bad);
        Assert.True(QrContentSpec.TryParse("{\"kind\":\"Phone\",\"fields\":{\"number\":5,\"x\":\"ok\"}}", out var odd));   // a non-text value is skipped
        Assert.Equal("ok", odd.Get("x"));
        Assert.Equal(string.Empty, odd.Get("number"));
    }

    [Fact]
    public void Schema_DescribesEveryKindAsData()
    {
        Assert.Equal(Enum.GetValues<QrContentKind>().Length, QrContentSchema.All.Count);
        foreach (var kind in QrContentSchema.All)
        {
            Assert.Equal("QrContent.Kind." + kind.Kind, kind.LabelKey);
            Assert.NotEmpty(kind.Fields);
            Assert.Equal(kind.Fields.Count, kind.Fields.Select(f => f.Name).Distinct().Count());
            Assert.All(kind.Fields, f =>
            {
                Assert.StartsWith("QrContent.Field.", f.LabelKey);
                Assert.False(string.IsNullOrEmpty(f.Sample));
                if (f.Hint == QrInputHint.Choice) Assert.Contains(f.Sample, f.Choices);
            });
            // The samples build a valid payload for every kind.
            var sample = new QrContentSpec(kind.Kind, kind.Fields.ToDictionary(f => f.Name, f => f.Sample));
            Assert.False(QrContentBuilder.Resolve(sample, NoRecord).HasErrors, kind.Kind.ToString());
        }
        Assert.Equal(new[] { "Wpa", "Wpa3Sae", "Wep", "None" }, QrContentSchema.For(QrContentKind.Wifi).Field("security")!.Choices);
    }

    [Fact]
    public void RequiredFields_AndLengths_AreReportedPerField()
    {
        var r = QrContentBuilder.Resolve(new QrContentSpec(QrContentKind.Wifi, new Dictionary<string, string> { ["ssid"] = new string('x', 40) }), NoRecord);
        Assert.Contains(r.Issues, i => i.Field == "ssid" && i.Issue.MessageKey == "QrContent.Field.TooLong" && i.Issue.Args.SequenceEqual(new[] { "ssid", "32" }));
        Assert.Null(r.Text);
        var empty = QrContentBuilder.Resolve(new QrContentSpec(QrContentKind.Email, new Dictionary<string, string>()), NoRecord);
        Assert.Contains(empty.Issues, i => i.Field == "to" && i.Issue.MessageKey == "QrContent.Field.Required");
        var choice = QrContentBuilder.Resolve(QrContentSpec.Create(QrContentKind.Wifi, ("ssid", "n"), ("password", "12345678"), ("security", "WPA2")), NoRecord);
        Assert.Contains(choice.Issues, i => i.Field == "security" && i.Issue.MessageKey == "QrContent.Field.Choice");
    }

    [Fact]
    public void FormatErrors_AreAttributedToTheirField()
    {
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.Email, ("to", "nobody"))).Issues, i => i.Field == "to" && i.Issue.MessageKey == "QrContent.Email.Address");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.VCard, ("firstName", "A"), ("email", "x"))).Issues, i => i.Field == "email");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.VCard, ("firstName", "A"), ("mobile", "x"))).Issues, i => i.Issue.MessageKey == "QrContent.Phone.Invalid");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.Wifi, ("ssid", "n"), ("password", "short"))).Issues, i => i.Field == "password" && i.Issue.MessageKey == "QrContent.Wifi.Password.Length");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.Location, ("latitude", "abc"), ("longitude", "1"))).Issues, i => i.Field == "latitude" && i.Issue.MessageKey == "QrContent.Field.Number");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.Location, ("latitude", "95"), ("longitude", "1"))).Issues, i => i.Field == "latitude" && i.Issue.MessageKey == "QrContent.Geo.Latitude");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.CalendarEvent, ("title", "t"), ("start", "yesterday"), ("end", "2026-01-01T10:00:00Z"))).Issues, i => i.Field == "start" && i.Issue.MessageKey == "QrContent.Field.DateTime");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.CalendarEvent, ("title", "t"), ("start", "2026-01-02T10:00:00Z"), ("end", "2026-01-01T10:00:00Z"))).Issues, i => i.Field == "end" && i.Issue.MessageKey == "QrContent.Event.EndBeforeStart");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.GS1DigitalLink, ("gtin", "09506000134353"))).Issues, i => i.Field == "gtin" && i.Issue.MessageKey == "Gs1.Lint.CheckDigit");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.GS1DigitalLink, ("gtin", "09506000134352"), ("expiry", "251399"))).Issues, i => i.Field == "expiry");
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.Url, ("url", "ftp://x.test/a"))).Issues, i => i.Field == "url" && i.Issue.MessageKey == "QrContent.Url.Scheme" && i.Issue.Severity == ValidationSeverity.Warning);
    }

    // ---- golden payloads ------------------------------------------------------------------------------

    [Fact]
    public void Golden_PayloadOfEveryKind()
    {
        Assert.Equal("https://example.com/menu?table=7", Text(Resolve(QrContentSpec.Create(QrContentKind.Url, ("url", "https://example.com/menu?table=7")))));
        Assert.Equal("https://example.com/p?id=1&x=2#top", Text(Resolve(QrContentSpec.Create(QrContentKind.Url, ("url", "https://example.com/p?utm_source=a&id=1&utm_medium=b&x=2#top"), ("stripTracking", "true")))));
        Assert.Equal("Hello\nworld", Text(Resolve(QrContentSpec.Create(QrContentKind.Text, ("text", "Hello\r\nworld")))));
        Assert.Equal("WIFI:T:WPA;S:CafeGuest;P:welcome2026;H:true;;",
            Text(Resolve(QrContentSpec.Create(QrContentKind.Wifi, ("ssid", "CafeGuest"), ("password", "welcome2026"), ("security", "Wpa"), ("hidden", "true")))));
        Assert.Equal("WIFI:T:nopass;S:Open;H:false;;", Text(Resolve(QrContentSpec.Create(QrContentKind.Wifi, ("ssid", "Open"), ("security", "None")))));
        Assert.Equal("BEGIN:VCARD\r\nVERSION:3.0\r\nN:Perez;Ana;;;\r\nFN:Ana Perez\r\nORG:Rotula\r\nTEL;TYPE=WORK,VOICE:+5071234567\r\nEMAIL;TYPE=INTERNET:ana@example.com\r\nEND:VCARD",
            Text(Resolve(QrContentSpec.Create(QrContentKind.VCard, ("firstName", "Ana"), ("lastName", "Perez"), ("organization", "Rotula"), ("phone", "+507 123-4567"), ("email", "ana@example.com")))));
        Assert.Equal("MECARD:N:Perez,Ana;TEL:+5071234567;EMAIL:ana@example.com;;",
            Text(Resolve(QrContentSpec.Create(QrContentKind.MeCard, ("firstName", "Ana"), ("lastName", "Perez"), ("phone", "+507 123-4567"), ("email", "ana@example.com")))));
        Assert.Equal("https://id.gs1.org/01/09506000134352/10/ABC123/21/SER9?17=251231",
            Text(Resolve(QrContentSpec.Create(QrContentKind.GS1DigitalLink, ("gtin", "09506000134352"), ("lot", "ABC123"), ("serial", "SER9"), ("expiry", "251231")))));
        Assert.Equal("https://example.com/01/09506000134352", Text(Resolve(QrContentSpec.Create(QrContentKind.GS1DigitalLink, ("prefix", "https://example.com"), ("gtin", "9506000134352")))));
        Assert.Equal("mailto:ana@example.com?subject=Hello%20World&body=Line1%0D%0ALine2",
            Text(Resolve(QrContentSpec.Create(QrContentKind.Email, ("to", "ana@example.com"), ("subject", "Hello World"), ("body", "Line1\nLine2")))));
        Assert.Equal("tel:+50761234567", Text(Resolve(QrContentSpec.Create(QrContentKind.Phone, ("number", "+507 6123-4567")))));
        Assert.Equal("SMSTO:+50761234567:Hola", Text(Resolve(QrContentSpec.Create(QrContentKind.Sms, ("number", "+507 6123-4567"), ("message", "Hola")))));
        Assert.Equal("geo:8.983333,-79.516667", Text(Resolve(QrContentSpec.Create(QrContentKind.Location, ("latitude", "8.983333"), ("longitude", "-79.516667")))));
        Assert.Equal("geo:8.983333,-79.516667?q=8.983333,-79.516667(Panama%20City)",
            Text(Resolve(QrContentSpec.Create(QrContentKind.Location, ("latitude", "8,983333"), ("longitude", "-79,516667"), ("label", "Panama City")))));   // decimal commas of a data column
        Assert.Equal("BEGIN:VEVENT\r\nSUMMARY:Lunch\r\nDTSTART:20260314T140000Z\r\nDTEND:20260314T153000Z\r\nLOCATION:Panama\r\nEND:VEVENT",
            Text(Resolve(QrContentSpec.Create(QrContentKind.CalendarEvent, ("title", "Lunch"), ("start", "2026-03-14T09:00:00-05:00"), ("end", "2026-03-14T10:30:00-05:00"), ("location", "Panama")))));
        Assert.Equal("BEGIN:VEVENT\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20261224\r\nDTEND;VALUE=DATE:20261226\r\nEND:VEVENT",
            Text(Resolve(QrContentSpec.Create(QrContentKind.CalendarEvent, ("title", "Holiday"), ("start", "2026-12-24"), ("end", "2026-12-25"), ("allDay", "true")))));
    }

    [Fact]
    public void Event_WithoutAnOffset_IsUtc_AndSaysSo()
    {
        var r = Resolve(QrContentSpec.Create(QrContentKind.CalendarEvent, ("title", "t"), ("start", "2026-03-14T09:00:00"), ("end", "2026-03-14T10:00:00")));
        Assert.Contains("DTSTART:20260314T090000Z", r.Text);
        Assert.Contains(r.Issues, i => i.Field == "start" && i.Issue.MessageKey == "QrContent.Event.NoOffset");
    }

    [Fact]
    public void Location_UsesAPoint_WhateverTheCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            Assert.Equal("geo:1.5,-2.25", Text(Resolve(QrContentSpec.Create(QrContentKind.Location, ("latitude", "1.5"), ("longitude", "-2,25")))));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    // ---- templates ------------------------------------------------------------------------------------

    [Fact]
    public void Template_KeepsThePlaceholders_AndEscapesFixedText()
    {
        var wifi = QrContentSpec.Create(QrContentKind.Wifi, ("ssid", "{{wifi_name}}"), ("password", "{{wifi_pass}}"), ("security", "Wpa"));
        Assert.Equal("WIFI:T:WPA;S:{{wifi_name}};P:{{wifi_pass}};H:false;;", QrContentBuilder.BuildTemplate(wifi));
        var card = QrContentSpec.Create(QrContentKind.VCard, ("firstName", "{{first}}"), ("lastName", "O'Neil, Jr"), ("phone", "{{phone}}"));
        Assert.Equal("BEGIN:VCARD\r\nVERSION:3.0\r\nN:O'Neil\\, Jr;{{first}};;;\r\nFN:{{first}} O'Neil\\, Jr\r\nTEL;TYPE=WORK,VOICE:{{phone}}\r\nEND:VCARD", QrContentBuilder.BuildTemplate(card));
        Assert.Equal("mailto:{{email}}?subject=Order%20{{order}}", QrContentBuilder.BuildTemplate(QrContentSpec.Create(QrContentKind.Email, ("to", "{{email}}"), ("subject", "Order {{order}}"))));
        Assert.Equal("tel:{{phone}}", QrContentBuilder.BuildTemplate(QrContentSpec.Create(QrContentKind.Phone, ("number", "{{phone}}"))));
        Assert.Equal("geo:{{lat}},{{lon}}", QrContentBuilder.BuildTemplate(QrContentSpec.Create(QrContentKind.Location, ("latitude", "{{lat}}"), ("longitude", "{{lon}}"))));
        Assert.Equal("https://id.gs1.org/01/{{gtin}}/10/{{lot}}?17={{exp}}", QrContentBuilder.BuildTemplate(QrContentSpec.Create(QrContentKind.GS1DigitalLink, ("gtin", "{{gtin}}"), ("lot", "{{lot}}"), ("expiry", "{{exp}}"))));
    }

    [Fact]
    public void Template_WithoutPlaceholders_EqualsTheResolvedPayload()
    {
        foreach (var kind in QrContentSchema.All)
        {
            var spec = new QrContentSpec(kind.Kind, kind.Fields.ToDictionary(f => f.Name, f => f.Sample));
            Assert.Equal(Text(QrContentBuilder.Resolve(spec, NoRecord)), QrContentBuilder.BuildTemplate(spec));
        }
    }

    [Fact]
    public void Validate_ChecksADesignWithPlaceholders_UsingSamples()
    {
        Assert.Empty(QrContentBuilder.Validate(QrContentSpec.Create(QrContentKind.Phone, ("number", "{{phone}}"))));
        var issues = QrContentBuilder.Validate(QrContentSpec.Create(QrContentKind.Email, ("to", "{{mail}}"), ("subject", new string('s', 300))));
        Assert.Contains(issues, i => i.Field == "subject" && i.Issue.MessageKey == "QrContent.Field.TooLong");
        Assert.Contains(QrContentBuilder.Validate(QrContentSpec.Create(QrContentKind.Phone, ("number", "abc"))), i => i.Field == "number");
        Assert.Contains(QrContentBuilder.Validate(new QrContentSpec(QrContentKind.Wifi, new Dictionary<string, string>())), i => i.Field == "ssid");
    }

    // ---- injection -------------------------------------------------------------------------------------

    private static readonly string[] VCardProperties = { "BEGIN", "VERSION", "N", "FN", "ORG", "TITLE", "TEL", "EMAIL", "URL", "ADR", "NOTE", "END" };

    private static void AssertOnlyKnownVCardLines(string payload)
    {
        var lines = payload.Split("\r\n");
        Assert.All(lines, l => Assert.Contains(VCardProperties, p => l.StartsWith(p + ":", StringComparison.Ordinal) || l.StartsWith(p + ";", StringComparison.Ordinal) || l.StartsWith(' ')));
        Assert.Single(lines, l => l == "BEGIN:VCARD");
        Assert.Single(lines, l => l == "END:VCARD");
    }

    [Theory]
    [InlineData("Ana\r\nTEL:666-INJECTED")]
    [InlineData("Ana\nEND:VCARD\nBEGIN:VCARD\nFN:Evil")]
    [InlineData("Ana\u2028TEL:666")]
    [InlineData("Smith; John")]
    [InlineData("Ana\0TEL:666")]
    public void VCard_NameCannotInjectProperties(string hostile)
    {
        var spec = QrContentSpec.Create(QrContentKind.VCard, ("firstName", "{{n}}"), ("lastName", "{{n}}"), ("organization", "{{n}}"), ("title", "{{n}}"), ("note", "{{n}}"), ("street", "{{n}}"));
        string payload = Text(Resolve(spec, ("n", hostile)));
        AssertOnlyKnownVCardLines(payload);
        Assert.DoesNotContain("\r\nTEL", payload);
        Assert.DoesNotContain("\r\nFN:Evil", payload);
    }

    [Fact]
    public void VCard_SemicolonAndCommaStayInsideTheirField()
    {
        string payload = Text(Resolve(QrContentSpec.Create(QrContentKind.VCard, ("firstName", "{{f}}"), ("lastName", "{{l}}")), ("f", "John"), ("l", "Smith; Jr, Esq")));
        Assert.Contains("N:Smith\\; Jr\\, Esq;John;;;", payload);
    }

    [Fact]
    public void VCard_NoteLineBreaksBecomeEscapedNewlines_NotLines()
    {
        string payload = Text(Resolve(QrContentSpec.Create(QrContentKind.VCard, ("firstName", "A"), ("note", "{{n}}")), ("n", "line1\r\nline2\nEND:VCARD")));
        Assert.Contains("NOTE:line1\\nline2\\nEND\\:VCARD".Replace("\\:", ":"), payload);
        Assert.Single(payload.Split("\r\n"), l => l == "END:VCARD");
    }

    [Fact]
    public void VCard_FoldingKeepsEveryLineWithin75Octets_WithAccentsAndCjk()
    {
        string hostile = string.Concat(Enumerable.Repeat("東京都é", 30));
        string payload = Text(Resolve(QrContentSpec.Create(QrContentKind.VCard, ("firstName", "A"), ("note", "{{n}}")), ("n", hostile)));
        Assert.All(payload.Split("\r\n"), l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.Contains("NOTE:" + hostile, payload.Replace("\r\n ", string.Empty));
    }

    [Fact]
    public void VCard_UrlWithAWhitespaceInjectionIsRefused()
    {
        var r = Resolve(QrContentSpec.Create(QrContentKind.VCard, ("firstName", "A"), ("url", "{{u}}")), ("u", "https://x.test/a b"));
        Assert.True(r.HasErrors);
        Assert.Null(r.Text);
    }

    [Theory]
    [InlineData("Perez;TEL:666")]
    [InlineData("Perez;;NOTE:pwned")]
    [InlineData("Pe\r\nrez")]
    public void MeCard_FieldsCannotAddFields(string hostile)
    {
        string payload = Text(Resolve(QrContentSpec.Create(QrContentKind.MeCard, ("lastName", "{{n}}"), ("note", "{{n}}")), ("n", hostile)));
        // Every special character of the value is escaped, so the payload is exactly the two fields and the terminator.
        static string Esc(string v) => System.Text.RegularExpressions.Regex.Replace(v, @"([\\;,:])", "\\$1");
        string cleaned = QrContentBuilder.Clean(hostile, false);
        Assert.Equal("MECARD:N:" + Esc(cleaned) + ";NOTE:" + Esc(QrContentBuilder.Clean(hostile, true).Replace("\n", " ")) + ";;", payload);
        Assert.DoesNotContain("\n", payload);
        Assert.DoesNotContain("\r", payload);
    }

    [Theory]
    [InlineData("x;S:evil;;")]
    [InlineData("x\";T:nopass;S:evil")]
    [InlineData("a:b,c\\d")]
    [InlineData("pass\r\nH:true")]
    public void Wifi_PasswordAndNameCannotInjectFields(string hostile)
    {
        string padded = hostile.PadRight(8, 'z');
        var spec = QrContentSpec.Create(QrContentKind.Wifi, ("ssid", "{{v}}"), ("password", "{{v}}"), ("security", "Wpa"));
        string payload = Text(Resolve(spec, ("v", padded)));
        var parsed = Assert.IsType<WifiParsedResult>(ResultParser.parseResult(new Result(payload, null, null, BarcodeFormat.QR_CODE)));
        // A scanner reads back exactly the one network and the one password that were given.
        string cleaned = QrContentBuilder.Clean(padded, false);
        Assert.Equal(cleaned, parsed.Ssid);
        Assert.Equal(cleaned, parsed.Password);
        Assert.Equal("WPA", parsed.NetworkEncryption);
        Assert.False(parsed.Hidden);
    }

    [Fact]
    public void Wifi_InjectedFieldMarkersAreEscaped()
    {
        string payload = Text(Resolve(QrContentSpec.Create(QrContentKind.Wifi, ("ssid", "net"), ("password", "{{p}}"), ("security", "Wpa")), ("p", "x;S:evil;;")));
        Assert.Equal("WIFI:T:WPA;S:net;P:x\\;S\\:evil\\;\\;;H:false;;", payload);
    }

    [Fact]
    public void Calendar_TextCannotAddPropertiesOrLines()
    {
        var spec = QrContentSpec.Create(QrContentKind.CalendarEvent, ("title", "{{t}}"), ("start", "2026-03-14T09:00:00Z"), ("end", "2026-03-14T10:00:00Z"),
            ("location", "{{t}}"), ("description", "{{t}}"));
        string payload = Text(Resolve(spec, ("t", "Meet\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nSUMMARY:Evil; x, y")));
        Assert.Single(payload.Split("\r\n"), l => l == "BEGIN:VEVENT");
        Assert.Single(payload.Split("\r\n"), l => l == "END:VEVENT");
        Assert.Equal(1, payload.Split("\r\n").Count(l => l.StartsWith("SUMMARY:", StringComparison.Ordinal)));
        Assert.Contains("Evil\\; x\\, y", payload);
    }

    [Fact]
    public void Email_SubjectAndBodyCannotAddHeaders()
    {
        string payload = Text(Resolve(QrContentSpec.Create(QrContentKind.Email, ("to", "ana@example.com"), ("subject", "{{s}}"), ("body", "{{s}}")), ("s", "hi&bcc=evil@x.com?cc=x\r\nBcc: evil")));
        Assert.Equal(1, payload.Count(c => c == '?'));
        Assert.Equal(1, payload.Count(c => c == '&'));        // only the one between subject and body
        // a recipient that tries to add a query or a second address in another way is no valid address
        Assert.True(Resolve(QrContentSpec.Create(QrContentKind.Email, ("to", "{{t}}")), ("t", "a@x.com?bcc=e@v.il")).HasErrors);
    }

    [Fact]
    public void PhoneAndSms_RefuseAnythingButANumber()
    {
        foreach (var hostile in new[] { "+507;ext=1", "123\r\nTEL", "tel:+1", "+507 61 23 4567 ,p1" })
        {
            Assert.True(Resolve(QrContentSpec.Create(QrContentKind.Phone, ("number", "{{n}}")), ("n", hostile)).HasErrors, hostile);
            Assert.True(Resolve(QrContentSpec.Create(QrContentKind.Sms, ("number", "{{n}}")), ("n", hostile)).HasErrors, hostile);
        }
        // The message is the last part of an SMSTO payload; line breaks are kept out of single-line fields only.
        Assert.Equal("SMSTO:+50761234567:a:b;c", Text(Resolve(QrContentSpec.Create(QrContentKind.Sms, ("number", "+50761234567"), ("message", "{{m}}")), ("m", "a:b;c"))));
    }

    [Fact]
    public void DigitalLink_ValuesAreEncodedAndCannotAddPathOrQuery()
    {
        string payload = Text(Resolve(QrContentSpec.Create(QrContentKind.GS1DigitalLink, ("gtin", "09506000134352"), ("lot", "{{l}}")), ("l", "A/../B?x=1&y=2")));
        Assert.Equal("https://id.gs1.org/01/09506000134352/10/A%2F..%2FB%3Fx%3D1%26y%3D2", payload);
    }

    [Fact]
    public void Url_TrackingStrip_AndSchemeChecks()
    {
        Assert.Contains(Resolve(QrContentSpec.Create(QrContentKind.Url, ("url", "{{u}}")), ("u", "javascript:alert(1)")).Issues, i => i.Issue.MessageKey == "QrContent.Url.Scheme");
        Assert.True(Resolve(QrContentSpec.Create(QrContentKind.Url, ("url", "{{u}}")), ("u", "https://x.test/a\r\nb")).HasErrors);
    }

    // ---- accents, CJK, ZPL and preview -----------------------------------------------------------------

    private static LabelDocument QrDoc(QrContentSpec spec, Action<BarcodeElement>? tweak = null)
    {
        var b = new BarcodeElement
        {
            Symbology = BarcodeSymbology.QrCode, X = 40, Y = 40, ModuleSize = 4, Data = QrContentBuilder.BuildTemplate(spec), QrContentSpec = spec.ToJson(),
            QrErrorCorrection = QrErrorCorrection.Medium,
        };
        tweak?.Invoke(b);
        var doc = NewDoc(b);
        foreach (var name in spec.Placeholders()) doc.Variables.Add(new LabelVariable(name, "sample"));
        return doc;
    }

    [Fact]
    public void Utf8Values_GoThroughHexEscapes_AndThePreviewDecodesToTheExactPayload()
    {
        var spec = QrContentSpec.Create(QrContentKind.VCard, ("firstName", "{{first}}"), ("lastName", "{{last}}"), ("street", "{{street}}"), ("city", "{{city}}"));
        var record = new Dictionary<string, string> { ["first"] = "José", ["last"] = "Peña", ["street"] = "Calle 5 #12, Ñandú", ["city"] = "東京" };
        var doc = QrDoc(spec);
        var result = ZplGenerator.Generate(doc, record);
        Assert.False(result.HasErrors, string.Join(", ", result.Issues));
        // ^CI28 is on, the data is UTF-8 written as _XX per byte, and ^FH says so: é = _C3_A9, 東 = _E6_9D_B1.
        Assert.Contains("^CI28", result.Zpl);
        Assert.Contains("^FH_", result.Zpl);
        Assert.Contains("Jos_C3_A9", result.Zpl);
        Assert.Contains("Pe_C3_B1a", result.Zpl);
        Assert.Contains("_E6_9D_B1_E4_BA_AC", result.Zpl);
        Assert.Contains("Calle 5 #12\\, _C3_91and_C3_BA", result.Zpl);

        string payload = QrContentBuilder.Resolve(spec, record).Text!;
        var read = RoundTrip(BarcodeSymbology.QrCode, payload, BarcodeFormat.QR_CODE);
        Assert.Equal(payload, read.Text);
        Assert.Contains("Peña;José", read.Text);
        Assert.Contains("東京", read.Text);
    }

    [Fact]
    public void EachRecordGetsItsOwnEscapedPayload()
    {
        var spec = QrContentSpec.Create(QrContentKind.VCard, ("firstName", "{{first}}"), ("lastName", "{{last}}"));
        var doc = QrDoc(spec, b => b.QrErrorCorrection = QrErrorCorrection.Low);
        string Zpl(string first, string last) => ZplGenerator.Generate(doc, new Dictionary<string, string> { ["first"] = first, ["last"] = last }).Zpl;
        string a = Zpl("Ana", "Perez"), b = Zpl("John", "Smith; Jr");
        Assert.Contains("N:Perez;Ana;;;", a);
        Assert.Contains("N:Smith\\; Jr;John;;;", b);
        Assert.NotEqual(a, b);
        // the template in Data is not what is printed
        Assert.DoesNotContain("{{", a);
    }

    [Fact]
    public void MissingVariable_IsAWarning_AndTheFieldIsEmpty()
    {
        var spec = QrContentSpec.Create(QrContentKind.Phone, ("number", "{{phone}}"));
        var doc = NewDoc(new BarcodeElement { Symbology = BarcodeSymbology.QrCode, QrContentSpec = spec.ToJson(), Data = "tel:{{phone}}", X = 10, Y = 10 });
        var result = ZplGenerator.Generate(doc);
        // an empty required field is an error: nothing is printed from a broken payload
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.MessageKey == "Variable.Missing");
        Assert.DoesNotContain("^BQ", result.Zpl);
    }

    [Fact]
    public void AnElementWithoutASpec_StillUsesItsData()
    {
        var doc = NewDoc(new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "Order {{id}}", X = 10, Y = 10 });
        Assert.Contains("^FDQA,Order 77^FS", ZplGenerator.Generate(doc, new Dictionary<string, string> { ["id"] = "77" }).Zpl.Replace("QA,Order", "QA,Order"));
    }

    // ---- capacity ---------------------------------------------------------------------------------------

    [Fact]
    public void Capacity_WarnsAndSuggestsWhatToDo()
    {
        var text = QrContentSpec.Create(QrContentKind.Text, ("text", "{{t}}"));
        // 2000 characters need 2000 bytes: more than 1663 (Q) but within 2331 (M)
        var q = QrContentBuilder.Resolve(text, new Dictionary<string, string> { ["t"] = new string('a', 2000) }, QrErrorCorrection.Quartile);
        Assert.Contains(q.Issues, i => i.Issue.MessageKey == "QrContent.Capacity.Exceeded" && i.Issue.Args.SequenceEqual(new[] { "2000", "1663", "Quartile" }));
        Assert.Contains(q.Issues, i => i.Issue.MessageKey == "QrContent.Capacity.TryLowerLevel" && i.Issue.Args[0] == "Medium");
        var tooMuch = QrContentBuilder.Resolve(text, new Dictionary<string, string> { ["t"] = new string('é', 3000) }, QrErrorCorrection.Low);   // 6000 bytes
        Assert.Contains(tooMuch.Issues, i => i.Issue.MessageKey == "QrContent.Capacity.Shorten");
        var dense = QrContentBuilder.Resolve(text, new Dictionary<string, string> { ["t"] = new string('a', 1500) }, QrErrorCorrection.Quartile);
        Assert.Contains(dense.Issues, i => i.Issue.MessageKey == "QrContent.Capacity.Dense");
        var small = QrContentBuilder.Resolve(text, new Dictionary<string, string> { ["t"] = "hi" }, QrErrorCorrection.Quartile);
        Assert.DoesNotContain(small.Issues, i => i.Issue.MessageKey.StartsWith("QrContent.Capacity.", StringComparison.Ordinal));
    }
}
