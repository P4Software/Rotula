using System.Text.RegularExpressions;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>Values read from a .rotula file are untrusted: an encoding or font name that would break ZPL generation is refused or skipped on load.</summary>
public class FileAndEncodingHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-harden-" + Guid.NewGuid().ToString("N"));

    public FileAndEncodingHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temp folder, disposable */ }
    }

    private static string Json(Action<LabelDocument> configure)
    {
        var doc = CoreTestSupport.NewDoc(new TextElement { Id = "t", X = 10, Y = 10, Text = "Hola" });
        configure(doc);
        return RotulaFileSerializer.Serialize(doc);
    }

    [Fact]
    public void ACodePageThatDoesNotExist_IsRefusedOnLoad()
    {
        string json = Regex.Replace(Json(d => d.Encoding = ZplEncoding.Cp850), @"""codePage"":\s*850", @"""codePage"": 99999");

        var ex = Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(json));
        Assert.Equal("File.Error.Corrupt", ex.MessageKey);
    }

    [Fact]
    public void ACodePageThatDoesNotMatchTheCiCode_IsRefusedOnLoad()
    {
        // ^CI13 is code page 850; claiming another page would print different characters than the file says.
        string json = Regex.Replace(Json(d => d.Encoding = ZplEncoding.Cp850), @"""codePage"":\s*850", @"""codePage"": 1252");

        Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(27)]
    [InlineData(28)]
    public void TheEncodingsTheAppWrites_StillRoundTrip(int ci)
    {
        ZplEncoding.TryFromCiCode(ci, out var enc, out _);
        var doc = RotulaFileSerializer.Deserialize(Json(d => d.Encoding = enc));

        Assert.Equal(enc, doc.Encoding);
    }

    [Fact]
    public void GenerationNeverThrowsForAnUnusableEncoding_ItReportsAnError()
    {
        var doc = CoreTestSupport.NewDoc(new TextElement { Id = "t", X = 10, Y = 10, Text = "Hola" });
        doc.Encoding = ZplEncoding.Legacy(13, 99999);

        var result = ZplGenerator.Generate(doc);

        Assert.Contains(result.Issues, i => i.MessageKey == "Encoding.Unsupported" && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void AQrManualByteCodeWithACharacterTheCodePageCannotHold_DoesNotThrow()
    {
        var doc = CoreTestSupport.NewDoc(new BarcodeElement { Id = "q", Symbology = BarcodeSymbology.QrCode, QrInputMode = QrInputMode.ManualByte, Data = "A€B", X = 10, Y = 10 });
        doc.Encoding = ZplEncoding.Cp850; // the euro sign is not in code page 850

        var result = ZplGenerator.Generate(doc);

        // The '?' that replaces the character is one byte, so the declared count is 3 like the bytes that are sent.
        Assert.Contains("^FDQM,B0003A?B^FS", result.Zpl);
        Assert.Contains(result.Issues, i => i.MessageKey == "Text.UnmappableChar");
    }

    [Fact]
    public void AFontAliasPathWithZplControlCharacters_IsSkippedOnLoad()
    {
        string json = Regex.Replace(Json(d => d.FontAliases.Add(new FontAlias('Q', "E:ARIAL.TTF"))), @"E:ARIAL\.TTF", "E:A^FO.TTF");

        var doc = RotulaFileSerializer.Deserialize(json);

        Assert.Empty(doc.FontAliases);
    }

    [Fact]
    public void AValidFontAlias_SurvivesTheRoundTrip()
    {
        var doc = RotulaFileSerializer.Deserialize(Json(d => d.FontAliases.Add(new FontAlias('Q', "E:ARIAL.TTF"))));

        Assert.Equal("E:ARIAL.TTF", Assert.Single(doc.FontAliases).Path);
    }

    [Fact]
    public void AnInvalidFontAlias_IsNeverWrittenIntoTheZpl()
    {
        var doc = CoreTestSupport.NewDoc(new TextElement { Id = "t", X = 10, Y = 10, Text = "Hola" });
        doc.FontAliases.Add(new FontAlias('Q', "E:A^XZ~DGR:X.GRF,1,1,00.TTF"));

        var result = ZplGenerator.Generate(doc);

        Assert.Contains(result.Issues, i => i.MessageKey == "Font.Alias.Invalid");
        Assert.DoesNotContain("^CW", result.Zpl);
        Assert.DoesNotContain("~DG", result.Zpl);
    }

    [Fact]
    public void AFailedSave_LeavesNoTempFileBehind()
    {
        string target = Path.Combine(_dir, "label.rotula");
        Directory.CreateDirectory(target); // a folder where the file should go makes the final move fail

        Assert.ThrowsAny<Exception>(() => RotulaFileSerializer.Save(new LabelDocument(), target));

        Assert.False(File.Exists(target + ".tmp"));
    }
}

/// <summary>A font alias that is only unusual (not dangerous) is kept on load and reported by the generator.</summary>
public class FontAliasLoadTests
{
    [Fact]
    public void ALongFontFileName_SurvivesLoad_AndTheGeneratorReportsIt()
    {
        var doc = CoreTestSupport.NewDoc(new TextElement { Id = "t", X = 10, Y = 10, Text = "Hola" });
        doc.FontAliases.Add(new FontAlias('Q', "E:ARIALBOLD.TTF")); // 9 characters: more than the printer accepts, but harmless to write

        var loaded = RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(doc));

        Assert.Equal("E:ARIALBOLD.TTF", Assert.Single(loaded.FontAliases).Path);
        var result = ZplGenerator.Generate(loaded);
        Assert.Contains(result.Issues, i => i.MessageKey == "Font.Alias.Invalid" && i.Severity == ValidationSeverity.Error);
        Assert.DoesNotContain("^CW", result.Zpl);
    }
}
