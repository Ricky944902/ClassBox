using System.Windows;

namespace ClassBox.Plugins.ClipboardHistory.Clipboard;

/// <summary>
/// 剪贴板后台监听器：在独立 STA 线程上轮询系统剪贴板文本，
/// 支持暂停/恢复，捕获到新内容时通过 Captured 事件通知界面。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly ClipboardHistoryEngine _engine;
    private readonly Thread _thread;
    private readonly int _intervalMs;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _disposed;
    private string _lastSeen = string.Empty;

    public ClipboardMonitor(ClipboardHistoryEngine engine, int intervalMs = 700)
    {
        _engine = engine;
        _intervalMs = Math.Max(200, intervalMs);
        _thread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "ClassBox.ClipboardMonitor"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>捕获到新的剪贴板文本（在监听线程上触发，界面层需自行调度）。</summary>
    public event EventHandler<ClipboardEntry>? Captured;

    public bool IsRunning => _running;

    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public ClipboardHistoryEngine Engine => _engine;

    public void Start()
    {
        if (_disposed) return;
        _running = true;
        if (!_thread.IsAlive) _thread.Start();
    }

    public void Stop()
    {
        _running = false;
    }

    private void PollLoop()
    {
        while (!_disposed)
        {
            if (_running && !_paused)
            {
                TryCaptureOnce();
            }

            Thread.Sleep(_intervalMs);
        }
    }

    /// <summary>读取一次当前剪贴板文本（需在 STA 线程调用）。</summary>
    public void TryCaptureOnce()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (string.Equals(text, _lastSeen, StringComparison.Ordinal)) return;

            _lastSeen = text;
            if (!_engine.Capture(text)) return;

            var entry = _engine.Entries.FirstOrDefault(e => string.Equals(e.Text, text, StringComparison.Ordinal));
            if (entry is not null) Captured?.Invoke(this, entry);
        }
        catch (Exception)
        {
            // 剪贴板被其它进程占用时忽略本次轮询，不中断监听。
        }
    }

    /// <summary>记录已处理的文本，避免回填后再次被当作新记录。</summary>
    public void MarkSeen(string? text)
    {
        _lastSeen = text ?? string.Empty;
    }

    /// <summary>把文本写回系统剪贴板（带重试，失败返回 false）。</summary>
    public static bool TrySetClipboardText(string text)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text ?? string.Empty);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        _running = false;
        try
        {
            if (_thread.IsAlive) _thread.Join(300);
        }
        catch
        {
            // 忽略线程退出异常。
        }
    }
}
