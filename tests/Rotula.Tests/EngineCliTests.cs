using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Rotula.Cli;
using Rotula.Cli.Server;
using Rotula.Engine;
using Rotula.Files.Policy;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>The <c>rotula</c> command line against a fake printer: exit codes, output, data files, keys and the settings starter.</summary>
public class EngineCliTests
{
    private sealed class Run(EngineFixture f, RotulaPolicy? policy = null)
    {
        public StringWriter Out { get; } = new();
        public StringWriter Err { get; } = new();
        public List<EngineSettingsLoad> Served { get; } = new();
        public List<string?> CounterPaths { get; } = new();

        public Task<int> Exec(params string[] args) => CliApp.RunAsync(args, new CliHost
        {
            Print = f.Service,
            Policy = policy ?? RotulaPolicy.None,
            Counters = path => { CounterPaths.Add(path); return f.Counters; },
            Rasterizer = null,
            BaseDirectory = f.Folder,
            Out = Out,
            Error = Err,
            DefaultLanguage = "en",
            Serve = (loaded, _, _, _, _, _) => { Served.Add(loaded); return Task.FromResult(0); },
        });
    }

    [Fact]
    public async Task Print_SendsTheLabel_AndWritesTheResultAsJson()
    {
        using var f = new EngineFixture();
        string label = f.SaveLabel("box.rotula");
        var run = new Run(f);
        int code = await run.Exec("print", label, "--printer", "Dock 1", "--set", "sku=CLI-1", "--copies=2", "--no-verify", "--reference", "PO-5", "--json");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("^FDCLI-1^FS", Assert.Single(f.Printer.Sent).Zpl);
        using var json = JsonDocument.Parse(run.Out.ToString());
        Assert.Equal("printed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("labels").GetInt32());
        Assert.Equal("PO-5", json.RootElement.GetProperty("reference").GetString());
    }

    [Fact]
    public async Task Print_FromACsvFile_PrintsEveryRecord()
    {
        using var f = new EngineFixture();
        string label = f.SaveLabel("box.rotula", null, "sku", "qty");
        string csv = Path.Combine(f.Folder, "picks.csv");
        File.WriteAllText(csv, "sku;qty\nA-1;4\nB-2;6\n");
        var run = new Run(f);
        int code = await run.Exec("print", label, "--printer", "Dock 1", "--data", csv, "--no-verify");

        Assert.Equal(ExitCodes.Ok, code);
        string zpl = Assert.Single(f.Printer.Sent).Zpl;
        Assert.Contains("^FDB-2^FS", zpl);
        Assert.Contains("^FD6^FS", zpl);
        Assert.Contains("2 record(s)", run.Out.ToString());
    }

    [Fact]
    public async Task DryRun_WritesTheZplToTheOutputFile()
    {
        using var f = new EngineFixture();
        string label = f.SaveLabel("box.rotula");
        string output = Path.Combine(f.Folder, "out.zpl");
        int code = await new Run(f).Exec("print", label, "--printer", "Dock 1", "--set", "sku=DRY", "--dry-run", "--output", output);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Empty(f.Printer.Sent);
        Assert.Contains("^FDDRY^FS", File.ReadAllText(output));
    }

    [Fact]
    public async Task ExitCodes_TellScriptsWhatHappened()
    {
        using var f = new EngineFixture(_ => HostStatusParser.Parse(HsSamples.S1(paperOut: 1) + HsSamples.S2() + HsSamples.S3));
        string label = f.SaveLabel("box.rotula");

        Assert.Equal(ExitCodes.PrinterNotReady, await new Run(f).Exec("print", label, "--printer", "Dock 1", "--set", "sku=x"));
        Assert.Equal(ExitCodes.PrinterNotReady, await new Run(f).Exec("status", "--printer", "Dock 1"));
        Assert.Equal(ExitCodes.Usage, await new Run(f).Exec("print", label, "--printr", "Dock 1"));
        Assert.Equal(ExitCodes.Usage, await new Run(f).Exec("print", label, "--printer"));
        Assert.Equal(ExitCodes.Usage, await new Run(f).Exec("print", label, "--printer", "Dock 1", "--copies", "many"));
        Assert.Equal(ExitCodes.Usage, await new Run(f).Exec("print", label, "--printer", "Dock 1", "--set", "novalue"));
        Assert.Equal(ExitCodes.Usage, await new Run(f).Exec("print", Path.Combine(f.Folder, "missing.rotula"), "--printer", "Dock 1"));
        Assert.Equal(ExitCodes.Usage, await new Run(f).Exec("frobnicate"));
        Assert.Equal(ExitCodes.Ok, await new Run(f).Exec("help"));
        Assert.Empty(f.Printer.Sent);
    }

    [Fact]
    public async Task Policy_BlocksPrintingStatusAndTheServer()
    {
        using var f = new EngineFixture();
        string label = f.SaveLabel("box.rotula");
        var blocked = new RotulaPolicy { PrintEngineBlocked = true };
        var run = new Run(f, blocked);
        Assert.Equal(ExitCodes.Refused, await run.Exec("print", label, "--printer", "Dock 1", "--set", "sku=x"));
        Assert.Equal(ExitCodes.Refused, await run.Exec("status", "--printer", "Dock 1"));
        Assert.Equal(ExitCodes.Refused, await run.Exec("serve"));
        Assert.Empty(f.Printer.Sent);
        Assert.Contains(EngineText.English.Get("Engine.Error.PrintingBlocked"), run.Err.ToString());
    }

    [Fact]
    public async Task Messages_FollowLang_AndThePolicyLanguage()
    {
        using var f = new EngineFixture();
        var es = new Run(f);
        await es.Exec("print", "--lang", "es");
        Assert.Contains(EngineText.For("es").Get("Engine.Cli.SeeHelp"), es.Err.ToString());

        var managed = new Run(f, new RotulaPolicy { Language = "pt-BR" });
        await managed.Exec("frobnicate");
        Assert.Contains(EngineText.For("pt-BR").Get("Engine.Cli.UnknownCommand", "frobnicate"), managed.Err.ToString());
    }

    [Fact]
    public async Task Status_AndPrinters_ReportInJson()
    {
        using var f = new EngineFixture();
        var status = new Run(f);
        Assert.Equal(ExitCodes.Ok, await status.Exec("status", "--host", "10.0.0.5", "--json"));
        Assert.True(JsonDocument.Parse(status.Out.ToString()).RootElement.GetProperty("ready").GetBoolean());

        var printers = new Run(f);
        Assert.Equal(ExitCodes.Ok, await printers.Exec("printers", "--json"));
        Assert.Equal("Dock 1", JsonDocument.Parse(printers.Out.ToString()).RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task NewKey_AndServeInit_MakeAWorkingSettingsFile()
    {
        using var f = new EngineFixture();
        var key = new Run(f);
        await key.Exec("new-key", "--json");
        using var made = JsonDocument.Parse(key.Out.ToString());
        Assert.True(ApiKeys.Matches(made.RootElement.GetProperty("key").GetString(), [made.RootElement.GetProperty("hash").GetString()!]));

        string settings = Path.Combine(f.Folder, "conf", EngineSettings.FileName);
        var init = new Run(f);
        Assert.Equal(ExitCodes.Ok, await init.Exec("serve", "--init", "--settings", settings, "--label-folder", f.Folder));
        Assert.Equal(ExitCodes.Usage, await new Run(f).Exec("serve", "--init", "--settings", settings));

        var serve = new Run(f);
        Assert.Equal(ExitCodes.Ok, await serve.Exec("serve", "--settings", settings, "--urls", "http://127.0.0.1:9999"));
        var loaded = Assert.Single(serve.Served);
        Assert.Equal(["http://127.0.0.1:9999"], loaded.Settings!.Urls);
        Assert.Equal([f.Folder], loaded.Settings.LabelFolders);
        // The starter allows no printer yet, and says so.
        Assert.Contains(EngineText.English.Get("Engine.Settings.NoPrinters"), serve.Err.ToString());
        Assert.Equal([null], serve.CounterPaths);

        // counterFile shares the designer's serial numbers with a service account; --counter-file still wins.
        string counters = Path.Combine(f.Folder, "counters.json");
        File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"language\": \"en\"", "\"language\": \"en\", \"counterFile\": " + JsonSerializer.Serialize(counters)));
        var shared = new Run(f);
        Assert.Equal(ExitCodes.Ok, await shared.Exec("serve", "--settings", settings));
        Assert.Equal([counters], shared.CounterPaths);
        var overridden = new Run(f);
        Assert.Equal(ExitCodes.Ok, await overridden.Exec("serve", "--settings", settings, "--counter-file", "other.json"));
        Assert.Equal(["other.json"], overridden.CounterPaths);
    }
}

/// <summary>The REST API over the in-memory test server: keys, allow-lists, label folders, status codes and languages.</summary>
public class EngineServerTests
{
    private static async Task<(WebApplication App, HttpClient Client, string Key)> Start(EngineFixture f, Func<EngineSettings, EngineSettings>? edit = null,
        RotulaPolicy? policy = null)
    {
        var (key, hash) = ApiKeys.Create();
        var settings = new EngineSettings
        {
            ApiKeyHashes = [hash],
            LabelFolders = [f.Folder],
            AllowedPrinters = ["Dock 1", "10.0.0.5"],
        };
        if (edit is not null) settings = edit(settings);
        var app = EngineServer.Build(settings, f.Engine, policy ?? RotulaPolicy.None, b => b.WebHost.UseTestServer());
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(ApiKeys.Header, key);
        return (app, client, key);
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Health_NeedsNoKey_EverythingElseDoes()
    {
        using var f = new EngineFixture();
        var (app, client, _) = await Start(f);
        await using var _a = app;
        client.DefaultRequestHeaders.Remove(ApiKeys.Header);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/printers")).StatusCode);
        var refused = await client.PostAsync("/api/v1/jobs", Body("{\"label\":\"box.rotula\",\"printer\":\"Dock 1\"}"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeys.Header, ApiKeys.Create().Key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/printers")).StatusCode);
        Assert.Empty(f.Printer.Sent);
    }

    [Fact]
    public async Task Job_PrintsALabelFromAFolder_WithRecords()
    {
        using var f = new EngineFixture();
        f.SaveLabel(Path.Combine("outbound", "carton.rotula"));
        var (app, client, _) = await Start(f);
        await using var _a = app;

        var response = await client.PostAsync("/api/v1/jobs", Body(
            "{\"label\":\"outbound/carton.rotula\",\"printer\":\"Dock 1\",\"verify\":false,\"reference\":\"WAVE-12\",\"records\":[{\"sku\":\"R1\"},{\"sku\":2}]}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PrintJobResult>(EngineJson.Options);
        Assert.Equal(PrintOutcome.Printed, result!.Outcome);
        Assert.Equal(2, result.Records);
        Assert.Equal("WAVE-12", result.Reference);
        string zpl = Assert.Single(f.Printer.Sent).Zpl;
        Assert.Contains("^FDR1^FS", zpl);
        Assert.Contains("^FD2^FS", zpl);
    }

    [Fact]
    public async Task Job_RefusesPrintersLabelsAndContentOutsideTheRules()
    {
        using var f = new EngineFixture();
        f.SaveLabel("box.rotula");
        var (app, client, _) = await Start(f);
        await using var _a = app;

        async Task<(HttpStatusCode, string)> Post(string json)
        {
            var r = await client.PostAsync("/api/v1/jobs", Body(json));
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return (r.StatusCode, doc.RootElement.GetProperty("errorCode").GetString()!);
        }

        Assert.Equal((HttpStatusCode.Forbidden, "Engine.Error.PrinterNotAllowed"), await Post("{\"label\":\"box.rotula\",\"printer\":\"Office\"}"));
        Assert.Equal((HttpStatusCode.Forbidden, "Engine.Error.PrinterNotAllowed"), await Post("{\"label\":\"box.rotula\",\"host\":\"10.0.0.5\",\"port\":6101}"));
        Assert.Equal((HttpStatusCode.Forbidden, "Engine.Error.LabelOutsideFolders"), await Post("{\"label\":\"../box.rotula\",\"printer\":\"Dock 1\"}"));
        Assert.Equal((HttpStatusCode.NotFound, "Engine.Error.LabelNotFound"), await Post("{\"label\":\"none.rotula\",\"printer\":\"Dock 1\"}"));
        Assert.Equal((HttpStatusCode.Forbidden, "Engine.Error.InlineLabelsOff"), await Post("{\"labelContent\":\"{}\",\"printer\":\"Dock 1\"}"));
        Assert.Equal((HttpStatusCode.BadRequest, "Engine.Error.LabelRequired"), await Post("{\"printer\":\"Dock 1\"}"));
        Assert.Equal((HttpStatusCode.BadRequest, "Engine.Error.BadRequest"), await Post("{\"label\":\"box.rotula\",\"printer\":\"Dock 1\",\"copeis\":3}"));
        Assert.Equal((HttpStatusCode.BadRequest, "Engine.Error.Copies"), await Post("{\"label\":\"box.rotula\",\"printer\":\"Dock 1\",\"copies\":5000}"));
        Assert.Empty(f.Printer.Sent);
    }

    [Fact]
    public async Task Job_WithInlineContent_WhenAllowed()
    {
        using var f = new EngineFixture();
        var (app, client, _) = await Start(f, s => s with { AllowInlineLabels = true });
        await using var _a = app;
        string content = Rotula.Files.RotulaFileSerializer.Serialize(EngineFixture.Label());
        var response = await client.PostAsync("/api/v1/jobs", Body(JsonSerializer.Serialize(new { labelContent = content, host = "10.0.0.5", values = new { sku = "INLINE" }, verify = false })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("^FDINLINE^FS", Assert.Single(f.Printer.Sent).Zpl);
    }

    [Fact]
    public async Task PaperOut_Is409_InTheCallersLanguage()
    {
        using var f = new EngineFixture(_ => HostStatusParser.Parse(HsSamples.S1(paperOut: 1) + HsSamples.S2() + HsSamples.S3));
        f.SaveLabel("box.rotula");
        var (app, client, _) = await Start(f);
        await using var _a = app;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs") { Content = Body("{\"label\":\"box.rotula\",\"printer\":\"Dock 1\",\"values\":{\"sku\":\"x\"}}") };
        request.Headers.AcceptLanguage.ParseAdd("es-MX");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PrintJobResult>(EngineJson.Options);
        Assert.Equal(PrintOutcome.HeldBack, result!.Outcome);
        Assert.Equal(EngineText.For("es").Describe(PrintError.FromStatus(HostStatusParser.Parse(HsSamples.S1(paperOut: 1) + HsSamples.S2() + HsSamples.S3), "Dock 1")!), result.Message);
        Assert.Empty(f.Printer.Sent);
    }

    [Fact]
    public async Task Status_AndPrinters_FollowTheAllowList()
    {
        using var f = new EngineFixture();
        f.Printer.Installed.Add(new PrinterInfo("Office", "USB002", "Laser", false, false));
        var (app, client, _) = await Start(f);
        await using var _a = app;

        var printers = await client.GetFromJsonAsync<List<PrinterEntry>>("/api/v1/printers", EngineJson.Options);
        Assert.True(printers!.Single(p => p.Name == "Dock 1").Allowed);
        Assert.False(printers!.Single(p => p.Name == "Office").Allowed);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/printers/Dock%201/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/printers/Office/status")).StatusCode);
        var tcp = await client.PostAsync("/api/v1/printers/status", Body("{\"host\":\"10.0.0.5\"}"));
        Assert.Equal(HttpStatusCode.OK, tcp.StatusCode);
        Assert.True((await tcp.Content.ReadFromJsonAsync<PrinterStatusReport>(EngineJson.Options))!.Ready);
    }

    [Fact]
    public async Task Policy_BlocksJobsAndStatus_ButNotHealth()
    {
        using var f = new EngineFixture();
        f.SaveLabel("box.rotula");
        var (app, client, _) = await Start(f, policy: new RotulaPolicy { PrintEngineBlocked = true });
        await using var _a = app;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/jobs", Body("{\"label\":\"box.rotula\",\"printer\":\"Dock 1\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/printers/Dock%201/status")).StatusCode);
        var health = await client.GetFromJsonAsync<HealthInfo>("/api/v1/health", EngineJson.Options);
        Assert.True(health!.PrintingBlocked);
        Assert.Empty(f.Printer.Sent);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesTheRoutes()
    {
        using var f = new EngineFixture();
        var (app, client, _) = await Start(f);
        await using var _a = app;
        string doc = await client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/v1/jobs", doc);
        Assert.Contains("/api/v1/printers/status", doc);
        Assert.Contains("Rotula print engine", doc);
    }

    [Fact]
    public void Outcomes_MapToHttpStatusCodes()
    {
        Assert.Equal(200, EngineServer.StatusFor(PrintOutcome.Printed));
        Assert.Equal(200, EngineServer.StatusFor(PrintOutcome.DryRun));
        Assert.Equal(400, EngineServer.StatusFor(PrintOutcome.Invalid));
        Assert.Equal(422, EngineServer.StatusFor(PrintOutcome.InvalidLabel));
        Assert.Equal(409, EngineServer.StatusFor(PrintOutcome.HeldBack));
        Assert.Equal(502, EngineServer.StatusFor(PrintOutcome.Failed));
    }
}
