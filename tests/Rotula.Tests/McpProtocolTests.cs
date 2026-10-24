using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rotula.Tests;

/// <summary>
/// Starts the real Rotula.Mcp.exe (a copy in a temporary folder, with its own settings file and log folder) and speaks the
/// protocol over stdio, exactly as an AI assistant application would.
/// </summary>
public class McpProtocolTests : IDisposable
{
    private static readonly string[] ExpectedTools =
    [
        "create_label", "open_label", "save_label", "get_label", "add_text", "add_barcode", "add_qr", "add_box", "add_line", "add_circle",
        "add_image", "add_data_field", "update_element", "move_element", "delete_element", "duplicate_element", "reorder_element",
        "set_label_settings", "list_symbologies", "validate_label", "generate_zpl", "import_zpl", "convert_dpi", "render_preview",
        "merge_preview", "list_printers", "get_printer_info", "get_printer_status", "print_label", "run_query", "undo_last",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rotula-mcp-tests", Guid.NewGuid().ToString("N"));
    private readonly string _program;
    private readonly string _labels;

    public McpProtocolTests()
    {
        string source = FindBuildFolder();
        _program = Path.Combine(_root, "program");
        _labels = Path.Combine(_root, "labels");
        Directory.CreateDirectory(_labels);
        CopyFolder(source, _program);
        // The settings sit next to the program, which is how a user would configure it. Logs go to the temp folder too.
        string json = $$"""
            { "allowedFolders": [ {{JsonSerializer.Serialize(_labels)}} ], "allowPrinting": false, "logFolder": {{JsonSerializer.Serialize(Path.Combine(_root, "logs"))}} }
            """;
        File.WriteAllText(Path.Combine(_program, "rotula-mcp.settings.json"), json);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task HandshakeListCreateAddGenerateRenderAndRefusedPrint()
    {
        await using var client = McpProcess.Start(Path.Combine(_program, "Rotula.Mcp.exe"));
        var init = await client.Request("initialize", new
        {
            protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test-client", version = "1.0" },
        });
        Assert.Equal("rotula", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Contains("print_label", init.GetProperty("result").GetProperty("instructions").GetString());
        await client.Notify("notifications/initialized");

        var list = await client.Request("tools/list");
        var tools = list.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()!).ToList();
        foreach (string expected in ExpectedTools) Assert.Contains(expected, names);
        foreach (var t in tools)
        {
            string name = t.GetProperty("name").GetString()!;
            Assert.Matches("^[a-z][a-z0-9_]*$", name); // snake_case
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()), name);
            Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString());
        }

        var created = await client.CallTool("create_label", new { width = 100, height = 50, name = "protocol" });
        Assert.False(IsError(created));
        var added = await client.CallTool("add_text", new { text = "Hola \u00f1 \u00bfqu\u00e9?", x = 5, y = 5 });
        Assert.False(IsError(added));
        var zpl = await client.CallTool("generate_zpl", new { });
        string zplText = zpl.GetProperty("result").GetProperty("structuredContent").GetProperty("zpl").GetString()!;
        Assert.Contains("^CI28", zplText);
        Assert.Contains("_C3_B1", zplText);

