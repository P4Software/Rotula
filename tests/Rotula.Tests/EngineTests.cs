using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Engine;
using Rotula.Files;
using Rotula.Files.Policy;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>Fixtures of the print engine tests: a label on disk, a fake printer and the engine over the real print service.</summary>
internal sealed class EngineFixture : IDisposable
{
    public EngineFixture(Func<int, PrinterStatus>? status = null)
    {
        Folder = Path.Combine(Path.GetTempPath(), "rotula-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Printer = new EnginePrinter();
        // A ready printer unless the test scripts something else.
        Status = new ScriptedStatusReader(status ?? (_ => HostStatusParser.Parse(HsSamples.Ready)));
        Counters = new MemoryCounterStore();
        Service = new PrintService(Printer, Printer, Printer, Status);
        Engine = new PrintEngine(Service, Counters);
    }

    public string Folder { get; }
    public EnginePrinter Printer { get; }
    public ScriptedStatusReader Status { get; }
    public MemoryCounterStore Counters { get; }
    public PrintService Service { get; }
    public PrintEngine Engine { get; }

    /// <summary>A 50 x 25 mm label with one text field per name, saved as <paramref name="file"/> in <see cref="Folder"/>.</summary>
    public string SaveLabel(string file, Action<LabelDocument>? edit = null, params string[] fields)
    {
        var doc = Label(edit, fields);
        string path = Path.Combine(Folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotulaFileSerializer.Save(doc, path);
        return path;
    }

    public static LabelDocument Label(Action<LabelDocument>? edit = null, params string[] fields)
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(25) };
        int y = 20;
        foreach (var f in fields.DefaultIfEmpty("sku"))
        {
            doc.Elements.Add(new TextElement { X = 20, Y = y, Text = "{{" + f + "}}", FontHeight = 30, FontWidth = 30 });
            y += 40;
        }
        edit?.Invoke(doc);
        return doc;
    }

    public static RotulaFileContent Content(Action<LabelDocument>? edit = null, params string[] fields) => new(Label(edit, fields), null);

    /// <summary>Adds a counter variable called "serial" that remembers its value (which needs a saved label, so an id).</summary>
    public static void RememberingCounter(LabelDocument doc)
    {
        doc.Id = "engine-test-label";
        doc.Variables.Add(new LabelVariable("serial", "S") { Kind = VariableKind.Counter, Counter = new CounterVariableOptions { Remember = true } });
        doc.Elements.Add(new TextElement { X = 20, Y = 150, Text = "N{{serial}}", FontHeight = 30, FontWidth = 30 });
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A spooler and network fake: records every job, can fail, can be slow, and counts how many jobs ran at once.</summary>
internal sealed class EnginePrinter : IRawPrinter, INetworkPrinter, IPrinterDiscovery
{
    private int _running;

    public List<(string Target, string Zpl)> Sent { get; } = new();
    public Func<int, PrintResult?>? Fail { get; set; }
    public TimeSpan Delay { get; set; }
    public int MaxConcurrent { get; private set; }
    public List<PrinterInfo> Installed { get; } = new() { new PrinterInfo("Dock 1", "USB001", "ZPL driver", true, false) };

    public Task<PrintResult> SendAsync(string printerName, string documentName, byte[] data, CancellationToken cancellationToken = default) =>
        Send(printerName, data, cancellationToken);

    public Task<PrintResult> SendAsync(string host, int port, byte[] data, NetworkPrinterOptions options, CancellationToken cancellationToken = default) =>
        Send(host + ":" + port, data, cancellationToken);

    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PrinterInfo>>(Installed);

    private async Task<PrintResult> Send(string target, byte[] data, CancellationToken ct)
    {
        int now = Interlocked.Increment(ref _running);
        lock (Sent) MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            int n;
            lock (Sent) n = Sent.Count + 1;
            if (Fail?.Invoke(n) is { } failure) return failure;
            lock (Sent) Sent.Add((target, Encoding.UTF8.GetString(data)));
            return PrintResult.Ok(data.Length);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>The engine itself: one label, records, status, serial numbers, questions, dry runs and the per-printer queue.</summary>
public class PrintEngineTests
{
    private static PrintJobRequest Dock(Action<PrintJobRequest>? edit = null)
    {
        var r = new PrintJobRequest { Printer = "Dock 1", Verify = false };
        edit?.Invoke(r);
        return r;
    }

    [Fact]
    public async Task OneLabel_WithValues_IsSentOnce_WithTheValueAndTheCopies()
    {
        using var f = new EngineFixture();
        var result = await f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => { r.Values = new() { ["sku"] = "ABC-123" }; r.Copies = 3; r.Reference = "SO-77"; }),
            null, EngineText.English);

        Assert.Equal(PrintOutcome.Printed, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Labels);
        Assert.Equal("SO-77", result.Reference);
        Assert.Equal("Dock 1", result.Printer);
        var (target, zpl) = Assert.Single(f.Printer.Sent);
        Assert.Equal("Dock 1", target);
        Assert.Contains("^FDABC-123^FS", zpl);
        Assert.Contains("^PQ3", zpl);
        Assert.Equal("Sent 3 label(s) to Dock 1.", result.Message);
    }

    [Fact]
    public async Task Records_PrintOneLabelEach_InOneJob_AndReportTheCount()
    {
        using var f = new EngineFixture();
        var records = new List<Dictionary<string, string>> { new() { ["sku"] = "A1" }, new() { ["sku"] = "B2" }, new() { ["sku"] = "C3" } };
        var result = await f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => r.Records = records), null, EngineText.English);

        Assert.Equal(PrintOutcome.Printed, result.Outcome);
        Assert.Equal(3, result.Records);
        Assert.Equal(3, result.Labels);
        Assert.Equal(1, result.Jobs);
        string zpl = Assert.Single(f.Printer.Sent).Zpl;
        Assert.Contains("^FDA1^FS", zpl);
        Assert.Contains("^FDC3^FS", zpl);
    }

