using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ClassBox.Plugins.QRCodeTool.Qr;
using Microsoft.Win32;

namespace ClassBox.Plugins.QRCodeTool.Views;

/// <summary>二维码工具页面。</summary>
public partial class QRCodeToolView : UserControl
{
    private readonly QRCodeToolPlugin _plugin;
    private QrCode? _current;
    private BitmapSource? _currentImage;
    private bool _ready;
    private bool _closed;

    public QRCodeToolView(QRCodeToolPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        _ready = true;
        InitOptions();
        RefreshInfo();
        Unloaded += (_, _) => Shutdown();
    }

    /// <summary>页面卸载：忽略后续事件回调。</summary>
    public void Shutdown()
    {
        _closed = true;
        _ready = false;
    }

    private void InitOptions()
    {
        EccBox.ItemsSource = new[] { "L（7%）", "M（15%）", "Q（25%）", "H（30%）" };
        EccBox.SelectedIndex = _plugin.Settings.EccLevel switch
        {
            "L" => 0,
            "Q" => 2,
            "H" => 3,
            _ => 1
        };

        ModuleBox.ItemsSource = new[] { 4, 6, 8, 10, 12, 16 };
        var wanted = _plugin.Settings.ModuleSize;
        var index = 2;
        var options = (int[])ModuleBox.ItemsSource;
        for (var i = 0; i < options.Length; i++)
        {
            if (options[i] == wanted) { index = i; break; }
        }

        ModuleBox.SelectedIndex = index;
    }

    private QrEccLevel GetEccLevel() => EccBox.SelectedIndex switch
    {
        0 => QrEccLevel.L,
        2 => QrEccLevel.Q,
        3 => QrEccLevel.H,
        _ => QrEccLevel.M
    };

    private int GetModuleSize() => ModuleBox.SelectedItem is int size ? size : 8;

    private void OnOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;

        if (EccBox is not null && ModuleBox is not null && EccBox.SelectedIndex >= 0 && ModuleBox.SelectedIndex >= 0)
        {
            _plugin.Settings.EccLevel = GetEccLevel() switch
            {
                QrEccLevel.L => "L",
                QrEccLevel.Q => "Q",
                QrEccLevel.H => "H",
                _ => "M"
            };
            _plugin.Settings.ModuleSize = GetModuleSize();
            _plugin.PersistSettings();
        }

        RefreshInfo();
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        RefreshInfo();
    }

    private void RefreshInfo()
    {
        if (!_ready || _closed) return;

        var text = InputBox?.Text ?? string.Empty;
        var bytes = string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);
        var max = QrEncoder.GetMaxDataBytes(GetEccLevel());
        var head = string.IsNullOrEmpty(text)
            ? "尚未生成"
            : $"输入 {text.Length} 字符 / {bytes} 字节 UTF-8";

        var status = _current?.Describe() ?? "未生成二维码";
        InfoText.Text = $"{head} · 当前等级上限 {max} 字节 · {status}";
    }

    private void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        Generate();
    }

    private bool Generate()
    {
        var text = InputBox.Text ?? string.Empty;
        if (!QrEncoder.TryEncode(text, GetEccLevel(), out var code, out var error) || code is null)
        {
            InfoText.Text = error;
            return false;
        }

        _current = code;
        _currentImage = QrRenderer.Render(code, GetModuleSize());
        PreviewImage.Source = _currentImage;
        EmptyHint.Visibility = Visibility.Collapsed;

        InfoText.Text = $"{code.Describe()} · 图片 {_currentImage.PixelWidth}×{_currentImage.PixelHeight} 像素 · 输入 {Encoding.UTF8.GetByteCount(text)} 字节";
        return true;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        if (_currentImage is null && !Generate())
        {
            return;
        }

        if (_currentImage is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "保存二维码",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = QrRenderer.BuildFileName(DateTime.Now),
            InitialDirectory = Directory.Exists(_plugin.Settings.SaveDirectory)
                ? _plugin.Settings.SaveDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var saved = QrRenderer.SavePng(_currentImage, dialog.FileName);
            var dir = Path.GetDirectoryName(saved);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                _plugin.Settings.SaveDirectory = dir!;
                _plugin.PersistSettings();
            }

            InfoText.Text = "已保存：" + saved;
            _plugin.Notify("二维码已保存", Path.GetFileName(saved));
            _plugin.LogInfo("二维码保存成功：" + saved);
        }
        catch (Exception ex)
        {
            _plugin.LogError("保存二维码失败", ex);
            InfoText.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        if (_currentImage is null && !Generate()) return;
        if (_currentImage is null) return;

        if (QrRenderer.TryCopyToClipboard(_currentImage))
        {
            InfoText.Text = $"已复制二维码图片（{_currentImage.PixelWidth}×{_currentImage.PixelHeight} 像素）到剪贴板。";
            _plugin.Notify("二维码已复制", "可直接粘贴到聊天窗口");
        }
        else
        {
            InfoText.Text = "复制失败：剪贴板被其它程序占用，请稍后重试。";
        }
    }

    // ===== 预设 =====

    private void OnPresetUrlClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        InputBox.Text = "https://";
        InputBox.CaretIndex = InputBox.Text.Length;
        InputBox.Focus();
    }

    private void OnPresetTextClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        InputBox.Text = "Class Box 二维码工具：纯文本内容示例。";
        RefreshInfo();
    }

    private void OnPresetWifiClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        InputBox.Text = "WIFI:T:WPA;S:我的WiFi名称;P:我的密码;;";
        InputBox.CaretIndex = InputBox.Text.Length;
        InputBox.Focus();
        RefreshInfo();
    }

    /// <summary>供冒烟测试调用。</summary>
    internal bool GenerateForTest(string text)
    {
        InputBox.Text = text;
        return Generate();
    }
}
