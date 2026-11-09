using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClassBox.Plugins.MarkdownPreview.Rendering;

/// <summary>
/// 手写极简 Markdown 渲染器：支持标题、粗体/斜体、行内代码、代码块、有序/无序列表、
/// 引用、水平线、链接与表格，输出 WPF FlowDocument，样式全部使用宿主主题资源键。
/// </summary>
public static class MarkdownRenderer
{
    private const string MonoFamily = "Consolas, Microsoft YaHei UI";

    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex HorizontalRulePattern = new(@"^\s{0,3}((-\s*){3,}|(\*\s*){3,}|(_\s*){3,})$", RegexOptions.Compiled);
    private static readonly Regex UnorderedItemPattern = new(@"^\s{0,3}[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedItemPattern = new(@"^\s{0,3}\d{1,9}[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new(@"\[([^\]]*)\]\(([^)\s]+)\)", RegexOptions.Compiled);

    private static readonly double[] HeadingSizes = { 25, 22, 19.5, 17.5, 16, 15 };

    /// <summary>把 Markdown 文本渲染成带有主题资源引用的 FlowDocument。</summary>
    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            ColumnWidth = double.PositiveInfinity,
            PagePadding = new Thickness(20, 12, 20, 18),
            FontSize = 14,
            LineHeight = 22
        };

        doc.SetResourceReference(TextElement.FontFamilyProperty, "AppFontFamily");
        doc.SetResourceReference(TextElement.ForegroundProperty, "AppTextPrimaryBrush");

        var lines = SplitLines(markdown);
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            // 代码块
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index = AddCodeBlock(doc, lines, index);
                continue;
            }

            // 水平线
            if (HorizontalRulePattern.IsMatch(line))
            {
                AddHorizontalRule(doc);
                index++;
                continue;
            }

            // 标题
            var heading = HeadingPattern.Match(line);
            if (heading.Success)
            {
                AddHeading(doc, heading.Groups[1].Value.Length, heading.Groups[2].Value);
                index++;
                continue;
            }

            // 表格
            if (index + 1 < lines.Length && IsTableRow(line) && IsTableSeparator(lines[index + 1]))
            {
                index = AddTable(doc, lines, index);
                continue;
            }