    [Fact]
    public async Task PaperOut_HoldsTheJobBack_AndSendsNothing()
    {
        using var f = new EngineFixture(_ => HostStatusParser.Parse(HsSamples.S1(paperOut: 1) + HsSamples.S2() + HsSamples.S3));
        var result = await f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => r.Values = new() { ["sku"] = "x" }), null, EngineText.English);

        Assert.Equal(PrintOutcome.HeldBack, result.Outcome);
        Assert.Equal(nameof(PrintErrorKind.PaperOut), result.ErrorCode);
        Assert.Empty(f.Printer.Sent);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task NoStatus_SkipsTheQuestion()
    {
        using var f = new EngineFixture(_ => HostStatusParser.Parse(HsSamples.S1(paperOut: 1) + HsSamples.S2() + HsSamples.S3));
        var result = await f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => { r.CheckStatus = false; r.Values = new() { ["sku"] = "x" }; }), null, EngineText.English);

        Assert.Equal(PrintOutcome.Printed, result.Outcome);
        Assert.Equal(0, f.Status.Calls);
    }

    [Fact]
    public async Task RememberedSerialNumbers_ContinueAcrossRequests_AndGoBackWhenNothingWasSent()
    {
        using var f = new EngineFixture();
        var content = new RotulaFileContent(EngineFixture.Label(EngineFixture.RememberingCounter), null);
        var records = new List<Dictionary<string, string>> { new() { ["sku"] = "A" }, new() { ["sku"] = "B" } };

        var first = await f.Engine.PrintAsync(content, Dock(r => r.Records = records), null, EngineText.English);
        Assert.Equal(PrintOutcome.Printed, first.Outcome);
        Assert.Contains("^FDN1^FS", f.Printer.Sent[0].Zpl);
        Assert.Contains("^FDN2^FS", f.Printer.Sent[0].Zpl);

        // The printer is unreachable: nothing moved, so the numbers 3 and 4 are given back and the next run uses them.
        f.Printer.Fail = _ => PrintResult.Fail(PrintErrorKind.Offline, "Dock 1");
        var failed = await f.Engine.PrintAsync(new RotulaFileContent(EngineFixture.Label(EngineFixture.RememberingCounter), null), Dock(r => r.Records = records), null, EngineText.English);
        Assert.Equal(PrintOutcome.Failed, failed.Outcome);
        Assert.Equal(nameof(PrintErrorKind.Offline), failed.ErrorCode);

        f.Printer.Fail = null;
        var again = await f.Engine.PrintAsync(new RotulaFileContent(EngineFixture.Label(EngineFixture.RememberingCounter), null), Dock(r => r.Records = records), null, EngineText.English);
        Assert.Equal(PrintOutcome.Printed, again.Outcome);
        Assert.Contains("^FDN3^FS", f.Printer.Sent[^1].Zpl);
    }

    [Fact]
    public async Task DryRun_ReturnsTheZpl_SendsNothing_AndUsesNoSerialNumber()
    {
        using var f = new EngineFixture();
        var dry = await f.Engine.PrintAsync(new RotulaFileContent(EngineFixture.Label(EngineFixture.RememberingCounter), null),
            Dock(r => { r.DryRun = true; r.Values = new() { ["sku"] = "D" }; r.Darkness = 20; }), null, EngineText.English);

        Assert.Equal(PrintOutcome.DryRun, dry.Outcome);
        Assert.Empty(f.Printer.Sent);
        Assert.Contains("^FDD^FS", dry.Zpl);
        Assert.StartsWith("~SD20", dry.Zpl);

        var real = await f.Engine.PrintAsync(new RotulaFileContent(EngineFixture.Label(EngineFixture.RememberingCounter), null),
            Dock(r => r.Values = new() { ["sku"] = "D" }), null, EngineText.English);
        Assert.Equal(PrintOutcome.Printed, real.Outcome);
        Assert.Contains("^FDN1^FS", f.Printer.Sent[0].Zpl);
    }

    [Fact]
    public async Task RequiredQuestion_WithoutAnswer_IsRefused_AndADefaultAnswersAnOptionalOne()
    {
        using var f = new EngineFixture();
        void Questions(LabelDocument d)
        {
            d.Variables.Add(new LabelVariable("lot", "SAMPLE") { Kind = VariableKind.Prompt, Prompt = new PromptVariableOptions { Required = true, Label = "Lot" } });
            d.Variables.Add(new LabelVariable("line", "SAMPLE") { Kind = VariableKind.Prompt, Prompt = new PromptVariableOptions { Default = "L2" } });
            d.Elements.Add(new TextElement { X = 20, Y = 150, Text = "{{lot}}/{{line}}", FontHeight = 30, FontWidth = 30 });
        }

        var refused = await f.Engine.PrintAsync(new RotulaFileContent(EngineFixture.Label(Questions), null), Dock(r => r.Values = new() { ["sku"] = "x" }), null, EngineText.English);
        Assert.Equal(PrintOutcome.InvalidLabel, refused.Outcome);
        Assert.Equal("Engine.Error.Answers", refused.ErrorCode);
        Assert.Contains("lot (Lot, *)", refused.Message);
        Assert.Empty(f.Printer.Sent);

        var printed = await f.Engine.PrintAsync(new RotulaFileContent(EngineFixture.Label(Questions), null),
            Dock(r => { r.Values = new() { ["sku"] = "x" }; r.Answers = new() { ["lot"] = "LOT9" }; }), null, EngineText.English);
        Assert.Equal(PrintOutcome.Printed, printed.Outcome);
        // The sample value is never printed in place of a missing answer; the designer's default is.
        Assert.Contains("^FDLOT9/L2^FS", Assert.Single(f.Printer.Sent).Zpl);
    }

    [Fact]
    public async Task ALabelWithErrors_IsNotSent()
    {
        using var f = new EngineFixture();
        var content = EngineFixture.Content(d => d.Elements.Add(new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.Ean13, Data = "12" }));
        var result = await f.Engine.PrintAsync(content, Dock(r => r.Values = new() { ["sku"] = "x" }), null, EngineText.English);

        Assert.Equal(PrintOutcome.InvalidLabel, result.Outcome);
        Assert.Contains(result.Notices, n => n.Severity == "error");
        Assert.Empty(f.Printer.Sent);
    }

    [Theory]
    [InlineData(null, null, null, "Engine.Error.PrinterRequired")]
    [InlineData("Dock 1", "10.0.0.5", null, "Engine.Error.PrinterBoth")]
    [InlineData(null, "10.0.0.5", 70000, "Print.Field.Port")]
    public async Task ABadPrinter_IsAnInvalidRequest(string? printer, string? host, int? port, string code)
    {
        using var f = new EngineFixture();
        var result = await f.Engine.PrintAsync(EngineFixture.Content(), new PrintJobRequest { Printer = printer, Host = host, Port = port }, null, EngineText.English);
        Assert.Equal(PrintOutcome.Invalid, result.Outcome);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task Limits_CopiesRecordsAndResolution_AreChecked()
    {
        using var f = new EngineFixture();
        var limits = new EngineLimits(5, 2);
        var copies = await f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => r.Copies = 6), null, EngineText.English, limits);
        Assert.Equal("Engine.Error.Copies", copies.ErrorCode);
        var records = await f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => r.Records = [new(), new(), new()]), null, EngineText.English, limits);
        Assert.Equal("Engine.Error.Records", records.ErrorCode);
        var dpi = await f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => r.Dpi = 400), null, EngineText.English, limits);
        Assert.Equal("Engine.Error.Dpi", dpi.ErrorCode);
        var both = await f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => { r.Records = [new()]; r.Values = new(); }), null, EngineText.English, limits);
        Assert.Equal("Engine.Error.ValuesAndRecords", both.ErrorCode);
        Assert.Empty(f.Printer.Sent);
    }

    [Fact]
    public async Task NetworkPrinters_GetTheirOwnTarget()
    {
        using var f = new EngineFixture();
        var result = await f.Engine.PrintAsync(EngineFixture.Content(), new PrintJobRequest { Host = "10.0.0.5", Port = 6101, Verify = false }, null, EngineText.English);
        Assert.Equal(PrintOutcome.Printed, result.Outcome);
        Assert.Equal("10.0.0.5:6101", Assert.Single(f.Printer.Sent).Target);
    }

    [Fact]
    public async Task JobsForTheSamePrinter_RunOneAfterTheOther()
    {
        using var f = new EngineFixture();
        f.Printer.Delay = TimeSpan.FromMilliseconds(60);
        var jobs = Enumerable.Range(0, 4).Select(i =>
            f.Engine.PrintAsync(EngineFixture.Content(), Dock(r => { r.CheckStatus = false; r.Values = new() { ["sku"] = "J" + i }; }), null, EngineText.English));
        var results = await Task.WhenAll(jobs);

        Assert.All(results, r => Assert.Equal(PrintOutcome.Printed, r.Outcome));
        Assert.Equal(4, f.Printer.Sent.Count);
        Assert.Equal(1, f.Printer.MaxConcurrent);
    }

    [Fact]
    public async Task Verification_AddsTheVerifiedNotice()
    {
        using var f = new EngineFixture();
        var result = await f.Engine.PrintAsync(EngineFixture.Content(), new PrintJobRequest { Printer = "Dock 1", Values = new() { ["sku"] = "v" } }, null, EngineText.English);
        Assert.Equal(PrintOutcome.Printed, result.Outcome);
        Assert.Contains(result.Notices, n => n.Code == PrinterStatusKeys.Verified);
    }

    [Fact]
    public async Task Status_IsDescribed_WithConditionCodes_InTheCallersLanguage()
    {
        using var f = new EngineFixture(_ => HostStatusParser.Parse(HsSamples.S1(paperOut: 1) + HsSamples.S2(headUp: 1) + HsSamples.S3));
        var report = await f.Engine.GetStatusAsync(PrinterTarget.Spooler("Dock 1"), EngineText.For("es"));

        Assert.True(report.Available);
        Assert.False(report.Ready);
        Assert.Contains("PaperOut", report.Blocking);
        Assert.Contains("HeadOpen", report.Blocking);
        Assert.Equal(EngineText.For("es").Describe(HostStatusParser.Parse(HsSamples.S1(paperOut: 1) + HsSamples.S2(headUp: 1) + HsSamples.S3)), report.Message);
        Assert.NotEqual(EngineText.English.Get("Print.Status.Blocked", "x"), EngineText.For("es").Get("Print.Status.Blocked", "x"));
    }

    [Fact]
    public void RecordsTable_TakesEveryFieldName_InTheOrderFirstSeen()
    {
        var table = PrintEngine.RecordsTable([new() { ["sku"] = "A" }, new() { ["qty"] = "2", ["SKU"] = "B" }]);
        Assert.Equal(new[] { "sku", "qty" }, table.Columns);
        Assert.Equal(new[] { "A", "" }, table.Rows[0]);
        Assert.Equal(new[] { "B", "2" }, table.Rows[1]);
    }
}

