using Rotula.Files.Policy;
using Rotula.Printing;

namespace Rotula.Cli;

/// <summary>Entry point of <c>rotula</c>: the print engine's command line and REST server.</summary>
public static class Program
{
    /// <summary>Runs the command and returns its exit code (see docs/print-engine.md).</summary>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var cts = new CancellationTokenSource();
        // Ctrl+C stops the work cleanly: serial numbers of labels that were not sent go back, and the server shuts down.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var host = new CliHost
        {
            Print = new PrintService(),
            // Group Policy / Intune can switch the print engine off for the whole computer (docs/deployment.md).
            Policy = RotulaPolicy.Read(RegistryPolicySource.ForThisComputer()),
        };
        return await CliApp.RunAsync(args, host, cts.Token).ConfigureAwait(false);
    }
}
