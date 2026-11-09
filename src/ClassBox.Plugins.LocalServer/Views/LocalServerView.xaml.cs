using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.LocalServer.Server;
using Microsoft.Win32;

namespace ClassBox.Plugins.LocalServer.Views;

/// <summary>「电脑变服务器」页面：选择共享目录、设置端口与并发、启停服务并实时查看请求日志。</summary>
public partial class LocalServerView : UserControl
{
    private const int MaxLogRows = 300;
    private const string ConfigKey = "localserver.settings";

    private readonly LocalServerPlugin _plugin;
    private readonly ObservableCollection<HttpLogEntry> _logs = new();
    private bool _attached;
    private bool _closed;

    public LocalServerView(LocalServerPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        LogList.ItemsSource = _logs;
        UrlList.ItemsSource = new ObservableCollection<string>();

        RestoreSettings();
        RefreshAddresses();

        var server = _plugin.Server;
        server.RequestLogged += Server_RequestLogged;
        server.ErrorOccurred += Server_ErrorOccurred;
        _attached = true;

        if (server.IsRunning)
        {
            ApplyRunningState(true, server.Port, server.RootDirectory);
        }
    }

    // ===================== 配置持久化 =====================

    private sealed class ServerSettings
    {
        public string? RootDirectory { get; set; }
        public string? Port { get; set; }
        public string? MaxConnections { get; set; }
    }

    private void RestoreSettings()
    {
        try
        {
            var settings = _plugin.Host.Configuration.Load(ConfigKey, new ServerSettings());
            if (!string.IsNullOrWhiteSpace(settings.RootDirectory) && Directory.Exists(settings.RootDirectory))
            {
                RootBox.Text = settings.RootDirectory;
            }
            if (!string.IsNullOrWhiteSpace(settings.Port)) PortBox.Text = settings.Port;
            if (!string.IsNullOrWhiteSpace(settings.MaxConnections)) ConcurrencyBox.Text = settings.MaxConnections;
        }
        catch (Exception ex)
        {
            _plugin.LogWarn($"读取上次配置失败：{ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            _plugin.Host.Configuration.Save(ConfigKey, new ServerSettings
            {
                RootDirectory = RootBox.Text,
                Port = PortBox.Text,
                MaxConnections = ConcurrencyBox.Text
            });
        }
        catch (Exception ex)
        {
            _plugin.LogWarn($"保存配置失败：{ex.Message}");
        }
    }

    // ===================== 目录与地址 =====================

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要共享给局域网的目录",
            Multiselect = false
        };

        var current = RootBox.Text;
        if (Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        RootBox.Text = dialog.FolderName;
        SaveSettings();
    }

    private void RefreshAddresses()
    {
        var list = UrlList.ItemsSource as ObservableCollection<string>;
        if (list is null) return;

        list.Clear();
        var port = ParseInt(PortBox.Text, 8080);
        foreach (var url in LocalAddresses.BuildAccessUrls(port))
        {
            list.Add(url);
        }

        if (UrlList.SelectedIndex < 0 && list.Count > 0) UrlList.SelectedIndex = 0;
    }

    private void RefreshIp_Click(object sender, RoutedEventArgs e) => RefreshAddresses();

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlList.SelectedItem as string ?? UrlList.Items.Cast<string>().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            Hint("没有可复制的地址");
            return;
        }

        try
        {
            Clipboard.SetText(url);
            Hint($"已复制地址：{url}");
        }
        catch (Exception ex)
        {
            Hint($"复制失败：{ex.Message}");
        }
    }

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlList.SelectedItem as string ?? UrlList.Items.Cast<string>().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            Hint("没有可打开的地址");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Hint($"调用浏览器失败：{ex.Message}");
        }
    }

    // ===================== 启停 =====================

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var root = RootBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Hint("请先选择一个存在的共享目录");
            return;
        }

        if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port < 1 || port > 65535)
        {
            Hint("端口必须是 1 - 65535 之间的整数");
            return;
        }

        var concurrency = ParseInt(ConcurrencyBox.Text, 8);
        if (concurrency < 1 || concurrency > 64) concurrency = 8;

        try
        {
            var boundPort = _plugin.Server.Start(root, port, concurrency);
            SaveSettings();
            ApplyRunningState(true, boundPort, root);
            Hint($"服务已启动，根目录：{root}，端口：{boundPort}，最大并发：{concurrency}");
            _plugin.LogInfo($"HTTP 文件服务已启动：{root} -> 0.0.0.0:{boundPort}");
        }
        catch (Exception ex)
        {
            ApplyRunningState(false, 0, string.Empty);
            Hint($"启动失败：{ex.Message}（端口被占用时可换一个端口）");
            _plugin.LogError("启动 HTTP 文件服务失败", ex);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _plugin.Server.Stop();
            _plugin.LogInfo("HTTP 文件服务已停止");
        }
        catch (Exception ex)
        {
            _plugin.LogError("停止 HTTP 文件服务失败", ex);
        }

        ApplyRunningState(false, 0, string.Empty);
        Hint("服务已停止，局域网内已无法访问");
    }

    private void ApplyRunningState(bool running, int port, string root)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        RootBox.IsEnabled = !running;
        PortBox.IsEnabled = !running;
        ConcurrencyBox.IsEnabled = !running;

        if (running)
        {
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource("AppSuccessBrush");
            StatusText.Text = $"服务运行中 · 端口 {port} · {root}";
            if (int.TryParse(PortBox.Text, out var typed) && typed != port) PortBox.Text = port.ToString();
            RefreshAddresses();
        }
        else
        {
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource("AppTextSecondaryBrush");
            StatusText.Text = "服务未启动";
        }

        UpdateLogSummary();
    }

    private void Hint(string message) => HintText.Text = message;

    // ===================== 日志 =====================

    private void Server_RequestLogged(HttpLogEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Server_RequestLogged(entry)));
            return;
        }

        _logs.Insert(0, entry);
        while (_logs.Count > MaxLogRows) _logs.RemoveAt(_logs.Count - 1);
        UpdateLogSummary();
    }

    private void Server_ErrorOccurred(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Server_ErrorOccurred(message)));
            return;
        }

        Hint(message);
        _plugin.LogWarn(message);
    }

    private void UpdateLogSummary()
    {
        var server = _plugin.Server;
        LogSummary.Text = $"· 共 {server.TotalRequests} 次请求，当前连接 {server.ActiveConnections}";
        EmptyLogHint.Visibility = _logs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        UpdateLogSummary();
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text?.Trim(), out var value) ? value : fallback;

    /// <summary>页面卸载：停止服务并解除事件订阅，避免宿主退出后仍有监听端口。</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;

        var server = _plugin.Server;
        if (_attached)
        {
            server.RequestLogged -= Server_RequestLogged;
            server.ErrorOccurred -= Server_ErrorOccurred;
            _attached = false;
        }

        try
        {
            server.Stop();
        }
        catch (Exception ex)
        {
            _plugin.LogWarn($"关闭页面时停止服务失败：{ex.Message}");
        }
    }
}
