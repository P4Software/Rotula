using System.Text;
using Rotula.Core.Zpl.Catalog;

// Rewrites docs/zpl-coverage.md from the command catalogue (src/Rotula.Core/Zpl/Catalog/commands.json).
// It needs no arguments: it finds the repository by looking for Rotula.sln, starting at the current folder and then at the program.
string? root = FindRoot(Directory.GetCurrentDirectory()) ?? FindRoot(AppContext.BaseDirectory);
if (root is null)
{
    Console.WriteLine("Could not find the Rotula repository (a folder with Rotula.sln). Run this tool from inside the repository.");
    return 1;
}

string target = Path.Combine(root, "docs", "zpl-coverage.md");
string text = ZplCoverageReport.Render();
string? current = File.Exists(target) ? File.ReadAllText(target, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
if (current == text)
{
    Console.WriteLine("docs/zpl-coverage.md is already up to date.");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(target)!);
// LF endings and no byte order mark, so the file is identical on every machine and the staleness test compares like with like.
File.WriteAllText(target, text, new UTF8Encoding(false));
Console.WriteLine("Wrote " + target + " (" + ZplCommandCatalog.All.Count + " commands).");
return 0;

static string? FindRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
    return dir?.FullName;
}
