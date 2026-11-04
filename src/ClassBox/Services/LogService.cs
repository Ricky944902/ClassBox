using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using ClassBox.Abstractions.Services;

namespace ClassBox.Services;

/// <summary>
/// 日志服务：内存集合供 UI 绑定 + 按天落盘。
/// 所有写入均在锁内完成，可从任意线程调用。
/// </summary>
public sealed class LogService : IAppLogger
{
    private readonly object _gate = new();
    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly string _logDirectory;
    private LogLevel _minimumLevel = LogLevel.Info;

    public LogService(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public event EventHandler<LogEntry>? EntryWritten;

    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    public string LogDirectory => _logDirectory;

    /// <summary>供 UI 直接绑定的只读视图（最近 500 条）。</summary>
    public ReadOnlyObservableCollection<LogEntry> Entries => new(_entries);

    public void Debug(string message, string? scope = null) => Write(LogLevel.Debug, message, null, scope);

    public void Info(string message, string? scope = null) => Write(LogLevel.Info, message, null, scope);

    public void Warn(string message, string? scope = null) => Write(LogLevel.Warning, message, null, scope);

    public void Error(string message, Exception? exception = null, string? scope = null)
        => Write(LogLevel.Error, message, exception, scope);

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void Write(LogLevel level, string message, Exception? exception, string? scope)
    {
        if (level < _minimumLevel && level != LogLevel.Error) return;

        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Scope = scope ?? "App",
            Message = message,
            Exception = exception?.ToString()
        };

        lock (_gate)
        {
            _entries.Add(entry);
            while (_entries.Count > 500)
            {
                _entries.RemoveAt(0);
            }
        }

        try
        {
            var file = Path.Combine(_logDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"{entry.Time:HH:mm:ss.fff} [{entry.LevelText}] [{entry.Scope}] {entry.Message}" +
                       (entry.Exception is null ? string.Empty : Environment.NewLine + entry.Exception);
            File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 日志落盘失败不得影响主流程
        }

        try
        {
            EntryWritten?.Invoke(this, entry);
        }
        catch
        {
            // 订阅方异常不得回传
        }
    }
}
