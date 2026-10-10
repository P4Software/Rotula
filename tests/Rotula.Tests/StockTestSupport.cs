using System.Text;
using Rotula.Core.Stocks;

namespace Rotula.Tests;

/// <summary>Shared helpers for the Stock* test files: temp folders and tiny catalogue JSON builders.</summary>
internal static class StockTestSupport
{
    /// <summary>The bundled catalogue (parsed once per process).</summary>
    public static StockCatalog Bundled => StockCatalog.LoadBundled();

    /// <summary>A fresh empty folder under the temp directory, deleted by the returned disposable.</summary>
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rotula-stock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>One valid entry as a JSON object text; tweak it with the replacements.</summary>
    public static string EntryJson(string id = "t-1", string name = "Test label 2 x 1 in", string extra = "")
        => "{\"id\":\"" + id + "\",\"brand\":\"Test\",\"name\":\"" + name + "\",\"widthMm\":50.8,\"heightMm\":25.4,\"widthIn\":2,\"heightIn\":1,"
           + "\"sources\":[\"t\"],\"verified\":false" + (extra.Length > 0 ? "," + extra : "") + "}";

    /// <summary>A catalogue file text around the given entry objects.</summary>
    public static string FileJson(IEnumerable<string> entries, int schemaVersion = 1)
        => "{\"metadata\":{\"schemaVersion\":" + schemaVersion + ",\"sources\":[{\"id\":\"t\",\"name\":\"Test\"}],\"notes\":\"test\"},\"entries\":[" + string.Join(",", entries) + "]}";

    public static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);
}
