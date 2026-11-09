using System.Windows;
using System.Windows.Controls;
using System.Text;
using ClassBox.Plugins.EncodeTool.Codecs;
using Microsoft.Win32;

namespace ClassBox.Plugins.EncodeTool.Views;

/// <summary>编码转换台页面：选择编码方式与方向后即时双向转换，支持文件载入与结果存盘。</summary>
public partial class EncodeToolView : UserControl
{
    private enum StatusKind
    {
        Info,
        Ok,
        Warn,
        Error
    }

    private readonly EncodeToolPlugin _plugin;
    private bool _ready;
    private bool _closed;

    public EncodeToolView(EncodeToolPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        foreach (var name in CodecEngine.KindNames) ModeBox.Items.Add(name);
        ModeBox.SelectedIndex = 0;

        _ready = true;
        UpdateStats();
        SetStatus("选择编码方式与方向，左侧输入即时在右侧得到结果。", StatusKind.Info);
    }

    public void Shutdown()
    {
        _closed = true;
    }

    private CodecKind CurrentKind => CodecEngine.FromName(ModeBox.SelectedItem as string);

    private bool IsDecoding => DecodeRadio.IsChecked == true;

    // ===================== 事件 =====================

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        Recompute();
    }

    private void Direction_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        Recompute();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        Recompute();
    }

    // ===================== 转换 =====================

    private void Recompute()
    {
        var kind = CurrentKind;
        var text = InputBox.Text;

        if (string.IsNullOrEmpty(text))
        {
            OutputBox.Text = string.Empty;
            UpdateStats();
            SetStatus($"{KindLabel(kind)} · 等待输入。", StatusKind.Info);
            return;
        }

        try
        {
            OutputBox.Text = IsDecoding ? CodecEngine.Decode(text, kind) : CodecEngine.Encode(text, kind);
            SetStatus($"{KindLabel(kind)} · 转换成功 · 输入 {text.Length} 字符 / 输出 {OutputBox.Text.Length} 字符", StatusKind.Ok);
        }
        catch (FormatException ex)
        {
            OutputBox.Text = string.Empty;
            SetStatus($"{KindLabel(kind)} · 解码失败：{ex.Message}", StatusKind.Error);
        }
        catch (Exception ex)
        {
            OutputBox.Text = string.Empty;
            SetStatus($"{KindLabel(kind)} · 处理失败：{ex.Message}", StatusKind.Error);
            _plugin.LogError("编码转换失败", ex);
        }

        UpdateStats();
    }

    private string KindLabel(CodecKind kind)
        => (IsDecoding ? "解码" : "编码") + " · " + CodecEngine.KindNames[(int)kind];

    // ===================== 文件 =====================

    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择要处理的文件",
            CheckFileExists = true,
            Filter = "所有文件|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            var kind = CurrentKind;

            if (IsDecoding)
            {
                InputBox.Text = CodecEngine.BytesToText(bytes);
                SetStatus($"已载入 {Path.GetFileName(dialog.FileName)}（{bytes.Length} 字节），可直接解码。", StatusKind.Ok);
            }
            else
            {
                OutputBox.Text = CodecEngine.EncodeBytes(bytes, kind);
                UpdateStats();
                SetStatus($"已读取 {Path.GetFileName(dialog.FileName)}（{bytes.Length} 字节）并完成 {KindLabel(kind)}。", StatusKind.Ok);
            }

            _plugin.LogInfo($"载入文件：{dialog.FileName}（{bytes.Length} 字节）");
        }
        catch (Exception ex)
        {
            SetStatus("读取文件失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("读取文件失败", ex);
        }
    }

    private void SaveResult_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (string.IsNullOrEmpty(OutputBox.Text))
        {
            SetStatus("输出为空，没有可保存的内容。", StatusKind.Warn);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存转换结果",
            FileName = IsDecoding ? "decoded.bin" : "encoded.txt",
            Filter = "所有文件|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            byte[] decodedBytes = Array.Empty<byte>();
            string? decodeError = null;
            var savedAsBytes = IsDecoding
                               && CodecEngine.TryGetBytes(InputBox.Text, CurrentKind, out decodedBytes, out decodeError);

            if (savedAsBytes)
            {
                File.WriteAllBytes(dialog.FileName, decodedBytes);
                SetStatus($"已按字节保存到 {Path.GetFileName(dialog.FileName)}（{decodedBytes.Length} 字节）。", StatusKind.Ok);
            }
            else
            {
                if (IsDecoding && !string.IsNullOrEmpty(decodeError))
                {
                    SetStatus("按字节解码失败，已改为保存文本：" + decodeError, StatusKind.Warn);
                }

                File.WriteAllText(dialog.FileName, OutputBox.Text, new UTF8Encoding(false));
                SetStatus($"已保存到 {Path.GetFileName(dialog.FileName)}。", StatusKind.Ok);
            }

            _plugin.LogInfo("保存转换结果：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("保存转换结果失败", ex);
        }
    }

    // ===================== 剪切板与清理 =====================

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OutputBox.Text))
        {
            SetStatus("输出为空，没有可复制的内容。", StatusKind.Warn);
            return;
        }

        try
        {
            Clipboard.SetText(OutputBox.Text);
            SetStatus($"已复制输出（{OutputBox.Text.Length} 字符）。", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("复制到剪贴板失败", ex);
        }
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OutputBox.Text))
        {
            SetStatus("输出为空，无法交换。", StatusKind.Warn);
            return;
        }

        InputBox.Text = OutputBox.Text;
        DecodeRadio.IsChecked = !IsDecoding;
        EncodeRadio.IsChecked = IsDecoding;
        SetStatus("已交换输入输出，并反向切换方向。", StatusKind.Info);
        Recompute();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        OutputBox.Clear();
        UpdateStats();
        SetStatus("已清空。", StatusKind.Info);
    }

    private void UpdateStats()
    {
        if (InputStats is not null) InputStats.Text = Describe(InputBox?.Text);
        if (OutputStats is not null) OutputStats.Text = Describe(OutputBox?.Text);
    }

    private static string Describe(string? text) => string.IsNullOrEmpty(text) ? "0 字符" : $"{text.Length} 字符";

    private void SetStatus(string message, StatusKind kind)
    {
        if (StatusText is null) return;
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            StatusKind.Ok => "AppSuccessBrush",
            StatusKind.Warn => "AppWarningBrush",
            StatusKind.Error => "AppDangerBrush",
            _ => "AppTextSecondaryBrush"
        });
    }
}