/// <summary>Labels, keys, settings, texts and JSON of the engine.</summary>
public class EngineSupportTests
{
    [Theory]
    [InlineData("../secret.rotula")]
    [InlineData("a/../../secret.rotula")]
    [InlineData("C:/labels/x.rotula")]
    [InlineData("/etc/x.rotula")]
    [InlineData("//server/share/x.rotula")]
    [InlineData("x.rotula:stream")]
    [InlineData("labels/x.txt")]
    [InlineData("")]
    public void Resolve_RefusesAnythingButARelativeRotulaPath(string requested)
    {
        using var f = new EngineFixture();
        Assert.Null(LabelFiles.Resolve(requested, [f.Folder], out var key));
        Assert.Equal("Engine.Error.LabelOutsideFolders", key);
    }

    [Fact]
    public void Resolve_FindsALabelInTheFirstFolderThatHasIt()
    {
        using var f = new EngineFixture();
        string path = f.SaveLabel(Path.Combine("shipping", "carton.rotula"));
        Assert.Equal(path, LabelFiles.Resolve("shipping/carton.rotula", [Path.Combine(f.Folder, "none"), f.Folder], out var key));
        Assert.Null(key);
        Assert.Equal(path, LabelFiles.Resolve(@"shipping\carton.rotula", [f.Folder], out _));
        Assert.Null(LabelFiles.Resolve("shipping/missing.rotula", [f.Folder], out key));
        Assert.Equal("Engine.Error.LabelNotFound", key);
    }

