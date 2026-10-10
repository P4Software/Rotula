using System.Text;
using ModelContextProtocol;
using Rotula.Mcp.Security;

namespace Rotula.Tests;

/// <summary>
/// Everything an assistant sends, and every file it points at, is hostile until proven otherwise. These cases throw odd input at
/// the tools; the only acceptable outcomes are a normal result or an <see cref="McpException"/>, never another exception.
/// </summary>
public class McpUntrustedInputTests : IDisposable
{
    private readonly McpTestEnv _env = new(s => s with { MaxFileBytes = 200_000 });

    public McpUntrustedInputTests()
    {
        _env.Label.CreateLabel(100, 60);
    }

    public void Dispose() => _env.Dispose();

    private static void Tolerates(Action action)
    {
        try { action(); }
        catch (McpException) { }
    }

    public static IEnumerable<object[]> Strings()
    {
        yield return [""];
        yield return [" "];
        yield return ["\0"];
        yield return ["\u0001\u0002\u007f"];
        yield return ["^XA^FO0,0^FDinjected^FS^XZ"];
        yield return ["^FS^XZ^XA^HY"]; // field terminators and a download command inside text
        yield return ["~HS~DG~EG"];
        yield return ["{{unknown}}{{{{nested}}}}{{"];
        yield return ["{{" + new string('a', 5000) + "}}"];
        yield return ["${env:PATH}%APPDATA%$(whoami)`id`"];
        yield return ["'; DROP TABLE labels; --"];
        yield return ["\uD800"]; // lone surrogate
        yield return ["￿￾"];
        yield return [new string('x', 3_999)];
        yield return [new string('x', 4_001)];
        yield return [string.Concat(Enumerable.Repeat("ñé€\U0001F600", 500))];
        yield return ["line1\nline2\r\nline3"];
        yield return ["_C3_B1 _ _ __ _G"]; // looks like the ^FH escape the generator uses
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void TextAndBarcodeDataNeverCrashTheEngine(string s)
    {
        Tolerates(() => _env.Elements.AddText(s, 1, 1));
        foreach (string sym in new[] { "Code128", "Code39", "Code93", "Ean13", "UpcA", "Interleaved2Of5", "QrCode", "DataMatrix", "Pdf417" })
            Tolerates(() => _env.Elements.AddBarcode(sym, s, 1, 20));
        Tolerates(() => _env.Label.GenerateZpl());
        Tolerates(() => _env.Label.ValidateLabel());
        Tolerates(() => _env.Preview.RenderPreview(maxPixels: 300));
        Tolerates(() => _env.Label.SaveLabel("fuzz.rotula", overwrite: true));
        Tolerates(() => _env.Label.OpenLabel("fuzz.rotula"));
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void ZplInjectionInTextStaysInsideItsField(string s)
    {
        var fresh = new McpTestEnv();
        try
        {
            fresh.Label.CreateLabel(100, 60);
            Tolerates(() => fresh.Elements.AddText(s, 1, 1));
            if (fresh.Label.GetLabel().Label.ElementCount == 0) return;
            string zpl = fresh.Label.GenerateZpl().Zpl;
            // Exactly one label start/end and one field: user text cannot add commands.
            Assert.Equal(1, Count(zpl, "^XA"));
            Assert.Equal(1, Count(zpl, "^XZ"));
            Assert.Equal(1, Count(zpl, "^FS"));
            Assert.DoesNotContain("^HY", zpl);
            Assert.DoesNotContain("~HS", zpl);
        }
        finally { fresh.Dispose(); }
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void ZplInjectionInBarcodeDataStaysInsideItsField(string s)
    {
        foreach (string sym in new[] { "QrCode", "DataMatrix", "Pdf417", "Code128", "Code39", "Code93" })
        {
            using var fresh = new McpTestEnv();
            fresh.Label.CreateLabel(100, 60);
            Tolerates(() => fresh.Elements.AddBarcode(sym, s, 1, 1));
            AssertOneFieldOnly(fresh, sym);
        }
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void ZplInjectionThroughACsvCellIntoABarcodeStaysInsideItsField(string cell)
    {
        using var fresh = new McpTestEnv();
        // A CSV cell is quoted so any text survives parsing; the hostile text then arrives as a merged value.
        string quoted = "\"" + cell.Replace("\"", "\"\"") + "\"";
        File.WriteAllText(Path.Combine(fresh.Labels, "cells.csv"), "Code\n" + quoted + "\n", new UTF8Encoding(false));
        foreach (string sym in new[] { "QrCode", "DataMatrix", "Pdf417", "Code128" })
        {
            fresh.Label.CreateLabel(100, 60);
            fresh.Elements.AddBarcode(sym, "{{Code}}", 1, 1);
            fresh.Elements.AddText("{{Code}}", 1, 30);
            var preview = Tolerant(() => fresh.Preview.MergePreview("cells.csv"));
            if (preview is null) continue;
            string zpl = preview.StructuredContent!.Value.GetProperty("merge").GetProperty("zpl").GetString()!;
            Assert.Equal(1, Count(zpl, "^XA"));
            Assert.Equal(1, Count(zpl, "^XZ"));
            Assert.DoesNotContain("^HY", zpl);
            Assert.DoesNotContain("~HS", zpl);
            Assert.DoesNotContain("~DG", zpl);
            // Every ^FS closes exactly one field of the label (the barcode and the text), never an injected extra one.
            Assert.True(Count(zpl, "^FS") <= 2, zpl);
        }
    }

    private static void AssertOneFieldOnly(McpTestEnv env, string symbology)
    {
        if (env.Label.GetLabel().Label.ElementCount == 0) return;
        string zpl = env.Label.GenerateZpl().Zpl;
        Assert.True(Count(zpl, "^XA") == 1 && Count(zpl, "^XZ") == 1, symbology + ": " + zpl);
        Assert.True(Count(zpl, "^FS") <= 1, symbology + ": " + zpl);
        Assert.DoesNotContain("^HY", zpl);
        Assert.DoesNotContain("~HS", zpl);
        Assert.DoesNotContain("~DG", zpl);
    }

    private static T? Tolerant<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch (McpException) { return null; }
    }

    [Fact]
    public void NumbersAtTheEdgesAreRefusedNotWrapped()
    {
        double[] nasty = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, double.MinValue, -1e12, 1e12, 0, -0.0, 1e-300];
        foreach (double v in nasty)
        {
            Tolerates(() => _env.Elements.AddText("n", v, 1));
            Tolerates(() => _env.Elements.AddText("n", 1, v));
            Tolerates(() => _env.Elements.AddBox(1, 1, v, v));
            Tolerates(() => _env.Elements.AddLine(1, 1, v, v));
            Tolerates(() => _env.Elements.AddCircle(1, 1, v, v));
            Tolerates(() => _env.Elements.AddQr("q", 1, 1, moduleSize: (int)Math.Clamp(v, int.MinValue, int.MaxValue)));
            Tolerates(() => _env.Label.CreateLabel(v, v));
            Tolerates(() => _env.Label.SetLabelSettings(width: v, marginLeft: v));
        }
        foreach (int v in new[] { int.MinValue, -1, 0, int.MaxValue })
        {
            Tolerates(() => _env.Elements.AddBarcode("Code128", "x", 1, 1, moduleWidthDots: v, columns: v, rows: v, pdf417SecurityLevel: v));
            Tolerates(() => _env.Elements.AddText("x", 1, 1, maxLines: v, blockWidth: 20));
            Tolerates(() => _env.Label.ConvertDpi(v));
            Tolerates(() => _env.Label.SetLabelSettings(printQuantity: v));
        }
        Tolerates(() => _env.Label.GenerateZpl());
        Tolerates(() => _env.Preview.RenderPreview(maxPixels: 200));
    }

    [Fact]
    public void ABagWithHostileValueKindsIsRefused()
    {
        string id = _env.Elements.AddText("t", 1, 1).Elements.Single().Id;
        var values = new object?[]
        {
            null, new object[] { 1, 2 }, new Dictionary<string, object> { ["a"] = 1 }, "", new string('z', 10_000), double.MaxValue, -1, true,
        };
        foreach (var v in values)
            foreach (string prop in new[] { "text", "x", "y", "fontHeight", "fontId", "rotation", "alignment", "blockWidth", "maxLines", "reverse", "originKind", "name" })
                Tolerates(() => _env.Elements.UpdateElement(id, new() { [prop] = McpTestEnv.J(v!) }));
        Assert.Equal(1, _env.Label.GetLabel().Label.ElementCount);
        Tolerates(() => _env.Label.GenerateZpl()); // whatever stuck, the label is still usable
    }

    [Fact]
    public void RandomPropertyBagsAreAlwaysHandledCleanly()
    {
        var rnd = new Random(12345);
        string[] props = ["text", "x", "y", "data", "symbology", "height", "width", "thickness", "color", "direction", "rotation", "moduleSize",
            "columns", "rows", "dither", "threshold", "diameter", "length", "alignment", "name", "reverse", "zzz", "", "__proto__", "constructor"];
        string a = _env.Elements.AddText("t", 1, 1).Elements.Single().Id;
        string b = _env.Elements.AddBarcode("QrCode", "q", 20, 1).Elements.Single().Id;
        string c = _env.Elements.AddBox(1, 30, 10, 10).Elements.Single().Id;
        for (int i = 0; i < 400; i++)
        {
            string id = new[] { a, b, c, "nope" }[rnd.Next(4)];
            var bag = new Dictionary<string, System.Text.Json.JsonElement>();
            for (int k = rnd.Next(1, 4); k > 0; k--)
            {
                object v = rnd.Next(6) switch
                {
                    0 => rnd.Next(-100, 100),
                    1 => rnd.NextDouble() * 1000,
                    2 => new string((char)rnd.Next(32, 127), rnd.Next(0, 30)),
                    3 => rnd.Next(2) == 0,
                    4 => "Black",
                    _ => "90",
                };
                bag[props[rnd.Next(props.Length)]] = McpTestEnv.J(v);
            }
            Tolerates(() => _env.Elements.UpdateElement(id, bag));
            if (i % 50 == 0) Tolerates(() => _env.Label.UndoLast());
        }
        Tolerates(() => _env.Label.GenerateZpl());
        Tolerates(() => _env.Preview.RenderPreview(maxPixels: 200));
    }

    [Fact]
    public void CraftedRotulaFilesNeverCrashOpen()
    {
        string[] bodies =
        [
            "", "{", "null", "[]", "{\"formatVersion\":1}", "{\"formatVersion\":\"1\"}", "{\"formatVersion\":-1}", "{\"formatVersion\":1e99}",
            "{\"formatVersion\":1,\"width\":{\"value\":-5,\"unit\":\"Millimeters\"},\"height\":{\"value\":1e300,\"unit\":\"Inches\"},\"elements\":[]}",
            "{\"formatVersion\":1,\"elements\":[{\"type\":\"nope\"}]}",
            "{\"formatVersion\":1,\"elements\":[{\"type\":\"text\",\"text\":null,\"x\":\"a\"}]}",
            "{\"formatVersion\":1,\"elements\":[{\"type\":\"image\",\"sourceWidth\":100000,\"sourceHeight\":100000,\"sourcePixels\":\"AAAA\"}]}",
            "{\"formatVersion\":1,\"elements\":[{\"type\":\"image\",\"sourceWidth\":2,\"sourceHeight\":2,\"sourcePixels\":\"AAAAAA==\",\"widthDots\":20000,\"heightDots\":20000}]}",
            "{\"formatVersion\":3,\"elements\":[{\"type\":\"rawZpl\",\"zpl\":\"^XA^HY^XZ\"}]}",
            "{\"formatVersion\":2,\"merge\":{\"sourcePath\":\"\\\\\\\\evil\\\\share\\\\x.csv\",\"counters\":[{\"name\":null}]},\"elements\":[]}",
            "﻿{\"formatVersion\":1,\"elements\":[]}",
            new string('[', 5000),
            "{\"formatVersion\":1,\"elements\":[" + string.Join(",", Enumerable.Repeat("{\"type\":\"box\"}", 500)) + "]}",
        ];
        int n = 0;
        foreach (string body in bodies)
        {
            File.WriteAllText(Path.Combine(_env.Labels, $"c{n}.rotula"), body, new UTF8Encoding(false));
            string name = $"c{n++}.rotula";
            Tolerates(() => _env.Label.OpenLabel(name));
            Tolerates(() => _env.Label.GenerateZpl());
        }
        // The 500-box file is over the element limit of 200 and must have been refused, not partly loaded.
        Assert.Throws<McpException>(() => _env.Label.OpenLabel($"c{bodies.Length - 1}.rotula"));
    }

    [Fact]
    public void RandomBytesAsZplNeverCrashImport()
    {
        var rnd = new Random(777);
        for (int i = 0; i < 60; i++)
        {
            var bytes = new byte[rnd.Next(0, 4000)];
            rnd.NextBytes(bytes);
            string name = $"r{i}.zpl";
            File.WriteAllBytes(Path.Combine(_env.Labels, name), bytes);
            Tolerates(() => _env.Label.ImportZpl(path: name));
            Tolerates(() => _env.Label.ImportZpl(Encoding.Latin1.GetString(bytes)));
        }
        string[] weird =
        [
            "^XA" + string.Concat(Enumerable.Repeat("^FO10,10^FDx^FS", 3000)) + "^XZ", // over the element limit
            "^XA^FO0,0^GFA,99999999,99999999,99999,FFFF^FS^XZ",
            "^XA^FO0,0^BQN,2,99999^FDQA,x^FS^XZ",
            "^XA^PW99999999999^LL-5^FO-99999,99999999^A0N,99999,99999^FDx^FS^XZ",
            "^" + new string('^', 100_000),
            "^XA^CI99^FDx^FS^XZ",
            "^XA^FH^FD_ZZ_4_FF^FS^XZ",
        ];
        foreach (string z in weird) Tolerates(() => _env.Label.ImportZpl(z));
    }

    [Fact]
    public void HugeInputsAreRefusedBySize()
    {
        Assert.Throws<McpException>(() => _env.Label.ImportZpl(new string('x', 300_000)));
        File.WriteAllBytes(Path.Combine(_env.Labels, "big.zpl"), new byte[300_000]);
        Assert.Throws<McpException>(() => _env.Label.ImportZpl(path: "big.zpl"));
        File.WriteAllBytes(Path.Combine(_env.Labels, "big.csv"), new byte[300_000]);
        Assert.Throws<McpException>(() => _env.Preview.MergePreview("big.csv"));
        Assert.Throws<McpException>(() => _env.Elements.AddText(new string('x', 4_001), 0, 0));
    }

    [Fact]
    public void CsvAndWorkbookContentIsTreatedAsData()
    {
        File.WriteAllText(Path.Combine(_env.Labels, "evil.csv"),
            "Name,Note\n=cmd|' /C calc'!A0,\"^XA^FDx^FS^XZ\"\n\"unterminated,quote\n");
        _env.Elements.AddDataField("Name", 1, 1);
        Tolerates(() => _env.Preview.MergePreview("evil.csv"));
        File.WriteAllText(Path.Combine(_env.Labels, "broken.xlsx"), "PK this is not a workbook");
        Assert.Throws<McpException>(() => _env.Preview.MergePreview("broken.xlsx"));
        File.WriteAllText(Path.Combine(_env.Labels, "binary.csv"), new string('\u0000', 500));
        Tolerates(() => _env.Preview.MergePreview("binary.csv"));
    }

    [Fact]
    public void AZipBombWorkbookIsRefused()
    {
        string path = Path.Combine(_env.Labels, "bomb.xlsx");
        using (var fs = File.Create(path))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", System.IO.Compression.CompressionLevel.SmallestSize);
            using var s = entry.Open();
            var chunk = new byte[1024 * 1024];
            for (int i = 0; i < 120; i++) s.Write(chunk); // 120 MB of zeros compresses to a few hundred KB
        }
        Assert.True(new FileInfo(path).Length < _env.Settings.MaxFileBytes, "the bomb must be small on disk to be a real test");
        var ex = Assert.Throws<McpException>(() => SafeFiles.CheckWorkbook(path));
        Assert.Contains("unreasonable", ex.Message);
        Assert.Throws<McpException>(() => _env.Preview.MergePreview("bomb.xlsx"));
    }

    [Fact]
    public void TheSessionSurvivesEveryFailure()
    {
        // After all of the above kinds of abuse the label is intact and can still be saved and printed to ZPL.
        _env.Elements.AddText("still here", 1, 1);
        Tolerates(() => _env.Elements.UpdateElement("t1", new() { ["bogus"] = McpTestEnv.J(1) }));
        var zpl = _env.Label.GenerateZpl();
        Assert.Contains("still here", zpl.Zpl);
        _env.Label.SaveLabel("final.rotula");
        Assert.Contains("still here", _env.Label.OpenLabel("final.rotula").Elements.Single().Properties["text"]!.ToString());
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
