using System.Text;

namespace Hdlss.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} {Level.ToString().ToUpperInvariant(),-7} {Message}";
}

/// <summary>
/// Daily activity log. Unlike 1.x, no exception is swallowed silently: anything caught and
/// ignored on purpose is still written here at Debug/Warning level.
/// </summary>
public sealed class Log
{
    private readonly string? _folder;
    private readonly object _gate = new();
    public event Action<LogEntry>? Written;

    public Log(string? folder) { _folder = folder; }

    public static Log Null { get; } = new(null);

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Warn(string context, Exception ex) => Write(LogLevel.Warning, $"{context}: {ex.GetType().Name}: {ex.Message}");
    public void Error(string context, Exception ex) => Write(LogLevel.Error, $"{context}: {ex}");

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        if (_folder != null)
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(_folder);
                    File.AppendAllText(Path.Combine(_folder, $"hdlss-{entry.Time:yyyyMMdd}.log"), entry + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { /* logging must never break an operation */ }
            }
        }
        Written?.Invoke(entry);
    }

    public void Prune(int keepDays)
    {
        if (_folder == null || !Directory.Exists(_folder)) return;
        foreach (var file in Directory.EnumerateFiles(_folder, "hdlss-*.log"))
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-keepDays))
                File.Delete(file);
    }
}

/// <summary>Progress report for long operations: message plus optional percentage (0–100).</summary>
public readonly record struct OperationProgress(string Message, double? Percent = null);
