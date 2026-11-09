using System.Text;
using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.TextDiff.Comparing;
using Microsoft.Win32;

namespace ClassBox.Plugins.TextDiff.Views;

/// <summary>文本对比页面：左右两栏文本按行做 LCS 差异对比，并逐行着色展示。</summary>
public partial class TextDiffView : UserControl
{
    private enum StatusKind
    {
        Info,
        Ok,
        Warn,
        Error
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly TextDiffPlugin _plugin;
    private bool _ready;
    private bool _closed;
    private DiffResult? _lastResult;

    public TextDiffView(TextDiffPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        LeftBox.Text =
            "Class Box 插件规范\r\n" +
            "1. 只允许使用 BCL 与 WPF\r\n" +
            "2. 页面必须是 UserControl\r\n" +
            "3. 主题画刷使用 DynamicResource\r\n" +
            "4. 禁止 MessageBox\r\n" +
            "5. 编译结果：0 错误";
        RightBox.Text =
            "Class Box 插件规范\r\n" +
            "1. 只允许使用 BCL 与 WPF（禁止第三方 NuGet）\r\n" +
            "2. 页面必须是 UserControl\r\n" +
            "3. 主题画刷使用 DynamicResource\r\n" +
            "4. 禁止 MessageBox 与自建 Window\r\n" +
            "5. 编译结果：0 错误 0 警告\r\n" +
            "6. 冒烟测试必须通过";

        _ready = true;
        UpdateStats();
        CompareNow();
    }

    public void Shutdown()
    {
        _closed = true;
    }

    // ===================== 事件 =====================

    private void Compare_Click(object sender, RoutedEventArgs e) => CompareNow();

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        UpdateStats();
    }

    private void LoadLeft_Click(object sender, RoutedEventArgs e) => LoadFile(LeftBox, "左侧");

    private void LoadRight_Click(object sender, RoutedEventArgs e) => LoadFile(RightBox, "右侧");

    private void LoadFile(TextBox target, string side)
    {
        if (!_ready || _closed) return;

        var dialog = new OpenFileDialog
        {
            Title = $"选择{side}要对比的文本文件",
            CheckFileExists = true,
            Filter = "文本文件|*.txt;*.md;*.csv;*.json;*.log;*.cs;*.xml;*.yml;*.yaml|所有文件|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            target.Text = ReadText(bytes);
            UpdateStats();
            SetStatus($"已载入{side}文件 {Path.GetFileName(dialog.FileName)}（{bytes.Length} 字节）。", StatusKind.Ok);
            _plugin.LogInfo($"载入{side}文件：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"载入{side}文件失败：{ex.Message}", StatusKind.Error);
            _plugin.LogError($"载入{side}文件失败", ex);
        }
    }

    /// <summary>优先按 UTF-8 严格解码，失败后回退到系统默认编码与 GB18030。</summary>
    private static string ReadText(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return Encoding.Default.GetString(bytes);
            }
        }
    }

    // ===================== 对比 =====================

    private void CompareNow()
    {
        if (!_ready || _closed) return;

        try
        {
            var result = DiffEngine.Compare(
                LeftBox.Text,
                RightBox.Text,
                IgnoreCaseBox.IsChecked == true,
                IgnoreWhitespaceBox.IsChecked == true);

            _lastResult = result;
            DiffList.ItemsSource = result.Lines;
            AddedBadge.Text = result.AddedCount.ToString();
            RemovedBadge.Text = result.RemovedCount.ToString();
            ModifiedBadge.Text = result.ModifiedCount.ToString();
            StatsText.Text = $"相同 {result.SameCount} 行 · 耗时 {result.ElapsedMs} ms";

            if (result.Warning is not null)
            {
                SetStatus(result.Warning, StatusKind.Warn);
            }
            else if (result.AddedCount + result.RemovedCount + result.ModifiedCount == 0)
            {
                SetStatus($"两侧内容完全一致（{result.SameCount} 行相同）。", StatusKind.Ok);
            }
            else
            {
                SetStatus(
                    $"对比完成 · 新增 {result.AddedCount} 行 / 删除 {result.RemovedCount} 行 / 修改 {result.ModifiedCount} 行 / 相同 {result.SameCount} 行",
                    StatusKind.Ok);
            }

            _plugin.LogInfo($"文本对比：新增 {result.AddedCount}，删除 {result.RemovedCount}，修改 {result.ModifiedCount}");
        }
        catch (Exception ex)
        {
            SetStatus("对比失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("文本对比失败", ex);
        }
    }

    // ===================== 复制与清理 =====================

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null || _lastResult.Lines.Count == 0)
        {
            SetStatus("当前没有对比结果可复制。", StatusKind.Warn);
            return;
        }

        try
        {
            var header = $"# 文本对比结果：新增 {_lastResult.AddedCount} 行 / 删除 {_lastResult.RemovedCount} 行 / 修改 {_lastResult.ModifiedCount} 行 / 相同 {_lastResult.SameCount} 行";
            Clipboard.SetText(header + Environment.NewLine + _lastResult.BuildFlatText());
            SetStatus("已复制完整对比结果到剪贴板。", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("复制对比结果失败", ex);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        LeftBox.Text = string.Empty;
        RightBox.Text = string.Empty;
        _lastResult = null;
        DiffList.ItemsSource = null;
        AddedBadge.Text = "0";
        RemovedBadge.Text = "0";
        ModifiedBadge.Text = "0";
        StatsText.Text = string.Empty;
        UpdateStats();
        SetStatus("已清空两侧文本与对比结果。", StatusKind.Info);
    }

    private void UpdateStats()
    {
        if (LeftStats is not null) LeftStats.Text = Describe(LeftBox?.Text);
        if (RightStats is not null) RightStats.Text = Describe(RightBox?.Text);
    }

    private static string Describe(string? text)
    {
        var lines = DiffEngine.SplitLines(text).Length;
        var chars = text?.Length ?? 0;
        return $"{lines} 行 · {chars} 字符";
    }

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