        var render = await client.CallTool("render_preview", new { });
        var content = render.GetProperty("result").GetProperty("content").EnumerateArray().ToList();
        var image = content.First(c => c.GetProperty("type").GetString() == "image");
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        var png = Convert.FromBase64String(image.GetProperty("data").GetString()!);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Contains("approximate", content.First(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString());

        // Printing is switched off in this settings file: refused, and the reason is in plain language.
        var print = await client.CallTool("print_label", new { printer = "Any Printer", copies = 1 });
        Assert.True(IsError(print));
        Assert.Contains("switched off", print.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        // Resources and prompts are served.
        var resources = await client.Request("resources/list");
        Assert.Contains("rotula://label/zpl", resources.GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()));
        var read = await client.Request("resources/read", new { uri = "rotula://label/zpl" });
        Assert.Contains("^XA", read.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());
        var prompts = await client.Request("prompts/list");
        Assert.Equal(3, prompts.GetProperty("result").GetProperty("prompts").GetArrayLength());

        // A file path outside the allowed folder is refused over the wire too.
        var escape = await client.CallTool("save_label", new { path = "C:\\Windows\\Temp\\escape.rotula" });
        Assert.True(IsError(escape));
        Assert.False(File.Exists("C:\\Windows\\Temp\\escape.rotula"));

        var saved = await client.CallTool("save_label", new { path = "protocol.rotula" });
        Assert.False(IsError(saved));
        Assert.True(File.Exists(Path.Combine(_labels, "protocol.rotula")));

        // Standard output carried nothing but JSON-RPC messages.
        Assert.NotEmpty(client.StdoutLines);
        foreach (string line in client.StdoutLines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
            Assert.True(doc.RootElement.TryGetProperty("id", out _) || doc.RootElement.TryGetProperty("method", out _), line);
        }

        int exit = await client.CloseAndWait();
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task GarbageOnStdinDoesNotKillTheServer()
    {
        await using var client = McpProcess.Start(Path.Combine(_program, "Rotula.Mcp.exe"));
        await client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } });
        await client.Notify("notifications/initialized");
        await client.SendRaw("this is not json\n");
        await client.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":900,\"method\":\"no/such/method\"}\n");
        await client.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":901,\"method\":\"tools/call\",\"params\":{\"name\":\"no_such_tool\",\"arguments\":{}}}\n");
        await client.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":902,\"method\":\"tools/call\",\"params\":{\"name\":\"add_text\",\"arguments\":{\"text\":5,\"x\":\"a\"}}}\n");
        // It still answers a normal request afterwards.
        var list = await client.Request("tools/list");
        Assert.True(list.GetProperty("result").GetProperty("tools").GetArrayLength() >= ExpectedTools.Length);
        var bad = await client.CallTool("get_label", new { }); // no label open: a tool error, not a crash
        Assert.True(IsError(bad));
        Assert.Contains("create_label", bad.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        foreach (string line in client.StdoutLines) JsonDocument.Parse(line).Dispose(); // still only JSON
        Assert.Equal(0, await client.CloseAndWait());
    }

    [Fact]
    public async Task AToolCallWithTheWrongArgumentTypesIsAnErrorResult()
    {
        await using var client = McpProcess.Start(Path.Combine(_program, "Rotula.Mcp.exe"));
        await client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } });
        await client.Notify("notifications/initialized");
        await client.CallTool("create_label", new { width = 50, height = 30 });
        var r = await client.CallTool("add_text", new { text = "x", x = "not a number", y = 1 });
        Assert.True(r.TryGetProperty("error", out _) || IsError(r));
        var ok = await client.CallTool("add_text", new { text = "x", x = 1, y = 1 });
        Assert.False(IsError(ok));
        await client.CloseAndWait();
    }

    private static bool IsError(JsonElement response) =>
        response.TryGetProperty("error", out _) ||
        (response.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean());

    // ---- locating and copying the build ------------------------------------------------------

    private static string FindBuildFolder()
    {
        // 1. Next to the tests (the project reference copies the program there).
        string here = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(here, "Rotula.Mcp.exe"))) return here;
        // 2. The program project's own output, matching the configuration of the running tests.
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string config = here.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string folder = Path.Combine(dir.FullName, "src", "Rotula.Mcp", "bin", config, "net10.0-windows");
        Assert.True(File.Exists(Path.Combine(folder, "Rotula.Mcp.exe")), "Build Rotula.Mcp first: " + folder);
        return folder;
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            // The settings file of a developer machine must not leak into the test.
            if (Path.GetFileName(file).Equals("rotula-mcp.settings.json", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string sub in Directory.GetDirectories(from))
            CopyFolder(sub, Path.Combine(to, Path.GetFileName(sub)));
    }
}

/// <summary>A minimal MCP client over a child process: newline-delimited JSON-RPC on stdin/stdout.</summary>
internal sealed class McpProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly List<string> _lines = new();
    private readonly System.Threading.Channels.Channel<JsonDocument> _messages = System.Threading.Channels.Channel.CreateUnbounded<JsonDocument>();
    private int _nextId = 1;

    private McpProcess(Process process)
    {
        _process = process;
        _ = Task.Run(ReadLoop);
        _ = Task.Run(async () => { while (await _process.StandardError.ReadLineAsync() is not null) { } });
    }

    public IReadOnlyList<string> StdoutLines { get { lock (_lines) return _lines.ToList(); } }

    public static McpProcess Start(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        return new McpProcess(Process.Start(psi)!);
    }

    private async Task ReadLoop()
    {
        string? line;
        while ((line = await _process.StandardOutput.ReadLineAsync()) is not null)
        {
            if (line.Length == 0) continue;
            lock (_lines) _lines.Add(line);
            try { _messages.Writer.TryWrite(JsonDocument.Parse(line)); }
            catch (JsonException) { /* recorded in _lines; the tests assert every line parses */ }
        }
        _messages.Writer.TryComplete();
    }

    public async Task SendRaw(string text)
    {
        await _process.StandardInput.WriteAsync(text);
        await _process.StandardInput.FlushAsync();
    }

    public Task Notify(string method) => SendRaw(JsonSerializer.Serialize(new { jsonrpc = "2.0", method }) + "\n");

    public async Task<JsonElement> Request(string method, object? parameters = null)
    {
        int id = _nextId++;
        await SendRaw(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }) + "\n");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (await _messages.Reader.WaitToReadAsync(cts.Token))
        {
            while (_messages.Reader.TryRead(out var doc))
            {
                if (doc.RootElement.TryGetProperty("id", out var got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == id)
                    return doc.RootElement.Clone();
            }
        }
        throw new InvalidOperationException("The server closed the connection before answering " + method);
    }

    public Task<JsonElement> CallTool(string name, object arguments) => Request("tools/call", new { name, arguments });

    public async Task<int> CloseAndWait()
    {
        _process.StandardInput.Close();
        if (!_process.WaitForExit(20_000)) { _process.Kill(true); throw new TimeoutException("The server did not exit after stdin closed."); }
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) { _process.StandardInput.Close(); if (!_process.WaitForExit(5000)) _process.Kill(true); }
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
        await Task.CompletedTask;
    }
}
