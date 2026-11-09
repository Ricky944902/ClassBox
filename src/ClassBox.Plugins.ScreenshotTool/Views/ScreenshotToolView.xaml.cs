using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassBox.Plugins.ScreenshotTool.Capture;
using Microsoft.Win32;

namespace ClassBox.Plugins.ScreenshotTool.Views;

/// <summary>截图工具页面。</summary>
public partial class ScreenshotToolView : UserControl
{
    private readonly ScreenshotToolPlugin _plugin;
    private readonly DispatcherTimer _countdownTimer;
    private BitmapSource? _original;
    private BitmapSource? _current;
    private Int32Rect? _selection;
    private Point _dragStart;
    private bool _dragging;
    private int _countdown;
    private bool _ready;
    private bool _closed;

    public ScreenshotToolView(ScreenshotToolPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += OnCountdownTick;

        _ready = true;
        LoadScreens();
        UpdateUi("就绪，可开始截图。");
        Unloaded += (_, _) => _countdownTimer.Stop();
    }

    /// <summary>页面关闭：停止倒计时并忽略后续回调。</summary>
    public void Shutdown()
    {
        _closed = true;
        _ready = false;
        _countdownTimer.Stop();
    }

    private void LoadScreens()
    {
        var screens = ScreenCapture.GetScreens();
        ScreenBox.ItemsSource = screens;
        var primary = screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
        ScreenBox.SelectedItem = primary;
    }

    private void UpdateUi(string status)
    {
        if (!_ready || _closed) return;

        if (StatusText is not null) StatusText.Text = status;

        var hasImage = _current is not null;
        if (EmptyHint is not null)
        {
            EmptyHint.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        }

        if (CropButton is not null) CropButton.IsEnabled = hasImage && _selection is not null;
        if (ResetButton is not null) ResetButton.IsEnabled = hasImage && !ReferenceEquals(_current, _original);
        if (CopyButton is not null) CopyButton.IsEnabled = hasImage;
        if (SaveButton is not null) SaveButton.IsEnabled = hasImage;

        if (InfoText is not null)
        {
            var current = _current is null
                ? "尚未截图"
                : $"当前图片 {_current.PixelWidth}×{_current.PixelHeight} 像素";
            var selection = _selection is null
                ? "未框选"
                : $"选区 {_selection.Value.Width}×{_selection.Value.Height} @({_selection.Value.X},{_selection.Value.Y})";
            InfoText.Text = $"{current} · {selection} · 保存目录 {_plugin.Settings.SaveDirectory}";
        }
    }

    // ===== 截取 =====

