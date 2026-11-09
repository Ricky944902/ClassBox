using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.PortScanner.Scanning;

namespace ClassBox.Plugins.PortScanner.Views;

/// <summary>端口扫描页面：参数设置、TCP Connect 扫描、Ping 检测与结果导出。</summary>
public partial class PortScannerView : UserControl
{
    private readonly PortScannerPlugin _plugin;
    private readonly ObservableCollection<PortScanResult> _display = new();
    private readonly List<PortScanResult> _all = new();

    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _closed;

    public PortScannerView(PortScannerPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        ResultList.ItemsSource = _display;
        ApplyFilter();
    }

    // ===================== 扫描 =====================

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var host = HostBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            Hint("请输入目标 IP 或主机名");
            return;
        }

        var ports = PortScannerEngine.ParsePorts(PortsBox.Text ?? string.Empty, out var error);
        if (ports.Count == 0)
        {
            Hint(error);
            return;
        }

        var timeout = ParseInt(TimeoutBox.Text, 800);
        if (timeout < 50 || timeout > 10000)
        {
            Hint("超时时间建议在 50 - 10000 毫秒之间");
            return;
        }

        var concurrency = ParseInt(ConcurrencyBox.Text, 64);
        if (concurrency < 1) concurrency = 1;
        if (concurrency > 512) concurrency = 512;
        ConcurrencyBox.Text = concurrency.ToString();

        IPAddress? address = null;
        SetBusy(true, "正在解析主机…");
        try
        {
            _cts = new CancellationTokenSource();
            address = await PortScannerEngine.ResolveHostAsync(host, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "已取消");
            return;
        }
        catch (Exception ex)
        {
            SetBusy(false, "解析失败");
            Hint($"无法解析主机「{host}」：{ex.Message}");
            _plugin.LogWarn($"解析主机失败：{host} - {ex.Message}");
            return;
        }

        _all.Clear();
        _display.Clear();
        EmptyHint.Visibility = Visibility.Visible;
        ExportBox.Visibility = Visibility.Collapsed;

        var total = ports.Count;
        var finished = 0;
        var openCount = 0;
        ScanProgress.Value = 0;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = $"正在扫描 {address}（{total} 个端口，超时 {timeout} ms，并发 {concurrency}）…";
        SummaryText.Text = $"· 0 / {total}";

        var token = _cts!.Token;
        try
        {
            var results = await PortScannerEngine.ScanAsync(
                address,
                ports,
                timeout,
                concurrency,
                result =>
                {
                    Interlocked.Increment(ref finished);
                    if (result.IsOpen) Interlocked.Increment(ref openCount);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _all.Add(result);
                        if (!OnlyOpenCheck.IsChecked.GetValueOrDefault(true) || result.IsOpen) _display.Add(result);
                        var done = Volatile.Read(ref finished);
                        ScanProgress.Value = Math.Min(100d, done * 100d / total);
                        SummaryText.Text = $"· 已完成 {done} / {total}，开放 {Volatile.Read(ref openCount)} 个";
                    }));
                },
                token);

            _all.Clear();
            _all.AddRange(results);
            ApplyFilter();

            var open = results.Count(r => r.IsOpen);
            var elapsed = results.Sum(r => r.ElapsedMs);
            SummaryText.Text = $"· 共 {results.Count} 个端口，开放 {open} 个，累计耗时 {elapsed} ms";
            StatusText.Text = $"扫描完成：{address} 开放 {open} 个端口";
            Hint(open == 0
                ? $"未发现开放端口。目标可能开启了防火墙，或超时时间（{timeout} ms）过短。"
                : $"发现 {open} 个开放端口，可点击「导出/复制结果」保存。");
            _plugin.LogInfo($"端口扫描完成：{address}，共 {results.Count} 个端口，开放 {open} 个");
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败";
            Hint($"扫描出错：{ex.Message}");
            _plugin.LogError("端口扫描失败", ex);
        }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            SetBusy(false, string.Empty);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) return;
        try
        {
            _cts?.Cancel();
            StatusText.Text = "正在取消…";
        }
        catch (ObjectDisposedException)
        {
            // 忽略：任务已结束
        }
    }

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var host = HostBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            Hint("请输入目标 IP 或主机名");
            return;
        }

        var timeout = ParseInt(TimeoutBox.Text, 800);
        PingBar.Visibility = Visibility.Visible;
        PingText.Text = $"正在 Ping {host}…";
        PingButton.IsEnabled = false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await PortScannerEngine.PingAsync(host, timeout, cts.Token);
            PingText.Text = result.ToLine();
            _plugin.LogInfo(result.ToLine());
        }
        catch (OperationCanceledException)
        {
            PingText.Text = $"Ping {host} 超时未返回";
        }
        catch (Exception ex)
        {
            PingText.Text = $"Ping {host} 失败：{ex.Message}";
        }
        finally
        {
            PingButton.IsEnabled = true;
        }
    }

    // ===================== 结果操作 =====================

    private void OnlyOpen_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        // XAML 中 IsChecked="True" 会在 InitializeComponent 期间就触发本方法，
        // 此时其余控件字段尚未赋值，必须提前返回，避免空引用。
        if (EmptyHint is null) return;

        var onlyOpen = OnlyOpenCheck?.IsChecked.GetValueOrDefault(true) ?? true;
        _display.Clear();
        foreach (var item in _all.Where(r => !onlyOpen || r.IsOpen))
        {
            _display.Add(item);
        }

        EmptyHint.Visibility = _display.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0)
        {
            Hint("还没有扫描结果可导出");
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"# 端口扫描结果  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"# 目标：{HostBox.Text}    端口参数：{PortsBox.Text}");
        builder.AppendLine("端口\t状态\t服务名\t耗时");
        foreach (var item in _all.Where(r => r.IsOpen).OrderBy(r => r.Port))
        {
            builder.AppendLine(item.ToLine());
        }

        var text = builder.ToString();
        ExportBox.Text = text;
        ExportBox.Visibility = Visibility.Visible;

        try
        {
            Clipboard.SetText(text);
            Hint("已把开放端口结果复制到剪贴板，下方文本框中也可手动全选复制");
        }
        catch (Exception ex)
        {
            Hint($"写入剪贴板失败：{ex.Message}（可在下方文本框手动复制）");
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _all.Clear();
        _display.Clear();
        ExportBox.Text = string.Empty;
        ExportBox.Visibility = Visibility.Collapsed;
        SummaryText.Text = "· 尚未扫描";
        EmptyHint.Visibility = Visibility.Visible;
        Hint("已清空扫描结果");
    }

    private void PresetRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string preset })
        {
            PortsBox.Text = preset;
        }
    }

    // ===================== 辅助 =====================

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        HostBox.IsEnabled = !busy;
        PortsBox.IsEnabled = !busy;

        if (busy)
        {
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource("AppAccentBrush");
        }
        else
        {
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource("AppTextSecondaryBrush");
            if (!string.IsNullOrEmpty(status)) StatusText.Text = status;
        }

        if (!busy)
        {
            try
            {
                _cts?.Dispose();
            }
            catch (Exception)
            {
                // 忽略释放异常
            }

            _cts = null;
        }
    }

    private void Hint(string message) => HintText.Text = message;

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text?.Trim(), out var value) ? value : fallback;

    /// <summary>页面卸载时取消扫描任务。</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (Exception ex)
        {
            _plugin.LogWarn($"关闭页面时取消扫描任务异常：{ex.Message}");
        }

        _cts = null;
    }
}
