namespace ClassBox.Services;

/// <summary>
/// 插件市场可见性策略：默认只展示白名单内的核心插件，其余插件（含后续新增插件）
/// 在插件中心与侧边栏默认隐藏，由「显示隐藏内容」开关控制是否展示。
/// 判定完全在宿主侧完成，不修改 ClassBox.Abstractions 公共契约，也不影响插件加载逻辑。
/// </summary>
public static class PluginVisibility
{
    /// <summary>默认可见的核心插件 Id（端口扫描、聊天工具、窗口置顶、人像监测）。</summary>
    private static readonly HashSet<string> DefaultVisibleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "classbox.plugin.portscanner",
        "classbox.plugin.classtalk",
        "classbox.builtin.topmost",
        "classbox.builtin.presence"
    };

    /// <summary>判断插件是否属于默认可见集合。</summary>
    public static bool IsDefaultVisible(string? pluginId)
        => !string.IsNullOrWhiteSpace(pluginId) && DefaultVisibleIds.Contains(pluginId);

    /// <summary>判断插件是否默认隐藏（不在白名单内即隐藏）。</summary>
    public static bool IsHiddenByDefault(string? pluginId) => !IsDefaultVisible(pluginId);
}
