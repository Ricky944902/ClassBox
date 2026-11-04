using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClassBox.Services;

namespace ClassBox.Views;

/// <summary>
/// 缩小后的悬浮球：始终置顶（窗口 + 周期性 SetWindowPos 双保障），
/// 单击还原主界面，拖动可挪位置，右键可退出。
/// </summary>
public partial class FloatingBallWindow : Window
{
    private const double DragThreshold = 3.0;

    private readonly MainWindow _host;
    private readonly WindowService? _windowService;

    private bool _dragging;
    private bool _moved;
    private Point _startCursorPixels;
    private double _startLeft;
    private double _startTop;

    public FloatingBallWindow(MainWindow host, WindowService? windowService)
    {
        InitializeComponent();
        _host = host;
        _windowService = windowService;

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        _windowService?.SetTopmost(handle, true);
        _windowService?.StartTopmostGuard(handle);
    }

    /// <summary>首次出现在主窗口附近（主窗口右下角外侧），若超出屏幕则回落到工作区右下角。</summary>
    public void PlaceRelativeTo(MainWindow host)
    {
        var left = host.Left + host.ActualWidth - Width - 8;
        var top = host.Top + host.ActualHeight - Height - 8;

        var area = SystemParameters.WorkArea;
        if (left < area.Left || left > area.Right - Width) left = area.Right - Width - 24;
        if (top < area.Top || top > area.Bottom - Height) top = area.Bottom - Height - 24;

        Left = left;
        Top = top;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        _dragging = true;
        _moved = false;
        _startCursorPixels = PointToScreen(e.GetPosition(this));
        _startLeft = Left;
        _startTop = Top;
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var current = PointToScreen(e.GetPosition(this));
        var dx = (current.X - _startCursorPixels.X) / dpi.DpiScaleX;
        var dy = (current.Y - _startCursorPixels.Y) / dpi.DpiScaleY;

        if (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold) _moved = true;

        Left = _startLeft + dx;
        Top = _startTop + dy;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        Root.ReleaseMouseCapture();
        ClampToScreen();

        if (_moved)
        {
            e.Handled = true;
            return;
        }

        RestoreHost();
        e.Handled = true;
    }

    private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 右键菜单由 ContextMenu 承载；拖动过程中不弹出菜单
        if (_moved)
        {
            e.Handled = true;
            return;
        }

        if (ContextMenu is not null)
        {
            ContextMenu.PlacementTarget = Root;
            ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void ShowMain_Click(object sender, RoutedEventArgs e) => RestoreHost();

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    /// <summary>还原主界面并让主界面同样强制置顶。</summary>
    private void RestoreHost()
    {
        _host.ShowFromBall();
        Hide();
    }

    private void ClampToScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;

        if (Left < left) Left = left;
        if (Top < top) Top = top;
        if (Left + Width > right) Left = right - Width;
        if (Top + Height > bottom) Top = bottom - Height;
    }
}