    [Fact]
    public void Load_ReportsMissingTooLargeAndDamagedFiles_ByKey()
    {
        using var f = new EngineFixture();
        Assert.Equal("Engine.Error.LabelNotFound", LabelFiles.Load(Path.Combine(f.Folder, "no.rotula")).ErrorKey);
        string path = f.SaveLabel("a.rotula");
        Assert.NotNull(LabelFiles.Load(path).Content);
        Assert.Equal("Engine.Error.LabelTooLarge", LabelFiles.Load(path, maxBytes: 10).ErrorKey);
        var damaged = LabelFiles.Parse("{ nope", "x.rotula");
        Assert.Equal("File.Error.Corrupt", damaged.ErrorKey);
        Assert.Equal("x.rotula", damaged.Args[0]);
    }

    [Fact]
    public void ApiKeys_MatchOnlyTheirOwnHash()
    {
        var (key, hash) = ApiKeys.Create();
        Assert.Equal(64, key.Length);
        Assert.True(ApiKeys.IsHash(hash));
        Assert.True(ApiKeys.Matches(key, [hash]));
        Assert.True(ApiKeys.Matches(key, [ApiKeys.Create().Hash, hash.ToUpperInvariant()]));
        Assert.False(ApiKeys.Matches(key + "x", [hash]));
        Assert.False(ApiKeys.Matches(hash, [hash])); // the hash itself is not a key
        Assert.False(ApiKeys.Matches(null, [hash]));
        Assert.False(ApiKeys.Matches(key, []));
    }

