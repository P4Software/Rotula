using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Prompts;

namespace Rotula.Files;

/// <summary>
/// Reads and writes one small text file under an exclusive lock, so two programs (or threads) can never interleave their changes, and replaces
/// it atomically so a crash or power cut leaves either the old or the new content, never half of one.
/// </summary>
/// <remarks>
/// The lock is an exclusive open of a sidecar file (<c>name.lock</c>). The operating system drops it when the owner dies, so a crashed program
/// cannot leave a stale lock behind, and it works across processes and user sessions without named kernel objects.
/// </remarks>
internal sealed class LockedTextFile
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private readonly TimeSpan _timeout;

    public LockedTextFile(string path, TimeSpan timeout)
    {
        _path = System.IO.Path.GetFullPath(path);
        _timeout = timeout;
    }

    public string Path => _path;

    /// <summary>Runs <paramref name="action"/> with the current text (null when the file does not exist). A returned text is saved; null leaves the file as it is.</summary>
    public T Run<T>(Func<string?, (T Result, string? NewText)> action)
    {
        using var gate = Acquire();
        string? text = Read();
        var (result, newText) = action(text);
        if (newText is not null) Write(newText);
        return result;
    }

    /// <summary>Moves the file aside (keeping it for the user) and returns the new name, or null when there was no file.</summary>
    public string? MoveAside()
    {
        using var gate = Acquire();
        if (!File.Exists(_path)) return null;
        string aside = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        for (int n = 2; File.Exists(aside); n++) aside = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + n;
        File.Move(_path, aside);
        return aside;
    }

    private FileStream Acquire()
    {
        string folder = System.IO.Path.GetDirectoryName(_path)!;
        var deadline = DateTime.UtcNow + _timeout;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(folder);
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another holder (or a scanner) has it for a moment; try again until the deadline.
                if (DateTime.UtcNow >= deadline)
                    throw new CounterStoreException(VariableMessageKeys.CounterStoreBusy, ex, _path);
                Thread.Sleep(10);
            }
        }
    }

    private string? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var info = new FileInfo(_path);
            if (info.Length > MaxBytes) throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, null, _path);
            return File.ReadAllText(_path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CounterStoreException(VariableMessageKeys.CounterStoreBusy, ex, _path);
        }
    }

    private void Write(string text)
    {
        string temp = _path + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                // Flush(true) pushes the data to the disk before the swap; otherwise a power cut could leave a renamed but empty file.
                fs.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak");
            else File.Move(temp, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CounterStoreException(VariableMessageKeys.CounterStoreBusy, ex, _path);
        }
    }
}

/// <summary>
/// The counters file: <c>%AppData%\Rotula\counters.json</c> by default. Every operation takes the file lock, reads the current content, applies
/// the change and writes the file atomically, so several Rotula windows (or a window and the MCP server) share one set of numbers without ever
/// handing out the same one twice. See <see cref="ICounterStore"/> for the reserve and confirm rules.
/// </summary>
/// <remarks>
/// A damaged file is never read as "empty" (that would restart the counters and reuse numbers): every call fails with
/// <see cref="VariableMessageKeys.CounterStoreCorrupt"/> until <see cref="RecoverFromCorruption"/> is called, which keeps the damaged file next to the
/// original under a new name and starts a fresh one. The previous version of the file is also kept as <c>counters.json.bak</c>.
/// </remarks>
public sealed class FileCounterStore : CounterStoreBase
{
    private readonly LockedTextFile _file;

    /// <summary>Creates a store over <paramref name="path"/>.</summary>
    /// <param name="path">The file; its folder is created when needed.</param>
    /// <param name="time">Clock for time stamps (tests).</param>
    /// <param name="lockTimeout">How long to wait for another holder of the lock (default 10 seconds).</param>
    public FileCounterStore(string path, TimeProvider? time = null, TimeSpan? lockTimeout = null) : base(time)
    {
        _file = new LockedTextFile(path, lockTimeout ?? TimeSpan.FromSeconds(10));
    }

    /// <summary><c>%AppData%\Rotula\counters.json</c>.</summary>
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "counters.json");

    /// <summary>Opens the store at <see cref="DefaultPath"/>.</summary>
    public static FileCounterStore OpenDefault() => new(DefaultPath);

    /// <summary>The file this store uses.</summary>
    public string Path => _file.Path;

    /// <inheritdoc />
    protected override T Transact<T>(bool write, Func<CounterBook, T> action)
    {
        // A pure look (a preview, the MCP server) must not create the folder or lock anything when there is nothing to read.
        if (!write && !File.Exists(_file.Path)) return action(new CounterBook());
        return _file.Run<T>(text =>
        {
            var book = text is null ? new CounterBook() : CounterBook.FromJson(text, _file.Path);
            T result = action(book);
            return (result, write ? book.ToJson() : null);
        });
    }

    /// <summary>
    /// After <see cref="VariableMessageKeys.CounterStoreCorrupt"/>: moves the damaged file aside (it is kept, so the last numbers can still be read from
    /// it by hand) and lets the counters start again from their saved positions of zero. Returns the new name of the damaged file, or null if there was none.
    /// The caller should then let the operator set each counter to the right number (<see cref="CounterAdmin.Reset"/>).
    /// </summary>
    public string? RecoverFromCorruption() => _file.MoveAside();
}

/// <summary>The remembered prompt answers: <c>%AppData%\Rotula\prompts.json</c> by default. A damaged file only loses the suggestions, so it is simply replaced.</summary>
public sealed class FilePromptMemory : IPromptMemory
{
    private const int MaxEntries = 5000;
    private readonly LockedTextFile _file;

    /// <summary>Creates the memory over <paramref name="path"/>.</summary>
    public FilePromptMemory(string path, TimeSpan? lockTimeout = null) => _file = new LockedTextFile(path, lockTimeout ?? TimeSpan.FromSeconds(5));

    /// <summary><c>%AppData%\Rotula\prompts.json</c>.</summary>
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "prompts.json");

    /// <inheritdoc />
    public string? Get(string labelId, string name)
    {
        try
        {
            return _file.Run<string?>(text => (Load(text).GetValueOrDefault(Key(labelId, name)), null));
        }
        catch (CounterStoreException)
        {
            return null; // a suggestion that cannot be read is simply not offered
        }
    }

    /// <inheritdoc />
    public void Remember(string labelId, IReadOnlyDictionary<string, string> answers)
    {
        try
        {
            _file.Run<int>(text =>
            {
                var map = Load(text);
                foreach (var (name, value) in answers)
                    map[Key(labelId, name)] = value.Length > 1000 ? value[..1000] : value;
                while (map.Count > MaxEntries) map.Remove(map.Keys.First());
                var obj = new JsonObject();
                foreach (var (k, v) in map) obj[k] = v;
                return (0, new JsonObject { ["version"] = 1, ["answers"] = obj }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            });
        }
        catch (CounterStoreException)
        {
            // Failing to remember an answer must never stop a print.
        }
    }

    private static string Key(string labelId, string name) => labelId + "/" + name.ToLowerInvariant();

    private static Dictionary<string, string> Load(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text is null) return map;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("answers", out var answers) && answers.ValueKind == JsonValueKind.Object)
                foreach (var p in answers.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && map.Count < MaxEntries)
                    {
                        string v = p.Value.GetString() ?? string.Empty;
                        map[p.Name] = v.Length > 1000 ? v[..1000] : v;
                    }
        }
        catch (JsonException)
        {
            // Damaged: start with no suggestions.
        }
        return map;
    }
}
