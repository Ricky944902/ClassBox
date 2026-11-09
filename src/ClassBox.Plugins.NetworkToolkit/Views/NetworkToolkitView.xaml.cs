using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.NetworkToolkit.Net;

namespace ClassBox.Plugins.NetworkToolkit.Views;

/// <summary>网络工具箱页面。</summary>
public partial class NetworkToolkitView : UserControl
{
    private readonly NetworkToolkitPlugin _plugin;
    private readonly CancellationTokenSource _cts = new();
    private List<PortUsage> _ports = new();
    private bool _ready;
    private bool _closed;

    public NetworkToolkitView(NetworkToolkitPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        PingCountBox.ItemsSource = new[] { 1, 2, 4, 6, 8, 10 };
        PingCountBox.SelectedItem = new[] { 1, 2, 4, 6, 8, 10 }.Contains(_plugin.Settings.PingCount)
            ? _plugin.Settings.PingCount
            : 4;

        PingHostBox.Text = _plugin.Settings.PingHost;
        DnsQueryBox.Text = _plugin.Settings.DnsQuery;
        PortQueryBox.Text = _plugin.Settings.PortQuery;

        _ready = true;
        LoadAdapters();
        LoadPorts();
        SetStatus("就绪，可切换到各标签页使用对应功能。");
    }

    /// <summary>页面卸载：取消后台任务并忽略后续回调。</summary>
    public void Shutdown()
    {
        _closed = true;
        _ready = false;
        try { _cts.Cancel(); } catch { /* 忽略 */ }
    }

    private void SetStatus(string text)
    {
        if (!_ready || _closed) return;
        if (StatusText is not null) StatusText.Text = text;
    }

    // ===== 网卡 =====

