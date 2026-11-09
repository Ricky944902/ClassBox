using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.SystemInfo.Collectors;

namespace ClassBox.Plugins.SystemInfo.Views;

/// <summary>系统信息面板页面。</summary>
public partial class SystemInfoView : UserControl
{
    private readonly SystemInfoPlugin _plugin;
    private SystemInfoSnapshot? _snapshot;
    private bool _busy;
    private bool _loaded;

    public SystemInfoView(SystemInfoPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshAsync();
    }

    // ===================== 采集与展示 =====================

    private async Task RefreshAsync()
    {
        if (_busy) return;

        _busy = true;
        RefreshButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        FooterText.Text = "正在采集系统信息…";

        try
        {
            var snapshot = await Task.Run(SystemInfoCollector.Capture);
            _snapshot = snapshot;
            Apply(snapshot);

            FooterText.Text = $"采集完成 · {snapshot.CollectedAt:HH:mm:ss} · 磁盘 {snapshot.Disks.Count} 个卷 · 网络适配器 {snapshot.Networks.Count} 个";
            _plugin.LogInfo("系统信息采集完成");
        }
        catch (Exception ex)
        {
            FooterText.Text = $"采集系统信息失败：{ex.Message}";
            _plugin.LogError("采集系统信息异常", ex);
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
            CopyButton.IsEnabled = _snapshot is not null;
        }
    }

    private void Apply(SystemInfoSnapshot snapshot)
    {
        OsNameText.Text = snapshot.OsName;
        OsVersionText.Text = snapshot.OsVersion;
        OsBuildText.Text = snapshot.OsBuild;
        OsArchText.Text = snapshot.OsArchitecture;
        RuntimeText.Text = snapshot.RuntimeVersion;
        MachineText.Text = snapshot.MachineName;
        UserText.Text = snapshot.UserName;
        UptimeText.Text = snapshot.UptimeText;

        CpuText.Text = snapshot.CpuName;
        CpuCoreText.Text = snapshot.CpuText;

        MemoryBar.Value = Math.Clamp(snapshot.MemoryUsagePercent, 0, 100);
        MemPercentText.Text = $"已用 {snapshot.MemoryUsageText}";
        MemTotalText.Text = $"总量 {snapshot.MemoryTotalText}";
        MemAvailText.Text = $"可用 {snapshot.MemoryAvailableText}";

        GpuText.Text = snapshot.GpuName;
        ResolutionText.Text = snapshot.ResolutionText;

        DiskList.ItemsSource = snapshot.Disks;
        NetList.ItemsSource = snapshot.Networks;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ===================== 复制 =====================

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            FooterText.Text = "尚未采集到系统信息，请先点击「刷新」。";
            return;
        }

        var text = _snapshot.ToPlainText();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                FooterText.Text = $"已复制全部系统信息到剪贴板（{text.Length} 个字符）。";
                _plugin.LogInfo("系统信息已复制到剪贴板");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                {
                    FooterText.Text = $"复制失败：{ex.Message}（剪贴板可能被其他程序占用，请稍后重试）";
                    _plugin.LogWarn($"复制系统信息失败：{ex.Message}");
                    return;
                }

                Thread.Sleep(60);
            }
        }
    }

    /// <summary>页面卸载时的清理入口。</summary>
    public void Shutdown()
    {
        DiskList.ItemsSource = null;
        NetList.ItemsSource = null;
        _snapshot = null;
    }
}
