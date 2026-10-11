using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rotula.Mcp.Hosting;
using Rotula.Mcp.Settings;
using Rotula.Printing;

namespace Rotula.Mcp;

/// <summary>Entry point of the Rotula MCP server: reads the settings, then serves the Model Context Protocol on stdin/stdout.</summary>
public static class Program
{
    /// <summary>Starts the server. Returns 0 on a normal shutdown, 1 when it could not start.</summary>
    public static async Task<int> Main()
    {
        string settingsPath = Path.Combine(AppContext.BaseDirectory, McpSettingsLoader.FileName);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var loaded = McpSettingsLoader.Load(settingsPath, documents);

        // Started by a person (double-click or a console) instead of by an AI assistant application: explain, then wait.
        if (!Console.IsInputRedirected)
        {
            ShowHelp(loaded);
            return 0;
        }

        string logFolder = loaded.Settings.LogFolder
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "mcp");
        using var fileLog = new RollingFileLoggerProvider(logFolder);
        using var stderrLog = new StderrLoggerProvider();
        try
        {
            var context = new McpContext(loaded.Settings, new PrintService())
            {
                // Read-only use: previews show the next remembered serial number; the server never reserves or changes one.
                CounterStore = new Rotula.Files.FileCounterStore(Rotula.Files.FileCounterStore.DefaultPath),
            };
            using var host = McpServerBuilder.Build(context, stderrLog, fileLog);
            var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Rotula.Mcp");
            log.LogInformation("Rotula MCP server {Version} started. Settings file: {Path}. Printing {Printing}, databases {Database}.",
                McpServerBuilder.Version(), loaded.Path, loaded.Settings.AllowPrinting ? "allowed" : "off", loaded.Settings.AllowDatabase ? "on" : "off");
            foreach (var note in loaded.Notes) log.LogWarning("{Note}", note);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rotula-mcp] The server could not start ({ex.GetType().Name}). Check rotula-mcp.settings.json or run the program again.");
            return 1;
        }
    }

    private static void ShowHelp(McpSettingsLoadResult loaded)
    {
        string exe = Environment.ProcessPath ?? "Rotula.Mcp.exe";
        Console.WriteLine("Rotula MCP server");
        Console.WriteLine();
        Console.WriteLine("This program is not meant to be used by hand. An AI assistant application starts it and talks to it");
        Console.WriteLine("through the Model Context Protocol, so the assistant can design labels with Rotula.");
        Console.WriteLine();
        Console.WriteLine("To connect it, add this to the MCP server settings of your assistant application:");
        Console.WriteLine();
        Console.WriteLine("  {\"mcpServers\":{\"rotula\":{\"command\":" + System.Text.Json.JsonSerializer.Serialize(exe) + "}}}");
        Console.WriteLine();
        Console.WriteLine("Settings file (folders, printing, databases): " + loaded.Path);
        Console.WriteLine("Allowed label folders: " + string.Join("; ", loaded.Settings.AllowedFolders));
        Console.WriteLine("Printing: " + (loaded.Settings.AllowPrinting ? "allowed for the listed printers" : "off"));
        Console.WriteLine("Databases: " + (loaded.Settings.AllowDatabase ? "on (read-only)" : "off"));
        foreach (var note in loaded.Notes) Console.WriteLine("Note: " + note);
        Console.WriteLine();
        Console.WriteLine("Press Enter to close this window.");
        try { Console.ReadLine(); } catch (IOException) { }
    }
}
