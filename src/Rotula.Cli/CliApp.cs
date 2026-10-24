using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Rotula.Core.Merge;
using Rotula.Core.Variables.Counters;
using Rotula.Engine;
using Rotula.Files.Policy;
using Rotula.Printing;

namespace Rotula.Cli;

/// <summary>Exit codes of <c>rotula</c>, stable so scripts and schedulers can act on them.</summary>
internal static class ExitCodes
{
    /// <summary>Printed (or the dry run worked, or the printer is ready).</summary>
    public const int Ok = 0;
    /// <summary>Sending failed (printer not found, offline, unreachable...), or the status could not be read.</summary>
    public const int Failed = 1;
    /// <summary>The command line or the request is wrong.</summary>
    public const int Usage = 2;
    /// <summary>The label has errors or the answers to its questions are missing.</summary>
    public const int InvalidLabel = 3;
    /// <summary>The printer reported a fault (paper out, head open, paused...) and nothing was sent, or the status says it cannot print.</summary>
    public const int PrinterNotReady = 4;
    /// <summary>A policy of this computer forbids it.</summary>
    public const int Refused = 5;
    /// <summary>Stopped with Ctrl+C.</summary>
    public const int Cancelled = 6;

    /// <summary>The exit code of a print outcome.</summary>
    public static int For(PrintOutcome outcome) => outcome switch
    {
        PrintOutcome.Printed or PrintOutcome.DryRun => Ok,
        PrintOutcome.Invalid => Usage,
        PrintOutcome.InvalidLabel => InvalidLabel,
        PrintOutcome.HeldBack => PrinterNotReady,
        PrintOutcome.Refused => Refused,
        PrintOutcome.Cancelled => Cancelled,
        _ => Failed,
    };
}

/// <summary>What the commands need from outside, so tests can run them with fakes and without touching the computer.</summary>
internal sealed class CliHost
{
    /// <summary>The print service.</summary>
    public required IPrintService Print { get; init; }

    /// <summary>The policy of this computer.</summary>
    public RotulaPolicy Policy { get; init; } = RotulaPolicy.None;

    /// <summary>Opens the counter store for serial numbers that remember their value (null path = the app's own file).</summary>
    public Func<string?, ICounterStore?> Counters { get; init; } = path => new Rotula.Files.FileCounterStore(path ?? Rotula.Files.FileCounterStore.DefaultPath);

    /// <summary>Draws text in Windows fonts.</summary>
    public Rotula.Core.Text.ITextRasterizer? Rasterizer { get; init; } = new Rotula.Rendering.SkiaTextRasterizer();

    /// <summary>Folder of the program (the default place of the settings file).</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Normal output.</summary>
    public TextWriter Out { get; init; } = Console.Out;

    /// <summary>Problems.</summary>
    public TextWriter Error { get; init; } = Console.Error;

    /// <summary>Language when neither <c>--lang</c> nor a policy chooses one.</summary>
    public string? DefaultLanguage { get; init; } = Environment.GetEnvironmentVariable("ROTULA_LANG") ?? CultureInfo.CurrentUICulture.Name;

    /// <summary>Starts the REST server and runs it until it stops; replaced in tests.</summary>
    public Func<EngineSettingsLoad, PrintEngine, EngineText, RotulaPolicy, string[], CancellationToken, Task<int>> Serve { get; init; } = Server.EngineServer.RunAsync;
}

