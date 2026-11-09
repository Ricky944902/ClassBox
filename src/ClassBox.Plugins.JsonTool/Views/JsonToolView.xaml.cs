using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.JsonTool.Json;

namespace ClassBox.Plugins.JsonTool.Views;

/// <summary>JSON 工具页面：左输入右输出，支持格式化、压缩、校验定位、键排序、转义与 C# 类生成。</summary>
public partial class JsonToolView : UserControl
{
    private enum StatusKind
    {
        Info,
        Ok,
        Warn,
        Error
    }

    private readonly JsonToolPlugin _plugin;
    private bool _ready;
    private bool _closed;

    public JsonToolView(JsonToolPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        IndentBox.Items.Add(JsonEngine.IndentTwoSpaces);
        IndentBox.Items.Add(JsonEngine.IndentFourSpaces);
        IndentBox.Items.Add(JsonEngine.IndentTab);
        IndentBox.SelectedIndex = 0;

        _ready = true;
        UpdateStats();
    }

    private string IndentUnit => JsonEngine.ResolveIndent(IndentBox.SelectedItem as string);

    public void Shutdown()
    {
        _closed = true;
    }

    // ===================== 功能入口 =====================

    private void Format_Click(object sender, RoutedEventArgs e)
        => Run(text => JsonEngine.Format(text, IndentUnit), "格式化");

    private void Compress_Click(object sender, RoutedEventArgs e)
        => Run(JsonEngine.Minify, "压缩");

    private void Sort_Click(object sender, RoutedEventArgs e)
        => Run(text => JsonEngine.SortKeys(text, IndentUnit), "键排序");

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("左侧内容为空，没有可校验的 JSON。", StatusKind.Warn);
            return;
        }

        if (JsonEngine.TryValidate(text, out var error))
        {
            SetStatus("校验通过 · " + JsonEngine.ValidateMessage(text), StatusKind.Ok);
            _plugin.LogInfo("JSON 校验通过");
            return;
        }

        SetStatus($"校验失败 · {error!.PositionText} · {error.Message}", StatusKind.Error);
        LocateError(error);
        _plugin.LogWarn("JSON 校验失败：" + error.Message);
    }

    private void Escape_Click(object sender, RoutedEventArgs e)
        => Run(JsonEngine.Escape, "转义");

    private void Unescape_Click(object sender, RoutedEventArgs e)
        => Run(JsonEngine.Unescape, "反转义");

    private void ToCSharp_Click(object sender, RoutedEventArgs e)
        => Run(text => JsonEngine.ToCSharp(text, ClassNameBox.Text), "生成 C# 类");

    // ===================== 通用处理 =====================

    private void Run(Func<string, string> transform, string action)
    {
        if (!_ready || _closed) return;

        var text = InputBox.SelectionLength > 0 ? InputBox.SelectedText : InputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("左侧没有可处理的内容，请先粘贴 JSON 文本。", StatusKind.Warn);
            return;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var result = transform(text);
            watch.Stop();

            OutputBox.Text = result;
            SetStatus($"{action}完成 · 输入 {text.Length} 字符 / 输出 {result.Length} 字符 · 耗时 {watch.ElapsedMilliseconds} ms", StatusKind.Ok);
            UpdateStats();
            _plugin.LogInfo($"{action}：{text.Length} → {result.Length} 字符");
        }
        catch (JsonInputException ex)
        {
            SetStatus($"JSON 解析失败 · {ex.Error.PositionText} · {ex.Error.Message}", StatusKind.Error);
            LocateError(ex.Error);
        }
        catch (Exception ex)
        {
            SetStatus($"{action}失败：{ex.Message}", StatusKind.Error);
            _plugin.LogError($"{action}失败", ex);
        }
    }

    private void LocateError(JsonError error)
    {
        if (error.Line <= 0) return;
        try
        {
            var lineIndex = Math.Min(Math.Max(error.Line - 1, 0), Math.Max(InputBox.LineCount - 1, 0));
            var charIndex = InputBox.GetCharacterIndexFromLineIndex(lineIndex);
            if (charIndex < 0) return;
            InputBox.CaretIndex = Math.Min(charIndex + Math.Max(error.Column - 1, 0), InputBox.Text.Length);
            InputBox.Focus();
        }
        catch
        {
            // 定位失败不影响主流程
        }
    }

    // ===================== 剪贴板与清理 =====================

    private void CopyOutput_Click(object sender, RoutedEventArgs e) => Copy(OutputBox.Text, "输出");

    private void CopyInput_Click(object sender, RoutedEventArgs e) => Copy(InputBox.Text, "输入");

    private void Copy(string text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetStatus($"{what}为空，没有可复制的内容。", StatusKind.Warn);
            return;
        }

        try
        {
            Clipboard.SetText(text);
            SetStatus($"已复制{what}（{text.Length} 字符）到剪贴板。", StatusKind.Ok);
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
            SetStatus("输出为空，没有可回填的内容。", StatusKind.Warn);
            return;
        }

        InputBox.Text = OutputBox.Text;
        SetStatus("已把输出回填到输入。", StatusKind.Info);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        OutputBox.Clear();
        SetStatus("已清空输入与输出。", StatusKind.Info);
        UpdateStats();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        UpdateStats();
    }

    private void UpdateStats()
    {
        var input = InputBox?.Text ?? string.Empty;
        var output = OutputBox?.Text ?? string.Empty;
        if (InputStats is not null) InputStats.Text = FormatCounts(input);
        if (OutputStats is not null) OutputStats.Text = FormatCounts(output);
    }

    private static string FormatCounts(string text)
    {
        if (string.IsNullOrEmpty(text)) return "0 行 · 0 字符";
        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n') lines++;
        }

        return $"{lines} 行 · {text.Length} 字符";
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
