namespace ClassBox.Abstractions.Services;

/// <summary>屏幕侧边非阻塞提示请求。</summary>
public sealed class SideAlertRequest
{
    public string Title { get; init; } = "提示";

    public string Message { get; init; } = string.Empty;

    /// <summary>标题前的图标字形（Segoe MDL2）。</summary>
    public string Glyph { get; init; } = "\uE7BA";

    /// <summary>自动关闭秒数，0 表示不自动关闭。</summary>
    public int AutoCloseSeconds { get; init; } = 6;

    /// <summary>指示条强调色（#RRGGBB），为空时使用主题强调色。</summary>
    public string? AccentColor { get; init; }

    /// <summary>是否使用警告色（红）。</summary>
    public bool IsUrgent { get; init; }
}

/// <summary>
/// 侧边提示服务：在屏幕右侧弹出非模态、不抢占焦点的提示条。
/// 严禁在实现中使用 MessageBox 之类的模态窗口。
/// </summary>
public interface ISideAlertService
{
    void Show(SideAlertRequest request);
}
