using System.ComponentModel;

namespace ClassBox.Plugins.ProcessManager.Processes;

/// <summary>进程列表行。</summary>
public sealed class ProcessItem : INotifyPropertyChanged
{
    private long _memoryBytes;
    private string _memoryText = "-";

    public int Pid { get; init; }

    public string Name { get; init; } = string.Empty;

    public string StartTimeText { get; init; } = "-";

    public string Path { get; init; } = "-";

    public string Title { get; init; } = string.Empty;

    /// <summary>系统关键进程：禁止结束。</summary>
    public bool IsProtected { get; init; }

    public string ProtectedText => IsProtected ? "受保护" : string.Empty;

    public long MemoryBytes
    {
        get => _memoryBytes;
        set
        {
            if (_memoryBytes == value) return;
            _memoryBytes = value;
            _memoryText = ProcessManagerEngine.FormatMemory(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MemoryText)));
        }
    }

    public string MemoryText
    {
        get => _memoryText;
        private set
        {
            _memoryText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MemoryText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>进程列表快照。</summary>
public sealed class ProcessSnapshot
{
    public IReadOnlyList<ProcessItem> Items { get; init; } = Array.Empty<ProcessItem>();

    public long TotalMemoryBytes { get; init; }

    public int SkippedCount { get; init; }

    public long ElapsedMs { get; init; }

    public string TotalMemoryText => ProcessManagerEngine.FormatMemory(TotalMemoryBytes);

    public string ElapsedText => $"{ElapsedMs} ms";
}
