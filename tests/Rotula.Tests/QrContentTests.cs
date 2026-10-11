using System.Globalization;
using System.Text;
using Rotula.Core.Barcodes.QrContent;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using ZXing;
using ZXing.Client.Result;
using static Rotula.Tests.Gs1DecodeSupport;

namespace Rotula.Tests;

/// <summary>
/// The QR content builders: exact payloads, escaping, validation, and a read-back through a QR Code with ZXing, whose own result parsers
/// (the ones phones' scanners are modelled on) must understand what was written.
/// </summary>
public class QrContentTests
{
    private static ParsedResult Parse(string payload)
    {
        // Drawn as a real QR Code and read back: the payload must survive the symbol exactly.
        var read = RoundTrip(BarcodeSymbology.QrCode, payload, BarcodeFormat.QR_CODE);
        Assert.Equal(payload, read.Text);
        return ResultParser.parseResult(new Result(read.Text, null, null, BarcodeFormat.QR_CODE));
    }

    private static List<string> Keys(IQrContent c) => c.Validate().Select(i => i.MessageKey).ToList();

    // ---- URL ----------------------------------------------------------------------------------------

    [Fact]
    public void Url_IsWrittenAsGiven()
    {
        var c = new QrUrlContent("  https://example.com/menu?table=7  ");
        Assert.Empty(c.Validate());
        Assert.Equal("https://example.com/menu?table=7", c.Build());
        var parsed = Assert.IsType<URIParsedResult>(Parse(c.Build()));
        Assert.Equal("https://example.com/menu?table=7", parsed.URI);
    }

    [Theory]
    [InlineData("", "QrContent.Url.Empty")]
    [InlineData("example.com", "QrContent.Url.Invalid")]
    [InlineData("https://exa mple.com", "QrContent.Url.Invalid")]
    [InlineData("file:///C:/secret.txt", "QrContent.Url.Invalid")]
    [InlineData("ftp://example.com/a", "QrContent.Url.Scheme")]
    public void Url_Problems(string url, string key)
    {
        var c = new QrUrlContent(url);
        Assert.Contains(key, Keys(c));
        if (key != "QrContent.Url.Scheme") Assert.Throws<InvalidOperationException>(() => c.Build());
    }

    // ---- Wi-Fi --------------------------------------------------------------------------------------

    [Fact]
    public void Wifi_WritesTheDocumentedLayout()
    {
        Assert.Equal("WIFI:T:WPA;S:CafeGuest;P:welcome2026;H:false;;", new QrWifiContent("CafeGuest", "welcome2026").Build());
        Assert.Equal("WIFI:T:WPA;S:Hidden;P:welcome2026;H:true;;", new QrWifiContent("Hidden", "welcome2026", WifiSecurity.Wpa, Hidden: true).Build());
        Assert.Equal("WIFI:T:SAE;S:Home;P:correct horse;H:false;;", new QrWifiContent("Home", "correct horse", WifiSecurity.Wpa3Sae).Build());
        Assert.Equal("WIFI:T:WEP;S:Old;P:klmno;H:false;;", new QrWifiContent("Old", "klmno", WifiSecurity.Wep).Build());
        Assert.Equal("WIFI:T:nopass;S:Open;H:false;;", new QrWifiContent("Open", "ignored", WifiSecurity.None).Build());
    }

    [Fact]
    public void Wifi_EscapesTheCharactersOfTheSyntax()
    {
        // \ ; , : " each get a backslash (ZXing wiki, Barcode Contents).
        var c = new QrWifiContent("My;Net:\"x\"\\y,z", "pa;ss:wo\\rd,\"1");
        Assert.Equal("WIFI:T:WPA;S:My\\;Net\\:\\\"x\\\"\\\\y\\,z;P:pa\\;ss\\:wo\\\\rd\\,\\\"1;H:false;;", c.Build());
        var parsed = Assert.IsType<WifiParsedResult>(Parse(c.Build()));
        Assert.Equal("My;Net:\"x\"\\y,z", parsed.Ssid);
        Assert.Equal("pa;ss:wo\\rd,\"1", parsed.Password);
    }

    [Fact]
    public void Wifi_QuotesNamesAndPasswordsThatAreOnlyHexDigits()
    {
        Assert.Equal("WIFI:T:WPA;S:\"1234\";P:\"deadbeef\";H:false;;", new QrWifiContent("1234", "deadbeef").Build());
    }

    [Fact]
    public void Wifi_IsReadByTheScannerParser()
    {
        var parsed = Assert.IsType<WifiParsedResult>(Parse(new QrWifiContent("CafeGuest", "welcome2026", WifiSecurity.Wpa, Hidden: true).Build()));
        Assert.Equal("CafeGuest", parsed.Ssid);
        Assert.Equal("welcome2026", parsed.Password);
        Assert.Equal("WPA", parsed.NetworkEncryption);
        Assert.True(parsed.Hidden);
    }

