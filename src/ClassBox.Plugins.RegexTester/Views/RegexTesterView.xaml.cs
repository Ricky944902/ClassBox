using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.RegexTester.Processing;

namespace ClassBox.Plugins.RegexTester.Views;

/// <summary>正则测试器页面：输入正则与待测文本，列出全部匹配、分组与替换预览。</summary>
public partial class RegexTesterView : UserControl
{
    private enum StatusKind
    {
        Info,
        Ok,
        Warn,
        Error
    }

    private readonly RegexTesterPlugin _plugin;
    private bool _ready;
    private bool _closed;

    public RegexTesterView(RegexTesterPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        foreach (var template in RegexEngine.Templates) TemplateBox.Items.Add(template.Name);

        PatternBox.Text = @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})";
        InputBox.Text = "项目启动：2024-03-18，验收：2024-11-06，归档：2025-01-09。\r\n备用联系方式 test@example.com / 13800138000";
        ReplacementBox.Text = "${year}年${month}月${day}日";

        _ready = true;
        UpdateInputStats();
        RunTest();
    }

    public void Shutdown()
    {
        _closed = true;
    }

    // ===================== 事件 =====================

    private void Run_Click(object sender, RoutedEventArgs e) => RunTest();

    private void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;

        var index = TemplateBox.SelectedIndex;
        if (index < 0 || index >= RegexEngine.Templates.Length) return;

        var template = RegexEngine.Templates[index];
        PatternBox.Text = template.Pattern;
        SetStatus($"已填入「{template.Name}」模板。", StatusKind.Info);
        RunTest();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        RunTest();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        UpdateInputStats();
    }

    private void ReplacementBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;

        // 仅刷新替换预览，避免每次输入都重跑匹配列表
        if (string.IsNullOrEmpty(ReplacementBox.Text) || string.IsNullOrEmpty(PatternBox.Text))
        {
            if (ReplacePreviewBox is not null) ReplacePreviewBox.Text = string.Empty;
            return;
        }

        PreviewReplace();
    }

    // ===================== 核心逻辑 =====================

    private void RunTest()
    {
        if (!_ready || _closed) return;

        var pattern = PatternBox.Text;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            MatchesGrid.ItemsSource = null;
            MatchSummary.Text = "匹配结果";
            GroupSummary.Text = string.Empty;
            ReplacePreviewBox.Text = string.Empty;
            SetStatus("请先填写正则表达式。", StatusKind.Warn);
            return;
        }

        var result = RegexEngine.Run(
            pattern,
            InputBox.Text,
            IgnoreCaseBox.IsChecked == true,
            MultilineBox.IsChecked == true,
            ReplacementBox.Text);

        MatchesGrid.ItemsSource = result.Matches;

        if (result.Error is not null && !result.TimedOut)
        {
            MatchSummary.Text = "匹配结果 · 表达式无效";
            GroupSummary.Text = string.Empty;
            ReplacePreviewBox.Text = string.Empty;
            SetStatus(result.Error, StatusKind.Error);
            _plugin.LogWarn("正则测试失败：" + result.Error);
            return;
        }

        MatchSummary.Text = $"匹配 {result.Count} 处 · 耗时 {result.ElapsedMs} ms" + (result.Truncated ? "（结果过多已截断）" : string.Empty);
        GroupSummary.Text = result.GroupNames.Count > 0 ? "命名分组：" + string.Join("、", result.GroupNames) : string.Empty;
        ReplacePreviewBox.Text = result.ReplacePreview;

        if (result.TimedOut)
        {
            SetStatus(result.Error!, StatusKind.Error);
            _plugin.LogWarn("正则匹配超时");
            return;
        }

        if (result.Count == 0)
        {
            SetStatus($"没有匹配到内容（耗时 {result.ElapsedMs} ms）。", StatusKind.Warn);
        }
        else
        {
            SetStatus($"匹配成功 · {result.Count} 处 · 耗时 {result.ElapsedMs} ms", StatusKind.Ok);
        }
    }

    private void PreviewReplace()
    {
        var result = RegexEngine.Run(
            PatternBox.Text,
            InputBox.Text,
            IgnoreCaseBox.IsChecked == true,
            MultilineBox.IsChecked == true,
            ReplacementBox.Text);

        ReplacePreviewBox.Text = result.Error is null ? result.ReplacePreview : "替换预览失败：" + result.Error;
    }

    private void UpdateInputStats()
    {
        if (InputStats is null) return;
        var text = InputBox?.Text ?? string.Empty;
        InputStats.Text = text.Length == 0 ? "0 字符" : $"{text.Length} 字符 · {CountLines(text)} 行";
    }

    private static int CountLines(string text)
    {
        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n') lines++;
        }

        return lines;
    }

    // ===================== 复制与清理 =====================

    private void CopyMatches_Click(object sender, RoutedEventArgs e)
    {
        if (MatchesGrid.ItemsSource is not IEnumerable<RegexMatchRow> rows)
        {
            SetStatus("当前没有匹配结果可复制。", StatusKind.Warn);
            return;
        }

        var values = rows.Select(r => r.Value).ToList();
        if (values.Count == 0)
        {
            SetStatus("当前没有匹配结果可复制。", StatusKind.Warn);
            return;
        }

        CopyText(string.Join(Environment.NewLine, values), $"{values.Count} 条匹配值");
    }

    private void CopyReplace_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ReplacePreviewBox.Text))
        {
            SetStatus("替换预览为空，没有可复制的内容。", StatusKind.Warn);
            return;
        }

        CopyText(ReplacePreviewBox.Text, "替换结果");
    }

    private void CopyText(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus($"已复制{what}到剪贴板。", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("复制到剪贴板失败", ex);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        PatternBox.Text = string.Empty;
        InputBox.Text = string.Empty;
        ReplacementBox.Text = string.Empty;
        MatchesGrid.ItemsSource = null;
        MatchSummary.Text = "匹配结果";
        GroupSummary.Text = string.Empty;
        ReplacePreviewBox.Text = string.Empty;
        UpdateInputStats();
        SetStatus("已清空全部内容。", StatusKind.Info);
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
