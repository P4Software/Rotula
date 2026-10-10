using System.Text;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Elements;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

[Collection("App")]
public class AppImporterTests : IDisposable
{
    public AppImporterTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    [Fact]
    public void CoreImporter_RebuildsADesign_FromGeneratedZpl()
    {
        var doc = new Rotula.Core.Model.LabelDocument();
        doc.Elements.Add(new TextElement { Text = "Hello", X = 40, Y = 50 });
        doc.Elements.Add(new BoxElement { X = 10, Y = 10, WidthDots = 200, HeightDots = 100 });
        string zpl = ZplGenerator.Generate(doc).GetZplOrThrow();

        var result = new CoreZplImporter().Import(Encoding.UTF8.GetBytes(zpl));
        Assert.NotNull(result.Document);
        Assert.Null(result.MessageKey);
        Assert.Contains(result.Document!.Elements, e => e is TextElement t && t.Text == "Hello");
        Assert.Contains(result.Document.Elements, e => e is BoxElement);
    }

    [Fact]
    public void CoreImporter_TurnsWarningsIntoPlainSentences_InTheCurrentLanguage()
    {
        byte[] zpl = Encoding.ASCII.GetBytes("^XA\n^FO10,10^FDHi^FS\n^ZZ9\n^XZ");
        var en = new CoreZplImporter().Import(zpl);
        Assert.NotEmpty(en.Warnings);
        Assert.All(en.Warnings, w => Assert.DoesNotContain("!Issue.", w)); // no missing translation
        Loc.Instance.SetLanguage("es");
        var es = new CoreZplImporter().Import(zpl);
        Assert.NotEqual(en.Warnings[0], es.Warnings[0]);
        Assert.All(es.Warnings, w => Assert.DoesNotContain("!Issue.", w));
    }

    [Fact]
    public void CoreImporter_NeverThrows_OnGarbage()
    {
        var result = new CoreZplImporter().Import(new byte[] { 0, 1, 2, 255, 254, 253, 0x5E });
        Assert.NotNull(result.Document);
    }
}
