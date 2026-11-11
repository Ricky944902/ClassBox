using ClassBox.Abstractions.Services;

namespace ClassBox.Plugins.KeyboardHijack.Core;

/// <summary>被本次接管最小化的窗口记录（用于一键还原）。</summary>
public sealed record HijackedWindow(IntPtr Handle, string Title, string ProcessName)
{
    public string Display => $"{ProcessName} · {Title}";
}

/// <summary>一次接管的结果。</summary>
public sealed record HijackResult(int MinimizedCount, IReadOnlyList<string> PreservedWindows);

/// <summary>
/// 接管引擎：枚举可见顶层窗口，跳过白名单窗口与已最小化窗口，
/// 其余窗口交给宿主 <see cref="IWindowService"/> 执行最小化 / 还原。
/// </summary>
internal sealed class WindowHijackEngine
{
    private readonly IWindowService _windows;
    private readonly IAppLogger _logger;
    private readonly List<HijackedWindow> _minimized = new();

    internal WindowHijackEngine(IWindowService windows, IAppLogger logger)
    {
        _windows = windows;
        _logger = logger;
    }

    /// <summary>上次接管中被最小化的窗口。</summary>
    internal IReadOnlyList<HijackedWindow> MinimizedWindows => _minimized;

    /// <summary>接管：最小化除白名单外的所有可见顶层窗口。</summary>
    internal HijackResult Hijack(IReadOnlyList<WhitelistRule> rules, bool trackRestore)
    {
        _minimized.Clear();

        var minimized = 0;
        var preserved = new List<string>();

        foreach (var window in _windows.EnumerateWindows())
        {
            try
            {
                if (IsPreserved(window, rules))
                {
                    preserved.Add($"{window.ProcessName} · {window.DisplayName}");
                    continue;
                }

                // 已经最小化的窗口无需处理，避免污染还原记录
                if (NativeMethods.IsIconic(window.Handle)) continue;

                if (_windows.Minimize(window.Handle))
                {
                    minimized++;
                    if (trackRestore)
                    {
                        _minimized.Add(new HijackedWindow(window.Handle, window.DisplayName, window.ProcessName));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"接管窗口失败：{ex.Message}");
            }
        }

        _logger.Info($"键盘接管：最小化 {minimized} 个窗口，保留 {preserved.Count} 个白名单窗口");
        return new HijackResult(minimized, preserved);
    }

    /// <summary>还原上次被最小化的窗口，返回成功还原数量。</summary>
    internal int RestoreAll()
    {
        var restored = 0;

        foreach (var item in _minimized.ToList())
        {
            try
            {
                if (!_windows.IsWindowAlive(item.Handle)) continue;
                if (_windows.Restore(item.Handle)) restored++;
            }
            catch (Exception ex)
            {
                _logger.Debug($"还原窗口失败：{ex.Message}");
            }
        }

        _minimized.Clear();

        if (restored > 0)
        {
            _logger.Info($"键盘接管：已还原 {restored} 个窗口");
        }

        return restored;
    }

    /// <summary>清空还原记录（不执行还原）。</summary>
    internal void ClearHistory() => _minimized.Clear();

    /// <summary>判断窗口是否命中白名单（进程名精确匹配 / 标题包含关键字）。</summary>
    internal static bool IsPreserved(WindowInfo window, IReadOnlyList<WhitelistRule>? rules)
    {
        if (rules is null || rules.Count == 0) return false;

        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Value)) continue;

            var keyword = rule.Value.Trim();

            if (string.Equals(rule.Kind, "title", StringComparison.OrdinalIgnoreCase))
            {
                if (window.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }

            if (string.Equals(window.ProcessName, keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
