namespace ClassBox.Abstractions.Services;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;

    public LogLevel Level { get; init; }

    public string Scope { get; init; } = "App";

    public string Message { get; init; } = string.Empty;

    public string? Exception { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => "日志"
    };
}

/// <summary>全局日志服务（内存环形缓冲 + 文件落盘）。</summary>
public interface IAppLogger
{
    event EventHandler<LogEntry>? EntryWritten;

    LogLevel MinimumLevel { get; set; }

    string LogDirectory { get; }

    void Debug(string message, string? scope = null);

    void Info(string message, string? scope = null);

    void Warn(string message, string? scope = null);

    void Error(string message, Exception? exception = null, string? scope = null);
}
