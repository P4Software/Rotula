using System.Text.Json;
using Rotula.App.Files;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

[Collection("App")]
public class AppSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesEveryElementAndSetting()
    {
        var original = AppTestSupport.RichDocument();
        var copy = RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(original));

        // Equality is proven through a second serialization: it covers every field the DTO layer knows about.
        Assert.Equal(RotulaFileSerializer.Serialize(original), RotulaFileSerializer.Serialize(copy));
        Assert.Equal(original.Elements.Count, copy.Elements.Count);
        Assert.Equal(original.Elements.Select(e => e.GetType()), copy.Elements.Select(e => e.GetType()));
        Assert.Equal(PrinterDpi.Dpi300, copy.Dpi);
        Assert.Equal(Core.Zpl.ZplEncoding.Cp1252, copy.Encoding);
        Assert.Equal("ABC-123", copy.Variables[0].SampleValue);
    }

    [Fact]
    public void RoundTrip_PreservesIdsAndImagePixels()
    {
        var original = AppTestSupport.RichDocument();
        var copy = RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(original));

        Assert.Equal(original.Elements.Select(e => e.Id), copy.Elements.Select(e => e.Id));
        var a = (ImageElement)original.Elements[^1];
        var b = (ImageElement)copy.Elements[^1];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(a.Source!.GetPixel(x, y), b.Source!.GetPixel(x, y));
    }

    [Fact]
    public void RoundTrip_DataFieldStaysADataField_NotPlainText()
    {
        var doc = new LabelDocument();
        doc.Elements.Add(new DataFieldElement { VariableName = "x" });
        var copy = RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(doc));
        Assert.IsType<DataFieldElement>(copy.Elements[0]);
    }

    [Fact]
    public void File_HasFormatVersionFirst_AndTypeDiscriminators()
    {
        string json = RotulaFileSerializer.Serialize(AppTestSupport.RichDocument());
        using var parsed = JsonDocument.Parse(json);
        var first = parsed.RootElement.EnumerateObject().First();
        Assert.Equal("formatVersion", first.Name);
        // Without data-merge settings the file stays at the base version, so older copies of Rotula can still open it.
        Assert.Equal(RotulaFileSerializer.BaseFormatVersion, first.Value.GetInt32());
        var types = parsed.RootElement.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Equal(new[] { "text", "dataField", "barcode", "box", "line", "circle", "image" }, types);
    }

    [Fact]
    public void Deserialize_NewerFormat_ExplainsToUpdate()
    {
        var ex = Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize("{\"formatVersion\": 99}"));
        Assert.Equal("File.Error.NewerVersion", ex.MessageKey);
        Assert.Equal("99", ex.Args[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"hello\": 1}")]
    [InlineData("{\"formatVersion\": \"one\"}")]
    [InlineData("{\"formatVersion\": 0}")]
    public void Deserialize_NotARotulaFile_IsRejectedWithLocalizableKey(string text)
    {
        var ex = Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(text));
        Assert.StartsWith("File.Error.", ex.MessageKey);
    }

    [Fact]
    public void Deserialize_UnknownElementType_IsCorrupt()
    {
        const string json = "{\"formatVersion\":1,\"label\":{},\"elements\":[{\"type\":\"hologram\",\"id\":\"a\"}]}";
        Assert.Equal("File.Error.Corrupt", Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(json)).MessageKey);
    }

    [Fact]
    public void Deserialize_BadEnumOrNumericEnum_IsCorrupt()
    {
        string json = RotulaFileSerializer.Serialize(AppTestSupport.RichDocument()).Replace("\"Mark\"", "\"Sideways\"");
        Assert.Equal("File.Error.Corrupt", Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(json)).MessageKey);

        string numeric = RotulaFileSerializer.Serialize(AppTestSupport.RichDocument()).Replace("\"Mark\"", "\"7\"");
        Assert.Equal("File.Error.Corrupt", Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(numeric)).MessageKey);
    }

    [Fact]
    public void Deserialize_UnsupportedDpi_IsCorrupt()
    {
        string json = RotulaFileSerializer.Serialize(new LabelDocument()).Replace("\"dpi\": 203", "\"dpi\": 111");
        Assert.Equal("File.Error.Corrupt", Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize(json)).MessageKey);
    }

    [Fact]
    public void Deserialize_IgnoresUnknownProperties_ForForwardCompatibility()
    {
        string json = RotulaFileSerializer.Serialize(new LabelDocument()).Replace("\"formatVersion\": 1,", "\"formatVersion\": 1, \"futureThing\": {\"a\":1},");
        Assert.NotNull(RotulaFileSerializer.Deserialize(json));
    }

    [Fact]
    public void SaveAndLoad_WorkOnDisk_AndLeaveNoTempFile()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "sub", "label.rotula");
            RotulaFileSerializer.Save(AppTestSupport.RichDocument(), path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(7, RotulaFileSerializer.Load(path).Elements.Count);
            // Saving again over an existing file must work too.
            RotulaFileSerializer.Save(new LabelDocument(), path);
            Assert.Empty(RotulaFileSerializer.Load(path).Elements);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Clipboard_RoundTripsElements_AndIgnoresForeignText()
    {
        var doc = AppTestSupport.RichDocument();
        string text = RotulaFileSerializer.SerializeElements(doc.Elements.Take(3));
        var back = RotulaFileSerializer.DeserializeElements(text);
        Assert.Equal(3, back.Count);
        Assert.IsType<DataFieldElement>(back[1]);

        Assert.Empty(RotulaFileSerializer.DeserializeElements("just some text"));
        Assert.Empty(RotulaFileSerializer.DeserializeElements(null));
        Assert.Empty(RotulaFileSerializer.DeserializeElements("{\"rotulaElements\": 1, \"elements\": [{\"type\": \"nope\"}]}"));
    }

    [Fact]
    public void Fingerprint_IgnoresPositionAndId_ButSeesContent()
    {
        var a = new TextElement { Text = "x", X = 1, Y = 2 };
        var b = new TextElement { Text = "x", X = 500, Y = 900 };
        var c = new TextElement { Text = "y" };
        Assert.Equal(RotulaFileSerializer.Fingerprint(a), RotulaFileSerializer.Fingerprint(b));
        Assert.NotEqual(RotulaFileSerializer.Fingerprint(a), RotulaFileSerializer.Fingerprint(c));
    }
}
