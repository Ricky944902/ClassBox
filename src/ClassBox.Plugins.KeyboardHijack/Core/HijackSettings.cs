using System.Text.Json.Serialization;

namespace ClassBox.Plugins.KeyboardHijack.Core;

/// <summary>白名单规则：被保留（不最小化）的窗口条件。</summary>
public sealed class WhitelistRule
{
    /// <summary>process = 进程名精确匹配；title = 窗口标题包含关键字。</summary>
    public string Kind { get; set; } = "process";

    public string Value { get; set; } = string.Empty;

    [JsonIgnore]
    public string KindText => Kind == "title" ? "标题包含" : "进程名";

    [JsonIgnore]
    public string Display => $"{KindText}：{Value}";
}

/// <summary>插件持久化配置（写入 %AppData%\Class Box\plugins\classbox.plugin.keyboardhijack）。</summary>
public sealed class HijackSettings
{
    /// <summary>是否启用全局快捷键。</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>修饰键位掩码（MOD_CONTROL | MOD_ALT 等）。</summary>
    public int HotkeyModifiers { get; set; } = HotkeyDefinition.DefaultModifiers;

    /// <summary>虚拟键码。</summary>
    public int HotkeyVirtualKey { get; set; } = HotkeyDefinition.DefaultVirtualKey;

    /// <summary>是否记录被最小化的窗口以便一键还原。</summary>
    public bool TrackRestore { get; set; } = true;

    /// <summary>触发后自动还原的延迟秒数，0 表示不自动还原。</summary>
    public int AutoRestoreSeconds { get; set; }

    /// <summary>白名单规则集合。</summary>
    public List<WhitelistRule> Rules { get; set; } = new();
}