    private void OnCaptureAllClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        ApplyCapture(ScreenCapture.TryCaptureAllScreens(out var image, out var error), image, error, "全屏");
    }

    private void OnCaptureScreenClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        if (ScreenBox.SelectedItem is not ScreenInfo screen)
        {
            UpdateUi("请先选择要抓取的显示器。");
            return;
        }

        ApplyCapture(ScreenCapture.TryCaptureScreen(screen.Index, out var image, out var error), image, error,
            screen.Describe());
    }

    private void ApplyCapture(bool ok, BitmapSource? image, string error, string label)
    {
        if (!ok || image is null)
        {
            UpdateUi("截图失败：" + (string.IsNullOrWhiteSpace(error) ? "无法获取屏幕内容" : error)
                     + "（远程会话或受保护的内容可能无法抓取）");
            _plugin.LogError("截图失败：" + error);
            return;
        }

        _original = image;
        _current = image;
        _selection = null;
        ClearSelectionVisual();

        PreviewImage.Source = image;
        _plugin.LogInfo($"截图成功（{label}）：{image.PixelWidth}×{image.PixelHeight}");
        UpdateUi($"截图完成（{label}）：{image.PixelWidth}×{image.PixelHeight} 像素");
    }

    private void OnDelayClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        _countdown = 3;
        DelayButton.IsEnabled = false;
        UpdateUi($"将在 {_countdown} 秒后截取全屏，请切换到目标画面…");
        _countdownTimer.Start();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _countdown--;
        if (_countdown > 0)
        {
            UpdateUi($"将在 {_countdown} 秒后截取全屏，请切换到目标画面…");
            return;
        }

        _countdownTimer.Stop();
        if (DelayButton is not null) DelayButton.IsEnabled = true;
        OnCaptureAllClick(this, new RoutedEventArgs());
    }

    // ===== 框选 =====

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_ready || _closed || _current is null) return;

        _dragStart = e.GetPosition(PreviewHost);
        _dragging = true;
        PreviewHost.CaptureMouse();

        if (SelectionRect is not null)
        {
            Canvas.SetLeft(SelectionRect, _dragStart.X);
            Canvas.SetTop(SelectionRect, _dragStart.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            SelectionRect.Visibility = Visibility.Visible;
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_ready || _closed || !_dragging || SelectionRect is null) return;

        var p = e.GetPosition(PreviewHost);
        var x = Math.Min(p.X, _dragStart.X);
        var y = Math.Min(p.Y, _dragStart.Y);
        var w = Math.Abs(p.X - _dragStart.X);
        var h = Math.Abs(p.Y - _dragStart.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_ready || _closed || !_dragging) return;

        _dragging = false;
        PreviewHost.ReleaseMouseCapture();
        var end = e.GetPosition(PreviewHost);

        if (TryMapToImage(_dragStart, out var x1, out var y1) && TryMapToImage(end, out var x2, out var y2))
        {
            var rect = new Int32Rect(
                Math.Min(x1, x2),
                Math.Min(y1, y2),
                Math.Abs(x2 - x1),
                Math.Abs(y2 - y1));

            if (rect.Width >= 2 && rect.Height >= 2)
            {
                _selection = rect;
                UpdateUi($"已框选 {rect.Width}×{rect.Height} 像素，点击「裁剪选区」应用。");
                return;
            }
        }

        _selection = null;
        ClearSelectionVisual();
        UpdateUi("框选区域过小，已忽略。");
    }

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (SelectionLayer is not null)
        {
            SelectionLayer.Width = PreviewHost.ActualWidth;
            SelectionLayer.Height = PreviewHost.ActualHeight;
        }
    }

    /// <summary>把预览控件坐标换算成图片像素坐标（Image 使用 Uniform 拉伸）。</summary>
    private bool TryMapToImage(Point point, out int px, out int py)
    {
        px = 0;
        py = 0;
        if (_current is null) return false;

        var hostW = PreviewHost.ActualWidth;
        var hostH = PreviewHost.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return false;

        var scale = Math.Min(hostW / _current.PixelWidth, hostH / _current.PixelHeight);
        if (scale <= 0) return false;

        var renderW = _current.PixelWidth * scale;
        var renderH = _current.PixelHeight * scale;
        var offsetX = (hostW - renderW) / 2;
        var offsetY = (hostH - renderH) / 2;

        var rx = (point.X - offsetX) / scale;
        var ry = (point.Y - offsetY) / scale;

        px = (int)Math.Round(Math.Max(0, Math.Min(_current.PixelWidth, rx)));
        py = (int)Math.Round(Math.Max(0, Math.Min(_current.PixelHeight, ry)));
        return true;
    }

    private void ClearSelectionVisual()
    {
        if (SelectionRect is null) return;
        SelectionRect.Visibility = Visibility.Collapsed;
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
    }

    // ===== 裁剪 / 复制 / 保存 =====

    private void OnCropClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (_original is null || _selection is null)
        {
            UpdateUi("请先在图片上拖动框选要裁剪的区域。");
            return;
        }

        try
        {
            _current = ImageCropper.Crop(_original, _selection.Value);
            PreviewImage.Source = _current;
            _selection = null;
            ClearSelectionVisual();
            UpdateUi($"裁剪完成：{_current.PixelWidth}×{_current.PixelHeight} 像素");
        }
        catch (Exception ex)
        {
            _plugin.LogError("裁剪失败", ex);
            UpdateUi("裁剪失败：" + ex.Message);
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed || _original is null) return;

        _current = _original;
        _selection = null;
        ClearSelectionVisual();
        PreviewImage.Source = _current;
        UpdateUi("已还原为原图。");
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed || _current is null) return;

        if (ImageCropper.TryCopyToClipboard(_current))
        {
            UpdateUi($"已复制到剪贴板：{_current.PixelWidth}×{_current.PixelHeight} 像素");
            _plugin.Notify("截图已复制", $"{_current.PixelWidth}×{_current.PixelHeight} 像素");
        }
        else
        {
            UpdateUi("复制失败：剪贴板正被其它程序占用，请稍后重试。");
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed || _current is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "另存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = ImageCropper.BuildFileName(DateTime.Now),
            InitialDirectory = Directory.Exists(_plugin.Settings.SaveDirectory)
                ? _plugin.Settings.SaveDirectory
                : ImageCropper.GetDefaultDirectory()
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var saved = ImageCropper.SavePng(_current, dialog.FileName);
            var dir = Path.GetDirectoryName(saved);
            if (!string.IsNullOrWhiteSpace(dir) && !string.Equals(dir, _plugin.Settings.SaveDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.SaveDirectory = dir!;
                _plugin.PersistSettings();
            }

            UpdateUi("已保存：" + saved);
            _plugin.Notify("截图已保存", Path.GetFileName(saved));
            _plugin.LogInfo("截图保存成功：" + saved);
        }
        catch (Exception ex)
        {
            _plugin.LogError("保存截图失败", ex);
            UpdateUi("保存失败：" + ex.Message);
        }
    }

    private void OnChooseDirClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var dialog = new OpenFolderDialog
        {
            Title = "选择截图默认保存目录",
            InitialDirectory = Directory.Exists(_plugin.Settings.SaveDirectory)
                ? _plugin.Settings.SaveDirectory
                : ImageCropper.GetDefaultDirectory()
        };

        if (dialog.ShowDialog() != true) return;

        _plugin.Settings.SaveDirectory = dialog.FolderName;
        _plugin.PersistSettings();
        UpdateUi("保存目录已更新：" + dialog.FolderName);
    }
}
