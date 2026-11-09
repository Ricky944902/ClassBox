using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ClassBox.Plugins.ProcessManager.Processes;

namespace ClassBox.Plugins.ProcessManager.Views;

/// <summary>进程管理页面。</summary>
public partial class ProcessManagerView : UserControl
{
    private readonly ProcessManagerPlugin _plugin;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _timer;
    private readonly List<ProcessItem> _items = new();

    private CancellationTokenSource? _cts;
    private ProcessItem? _pendingKill;
    private bool _busy;
    private bool _closed;

    public ProcessManagerView(ProcessManagerPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        _view = new ListCollectionView(_items);
        _view.Filter = FilterItem;
        ProcessGrid.ItemsSource = _view;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync(silent: true);

        Loaded += async (_, _) => await RefreshAsync(silent: false);
        Unloaded += (_, _) => StopTimer();
    }

    // ===================== 刷新 =====================

    private async Task RefreshAsync(bool silent)
    {
        if (_busy || _closed) return;

        _busy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        if (!silent)
        {
            RefreshButton.IsEnabled = false;
            FooterText.Text = "正在枚举进程…";
        }

        try
        {
            var snapshot = await ProcessManagerEngine.CaptureAsync(token);
            if (_closed) return;

            var selectedPid = (ProcessGrid.SelectedItem as ProcessItem)?.Pid;

            _items.Clear();
            _items.AddRange(snapshot.Items);
            _view.Refresh();

            CountText.Text = $"{snapshot.Items.Count:N0}";
            MemoryText.Text = snapshot.TotalMemoryText;

            if (selectedPid is { } pid)
            {
                var keep = _items.FirstOrDefault(p => p.Pid == pid);
                if (keep is not null) ProcessGrid.SelectedItem = keep;
            }

            FooterText.Text = silent
                ? $"已自动刷新 · {DateTime.Now:HH:mm:ss} · 内存合计 {snapshot.TotalMemoryText}"
                : $"枚举完成，共 {snapshot.Items.Count} 个进程，内存合计 {snapshot.TotalMemoryText}，耗时 {snapshot.ElapsedText}" +
                  (snapshot.SkippedCount > 0 ? $"（{snapshot.SkippedCount} 个进程信息读取失败已跳过）" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // 页面关闭或取消，忽略
        }
        catch (Exception ex)
        {
            FooterText.Text = $"刷新进程列表失败：{ex.Message}";
            _plugin.LogError("刷新进程列表异常", ex);
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(silent: false);

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshCheck.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    private void StopTimer()
    {
        if (_timer.IsEnabled) _timer.Stop();
    }

    // ===================== 过滤与排序 =====================

    private bool FilterItem(object obj)
    {
        if (obj is not ProcessItem item) return false;

        var keyword = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword)) return true;

        return item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || item.Pid.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || item.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        var count = _view.Count;
        FooterText.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "已清除过滤条件。"
            : $"过滤结果：{count} 个进程匹配「{SearchBox.Text.Trim()}」";
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view is null || SortCombo is null) return;

        _view.SortDescriptions.Clear();
        switch (SortCombo.SelectedIndex)
        {
            case 1:
                _view.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.MemoryBytes), ListSortDirection.Ascending));
                break;
            case 2:
                _view.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.Name), ListSortDirection.Ascending));
                break;
            case 3:
                _view.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.Name), ListSortDirection.Descending));
                break;
            case 4:
                _view.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.Pid), ListSortDirection.Ascending));
                break;
            default:
                _view.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.MemoryBytes), ListSortDirection.Descending));
                break;
        }

        _view.Refresh();
    }

    // ===================== 结束进程（页面内确认） =====================

    private void ProcessGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessItem item) return;
        FooterText.Text = $"进程：{item.Name}（PID {item.Pid}）· 路径：{item.Path} · 启动时间：{item.StartTimeText}";
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessItem item)
        {
            FooterText.Text = "请先在列表中选择要结束的进程。";
            return;
        }

        if (item.IsProtected)
        {
            FooterText.Text = $"「{item.Name}」(PID {item.Pid}) 是系统关键进程或宿主自身，禁止结束。";
            return;
        }

        _pendingKill = item;
        ConfirmDetailText.Text =
            $"进程名称：{item.Name}\nPID：{item.Pid}\n内存占用：{item.MemoryText}\n路径：{item.Path}\n\n" +
            "结束后该程序未保存的数据将丢失，且无法撤销。";
        ConfirmOverlay.Visibility = Visibility.Visible;
        StopTimer();
    }

    private async void ConfirmKill_Click(object sender, RoutedEventArgs e)
    {
        var item = _pendingKill;
        HideConfirm();

        if (item is null) return;

        FooterText.Text = $"正在结束进程「{item.Name}」(PID {item.Pid}) …";
        var (success, message) = await ProcessManagerEngine.KillAsync(item);

        FooterText.Text = message;
        if (success)
        {
            _plugin.LogWarn($"用户结束了进程：{item.Name} (PID {item.Pid})，路径 {item.Path}");
            await RefreshAsync(silent: true);
        }
        else
        {
            _plugin.LogWarn($"结束进程失败：{item.Name} (PID {item.Pid}) - {message}");
        }

        if (AutoRefreshCheck.IsChecked == true) _timer.Start();
    }

    private void CancelKill_Click(object sender, RoutedEventArgs e)
    {
        HideConfirm();
        FooterText.Text = "已取消结束进程操作。";
        if (AutoRefreshCheck.IsChecked == true) _timer.Start();
    }

    private void HideConfirm()
    {
        _pendingKill = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>页面卸载：停止自动刷新与后台枚举。</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;

        StopTimer();
        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            _plugin.LogWarn($"关闭进程管理页面时出现异常：{ex.Message}");
        }
    }
}
