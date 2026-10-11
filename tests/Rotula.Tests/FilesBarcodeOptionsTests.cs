using System.Text.Json;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>The .rotula file must keep every option of the later barcode symbologies and stay safe on hostile input.</summary>
public class FilesBarcodeOptionsTests
{
    /// <summary>An element with every extra option changed away from its default.</summary>
    private static BarcodeElement NonDefault(BarcodeSymbology symbology) => new()
    {
        Symbology = symbology, Data = BarcodeCatalog.Get(symbology).ExampleData,
        Code11CheckDigitCount = 1, MsiCheckDigit = MsiCheckDigitMode.Mod11Mod10, MsiCheckDigitInText = true,
        CodabarStart = 'B', CodabarStop = 'D', DataBarType = DataBarType.Expanded, DataBarSeparatorHeight = 2, DataBarSegmentsPerRow = 8,
        AztecEci = true, AztecErrorControl = 205, AztecMenuSymbol = true, AztecStructuredAppendCount = 3, AztecStructuredAppendId = "SET-9",
        MicroPdf417Mode = 7, CodablockMode = CodablockMode.A, CodablockSecurity = false,
        MaxiCodeMode = 4, MaxiCodeSymbolNumber = 2, MaxiCodeSymbolTotal = 3,
        Tlc39MicroModuleWidth = 3, Tlc39MicroRowHeight = 9, Code49StartMode = '3',
    };

    public static IEnumerable<object[]> NewSymbologies() =>
        Enum.GetValues<BarcodeSymbology>().Where(s => s > BarcodeSymbology.Pdf417).Select(s => new object[] { s });

    private static BarcodeElement RoundTrip(BarcodeElement element, out string json)
    {
        var doc = new LabelDocument();
        doc.Elements.Add(element);
        json = RotulaFileSerializer.Serialize(doc);
        return (BarcodeElement)RotulaFileSerializer.Deserialize(json).Elements[0];
    }

    [Theory, MemberData(nameof(NewSymbologies))]
    public void EveryNewOption_RoundTripsForEveryNewSymbology(BarcodeSymbology symbology)
    {
        var original = NonDefault(symbology);
        var copy = RoundTrip(original, out var json);

        Assert.Equal(symbology, copy.Symbology);
        Assert.Equal(original.ExportExtraOptions(), copy.ExportExtraOptions());
        Assert.Equal(22, copy.ExportExtraOptions().Count);
        Assert.Equal(RotulaFileSerializer.BarcodeBreadthFormatVersion, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void ACopyAndPaste_KeepsTheOptions()
    {
        var original = NonDefault(BarcodeSymbology.DataBar);
        var back = RotulaFileSerializer.DeserializeElements(RotulaFileSerializer.SerializeElements(new LabelElement[] { original }));
        Assert.Equal(original.ExportExtraOptions(), ((BarcodeElement)back[0]).ExportExtraOptions());
    }

    [Fact]
    public void AnOldBarcode_StaysAtVersionOne_AndHasNoExtraOptionsField()
    {
        var doc = new LabelDocument();
        doc.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "x" });
        string json = RotulaFileSerializer.Serialize(doc);
        Assert.Equal(1, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());
        Assert.DoesNotContain("extraOptions", json);
    }

    [Fact]
    public void ANewSymbologyWithDefaultOptions_NeedsVersionFive()
    {
        var doc = new LabelDocument();
        doc.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.Ean8, Data = "9638507" });
        string json = RotulaFileSerializer.Serialize(doc);
        Assert.Equal(5, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());
        Assert.DoesNotContain("extraOptions", json);
    }

    [Fact]
    public void AFileWrittenWithoutExtras_OpensWithDefaults()
    {
        const string json = """{"formatVersion":1,"elements":[{"type":"barcode","symbology":"Code39","data":"A1"}]}""";
        var b = (BarcodeElement)RotulaFileSerializer.Deserialize(json).Elements[0];
        Assert.Equal(BarcodeSymbology.Code39, b.Symbology);
        Assert.Empty(b.ExportExtraOptions());
    }

    [Fact]
    public void HostileExtras_AreIgnoredWithoutFailingTheFile()
    {
        string huge = new('9', 100_000);
        var many = string.Join(",", Enumerable.Range(0, 5000).Select(i => $"\"K{i}\":\"v\""));
        string json = $$"""
            {"formatVersion":5,"elements":[{"type":"barcode","symbology":"Aztec","data":"x","extraOptions":{
              "NoSuchOption":"1","MaxiCodeMode":"{{huge}}","MsiCheckDigit":"7","DataBarType":"Evil","CodabarStart":"ABC","AztecEci":"maybe",
              "Code11CheckDigitCount":{"a":1},"Tlc39MicroRowHeight":[1],"Code49StartMode":null,"AztecStructuredAppendId":"{{huge}}",
              "AztecMenuSymbol":true,"MicroPdf417Mode":12, {{many}} } }]}
            """;
        var b = (BarcodeElement)RotulaFileSerializer.Deserialize(json).Elements[0];
        var fresh = new BarcodeElement();
        Assert.Equal(fresh.MaxiCodeMode, b.MaxiCodeMode);
        Assert.Equal(fresh.MsiCheckDigit, b.MsiCheckDigit);
        Assert.Equal(fresh.DataBarType, b.DataBarType);
        Assert.Equal(fresh.CodabarStart, b.CodabarStart);
        Assert.Equal(fresh.AztecEci, b.AztecEci);
        Assert.Equal(fresh.Code11CheckDigitCount, b.Code11CheckDigitCount);
        Assert.Equal(fresh.Tlc39MicroRowHeight, b.Tlc39MicroRowHeight);
        Assert.Equal(fresh.Code49StartMode, b.Code49StartMode);
        Assert.Equal(string.Empty, b.AztecStructuredAppendId);
        // Well-formed values next to the hostile ones are still applied, including a bare JSON number or boolean.
        Assert.True(b.AztecMenuSymbol);
        Assert.Equal(12, b.MicroPdf417Mode);
    }

    [Fact]
    public void ExtrasNotAnObject_AreACleanCorruptFileError()
    {
        const string json = """{"formatVersion":5,"elements":[{"type":"barcode","symbology":"Aztec","extraOptions":"nope"}]}""";
        var ex = Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(json));
        Assert.Equal("File.Error.Corrupt", ex.MessageKey);
    }

    [Fact]
    public void AFileNewerThanThisBuild_IsStillRefused()
    {
        var ex = Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize("{\"formatVersion\":" + (RotulaFileSerializer.CurrentFormatVersion + 1) + "}"));
        Assert.Equal("File.Error.NewerVersion", ex.MessageKey);
    }
}
