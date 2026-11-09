using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using ClassBox.Plugins.WebBrowser.Infrastructure;

namespace ClassBox.Plugins.WebBrowser.Views;

/// <summary>
/// 简易浏览器页面：托管 WPF 内置 WebBrowser，提供地址导航、前进后退、关键词搜索与外部打开。
/// </summary>
public partial class WebBrowserView : UserControl
{
    private const string HomeUrl = "https://www.bing.com";

    private readonly WebBrowserPlugin _plugin;
    private bool _closed;
    private string _currentUrl = HomeUrl;

    public WebBrowserView(WebBrowserPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        BrowserHost.EnableModernEmulation();
        Browser.Navigating += Browser_Navigating;
        Browser.Navigated += Browser_Navigated;

        AddressBox.Text = HomeUrl;
        NavigateTo(HomeUrl);
        UpdateNavButtons();
    }

    // ===================== 导航 =====================

    /// <summary>把用户输入规范化为可访问的 URL：网址原样补齐协议，关键词转必应搜索。</summary>
    internal static string? NormalizeAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
        {
            var scheme = absolute.Scheme;
            if (scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps ||
                scheme == Uri.UriSchemeFile || scheme == "about")
            {
                return absolute.ToString();
            }
        }

        // 形如 example.com / sub.example.com/path / localhost:8080 的输入按网址处理
        var hostPart = text.Split('/', 2)[0];
        var isHostLike = !text.Contains(' ') &&
                         (hostPart.Contains('.') || hostPart.Contains(':') ||
                          hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase));

        if (isHostLike) return "https://" + text;

        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
    }

    private void NavigateTo(string? raw)
    {
        var url = NormalizeAddress(raw);
        if (url is null)
        {
            ShowError("请输入网址或搜索关键词");
            return;
        }

        try
        {
            HideError();
            StatusText.Text = "正在加载…";
            Browser.Navigate(url);
        }
        catch (Exception ex)
        {
            ShowError($"导航失败：{ex.Message}");
            StatusText.Text = "导航失败";
            _plugin.LogError($"导航到 {url} 失败", ex);
        }
    }

    private void Browser_Navigating(object sender, NavigatingCancelEventArgs e)
    {
        try
        {
            var uri = e.Uri?.ToString();
            if (!string.IsNullOrWhiteSpace(uri))
            {
                _currentUrl = uri;
                AddressBox.Text = uri;
            }
            StatusText.Text = "正在加载…";
        }
        catch
        {
            // 浏览过程中的偶发异常不影响主流程
        }
    }

    private void Browser_Navigated(object sender, NavigationEventArgs e)
    {
        try
        {
            _currentUrl = e.Uri?.ToString() ?? _currentUrl;
            AddressBox.Text = _currentUrl;
            StatusText.Text = "已加载";
            HideError();
            UpdateNavButtons();
        }
        catch (Exception ex)
        {
            _plugin.LogError("处理导航完成事件失败", ex);
        }
    }

    private void UpdateNavButtons()
    {
        try
        {
            BackButton.IsEnabled = Browser.CanGoBack;
            ForwardButton.IsEnabled = Browser.CanGoForward;
        }
        catch
        {
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;
        }
    }

    // ===================== 按钮 =====================

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Browser.CanGoBack) Browser.GoBack();
        }
        catch (Exception ex)
        {
            ShowError($"后退失败：{ex.Message}");
        }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Browser.CanGoForward) Browser.GoForward();
        }
        catch (Exception ex)
        {
            ShowError($"前进失败：{ex.Message}");
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Browser.Refresh();
            StatusText.Text = "已刷新";
        }
        catch (Exception ex)
        {
            ShowError($"刷新失败：{ex.Message}");
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => NavigateTo(HomeUrl);

    private void Go_Click(object sender, RoutedEventArgs e) => NavigateTo(AddressBox.Text);

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        NavigateTo(AddressBox.Text);
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var target = NormalizeAddress(string.IsNullOrWhiteSpace(AddressBox.Text) ? _currentUrl : AddressBox.Text);
        if (target is null)
        {
            ShowError("当前没有可打开的地址");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            StatusText.Text = "已在系统浏览器中打开";
        }
        catch (Exception ex)
        {
            ShowError($"无法调用系统浏览器：{ex.Message}");
            _plugin.LogError("调用系统浏览器失败", ex);
        }
    }

    // ===================== 状态 =====================

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBar.Visibility = Visibility.Collapsed;

    /// <summary>页面卸载时释放浏览器控件，避免宿主关闭时残留 IE 进程。</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            Browser.Navigating -= Browser_Navigating;
            Browser.Navigated -= Browser_Navigated;
            Browser.Dispose();
        }
        catch (Exception ex)
        {
            _plugin.LogError("释放浏览器控件失败", ex);
        }
    }
}