    [Theory]
    [InlineData("es-MX", "es")]
    [InlineData("pt", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("fr-FR,en;q=0.5", "fr-CA")]
    [InlineData("de-DE,es;q=0.8", "es")]
    [InlineData("de", "en")]
    [InlineData(null, "en")]
    public void Language_IsTheClosestShippedOne(string? requested, string expected) => Assert.Equal(expected, EngineText.Resolve(requested));

    [Fact]
    public void Languages_AreThePolicyLanguages()
    {
        Assert.Equal(RotulaPolicy.Languages.Where(l => l != "auto"), EngineText.Languages);
        // Every language has its texts embedded.
        foreach (var lang in EngineText.Languages) Assert.NotEqual("Engine.Cli.SeeHelp", EngineText.For(lang).Get("Engine.Cli.SeeHelp"));
    }

    [Fact]
    public void EveryKeyTheEngineUses_HasTextInEveryLanguage()
    {
        string root = Root();
        var shape = new Regex("\"((?:Engine|Print|Data|File|Issue)\\.[A-Za-z0-9]+(?:\\.[A-Za-z0-9]+)+)\"");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { "Rotula.Engine", "Rotula.Cli" })
            foreach (var cs in Directory.EnumerateFiles(Path.Combine(root, "src", dir), "*.cs", SearchOption.AllDirectories)
                         .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
                foreach (Match m in shape.Matches(File.ReadAllText(cs)))
                    used.Add(m.Groups[1].Value);
        Assert.Contains("Engine.Cli.Help", used);
        Assert.True(used.Count > 40, "expected the engine keys, found " + used.Count);
        foreach (var lang in EngineText.Languages)
        {
            var keys = XDocument.Load(Path.Combine(root, "src", "Rotula.App", "Resources", $"Strings.{lang}.resx")).Root!.Elements("data")
                .Select(d => (string)d.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
            Assert.Empty(used.Where(k => !keys.Contains(k)).Select(k => lang + ": " + k));
        }
    }

    [Fact]
    public void Settings_LoadWithTheirRules_AndRefuseTyposAndMissingKeys()
    {
        using var f = new EngineFixture();
        string path = Path.Combine(f.Folder, EngineSettings.FileName);
        var (_, hash) = ApiKeys.Create();
        File.WriteAllText(path, EngineSettingsLoader.Starter(hash, f.Folder).Replace("\"allowedPrinters\": []", "\"allowedPrinters\": [\"Dock 1\", \"10.0.0.5\", \"10.0.0.6:6101\"]"));
        var loaded = EngineSettingsLoader.Load(path, EngineText.English);
        Assert.Empty(loaded.Errors);
        var s = loaded.Settings!;
        Assert.Equal([hash], s.ApiKeyHashes);
        Assert.True(s.IsPrinterAllowed(PrinterTarget.Spooler("dock 1")));
        Assert.True(s.IsPrinterAllowed(PrinterTarget.TcpIp("10.0.0.5")));
        Assert.False(s.IsPrinterAllowed(PrinterTarget.TcpIp("10.0.0.5", 6101)));
        Assert.True(s.IsPrinterAllowed(PrinterTarget.TcpIp("10.0.0.6", 6101)));
        Assert.False(s.IsPrinterAllowed(PrinterTarget.Spooler("Office")));

        File.WriteAllText(path, "{ \"apiKeyHashes\": [\"" + hash + "\"], \"allowedPrinter\": [\"Dock 1\"] }");
        Assert.Null(EngineSettingsLoader.Load(path, EngineText.English).Settings);
        File.WriteAllText(path, "{ \"labelFolders\": [] }");
        Assert.Contains(EngineSettingsLoader.Load(path, EngineText.English).Errors, e => e.Contains("apiKeyHashes"));
        File.WriteAllText(path, "{ \"apiKeyHashes\": [\"secret\"] }");
        Assert.Null(EngineSettingsLoader.Load(path, EngineText.English).Settings);
        Assert.NotEmpty(EngineSettingsLoader.Load(Path.Combine(f.Folder, "missing.json"), EngineText.English).Errors);
    }

    [Fact]
    public void Json_ReadsRecordsWithNumbersAndRefusesUnknownFields()
    {
        Assert.Null(EngineJson.ReadData("[{\"sku\":\"A\",\"qty\":5,\"price\":1.50,\"ok\":true,\"none\":null}]", out var records, out _, out _));
        var r = Assert.Single(records!);
        Assert.Equal("5", r["qty"]);
        Assert.Equal("1.50", r["price"]);
        Assert.Equal("true", r["ok"]);
        Assert.Equal(string.Empty, r["none"]);
        Assert.Null(EngineJson.ReadData("{\"sku\":\"A\"}", out _, out var values, out _));
        Assert.Equal("A", values!["SKU"]);
        Assert.Equal("Engine.Error.DataShape", EngineJson.ReadData("[1]", out _, out _, out _));
        Assert.Equal("Engine.Error.DataShape", EngineJson.ReadData("[{\"a\":{\"b\":1}}]", out _, out _, out _));

        var request = System.Text.Json.JsonSerializer.Deserialize<PrintJobRequest>("{\"printer\":\"Dock 1\",\"copies\":2,\"values\":{\"qty\":7}}", EngineJson.Options)!;
        Assert.Equal("7", request.Values!["qty"]);
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<PrintJobRequest>("{\"printer\":\"Dock 1\",\"copeis\":2}", EngineJson.Options));
    }

    [Fact]
    public void Policy_PrintEngineValue_BlocksOnlyWithZero()
    {
        Assert.False(RotulaPolicy.None.PrintEngineBlocked);
        Assert.True(RotulaPolicy.Read(new DictionaryPolicy(new() { [PolicyNames.PrintEngine] = 0 })).PrintEngineBlocked);
        Assert.False(RotulaPolicy.Read(new DictionaryPolicy(new() { [PolicyNames.PrintEngine] = 1 })).PrintEngineBlocked);
        Assert.Contains(PolicyNames.PrintEngine, PolicyNames.All);
    }

    private sealed class DictionaryPolicy(Dictionary<string, int> values) : IPolicySource
    {
        public string? GetString(string name) => null;
        public int? GetInt(string name) => values.TryGetValue(name, out var v) ? v : null;
    }

    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Rotula.sln not found above the test folder.");
    }
}
