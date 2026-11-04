using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ClassBox.Services;
using ClassBox.Views;

namespace ClassBox;

public partial class MainWindow : Window
{
    private WindowService? _windowService;
    private FloatingBallWindow? _ball;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>由 App 在启动阶段注入，用于置顶与置顶守卫。</summary>
    public void AttachWindowService(WindowService windowService)
    {
        _windowService = windowService;

        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            EnableForceTopmost();
            return;
        }

        SourceInitialized += (_, _) => EnableForceTopmost();
    }

    private void EnableForceTopmost()
    {
        Topmost = true;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        _windowService?.SetTopmost(handle, true);
        _windowService?.StartTopmostGuard(handle);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph is null) return;

        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        // 最大化时留出 1px 边距，避免窗口边框被屏幕边缘裁掉
        BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(0);
    }

    private void Ball_Click(object sender, RoutedEventArgs e) => CollapseToBall();

    /// <summary>收起到悬浮球：主界面隐藏，球保持置顶。</summary>
    public void CollapseToBall()
    {
        if (_ball is null)
        {
            _ball = new FloatingBallWindow(this, _windowService);
            _ball.Closed += (_, _) => _ball = null;
            _ball.PlaceRelativeTo(this);
        }

        _ball.Show();
        _ball.Activate();
        Hide();
    }

    /// <summary>由悬浮球调用：把主界面重新显示并置顶。</summary>
    public void ShowFromBall()
    {
        ShowInTaskbar = true;

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Topmost = true;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _windowService?.SetTopmost(handle, true, activate: true);
        }

        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 主界面关闭时一并关掉悬浮球，确保进程正常退出
        _ball?.Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _windowService?.StopTopmostGuard(handle);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