    [Theory]
    [InlineData("", "pass1234", WifiSecurity.Wpa, "QrContent.Wifi.Ssid.Empty")]
    [InlineData("0123456789012345678901234567890123", "pass1234", WifiSecurity.Wpa, "QrContent.Wifi.Ssid.TooLong")]   // 34 bytes
    [InlineData("Net", "", WifiSecurity.Wpa, "QrContent.Wifi.Password.Missing")]
    [InlineData("Net", "short", WifiSecurity.Wpa, "QrContent.Wifi.Password.Length")]          // WPA needs 8 to 63 characters
    [InlineData("Net", "abcdef", WifiSecurity.Wep, "QrContent.Wifi.Password.Length")]         // WEP needs 5, 13, 10 or 26
    [InlineData("Net", "secret", WifiSecurity.None, "QrContent.Wifi.Password.Ignored")]
    public void Wifi_Problems(string ssid, string password, WifiSecurity security, string key) =>
        Assert.Contains(key, Keys(new QrWifiContent(ssid, password, security)));

    [Fact]
    public void Wifi_SsidLimitIsInBytes_NotCharacters()
    {
        // 17 two-byte characters are 34 bytes.
        Assert.Contains("QrContent.Wifi.Ssid.TooLong", Keys(new QrWifiContent(new string('ñ', 17), "pass1234")));
        Assert.DoesNotContain("QrContent.Wifi.Ssid.TooLong", Keys(new QrWifiContent(new string('ñ', 16), "pass1234")));
    }

    // ---- vCard --------------------------------------------------------------------------------------

    [Fact]
    public void VCard_WritesVersion3_WithCrLf()
    {
        var card = new QrVCardContent
        {
            FirstName = "Ana", LastName = "Pérez", Organization = "Rotula SA", Title = "Logistics", Phone = "+507 123-4567", Mobile = "+507 6123 4567",
            Email = "ana@example.com", Url = "https://example.com", Note = "Hello",
            Address = new QrAddress("Calle 50 #12", "Panama", "Panama", "0801", "PA"),
        };
        string text = card.Build();
        Assert.Equal(
            "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Pérez;Ana;;;\r\nFN:Ana Pérez\r\nORG:Rotula SA\r\nTITLE:Logistics\r\n" +
            "TEL;TYPE=WORK,VOICE:+5071234567\r\nTEL;TYPE=CELL,VOICE:+50761234567\r\nEMAIL;TYPE=INTERNET:ana@example.com\r\nURL:https://example.com\r\n" +
            "ADR;TYPE=WORK:;;Calle 50 #12;Panama;Panama;0801;PA\r\nNOTE:Hello\r\nEND:VCARD", text);
    }

    [Fact]
    public void VCard_EscapesCommasSemicolonsBackslashesAndNewlines()
    {
        var card = new QrVCardContent { FirstName = "Ann; Marie", LastName = "O'Neil, Jr", Note = "a\\b\nc,d;e" };
        string text = card.Build();
        Assert.Contains("N:O'Neil\\, Jr;Ann\\; Marie;;;\r\n", text);
        Assert.Contains("FN:Ann\\; Marie O'Neil\\, Jr\r\n", text);
        Assert.Contains("NOTE:a\\\\b\\nc\\,d\\;e\r\n", text);
    }

