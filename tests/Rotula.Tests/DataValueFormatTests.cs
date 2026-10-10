using System.Globalization;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>The documented value-to-string rules, checked under a hostile culture.</summary>
public class DataValueFormatTests
{
    public static IEnumerable<object?[]> Cases() => new (object? Value, string Expected)[]
    {
        (null, ""),
        (DBNull.Value, ""),
        ("  keeps  spaces ", "  keeps  spaces "),
        ("Niño café ¿?", "Niño café ¿?"),
        ('x', "x"),
        (true, "TRUE"),
        (false, "FALSE"),
        (42, "42"),
        (-7L, "-7"),
        ((byte)255, "255"),
        (12.50m, "12.50"),
        (1234567.891m, "1234567.891"),
        (0.1 + 0.2, "0.30000000000000004"),
        (1.5f, "1.5"),
        (1e21, "1E+21"),
        (double.NaN, "NaN"),
        (new DateTime(2024, 3, 5), "2024-03-05"),
        (new DateTime(2024, 3, 5, 14, 30, 0), "2024-03-05 14:30:00"),
        (new DateTime(2024, 3, 5, 14, 30, 0, 250), "2024-03-05 14:30:00.25"),
        (new DateTimeOffset(2024, 3, 5, 14, 30, 0, TimeSpan.FromHours(-5)), "2024-03-05 14:30:00-05:00"),
        (new DateOnly(2024, 12, 31), "2024-12-31"),
        (new TimeOnly(9, 5, 0), "09:05:00"),
        (new TimeSpan(13, 45, 0), "13:45:00"),
        (new Guid("0F8FAD5B-D9CB-469F-A165-70867728950E"), "0f8fad5b-d9cb-469f-a165-70867728950e"),
        (new byte[] { 0x00, 0xAB, 0xFF }, "0x00ABFF"),
        (Array.Empty<byte>(), "0x"),
        (new[] { 1, 2, 3 }, "1, 2, 3"),
        (new[] { "a", "b" }, "a, b"),
    }.Select(c => new object?[] { c.Value, c.Expected });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Formats_Invariantly(object? value, string expected)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            // A culture with comma decimals, different digits and a different calendar must not leak into labels.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(expected, DatabaseValueFormatter.Format(value));
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            Assert.Equal(expected, DatabaseValueFormatter.Format(value));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}
