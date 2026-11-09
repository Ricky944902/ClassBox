using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassBox.Plugins.TextDiff.Comparing;

/// <summary>差异行的类型。</summary>
public enum DiffKind
{
    Same,
    Added,
    Removed,
    Modified
}

/// <summary>对比结果中的一行（左右两侧内容共存，便于并排着色展示）。</summary>
public sealed class DiffLine
{
    public DiffKind Kind { get; init; }

    public int LeftNumber { get; init; }

    public int RightNumber { get; init; }

    public string LeftText { get; init; } = string.Empty;

    public string RightText { get; init; } = string.Empty;

    public string LeftNumberText => LeftNumber > 0 ? LeftNumber.ToString() : string.Empty;

    public string RightNumberText => RightNumber > 0 ? RightNumber.ToString() : string.Empty;

    public string Marker => Kind switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "-",
        DiffKind.Modified => "~",
        _ => " "
    };

    public string KindText => Kind switch
    {
        DiffKind.Added => "新增",
        DiffKind.Removed => "删除",
        DiffKind.Modified => "修改",
        _ => "相同"
    };

    public string FlatText => Kind switch
    {
        DiffKind.Added => "+ " + RightText,
        DiffKind.Removed => "- " + LeftText,
        DiffKind.Modified => "~ - " + LeftText + Environment.NewLine + "~ + " + RightText,
        _ => "  " + LeftText
    };
}

/// <summary>对比结果。</summary>
public sealed class DiffResult
{
    public List<DiffLine> Lines { get; } = new();

    public long ElapsedMs { get; set; }

    public string? Warning { get; set; }

    public int AddedCount { get; private set; }

    public int RemovedCount { get; private set; }

    public int ModifiedCount { get; private set; }

    public int SameCount { get; private set; }

    internal void Add(DiffLine line)
    {
        Lines.Add(line);
        switch (line.Kind)
        {
            case DiffKind.Added: AddedCount++; break;
            case DiffKind.Removed: RemovedCount++; break;
            case DiffKind.Modified: ModifiedCount++; break;
            default: SameCount++; break;
        }
    }

    public string BuildFlatText()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines) sb.AppendLine(line.FlatText);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>行级 LCS 差异算法：输出相同 / 新增 / 删除 / 修改四类行，支持忽略大小写与空白。</summary>
public static class DiffEngine
{
    /// <summary>参与 LCS 的最大行数，超出后退化为逐行对齐比较，避免内存与耗时爆炸。</summary>
    public const int MaxLinesForLcs = 2000;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static DiffResult Compare(string left, string right, bool ignoreCase, bool ignoreWhitespace)
    {
        var watch = Stopwatch.StartNew();
        var result = new DiffResult();

        var leftLines = SplitLines(left);
        var rightLines = SplitLines(right);
        var normLeft = Normalize(leftLines, ignoreCase, ignoreWhitespace);
        var normRight = Normalize(rightLines, ignoreCase, ignoreWhitespace);

        if (normLeft.Length > MaxLinesForLcs || normRight.Length > MaxLinesForLcs)
        {
            result.Warning = $"文本超过 {MaxLinesForLcs} 行，已退化为逐行对齐比较（不做 LCS 最优匹配）。";
            ComparePositional(leftLines, rightLines, normLeft, normRight, result);
        }
        else
        {
            CompareWithLcs(leftLines, rightLines, normLeft, normRight, result);
        }

        watch.Stop();
        result.ElapsedMs = watch.ElapsedMilliseconds;
        return result;
    }

    // ===================== LCS =====================

    private static void CompareWithLcs(string[] a, string[] b, string[] na, string[] nb, DiffResult result)
    {
        var n = na.Length;
        var m = nb.Length;
        var dp = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(na[i], nb[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var pendingDelete = new List<int>();
        var pendingInsert = new List<int>();

        void Flush()
        {
            var pairCount = Math.Min(pendingDelete.Count, pendingInsert.Count);

            for (var k = 0; k < pairCount; k++)
            {
                var li = pendingDelete[k];
                var ri = pendingInsert[k];
                result.Add(new DiffLine
                {
                    Kind = DiffKind.Modified,
                    LeftNumber = li + 1,
                    RightNumber = ri + 1,
                    LeftText = a[li],
                    RightText = b[ri]
                });
            }

            for (var k = pairCount; k < pendingDelete.Count; k++)
            {
                var li = pendingDelete[k];
                result.Add(new DiffLine
                {
                    Kind = DiffKind.Removed,
                    LeftNumber = li + 1,
                    LeftText = a[li]
                });
            }

            for (var k = pairCount; k < pendingInsert.Count; k++)
            {
                var ri = pendingInsert[k];
                result.Add(new DiffLine
                {
                    Kind = DiffKind.Added,
                    RightNumber = ri + 1,
                    RightText = b[ri]
                });
            }

            pendingDelete.Clear();
            pendingInsert.Clear();
        }

        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(na[x], nb[y], StringComparison.Ordinal))
            {
                Flush();
                result.Add(new DiffLine
                {
                    Kind = DiffKind.Same,
                    LeftNumber = x + 1,
                    RightNumber = y + 1,
                    LeftText = a[x],
                    RightText = b[y]
                });
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                pendingDelete.Add(x);
                x++;
            }
            else
            {
                pendingInsert.Add(y);
                y++;
            }
        }

        while (x < n)
        {
            pendingDelete.Add(x);
            x++;
        }

        while (y < m)
        {
            pendingInsert.Add(y);
            y++;
        }

        Flush();
    }

    // ===================== 逐行对齐（超长文本兜底） =====================

    private static void ComparePositional(string[] a, string[] b, string[] na, string[] nb, DiffResult result)
    {
        var max = Math.Max(na.Length, nb.Length);
        for (var i = 0; i < max; i++)
        {
            var hasLeft = i < na.Length;
            var hasRight = i < nb.Length;

            if (hasLeft && hasRight)
            {
                var equal = string.Equals(na[i], nb[i], StringComparison.Ordinal);
                result.Add(new DiffLine
                {
                    Kind = equal ? DiffKind.Same : DiffKind.Modified,
                    LeftNumber = i + 1,
                    RightNumber = i + 1,
                    LeftText = a[i],
                    RightText = b[i]
                });
            }
            else if (hasLeft)
            {
                result.Add(new DiffLine { Kind = DiffKind.Removed, LeftNumber = i + 1, LeftText = a[i] });
            }
            else
            {
                result.Add(new DiffLine { Kind = DiffKind.Added, RightNumber = i + 1, RightText = b[i] });
            }
        }
    }

    // ===================== 工具 =====================

    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        return lines;
    }

    private static string[] Normalize(string[] lines, bool ignoreCase, bool ignoreWhitespace)
    {
        var result = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (ignoreWhitespace) line = WhitespaceRun.Replace(line, " ").Trim();
            if (ignoreCase) line = line.ToUpperInvariant();
            result[i] = line;
        }

        return result;
    }
}