    [Fact]
    public void VCard_FoldsLongLinesAt75Octets_WithoutSplittingACharacter()
    {
        // 40 two-byte characters + the "NOTE:" label are 85 octets, so the line must be folded.
        string note = string.Concat(Enumerable.Repeat("é", 40)) + " and a very long ordinary tail that keeps going for more than one line of seventy-five octets";
        string text = new QrVCardContent { FirstName = "Ana", Note = note }.Build();
        var lines = text.Split("\r\n");
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75, l));
        Assert.Contains(lines, l => l.StartsWith(' '));
        // Unfolding (a CRLF followed by one space is removed) restores the property exactly.
        string unfolded = text.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("NOTE:" + note, unfolded);
        // No line was cut in the middle of a multi-byte character: every folded text is valid UTF-8 by itself.
        Assert.All(lines, l => Assert.Equal(l, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(l))));
    }

    [Fact]
    public void VCard_IsReadByTheScannerParser()
    {
        var card = new QrVCardContent { FirstName = "Ana", LastName = "Perez", Organization = "Rotula", Phone = "+507 123-4567", Email = "ana@example.com" };
        var parsed = Assert.IsType<AddressBookParsedResult>(Parse(card.Build()));
        Assert.Contains("Ana Perez", parsed.Names);
        Assert.Contains("+5071234567", parsed.PhoneNumbers);
        Assert.Contains("ana@example.com", parsed.Emails);
        Assert.Equal("Rotula", parsed.Org);
    }

    [Fact]
    public void VCard_Validation()
    {
        Assert.Contains("QrContent.VCard.NameMissing", Keys(new QrVCardContent()));
        Assert.DoesNotContain("QrContent.VCard.NameMissing", Keys(new QrVCardContent { Organization = "Only a company" }));
        Assert.Contains("QrContent.Email.Address", Keys(new QrVCardContent { FirstName = "A", Email = "not-an-email" }));
        Assert.Contains("QrContent.Phone.Invalid", Keys(new QrVCardContent { FirstName = "A", Phone = "call me" }));
        Assert.Contains("QrContent.Url.Invalid", Keys(new QrVCardContent { FirstName = "A", Url = "nope" }));
    }

    // ---- MeCard -------------------------------------------------------------------------------------

    [Fact]
    public void MeCard_WritesTheCompactLayout_AndEscapes()
    {
        var card = new QrMeCardContent
        {
            FirstName = "Ana", LastName = "Perez", Phone = "+507 123-4567", Email = "ana@example.com", Url = "https://example.com",
            Address = new QrAddress("Calle 50", "Panama", "", "0801", "PA"), Note = "VIP; call:first",
        };
        Assert.Equal("MECARD:N:Perez,Ana;TEL:+5071234567;EMAIL:ana@example.com;URL:https\\://example.com;ADR:,,Calle 50,Panama,,0801,PA;NOTE:VIP\\; call\\:first;;", card.Build());
        var parsed = Assert.IsType<AddressBookParsedResult>(Parse(card.Build()));
        Assert.Contains("Ana Perez", parsed.Names);
        Assert.Contains("+5071234567", parsed.PhoneNumbers);
    }

    [Fact]
    public void MeCard_NeedsAName() => Assert.Contains("QrContent.MeCard.NameMissing", Keys(new QrMeCardContent { Phone = "123456" }));

    // ---- e-mail, phone, SMS ---------------------------------------------------------------------------

    [Fact]
    public void Email_EncodesWithPercentTwenty_NotPlus()
    {
        var c = new QrEmailContent("ana@example.com", "Hello World & more", "Line1\nLine2 100% sure+ok");
        Assert.Equal("mailto:ana@example.com?subject=Hello%20World%20%26%20more&body=Line1%0D%0ALine2%20100%25%20sure%2Bok", c.Build());
        var parsed = Assert.IsType<EmailAddressParsedResult>(Parse(c.Build()));
        Assert.Equal(new[] { "ana@example.com" }, parsed.Tos);
        Assert.Equal("Hello World & more", parsed.Subject);
        Assert.Equal("Line1\r\nLine2 100% sure+ok", parsed.Body);
    }

    [Fact]
    public void Email_AddressOnlyAndSeveralRecipients()
    {
        Assert.Equal("mailto:a@example.com", new QrEmailContent("a@example.com").Build());
        Assert.Equal("mailto:a@example.com,b@example.org", new QrEmailContent("a@example.com, b@example.org").Build());
        Assert.Equal("mailto:first%2Blast@example.com", new QrEmailContent("first+last@example.com").Build());
    }

    [Theory]
    [InlineData("", "QrContent.Email.Empty")]
    [InlineData("nobody", "QrContent.Email.Address")]
    [InlineData("a@example.com, broken", "QrContent.Email.Address")]
    public void Email_Problems(string to, string key) => Assert.Contains(key, Keys(new QrEmailContent(to)));

    [Fact]
    public void Phone_RemovesTheSeparatorsPeopleType()
    {
        Assert.Equal("tel:+50761234567", new QrPhoneContent("+507 (6123) 4567").Build());
        Assert.Equal("tel:5551234", new QrPhoneContent("555.1234").Build());
        var parsed = Assert.IsType<TelParsedResult>(Parse("tel:+50761234567"));
        Assert.Equal("+50761234567", parsed.Number);
        Assert.Contains("QrContent.Phone.Invalid", Keys(new QrPhoneContent("12")));
        Assert.Contains("QrContent.Phone.Invalid", Keys(new QrPhoneContent("555-CALL")));
        Assert.Contains("QrContent.Phone.Invalid", Keys(new QrPhoneContent("+1234567890123456")));   // 16 digits: more than E.164 allows
    }

    [Fact]
    public void Sms_UsesSmsto()
    {
        var c = new QrSmsContent("+507 6123-4567", "Hello: see you at 5");
        Assert.Equal("SMSTO:+50761234567:Hello: see you at 5", c.Build());
        var parsed = Assert.IsType<SMSParsedResult>(Parse(c.Build()));
        Assert.Equal(new[] { "+50761234567" }, parsed.Numbers);
        Assert.Equal("Hello: see you at 5", parsed.Body);
        Assert.Contains("QrContent.Sms.Long", Keys(new QrSmsContent("123456", new string('x', 161))));
        Assert.Contains("QrContent.Phone.Invalid", Keys(new QrSmsContent("x", "hi")));
    }

    // ---- geo and calendar -----------------------------------------------------------------------------

    [Fact]
    public void Geo_UsesAPointWhateverTheCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            Assert.Equal("geo:8.983333,-79.516667", new QrGeoContent(8.983333, -79.516667).Build());
            Assert.Equal("geo:40.7128,-74.006,10.5", new QrGeoContent(40.7128, -74.0060, 10.5).Build());
            Assert.Equal("geo:0,0", new QrGeoContent(0, -0.0).Build());
            Assert.Equal("geo:-90,180", new QrGeoContent(-90, 180).Build());
        }
        finally { CultureInfo.CurrentCulture = saved; }
        var parsed = Assert.IsType<GeoParsedResult>(Parse("geo:8.983333,-79.516667"));
        Assert.Equal(8.983333, parsed.Latitude, 6);
        Assert.Equal(-79.516667, parsed.Longitude, 6);
    }

    [Theory]
    [InlineData(91, 0, "QrContent.Geo.Latitude")]
    [InlineData(0, -181, "QrContent.Geo.Longitude")]
    [InlineData(double.NaN, 0, "QrContent.Geo.Latitude")]
    public void Geo_Problems(double lat, double lon, string key) => Assert.Contains(key, Keys(new QrGeoContent(lat, lon)));

    [Fact]
    public void Event_IsWrittenInUtc()
    {
        // 9:00 at UTC-5 is 14:00 UTC.
        var e = new QrCalendarEventContent
        {
            Summary = "Team, lunch; room 2", Start = new DateTimeOffset(2026, 3, 14, 9, 0, 0, TimeSpan.FromHours(-5)),
            End = new DateTimeOffset(2026, 3, 14, 10, 30, 0, TimeSpan.FromHours(-5)), Location = "Panama City", Description = "Bring\nslides",
        };
        Assert.Equal("BEGIN:VEVENT\r\nSUMMARY:Team\\, lunch\\; room 2\r\nDTSTART:20260314T140000Z\r\nDTEND:20260314T153000Z\r\nLOCATION:Panama City\r\nDESCRIPTION:Bring\\nslides\r\nEND:VEVENT", e.Build());
        var parsed = Assert.IsType<CalendarParsedResult>(Parse(e.Build()));
        Assert.Equal("Team, lunch; room 2", parsed.Summary);
        Assert.Equal("Panama City", parsed.Location);
    }

    [Fact]
    public void Event_AllDayUsesDatesAndAnExclusiveEnd_AndCanBeWrapped()
    {
        var e = new QrCalendarEventContent
        {
            Summary = "Holiday", AllDay = true, Start = new DateTimeOffset(2026, 12, 24, 0, 0, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 12, 25, 0, 0, 0, TimeSpan.Zero),
            WrapInCalendar = true,
        };
        Assert.Equal("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20261224\r\nDTEND;VALUE=DATE:20261226\r\nEND:VEVENT\r\nEND:VCALENDAR", e.Build());
    }

    [Fact]
    public void Event_Validation()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Contains("QrContent.Event.SummaryMissing", Keys(new QrCalendarEventContent { Start = now, End = now }));
        Assert.Contains("QrContent.Event.EndBeforeStart", Keys(new QrCalendarEventContent { Summary = "x", Start = now.AddHours(1), End = now }));
    }

    [Fact]
    public void Event_LongSummaryIsFolded()
    {
        var e = new QrCalendarEventContent { Summary = new string('S', 200), Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch };
        Assert.All(e.Build().Split("\r\n"), l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
    }

    [Fact]
    public void EveryKind_RefusesToBuildInvalidContent()
    {
        IQrContent[] bad =
        {
            new QrUrlContent(""), new QrWifiContent(""), new QrVCardContent(), new QrMeCardContent(), new QrEmailContent(""), new QrPhoneContent(""),
            new QrSmsContent(""), new QrGeoContent(100, 0), new QrCalendarEventContent(),
        };
        foreach (var c in bad)
        {
            Assert.Contains(c.Validate(), i => i.Severity == ValidationSeverity.Error);
            Assert.Throws<InvalidOperationException>(() => c.Build());
        }
    }
}
