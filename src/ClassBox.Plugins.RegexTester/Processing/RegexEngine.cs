using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassBox.Plugins.RegexTester.Processing;

/// <summary>单条匹配结果。</summary>
public sealed class RegexMatchRow
{
    public int Index { get; init; }

    public int Position { get; init; }

    public int Line { get; init; }

    public int Length { get; init; }

    public string Value { get; init; } = string.Empty;

    public string Groups { get; init; } = string.Empty;

    public string PositionText => $"第 {Line} 行 / 偏移 {Position}";
}

/// <summary>一次正则测试的完整结果。</summary>
public sealed class RegexRunResult
{
    public List<RegexMatchRow> Matches { get; } = new();

    public List<string> GroupNames { get; } = new();

    public string ReplacePreview { get; set; } = string.Empty;

    public long ElapsedMs { get; set; }

    public bool TimedOut { get; set; }

    public bool Truncated { get; set; }

    public string? Error { get; set; }

    public int Count => Matches.Count;
}

/// <summary>正则测试引擎：带 2 秒超时保护，输出匹配位置、分组与替换预览。</summary>
public static class RegexEngine
{
    public const int TimeoutMs = 2000;

    public const int DefaultMaxMatches = 500;

    public static readonly (string Name, string Pattern)[] Templates =
    {
        ("邮箱", @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"),
        ("手机号", @"(?<!\d)1[3-9]\d{9}(?!\d)"),
        ("URL", @"https?://[^\s""'<>]+"),
        ("IPv4", @"\b(?:(?:25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\b"),
        ("日期", @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})"),
        ("中文字符", @"[\u4e00-\u9fa5]+")
    };

    public static RegexRunResult Run(
        string pattern,
        string input,
        bool ignoreCase,
        bool multiline,
        string replacement,
        int maxMatches = DefaultMaxMatches)
    {
        var result = new RegexRunResult();

        if (string.IsNullOrEmpty(pattern))
        {
            result.Error = "请先填写正则表达式。";
            return result;
        }

        Regex regex;
        try
        {
            var options = RegexOptions.None;
            if (ignoreCase) options |= RegexOptions.IgnoreCase;
            if (multiline) options |= RegexOptions.Multiline;
            regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(TimeoutMs));
        }
        catch (ArgumentException ex)
        {
            result.Error = "正则语法错误：" + ex.Message;
            return result;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var name in regex.GetGroupNames())
            {
                if (!int.TryParse(name, out _)) result.GroupNames.Add(name);
            }

            var text = input ?? string.Empty;
            var lineStarts = BuildLineStarts(text);
            var index = 0;

            foreach (Match match in regex.Matches(text))
            {
                if (index >= maxMatches)
                {
                    result.Truncated = true;
                    break;
                }

                index++;
                result.Matches.Add(new RegexMatchRow
                {
                    Index = index,
                    Position = match.Index + 1,
                    Line = ResolveLine(lineStarts, match.Index),
                    Length = match.Length,
                    Value = Visible(match.Value, 160),
                    Groups = DescribeGroups(regex, match)
                });
            }

            if (!string.IsNullOrEmpty(replacement))
            {
                var replaced = regex.Replace(text, replacement);
                result.ReplacePreview = replaced.Length > 20000
                    ? replaced.Substring(0, 20000) + "\r\n…（预览已截断）"
                    : replaced;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            result.TimedOut = true;
            result.Error = $"匹配超时（超过 {TimeoutMs} ms），可能是灾难性回溯，请简化表达式或减少输入文本。";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        watch.Stop();
        result.ElapsedMs = watch.ElapsedMilliseconds;
        return result;
    }

    private static string DescribeGroups(Regex regex, Match match)
    {
        var sb = new StringBuilder();
        for (var i = 1; i < match.Groups.Count; i++)
        {
            var group = match.Groups[i];
            var name = regex.GroupNameFromNumber(i);
            if (int.TryParse(name, out _)) name = i.ToString();
            if (!group.Success) continue;
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append(name).Append('=').Append(Visible(group.Value, 60));
        }

        return sb.ToString();
    }

    private static List<int> BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }

        return starts;
    }

    private static int ResolveLine(List<int> lineStarts, int position)
    {
        var index = lineStarts.BinarySearch(position);
        if (index >= 0) return index + 1;
        var insert = ~index - 1;
        return Math.Max(insert, 0) + 1;
    }

    /// <summary>把不可见字符转成可见形式，并限制长度。</summary>
    public static string Visible(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }

            if (sb.Length > max) break;
        }

        var text = sb.ToString();
        return text.Length > max ? text.Substring(0, max) + "…" : text;
    }
}
