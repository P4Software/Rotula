using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rotula.Engine;
using Rotula.Files.Policy;
using Rotula.Printing;

namespace Rotula.Cli.Server;

/// <summary>An error answer of the REST API: a stable code for programs and a sentence for people.</summary>
/// <param name="ErrorCode">Stable key, for example <c>Engine.Error.PrinterNotAllowed</c>.</param>
/// <param name="Message">The sentence in the caller's language.</param>
public sealed record ApiError(string ErrorCode, string Message);

/// <summary>The body of a status request for a network printer (or a Windows printer whose name is awkward in a URL).</summary>
/// <param name="Printer">An installed Windows printer.</param>
/// <param name="Host">Host or IP address of a network printer.</param>
/// <param name="Port">Its TCP port (9100 when not set).</param>
public sealed record StatusRequest(string? Printer, string? Host, int? Port);

/// <summary>The answer of the health check.</summary>
/// <param name="Status">Always <c>ok</c> when the server answers.</param>
/// <param name="Version">The program version.</param>
/// <param name="PrintingBlocked">True when the organization's policy switched printing off.</param>
public sealed record HealthInfo(string Status, string Version, bool PrintingBlocked);

/// <summary>
/// The REST API of the print engine (<c>rotula serve</c>). Every route except the health check and the OpenAPI document needs an API key
/// (<c>X-Api-Key</c> header, or <c>Authorization: Bearer</c>); printers and labels are limited to what the settings file lists.
/// </summary>
internal static class EngineServer
{
    /// <summary>Starts the server, prints where it listens, and runs until Ctrl+C or the Windows service stops it.</summary>
    public static async Task<int> RunAsync(EngineSettingsLoad loaded, PrintEngine engine, EngineText text, RotulaPolicy policy, string[] args, CancellationToken ct)
    {
        var settings = loaded.Settings ?? throw new ArgumentException("The settings are not usable.", nameof(loaded));
        await using var app = Build(settings, engine, policy);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // Typically the port is taken by another program (or another copy of this one).
            Console.Error.WriteLine(text.Get("Engine.Server.CannotListen", string.Join(", ", settings.Urls), ex.Message));
            return ExitCodes.Failed;
        }
        Console.WriteLine(text.Get("Engine.Server.Listening", string.Join(", ", settings.Urls), loaded.Path));
        try { await app.WaitForShutdownAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    /// <summary>Builds the web application. <paramref name="configure"/> lets tests swap Kestrel for the in-memory test server.</summary>
    public static WebApplication Build(EngineSettings settings, PrintEngine engine, RotulaPolicy policy, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(policy);

        // No command-line arguments are handed to the host: they are rotula's own, and must not reach the web host's configuration.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Host.UseWindowsService(o => o.ServiceName = "RotulaEngine");
        builder.WebHost.UseUrls([.. settings.Urls]);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = settings.MaxRequestBytes;
        });
        builder.Services.AddOpenApi("v1", o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "Rotula print engine";
            doc.Info.Version = CliApp.Version();
            return Task.CompletedTask;
        }));
        configure?.Invoke(builder);

        var app = builder.Build();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Rotula.Engine.Jobs");

        app.MapOpenApi("/openapi/{documentName}.json");
        var api = app.MapGroup("/api/v1");

        api.MapGet("/health", () => Results.Json(new HealthInfo("ok", CliApp.Version(), policy.PrintEngineBlocked), EngineJson.Options))
            .WithName("Health").WithSummary("Answers when the server runs. No API key needed.").Produces<HealthInfo>();

        var secured = api.MapGroup(string.Empty).AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            string? key = http.Request.Headers[ApiKeys.Header].FirstOrDefault();
            string auth = http.Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(key) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = auth[7..].Trim();
            if (!ApiKeys.Matches(key, settings.ApiKeyHashes))
            {
                log.LogWarning("Refused a call to {Path} from {Remote}: no valid API key.", LogValue(http.Request.Path.Value), http.Connection.RemoteIpAddress);
                return Error(Text(http, settings), StatusCodes.Status401Unauthorized, "Engine.Error.ApiKey");
            }
            // A short, non-reversible id of the key for the log: enough to tell callers apart, useless to call the server.
            http.Items["keyId"] = ApiKeys.Hash(key!)[..8];
            return await next(context).ConfigureAwait(false);
        });

        secured.MapGet("/printers", async (HttpContext http) =>
        {
            var printers = await engine.GetPrintersAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Json(printers.Select(p => new PrinterEntry(p.Name, p.PortName, p.DriverName, p.IsDefault, p.IsOffline,
                settings.IsPrinterAllowed(PrinterTarget.Spooler(p.Name)))).ToList(), EngineJson.Options);
        }).WithName("ListPrinters").WithSummary("Lists the Windows printers and which ones this server may print to.").Produces<List<PrinterEntry>>();

        secured.MapGet("/printers/{name}/status", (HttpContext http, string name) => StatusAsync(http, name, null, null))
            .WithName("GetPrinterStatus").WithSummary("Asks a Windows printer for its status (~HS).")
            .Produces<PrinterStatusReport>().Produces<ApiError>(StatusCodes.Status403Forbidden);

        secured.MapPost("/printers/status", async (HttpContext http) =>
        {
            var body = await ReadAsync<StatusRequest>(http, settings).ConfigureAwait(false);
            if (body.Error is not null) return body.Error;
            return await StatusAsync(http, body.Value!.Printer, body.Value.Host, body.Value.Port).ConfigureAwait(false);
        }).WithName("PrinterStatus").WithSummary("Asks a Windows or network printer for its status (~HS).").Accepts<StatusRequest>("application/json")
            .Produces<PrinterStatusReport>().Produces<ApiError>(StatusCodes.Status400BadRequest).Produces<ApiError>(StatusCodes.Status403Forbidden);

        secured.MapPost("/jobs", async (HttpContext http) =>
        {
            var text = Text(http, settings);
            if (policy.PrintEngineBlocked) return Error(text, StatusCodes.Status403Forbidden, "Engine.Error.PrintingBlocked");
            var body = await ReadAsync<PrintJobRequest>(http, settings).ConfigureAwait(false);
            if (body.Error is not null) return body.Error;
            var request = body.Value!;

            if (PrintEngine.TryBuildTarget(request.Printer, request.Host, request.Port, out var target) is { } targetError)
                return Error(text, StatusCodes.Status400BadRequest, targetError);
            if (!settings.IsPrinterAllowed(target!))
                return Error(text, StatusCodes.Status403Forbidden, "Engine.Error.PrinterNotAllowed", target!.DisplayName);

            var label = LoadLabel(request, settings, text, out var labelError);
            if (labelError is not null) return labelError;

            var result = await engine.PrintAsync(label!, request, null, text, settings.Limits, http.RequestAborted).ConfigureAwait(false);
            // The audit line: who (key id, address), what (reference, printer, labels) and how it ended. Never the label data.
            log.LogInformation("Job {Reference} key {KeyId} from {Remote}: {Outcome} on {Printer}, {Labels} label(s) in {Jobs} job(s){Error}.",
                LogValue(request.Reference), http.Items["keyId"], http.Connection.RemoteIpAddress, result.Outcome, LogValue(result.Printer), result.Labels, result.Jobs,
                result.ErrorCode is null ? string.Empty : " (" + result.ErrorCode + ")");
            return Results.Json(result, EngineJson.Options, statusCode: StatusFor(result.Outcome));
        }).WithName("Print").WithSummary("Prints a label, once or for every record, on an allowed printer.")
            .Accepts<PrintJobRequest>("application/json")
            .Produces<PrintJobResult>().Produces<ApiError>(StatusCodes.Status400BadRequest).Produces<ApiError>(StatusCodes.Status401Unauthorized)
            .Produces<ApiError>(StatusCodes.Status403Forbidden).Produces<ApiError>(StatusCodes.Status404NotFound)
            .Produces<PrintJobResult>(StatusCodes.Status409Conflict).Produces<PrintJobResult>(StatusCodes.Status422UnprocessableEntity)
            .Produces<PrintJobResult>(StatusCodes.Status502BadGateway);

        return app;

        async Task<IResult> StatusAsync(HttpContext http, string? printer, string? host, int? port)
        {
            var text = Text(http, settings);
            if (policy.PrintEngineBlocked) return Error(text, StatusCodes.Status403Forbidden, "Engine.Error.PrintingBlocked");
            if (PrintEngine.TryBuildTarget(printer, host, port, out var target) is { } error) return Error(text, StatusCodes.Status400BadRequest, error);
            if (!settings.IsPrinterAllowed(target!)) return Error(text, StatusCodes.Status403Forbidden, "Engine.Error.PrinterNotAllowed", target!.DisplayName);
            var report = await engine.GetStatusAsync(target!, text, http.RequestAborted).ConfigureAwait(false);
            return Results.Json(report, EngineJson.Options);
        }
    }

    /// <summary>The HTTP status of a print outcome. The body is the full result in every case.</summary>
    internal static int StatusFor(PrintOutcome outcome) => outcome switch
    {
        PrintOutcome.Printed or PrintOutcome.DryRun => StatusCodes.Status200OK,
        PrintOutcome.Invalid => StatusCodes.Status400BadRequest,
        PrintOutcome.InvalidLabel => StatusCodes.Status422UnprocessableEntity,
        PrintOutcome.Refused => StatusCodes.Status403Forbidden,
        PrintOutcome.HeldBack => StatusCodes.Status409Conflict,
        PrintOutcome.Cancelled => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway,
    };

    /// <summary>Finds the label of a request within the server's rules: a file in a label folder, or content when the settings allow it.</summary>
    private static Rotula.Files.RotulaFileContent? LoadLabel(PrintJobRequest request, EngineSettings settings, EngineText text, out IResult? error)
    {
        error = null;
        bool hasPath = !string.IsNullOrWhiteSpace(request.Label), hasContent = !string.IsNullOrWhiteSpace(request.LabelContent);
        if (hasPath == hasContent)
        {
            error = Error(text, StatusCodes.Status400BadRequest, hasPath ? "Engine.Error.LabelBoth" : "Engine.Error.LabelRequired");
            return null;
        }
        LabelLoad load;
        if (hasContent)
        {
            if (!settings.AllowInlineLabels)
            {
                error = Error(text, StatusCodes.Status403Forbidden, "Engine.Error.InlineLabelsOff");
                return null;
            }
            if (request.LabelContent!.Length > settings.MaxLabelBytes)
            {
                error = Error(text, StatusCodes.Status400BadRequest, "Engine.Error.LabelTooLarge", "labelContent", settings.MaxLabelBytes / 1024);
                return null;
            }
            load = LabelFiles.Parse(request.LabelContent);
        }
        else
        {
            string? path = LabelFiles.Resolve(request.Label, settings.LabelFolders, out var key);
            if (path is null)
            {
                error = Error(text, key == "Engine.Error.LabelNotFound" ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden, key!, request.Label!);
                return null;
            }
            load = LabelFiles.Load(path, settings.MaxLabelBytes);
        }
        if (load.Content is null)
        {
            error = Error(text, StatusCodes.Status422UnprocessableEntity, load.ErrorKey!, load.Args);
            return null;
        }
        return load.Content;
    }

    /// <summary>Reads a JSON body with the engine's rules (unknown fields refused), or the 400 answer that says what is wrong with it.</summary>
    private static async Task<(T? Value, IResult? Error)> ReadAsync<T>(HttpContext http, EngineSettings settings) where T : class
    {
        var text = Text(http, settings);
        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(http.Request.Body, EngineJson.Options, http.RequestAborted).ConfigureAwait(false);
            return value is null ? (null, Error(text, StatusCodes.Status400BadRequest, "Engine.Error.BadRequest", "null")) : (value, null);
        }
        catch (JsonException ex)
        {
            return (null, Error(text, StatusCodes.Status400BadRequest, "Engine.Error.BadRequest", ex.Message));
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
        {
            // Body larger than maxRequestBytes, or cut off.
            return (null, Error(text, ex.StatusCode, "Engine.Error.BadRequest", ex.Message));
        }
    }

    /// <summary>The caller's language: <c>?lang=</c>, then <c>Accept-Language</c>, then the server's setting.</summary>
    /// <summary>A caller's text made safe for one log line: control characters (CR, LF...) become spaces, so a request cannot forge
    /// log entries, and long values are cut.</summary>
    internal static string LogValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "-";
        if (value.Length > 200) value = value[..200] + "...";
        value = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return string.Create(value.Length, value, static (span, v) =>
        {
            for (int i = 0; i < v.Length; i++) span[i] = char.IsControl(v[i]) ? ' ' : v[i];
        });
    }

    private static EngineText Text(HttpContext http, EngineSettings settings) =>
        EngineText.For(http.Request.Query["lang"].FirstOrDefault() ?? http.Request.Headers.AcceptLanguage.FirstOrDefault() ?? settings.Language);

    private static IResult Error(EngineText text, int status, string key, params object[] args) =>
        Results.Json(new ApiError(key, text.Get(key, args)), EngineJson.Options, statusCode: status);
}
