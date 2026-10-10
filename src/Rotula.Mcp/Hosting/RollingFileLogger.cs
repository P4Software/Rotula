using System.Text;
using Microsoft.Extensions.Logging;

namespace Rotula.Mcp.Hosting;

/// <summary>
/// A small logger that appends to one file per day under a folder, keeps the newest week and never throws. It writes
/// plain text lines and is used together with <see cref="StderrLoggerProvider"/>; stdout is never used because it carries the
/// protocol.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const int KeepFiles = 7;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private readonly string _folder;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private bool _failed;

    /// <summary>Creates the provider and removes log files older than the newest seven.</summary>
    /// <param name="folder">Log folder (created if missing).</param>
    /// <param name="now">Clock, injectable for tests.</param>
    public RollingFileLoggerProvider(string folder, Func<DateTime>? now = null)
    {
        _folder = folder;
        _now = now ?? (() => DateTime.Now);
        Prune();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() { }

    private void Write(LogLevel level, string message)
    {
        if (_failed) return;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                string path = Path.Combine(_folder, $"rotula-mcp-{_now():yyyyMMdd}.log");
                // A runaway loop must not fill the disk: stop appending once the day's file is large.
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes) return;
                File.AppendAllText(path, $"{_now():HH:mm:ss} {level,-11} {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failed = true; // logging is a convenience; the server must keep working without it
            }
        }
    }

    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            foreach (var old in Directory.GetFiles(_folder, "rotula-mcp-*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepFiles))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // A tool that refuses a request throws McpException on purpose; the SDK reports it as an unhandled error, which it is not.
            // The call filter already logs "failed" for it.
            if (exception is ModelContextProtocol.McpException) return;
            // Only the exception type is logged, never its message: messages can quote file content or connection details.
            string text = formatter(state, exception) + (exception is null ? "" : " [" + exception.GetType().Name + "]");
            owner.Write(logLevel, category + ": " + text);
        }
    }
}

/// <summary>Writes log lines to standard error, which clients show to the user and which never mixes with the protocol on stdout.</summary>
public sealed class StderrLoggerProvider : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new StderrLogger();

    /// <inheritdoc />
    public void Dispose() { }

    private sealed class StderrLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (exception is ModelContextProtocol.McpException) return;
            try
            {
                Console.Error.WriteLine("[rotula-mcp] " + formatter(state, exception) + (exception is null ? "" : " [" + exception.GetType().Name + "]"));
            }
            catch (IOException) { }
        }
    }
}
