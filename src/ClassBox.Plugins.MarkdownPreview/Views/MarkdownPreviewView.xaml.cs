using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClassBox.Plugins.MarkdownPreview.Rendering;
using Microsoft.Win32;

namespace ClassBox.Plugins.MarkdownPreview.Views;

/// <summary>Markdown 预览页面：左编辑右实时预览，支持打开/保存 .md 与导出纯文本。</summary>
public partial class MarkdownPreviewView : UserControl
{
    private enum StatusKind
    {
        Info,
        Ok,
        Warn,
        Error
    }

    private readonly MarkdownPreviewPlugin _plugin;
    private readonly DispatcherTimer _previewTimer;
    private bool _ready;
    private bool _closed;

    public MarkdownPreviewView(MarkdownPreviewPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderNow();
        };

        EditorBox.Text =
            "# Class Box 插件开发说明\n" +
            "\n" +
            "这是一个 **实时预览** 的 Markdown 编辑器，支持 *斜体*、`行内代码` 与 ~~删除线~~。\n" +
            "\n" +
            "## 核心约定\n" +
            "\n" +
            "1. 每个插件包含 csproj、插件类与 UserControl 页面\n" +
            "2. 页面画刷统一使用 `DynamicResource` 主题键\n" +
            "3. 禁止使用 MessageBox 与自建 Window\n" +
            "4. 编译结果必须是 **0 错误**\n" +
            "\n" +
            "### 可用控件\n" +
            "\n" +
            "- 文本编辑：`FluentTextBoxStyle`\n" +
            "- 按钮：`AccentButtonStyle` / `FluentButtonStyle`\n" +
            "- 容器：`CardStyle`\n" +
            "\n" +
            "> 提示：所有主题键都来自宿主 ClassBox 的主题字典，插件不要硬编码颜色。\n" +
            "\n" +
            "```csharp\n" +
            "public sealed class DemoPlugin : PluginBase\n" +
            "{\n" +
            "    protected override Task OnStartAsync(CancellationToken ct) => Task.CompletedTask;\n" +
            "}\n" +
            "```\n" +
            "\n" +
            "### 支持的语法一览\n" +
            "\n" +
            "| 语法 | 效果 | 说明 |\n" +
            "| --- | --- | --- |\n" +
            "| `# 标题` | 一级标题 | 共 6 级 |\n" +
            "| `**粗体**` | 加粗显示 | 行内元素 |\n" +
            "| 表格 | 自动渲染 | 支持对齐 |\n" +
            "\n" +
            "---\n" +
            "\n" +
            "更多信息可参考 [Markdown 语法说明](https://commonmark.org/help/)。\n";

        _ready = true;
        UpdateStats();
        RenderNow();
        Loaded += (_, _) => RenderNow();
    }

    public void Shutdown()
    {
        _closed = true;
        _previewTimer.Stop();
    }

    // ===================== 事件 =====================

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;

        UpdateStats();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;

        var dialog = new OpenFileDialog
        {
            Title = "打开 Markdown 文件",
            CheckFileExists = true,
            Filter = "Markdown 文件|*.md;*.markdown;*.mdown;*.txt|所有文件|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            EditorBox.Text = ReadText(File.ReadAllBytes(dialog.FileName));
            SetStatus($"已打开 {Path.GetFileName(dialog.FileName)}。", StatusKind.Ok);
            _plugin.LogInfo("打开 Markdown 文件：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("打开文件失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("打开 Markdown 文件失败", ex);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;

        var dialog = new SaveFileDialog
        {
            Title = "保存 Markdown 文件",
            Filter = "Markdown 文件|*.md|文本文件|*.txt",
            DefaultExt = ".md",
            FileName = "未命名.md"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, EditorBox.Text, new UTF8Encoding(false));
            SetStatus($"已保存到 {dialog.FileName}。", StatusKind.Ok);
            _plugin.LogInfo("保存 Markdown 文件：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("保存 Markdown 文件失败", ex);
        }
    }

    private void ExportPlain_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出纯文本",
            Filter = "文本文件|*.txt",
            DefaultExt = ".txt",
            FileName = "导出正文.txt"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, MarkdownRenderer.ToPlainText(EditorBox.Text), new UTF8Encoding(false));
            SetStatus($"纯文本已导出到 {dialog.FileName}。", StatusKind.Ok);
            _plugin.LogInfo("导出纯文本：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("导出纯文本失败", ex);
        }
    }

    private void CopyMarkdown_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(EditorBox.Text, "Markdown 源码");

    private void CopyPlain_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(MarkdownRenderer.ToPlainText(EditorBox.Text), "纯文本正文");

    private void CopyToClipboard(string text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("没有可复制的内容。", StatusKind.Warn);
            return;
        }

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
        EditorBox.Text = string.Empty;
        RenderNow();
        SetStatus("已清空编辑区。", StatusKind.Info);
    }

    // ===================== 渲染 =====================

    private void RenderNow()
    {
        if (!_ready || _closed) return;

        try
        {
            var document = MarkdownRenderer.Render(EditorBox.Text);
            PreviewViewer.Document = document;
            PreviewHint.Text = $"{document.Blocks.Count} 个块";
        }
        catch (Exception ex)
        {
            SetStatus("预览渲染失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("Markdown 渲染失败", ex);
        }
    }

    private void UpdateStats()
    {
        var text = EditorBox?.Text ?? string.Empty;
        var lines = text.Length == 0 ? 0 : text.Replace("\r\n", "\n").Split('\n').Length;
        if (StatsText is not null) StatsText.Text = $"{lines} 行 · {text.Length} 字符";
        if (CursorText is not null) CursorText.Text = $"{CountHeadings(text)} 个标题";
    }

    private static int CountHeadings(string text)
    {
        var count = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) count++;
        }

        return count;
    }

    private static string ReadText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
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