    private void OnRefreshAdapterClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        LoadAdapters();
        SetStatus("网卡信息已刷新。");
    }

    private void LoadAdapters()
    {
        try
        {
            var adapters = NetworkInfoProvider.GetAdapters();
            var lines = adapters.Select(a =>
                $"{a.Name}｜{a.Type}｜{a.Status}｜速度 {a.Speed}\n" +
                $"    IPv4：{a.IpText}\n    网关：{a.GatewayText}\n    DNS：{a.DnsText}\n    MAC：{a.Mac}").ToList();

            AdapterList.ItemsSource = lines;
            AdapterOverviewBox.Text = NetworkInfoProvider.BuildOverview(adapters);
        }
        catch (Exception ex)
        {
            _plugin.LogError("读取网卡信息失败", ex);
            if (AdapterList is not null) AdapterList.ItemsSource = new[] { "读取失败：" + ex.Message };
            if (AdapterOverviewBox is not null) AdapterOverviewBox.Text = "读取失败：" + ex.Message;
        }
    }

    private void OnCopyAdapterClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var text = AdapterOverviewBox?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            System.Windows.Clipboard.SetText(text);
            SetStatus("网络概览已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            _plugin.LogError("复制网络概览失败", ex);
            SetStatus("复制失败：剪贴板被占用。");
        }
    }

    // ===== Ping =====

    private async void OnPingClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (PingButton.IsEnabled == false) return;

        var host = (PingHostBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            SetStatus("请输入要 Ping 的主机名或 IP。");
            return;
        }

        var count = PingCountBox.SelectedItem is int c ? c : 4;

        _plugin.Settings.PingHost = host;
        _plugin.Settings.PingCount = count;
        _plugin.PersistSettings();

        PingButton.IsEnabled = false;
        PingOutputList.ItemsSource = null;
        var lines = new List<string>();
        SetStatus($"正在 Ping {host}（{count} 次）…");

        try
        {
            var summary = await PingRunner.RunAsync(host, count, 2000, probe =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;
                    lines.Add(probe.Display);
                    PingOutputList.ItemsSource = null;
                    PingOutputList.ItemsSource = lines.ToList();
                }));
            }, _cts.Token);

            if (_closed) return;
            PingSummaryText.Text = summary.Describe();
            SetStatus(summary.Received > 0 ? "Ping 完成。" : "Ping 完成：全部失败，请检查主机名、网络或防火墙。");
            _plugin.LogInfo(summary.Describe());
        }
        catch (Exception ex)
        {
            _plugin.LogError("Ping 执行失败", ex);
            SetStatus("Ping 执行失败：" + ex.Message);
        }
        finally
        {
            if (!_closed && PingButton is not null) PingButton.IsEnabled = true;
        }
    }

    // ===== DNS =====

    private async void OnDnsClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var query = (DnsQueryBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            DnsResultBox.Text = "请输入要解析的域名或 IP。";
            return;
        }

        _plugin.Settings.DnsQuery = query;
        _plugin.PersistSettings();

        DnsButton.IsEnabled = false;
        DnsResultBox.Text = "正在解析…";

        try
        {
            var result = await NetworkDiagnostics.ResolveAsync(query, _cts.Token);
            if (_closed) return;

            if (!result.Success)
            {
                DnsResultBox.Text = result.Error;
                SetStatus("DNS 解析失败。");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("主机名：" + result.HostName);
            sb.AppendLine("地址数量：" + result.Addresses.Count);
            foreach (var address in result.Addresses) sb.AppendLine("  · " + address);
            if (result.Aliases.Count > 0)
            {
                sb.AppendLine("别名：");
                foreach (var alias in result.Aliases) sb.AppendLine("  · " + alias);
            }

            DnsResultBox.Text = sb.ToString().TrimEnd();
            SetStatus($"DNS 解析完成：{query} → {result.Addresses.Count} 个地址。");
        }
        catch (Exception ex)
        {
            _plugin.LogError("DNS 解析异常", ex);
            DnsResultBox.Text = "解析异常：" + ex.Message;
        }
        finally
        {
            if (!_closed && DnsButton is not null) DnsButton.IsEnabled = true;
        }
    }

    // ===== 端口 =====

    private void OnPortRefreshClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        LoadPorts();
        SetStatus($"已加载 {_ports.Count} 条 TCP 端口记录。");
    }

    private void OnPortQueryClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var text = (PortQueryBox.Text ?? string.Empty).Trim();
        if (!int.TryParse(text, out var port) || port < 1 || port > 65535)
        {
            PortSummaryText.Text = "端口号无效，请输入 1-65535 之间的整数。";
            return;
        }

        _plugin.Settings.PortQuery = text;
        _plugin.PersistSettings();

        if (_ports.Count == 0) LoadPorts();

        var matches = NetworkDiagnostics.QueryPort(port, _ports);
        if (matches.Count == 0)
        {
            var listeners = _ports.Where(p => p.State == "LISTEN").ToList();
            PortList.ItemsSource = listeners.Select(p => p.Display).ToList();
            PortSummaryText.Text = $"端口 {port} 当前未被占用（已列出全部 {listeners.Count} 个监听端口供参考）。";
            return;
        }

        PortList.ItemsSource = matches.Select(p => p.Display).ToList();
        var owners = string.Join("，", matches
            .Where(p => !string.IsNullOrWhiteSpace(p.ProcessName))
            .Select(p => $"{p.ProcessName}(PID {p.ProcessId})")
            .Distinct());
        PortSummaryText.Text = $"端口 {port} 命中 {matches.Count} 条记录" + (owners.Length > 0 ? "：" + owners : "。");
    }

    private void LoadPorts()
    {
        try
        {
            _ports = NetworkDiagnostics.GetTcpUsage();
            var listening = _ports.Where(p => p.State == "LISTEN").ToList();
            PortList.ItemsSource = listening.Take(500).Select(p => p.Display).ToList();
            PortSummaryText.Text = $"共 {_ports.Count} 条 TCP 记录，其中监听端口 {listening.Count} 个（列表最多显示 500 条）。";
        }
        catch (Exception ex)
        {
            _plugin.LogError("读取端口占用失败", ex);
            PortList.ItemsSource = new[] { "读取失败：" + ex.Message };
            PortSummaryText.Text = "读取端口占用失败：" + ex.Message;
        }
    }

    // ===== 公网 IP =====

    private async void OnPublicIpClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        PublicIpButton.IsEnabled = false;
        PublicIpText.Text = "查询中…";
        SetStatus("正在查询公网出口 IP…");

        try
        {
            var result = await PublicIpQuery.QueryAsync(_cts.Token);
            if (_closed) return;

            PublicIpText.Text = result.Describe();
            SetStatus(result.Success ? "公网 IP 查询完成。" : "公网 IP 查询不可用（离线或网络受限），功能已优雅降级。");
            if (result.Success) _plugin.LogInfo(result.Describe());
        }
        catch (Exception ex)
        {
            _plugin.LogError("公网 IP 查询异常", ex);
            PublicIpText.Text = "公网 IP 查询不可用：" + ex.Message;
            SetStatus("公网 IP 查询不可用，已优雅降级。");
        }
        finally
        {
            if (!_closed && PublicIpButton is not null) PublicIpButton.IsEnabled = true;
        }
    }

    /// <summary>供冒烟测试：读取网卡数量与端口数量。</summary>
    internal (int AdapterLines, int PortRecords, int ListeningPorts) GetSnapshotForTest()
    {
        var adapterLines = AdapterList.ItemsSource?.Cast<object>().Count() ?? 0;
        var listening = _ports.Count(p => p.State == "LISTEN");
        return (adapterLines, _ports.Count, listening);
    }
}
