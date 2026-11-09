using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.ClipboardHistory.Clipboard;

namespace ClassBox.Plugins.ClipboardHistory.Views;

/// <summary>剪贴板历史页面。</summary>
public partial class ClipboardHistoryView : UserControl
{
    private readonly ClipboardHistoryPlugin _plugin;
    private bool _ready;
    private bool _closed;

    public ClipboardHistoryView(ClipboardHistoryPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        _ready = true;
        _plugin.Monitor.Captured += OnMonitorCaptured;
        RefreshList();
        UpdatePauseState();
        Unloaded += OnViewUnloaded;
    }

    /// <summary>页面关闭：解除监听并落盘。</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _ready = false;
        try
        {
            _plugin.Monitor.Captured -= OnMonitorCaptured;
        }
        catch
        {
            // 插件已停止时监听器可能已释放，忽略。
        }

        _plugin.PersistIfDue(true);
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面仅隐藏时不销毁监听，仅取消事件订阅，避免重复订阅。
        try
        {
            _plugin.Monitor.Captured -= OnMonitorCaptured;
        }
        catch
        {
            // 忽略。
        }
    }

    private void OnMonitorCaptured(object? sender, ClipboardEntry entry)
    {
        if (_closed) return;
        try
        {
            if (Dispatcher.CheckAccess())
            {
                RefreshList();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(RefreshList));
            }
        }
        catch
        {
            // 页面已卸载，忽略。
        }
    }

    private void RefreshList()
    {
        if (!_ready || _closed) return;

        var engine = _plugin.Engine;
        var keyword = SearchBox.Text ?? string.Empty;
        var items = engine.Query(keyword);

        var selectedId = (HistoryList.SelectedItem as ClipboardEntry)?.Id;
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = items;

        if (selectedId is not null)
        {
            var reselect = items.FirstOrDefault(i => i.Id == selectedId);
            if (reselect is not null) HistoryList.SelectedItem = reselect;
        }

        CountText.Text = $"共 {engine.Count} 条 · 收藏 {engine.FavoriteCount} 条";

        if (EmptyHint is not null)
        {
            EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (engine.Count == 0)
            {
                EmptyHint.Text = "暂无剪贴板记录，复制一段文字后会自动出现在这里。";
            }
            else
            {
                EmptyHint.Text = "没有匹配的历史记录。";
            }
        }
    }

    private void UpdatePauseState()
    {
        if (!_ready || _closed) return;

        var paused = _plugin.Monitor.IsPaused;
        PauseButton.Content = paused ? "继续监听" : "暂停监听";
        StatusText.Text = paused
            ? "监听已暂停，暂停期间复制的文本不会被记录。"
            : "正在监听剪贴板…（每 0.7 秒轮询一次，仅记录文本）";
    }

    // ===== 事件 =====

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        RefreshList();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;

        if (HistoryList.SelectedItem is ClipboardEntry entry)
        {
            StatusText.Text = $"已选中 {entry.TimeText} 的记录，{entry.LengthText}；可点击「回填」写回剪贴板。";
        }
    }

    private void OnPauseToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var monitor = _plugin.Monitor;
        monitor.IsPaused = !monitor.IsPaused;
        UpdatePauseState();
        _plugin.LogInfo(monitor.IsPaused ? "剪贴板监听已暂停" : "剪贴板监听已恢复");
    }

    private void OnCopyBackClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if ((sender as FrameworkElement)?.DataContext is not ClipboardEntry entry) return;

        if (ClipboardMonitor.TrySetClipboardText(entry.Text))
        {
            _plugin.Monitor.MarkSeen(entry.Text);
            StatusText.Text = "已回填到剪贴板：" + entry.Summary;
        }
        else
        {
            StatusText.Text = "回填失败：剪贴板正被其它程序占用，请稍后重试。";
        }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if ((sender as FrameworkElement)?.DataContext is not ClipboardEntry entry) return;

        _plugin.Engine.ToggleFavorite(entry);
        _plugin.PersistIfDue(true);
        RefreshList();
        StatusText.Text = entry.IsFavorite ? "已收藏并置顶：" + entry.Summary : "已取消收藏：" + entry.Summary;
    }

    private void OnDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if ((sender as FrameworkElement)?.DataContext is not ClipboardEntry entry) return;

        _plugin.Engine.Remove(entry);
        _plugin.PersistIfDue(true);
        RefreshList();
        StatusText.Text = "已删除一条记录：" + entry.Summary;
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var removed = _plugin.Engine.ClearAll(keepFavorites: false);
        _plugin.PersistIfDue(true);
        RefreshList();
        StatusText.Text = removed == 0 ? "历史已为空，无需清除。" : $"已清除全部 {removed} 条记录。";
        _plugin.LogInfo($"清除全部剪贴板历史，共 {removed} 条");
    }

    private void OnClearUnfavoriteClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var removed = _plugin.Engine.ClearAll(keepFavorites: true);
        _plugin.PersistIfDue(true);
        RefreshList();
        StatusText.Text = removed == 0 ? "没有可清除的未收藏记录。" : $"已清除 {removed} 条未收藏记录，收藏项保留。";
    }
}