            // 引用
            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                index = AddBlockquote(doc, lines, index);
                continue;
            }

            // 无序列表
            if (UnorderedItemPattern.IsMatch(line))
            {
                index = AddList(doc, lines, index, ordered: false);
                continue;
            }

            // 有序列表
            if (OrderedItemPattern.IsMatch(line))
            {
                index = AddList(doc, lines, index, ordered: true);
                continue;
            }

            // 普通段落（连续非空行合并）
            index = AddParagraph(doc, lines, index);
        }

        return doc;
    }

    // ===================== 块级元素 =====================

    private static void AddHeading(FlowDocument doc, int level, string text)
    {
        var paragraph = new Paragraph
        {
            FontSize = HeadingSizes[Math.Clamp(level - 1, 0, HeadingSizes.Length - 1)],
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 8)
        };

        AppendInline(paragraph.Inlines, text, 0);
        doc.Blocks.Add(paragraph);
    }

    private static void AddHorizontalRule(FlowDocument doc)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 10, 0, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0)
        };
        paragraph.SetResourceReference(Block.BorderBrushProperty, "AppStrokeBrush");
        doc.Blocks.Add(paragraph);
    }

    private static int AddCodeBlock(FlowDocument doc, string[] lines, int start)
    {
        var paragraph = new Paragraph
        {
            FontFamily = new FontFamily(MonoFamily),
            FontSize = 12.5,
            Margin = new Thickness(0, 8, 0, 12),
            Padding = new Thickness(12, 10, 12, 10)
        };
        paragraph.SetResourceReference(Block.BackgroundProperty, "AppLayerAltBrush");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "AppTextPrimaryBrush");

        var index = start + 1;
        var first = true;
        while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            if (!first) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[index]));
            first = false;
            index++;
        }

        if (first)
        {
            paragraph.Inlines.Add(new Run("（空代码块）"));
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "AppTextSecondaryBrush");
        }

        doc.Blocks.Add(paragraph);
        return index < lines.Length ? index + 1 : index;
    }

    private static int AddBlockquote(FlowDocument doc, string[] lines, int start)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 6, 0, 10),
            Padding = new Thickness(12, 4, 8, 4),
            BorderThickness = new Thickness(3, 0, 0, 0),
            FontStyle = FontStyles.Italic
        };
        paragraph.SetResourceReference(Block.BorderBrushProperty, "AppAccentBrush");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "AppTextSecondaryBrush");

        var index = start;
        var first = true;
        while (index < lines.Length && lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal))
        {
            var content = lines[index].TrimStart().TrimStart('>').TrimStart();
            if (!first) paragraph.Inlines.Add(new LineBreak());
            AppendInline(paragraph.Inlines, content, 0);
            first = false;
            index++;
        }

        doc.Blocks.Add(paragraph);
        return index;
    }

    private static int AddList(FlowDocument doc, string[] lines, int start, bool ordered)
    {
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 6, 0, 10),
            Padding = new Thickness(26, 0, 0, 0)
        };

        var pattern = ordered ? OrderedItemPattern : UnorderedItemPattern;
        var index = start;

        while (index < lines.Length)
        {
            var match = pattern.Match(lines[index]);
            if (!match.Success) break;

            var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            AppendInline(paragraph.Inlines, match.Groups[1].Value, 0);
            list.ListItems.Add(new ListItem(paragraph));
            index++;
        }

        doc.Blocks.Add(list);
        return index;
    }

    private static int AddParagraph(FlowDocument doc, string[] lines, int start)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 4, 0, 8) };
        var index = start;
        var builder = new StringBuilder();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) break;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
            if (HeadingPattern.IsMatch(line)) break;
            if (HorizontalRulePattern.IsMatch(line)) break;
            if (UnorderedItemPattern.IsMatch(line)) break;
            if (OrderedItemPattern.IsMatch(line)) break;
            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal)) break;
            if (index + 1 < lines.Length && IsTableRow(line) && IsTableSeparator(lines[index + 1])) break;

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(line.Trim());
            index++;
        }

        if (builder.Length == 0)
        {
            index = start + 1;
            builder.Append(lines[start]);
        }

        AppendInline(paragraph.Inlines, builder.ToString(), 0);
        doc.Blocks.Add(paragraph);
        return index;
    }

    private static int AddTable(FlowDocument doc, string[] lines, int start)
    {
        var header = SplitTableRow(lines[start]);
        var alignments = SplitTableRow(lines[start + 1])
            .Select(cell => cell.StartsWith(":", StringComparison.Ordinal) && cell.EndsWith(":", StringComparison.Ordinal) ? TextAlignment.Center
                : cell.EndsWith(":", StringComparison.Ordinal) ? TextAlignment.Right
                : TextAlignment.Left)
            .ToList();

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 8, 0, 12)
        };

        for (var i = 0; i < header.Count; i++)
        {
            table.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        group.Rows.Add(BuildRow(header, alignments, isHeader: true, columnCount: header.Count));

        var index = start + 2;
        while (index < lines.Length && IsTableRow(lines[index]) && !IsTableSeparator(lines[index]))
        {
            group.Rows.Add(BuildRow(SplitTableRow(lines[index]), alignments, isHeader: false, columnCount: header.Count));
            index++;
        }

        doc.Blocks.Add(table);
        return index;
    }

    private static TableRow BuildRow(List<string> cells, List<TextAlignment> alignments, bool isHeader, int columnCount)
    {
        var row = new TableRow();
        if (isHeader)
        {
            row.SetResourceReference(TableRow.BackgroundProperty, "AppLayerAltBrush");
        }

        for (var i = 0; i < columnCount; i++)
        {
            var text = i < cells.Count ? cells[i] : string.Empty;
            var paragraph = new Paragraph(new Run(text))
            {
                Margin = new Thickness(0),
                Padding = new Thickness(8, 5, 8, 5),
                TextAlignment = i < alignments.Count ? alignments[i] : TextAlignment.Left,
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal
            };

            var cell = new TableCell(paragraph)
            {
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "AppStrokeBrush");

            row.Cells.Add(cell);
        }

        return row;
    }

    private static bool IsTableRow(string line) => line.Contains('|') && line.Trim().Length > 1;

    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.Contains('-') || !trimmed.Contains('|')) return false;

        foreach (var c in trimmed)
        {
            if (c != '|' && c != '-' && c != ':' && c != ' ') return false;
        }

        return true;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(0, trimmed.Length - 1);
        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    // ===================== 行内元素 =====================

    private static void AppendInline(InlineCollection target, string text, int depth)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (depth > 6)
        {
            target.Add(new Run(text));
            return;
        }

        var buffer = new StringBuilder();

        void FlushBuffer()
        {
            if (buffer.Length == 0) return;
            target.Add(new Run(buffer.ToString()));
            buffer.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // 行内代码
            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushBuffer();
                    var code = new Run(text.Substring(i + 1, end - i - 1))
                    {
                        FontFamily = new FontFamily(MonoFamily),
                        FontSize = 12.5
                    };
                    code.SetResourceReference(TextElement.BackgroundProperty, "AppLayerAltBrush");
                    code.SetResourceReference(TextElement.ForegroundProperty, "AppAccentBrush");
                    target.Add(code);
                    i = end + 1;
                    continue;
                }
            }

            // 粗体
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    FlushBuffer();
                    var bold = new Bold();
                    AppendInline(bold.Inlines, text.Substring(i + 2, end - i - 2), depth + 1);
                    target.Add(bold);
                    i = end + 2;
                    continue;
                }
            }

            // 斜体
            if (c == '*' && i + 1 < text.Length && text[i + 1] != '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    FlushBuffer();
                    var italic = new Italic();
                    AppendInline(italic.Inlines, text.Substring(i + 1, end - i - 1), depth + 1);
                    target.Add(italic);
                    i = end + 1;
                    continue;
                }
            }

            // 删除线
            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    FlushBuffer();
                    var span = new Span();
                    span.TextDecorations = TextDecorations.Strikethrough;
                    AppendInline(span.Inlines, text.Substring(i + 2, end - i - 2), depth + 1);
                    target.Add(span);
                    i = end + 2;
                    continue;
                }
            }

            // 链接
            if (c == '[')
            {
                var close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                if (close > i)
                {
                    var urlEnd = text.IndexOf(')', close + 2);
                    if (urlEnd > close + 1)
                    {
                        var label = text.Substring(i + 1, close - i - 1);
                        var url = text.Substring(close + 2, urlEnd - close - 2).Trim();
                        FlushBuffer();
                        target.Add(BuildLink(label, url, depth));
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            buffer.Append(c);
            i++;
        }

        FlushBuffer();
    }

    private static Inline BuildLink(string label, string url, int depth)
    {
        var hyperlink = new Hyperlink { ToolTip = url };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AppAccentBrush");
        AppendInline(hyperlink.Inlines, string.IsNullOrEmpty(label) ? url : label, depth + 1);

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.RequestNavigate += (_, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch
                {
                    // 打不开链接时静默忽略，不影响预览
                }

                e.Handled = true;
            };
        }

        return hyperlink;
    }

    // ===================== 纯文本导出 =====================

    /// <summary>剥离 Markdown 标记，得到纯文本（保留段落与列表层次）。</summary>
    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        var sb = new StringBuilder();
        var inCode = false;

        foreach (var raw in SplitLines(markdown))
        {
            var line = raw;

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                sb.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine();
                continue;
            }

            if (HorizontalRulePattern.IsMatch(line))
            {
                sb.AppendLine("────────");
                continue;
            }

            var heading = HeadingPattern.Match(line);
            if (heading.Success)
            {
                sb.AppendLine(StripInline(heading.Groups[2].Value));
                continue;
            }

            var unordered = UnorderedItemPattern.Match(line);
            if (unordered.Success)
            {
                sb.Append("• ").AppendLine(StripInline(unordered.Groups[1].Value));
                continue;
            }

            var ordered = OrderedItemPattern.Match(line);
            if (ordered.Success)
            {
                sb.Append("- ").AppendLine(StripInline(ordered.Groups[1].Value));
                continue;
            }

            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                sb.Append("  ").AppendLine(StripInline(line.TrimStart().TrimStart('>').Trim()));
                continue;
            }

            if (IsTableSeparator(line)) continue;
            if (IsTableRow(line))
            {
                sb.AppendLine(string.Join(" | ", SplitTableRow(line)));
                continue;
            }

            sb.AppendLine(StripInline(line));
        }

        return sb.ToString().TrimEnd();
    }

    private static string StripInline(string text)
    {
        var result = LinkPattern.Replace(text, "$1");
        result = result.Replace("**", string.Empty).Replace("~~", string.Empty).Replace("`", string.Empty);
        result = Regex.Replace(result, @"(?<!\*)\*(?!\*)", string.Empty);
        return result;
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}