/// <summary>The <c>rotula</c> commands: print, status, printers, new-key, serve, help, version.</summary>
internal static class CliApp
{
    /// <summary>Runs one command line and returns the exit code. Never throws for a user or printer problem.</summary>
    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken ct = default)
    {
        var (line, parseError, parseArg) = CommandLine.Parse(args);
        // The language is chosen before anything is said: --lang, then the organization's policy, then Windows.
        var text = EngineText.For(line.Value("lang") ?? (host.Policy.Language is { } p && p != "auto" ? p : null) ?? host.DefaultLanguage);
        if (parseError is not null)
        {
            host.Error.WriteLine(text.Get(parseError, parseArg ?? string.Empty));
            host.Error.WriteLine(text.Get("Engine.Cli.SeeHelp"));
            return ExitCodes.Usage;
        }
        if (line.Has("version") || line.Command == "version")
        {
            host.Out.WriteLine("rotula " + Version());
            return ExitCodes.Ok;
        }
        if (line.Has("help") || line.Command is "" or "help")
        {
            host.Out.WriteLine(text.Get("Engine.Cli.Help", Version()));
            return ExitCodes.Ok;
        }

        try
        {
            return line.Command switch
            {
                "print" => await PrintAsync(line, host, text, ct).ConfigureAwait(false),
                "status" => await StatusAsync(line, host, text, ct).ConfigureAwait(false),
                "printers" => await PrintersAsync(line, host, text, ct).ConfigureAwait(false),
                "new-key" => NewKey(line, host, text),
                "serve" => await ServeAsync(line, host, text, args, ct).ConfigureAwait(false),
                _ => Usage(host, text, "Engine.Cli.UnknownCommand", line.Command),
            };
        }
        catch (OperationCanceledException)
        {
            host.Error.WriteLine(text.Get("Print.Error.Cancelled"));
            return ExitCodes.Cancelled;
        }
    }

    // ---- print ------------------------------------------------------------------------------------------------------------

    private static async Task<int> PrintAsync(CommandLine line, CliHost host, EngineText text, CancellationToken ct)
    {
        if (line.Positionals.Count != 1) return Usage(host, text, "Engine.Cli.PrintNeedsLabel", string.Empty);
        if (host.Policy.PrintEngineBlocked) return Refused(line, host, text);

        var request = new PrintJobRequest
        {
            Label = line.Positionals[0],
            Printer = line.Value("printer"),
            Host = line.Value("host"),
            DryRun = line.Has("dry-run"),
            CheckStatus = !line.Has("no-status"),
            Verify = !line.Has("no-verify"),
            Reference = line.Value("reference"),
        };
        foreach (var (name, apply) in new (string, Action<int>)[]
                 {
                     ("port", v => request.Port = v), ("copies", v => request.Copies = v), ("dpi", v => request.Dpi = v),
                     ("darkness", v => request.Darkness = v), ("speed", v => request.Speed = v),
                 })
        {
            if (line.Value(name) is not { } raw) continue;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return Usage(host, text, "Engine.Cli.BadValue", "--" + name, raw);
            apply(v);
        }
        var values = CommandLine.Pairs(line.Values("set"), out var badSet);
        if (values is null) return Usage(host, text, "Engine.Cli.BadPair", "--set", badSet!);
        var answers = CommandLine.Pairs(line.Values("answer"), out var badAnswer);
        if (answers is null) return Usage(host, text, "Engine.Cli.BadPair", "--answer", badAnswer!);
        if (answers.Count > 0) request.Answers = answers;

        MergeTable? table = null;
        if (line.Value("data") is { } dataPath)
        {
            string? error = ReadData(dataPath, line.Value("sheet"), out table, out var jsonValues, out var detail);
            if (error is not null) return Report(line, host, text, ExitCodes.Usage, text.Get(error, dataPath, detail ?? string.Empty));
            if (jsonValues is not null) foreach (var kv in jsonValues) values.TryAdd(kv.Key, kv.Value);
        }
        if (values.Count > 0) request.Values = values;

        var label = LabelFiles.Load(request.Label);
        if (label.Content is null) return Report(line, host, text, ExitCodes.Usage, text.Get(label.ErrorKey!, label.Args));

        var engine = new PrintEngine(host.Print, host.Counters(line.Value("counter-file")), host.Rasterizer);
        var result = await engine.PrintAsync(label.Content, request, table, text, EngineLimits.Default, ct).ConfigureAwait(false);

        if (result.Outcome == PrintOutcome.DryRun && result.Zpl is not null && line.Value("output") is { } output)
        {
            try { await File.WriteAllTextAsync(output, result.Zpl, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Report(line, host, text, ExitCodes.Failed, text.Get("Engine.Error.OutputFile", output, ex.Message));
            }
        }

        if (line.Has("json"))
        {
            host.Out.WriteLine(JsonSerializer.Serialize(result, EngineJson.Indented));
        }
        else
        {
            var writer = result.Succeeded ? host.Out : host.Error;
            writer.WriteLine(result.Message);
            foreach (var n in result.Notices)
                writer.WriteLine("  - " + (n.Record is { } r ? text.Get("Engine.Cli.RecordPrefix", r) + " " : string.Empty) + n.Message);
            // Without --output the ZPL of a dry run goes to the output, so it can be piped into a file or another tool.
            if (result.Outcome == PrintOutcome.DryRun && result.Zpl is not null && line.Value("output") is null) host.Out.Write(result.Zpl);
        }
        return ExitCodes.For(result.Outcome);
    }

    /// <summary>Reads --data: JSON (records or one label's values), Excel (.xlsx) or delimited text (CSV, tab, semicolon; detected).</summary>
    internal static string? ReadData(string path, string? sheet, out MergeTable? table, out Dictionary<string, string>? values, out string? detail)
    {
        table = null;
        values = null;
        detail = null;
        try
        {
            if (!File.Exists(path)) return "Engine.Error.DataMissing";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".json")
            {
                string? key = EngineJson.ReadData(File.ReadAllText(path), out var records, out values, out detail);
                if (key is not null) return key;
                if (records is not null) table = PrintEngine.RecordsTable(records);
                return null;
            }
            table = ext is ".xlsx" or ".xlsm"
                ? XlsxReader.ReadFile(path, new XlsxOptions { SheetName = sheet })
                : CsvReader.ReadFile(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException
                                       or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            detail = ex.Message;
            return "Engine.Error.DataFile";
        }
    }

    // ---- status and printers ----------------------------------------------------------------------------------------------

    private static async Task<int> StatusAsync(CommandLine line, CliHost host, EngineText text, CancellationToken ct)
    {
        if (host.Policy.PrintEngineBlocked) return Refused(line, host, text);
        int? port = null;
        if (line.Value("port") is { } rawPort)
        {
            if (!int.TryParse(rawPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)) return Usage(host, text, "Engine.Cli.BadValue", "--port", rawPort);
            port = p;
        }
        if (PrintEngine.TryBuildTarget(line.Value("printer") ?? line.Positionals.FirstOrDefault(), line.Value("host"), port, out var target) is { } error)
            return Usage(host, text, error, string.Empty);
        var engine = new PrintEngine(host.Print);
        var report = await engine.GetStatusAsync(target!, text, ct).ConfigureAwait(false);
        if (line.Has("json")) host.Out.WriteLine(JsonSerializer.Serialize(report, EngineJson.Indented));
        else host.Out.WriteLine(report.Printer + ": " + report.Message);
        return !report.Available ? ExitCodes.Failed : report.Ready == true ? ExitCodes.Ok : ExitCodes.PrinterNotReady;
    }

    private static async Task<int> PrintersAsync(CommandLine line, CliHost host, EngineText text, CancellationToken ct)
    {
        var engine = new PrintEngine(host.Print);
        var printers = (await engine.GetPrintersAsync(ct).ConfigureAwait(false))
            .Select(p => new PrinterEntry(p.Name, p.PortName, p.DriverName, p.IsDefault, p.IsOffline, true)).ToList();
        if (line.Has("json"))
        {
            host.Out.WriteLine(JsonSerializer.Serialize(printers, EngineJson.Indented));
            return ExitCodes.Ok;
        }
        if (printers.Count == 0) host.Out.WriteLine(text.Get("Engine.Printers.None"));
        foreach (var p in printers)
        {
            string flags = (p.IsDefault ? " " + text.Get("Engine.Printers.Default") : string.Empty) + (p.IsOffline ? " " + text.Get("Engine.Printers.Offline") : string.Empty);
            host.Out.WriteLine(p.Name + "  (" + p.Port + ", " + p.Driver + ")" + flags);
        }
        return ExitCodes.Ok;
    }

    // ---- keys and the server ----------------------------------------------------------------------------------------------

    private static int NewKey(CommandLine line, CliHost host, EngineText text)
    {
        var (key, hash) = ApiKeys.Create();
        if (line.Has("json")) host.Out.WriteLine(JsonSerializer.Serialize(new { key, hash }, EngineJson.Indented));
        else host.Out.WriteLine(text.Get("Engine.Cli.NewKey", key, hash));
        return ExitCodes.Ok;
    }

    private static async Task<int> ServeAsync(CommandLine line, CliHost host, EngineText text, string[] args, CancellationToken ct)
    {
        if (host.Policy.PrintEngineBlocked) return Refused(line, host, text);
        string path = Path.GetFullPath(line.Value("settings") ?? Path.Combine(host.BaseDirectory, EngineSettings.FileName));
        if (line.Has("init"))
        {
            if (File.Exists(path)) return Usage(host, text, "Engine.Cli.InitExists", path);
            var (key, hash) = ApiKeys.Create();
            string folder = line.Value("label-folder") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rotula Labels");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, EngineSettingsLoader.Starter(hash, folder), new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                host.Error.WriteLine(text.Get("Engine.Settings.Invalid", path, ex.Message));
                return ExitCodes.Failed;
            }
            host.Out.WriteLine(text.Get("Engine.Cli.InitDone", path));
            host.Out.WriteLine(text.Get("Engine.Cli.NewKey", key, hash));
            return ExitCodes.Ok;
        }

        var loaded = EngineSettingsLoader.Load(path, text);
        foreach (var warning in loaded.Warnings) host.Error.WriteLine(text.Get("Engine.Cli.WarningPrefix") + " " + warning);
        if (loaded.Settings is null)
        {
            foreach (var error in loaded.Errors) host.Error.WriteLine(error);
            return ExitCodes.Usage;
        }
        var settings = loaded.Settings;
        if (line.Values("urls") is { Count: > 0 } urls) settings = settings with { Urls = urls.SelectMany(u => u.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList() };
        var engine = new PrintEngine(host.Print, host.Counters(line.Value("counter-file") ?? settings.CounterFile), host.Rasterizer);
        return await host.Serve(loaded with { Settings = settings }, engine, EngineText.For(settings.Language), host.Policy, args, ct).ConfigureAwait(false);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private static int Refused(CommandLine line, CliHost host, EngineText text) =>
        Report(line, host, text, ExitCodes.Refused, text.Get("Engine.Error.PrintingBlocked"), "Engine.Error.PrintingBlocked");

    private static int Usage(CliHost host, EngineText text, string key, params object[] args)
    {
        host.Error.WriteLine(text.Get(key, args));
        host.Error.WriteLine(text.Get("Engine.Cli.SeeHelp"));
        return ExitCodes.Usage;
    }

    /// <summary>A problem found before the engine ran: as JSON with --json (same shape as a print result), otherwise as one line.</summary>
    private static int Report(CommandLine line, CliHost host, EngineText text, int code, string message, string? errorCode = null)
    {
        if (line.Has("json"))
        {
            var outcome = code switch { ExitCodes.Refused => PrintOutcome.Refused, ExitCodes.Usage => PrintOutcome.Invalid, _ => PrintOutcome.Failed };
            host.Out.WriteLine(JsonSerializer.Serialize(new PrintJobResult(outcome, message, errorCode ?? outcome.ToString(), string.Empty, 0, 0, 0, 0, [], null,
                line.Value("reference")), EngineJson.Indented));
        }
        else
        {
            host.Error.WriteLine(message);
        }
        return code;
    }

    internal static string Version() =>
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
