using System.Windows.Media;

namespace ClassBox.Abstractions.Services;

/// <summary>顶层窗口快照。</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }

    public string Title { get; init; } = string.Empty;

    public string ProcessName { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    public bool IsTopmost { get; init; }

    public bool IsAdmin { get; init; }

    public ImageSource? Icon { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? $"(无标题) {ProcessName}" : Title;

    public string HandleText => $"0x{Handle.ToInt64():X}";
}

/// <summary>
/// Win32 窗口能力封装。所有窗口操作只能经由本服务，ViewModel 不得直接 P/Invoke。
/// 每次调用都可能失败（权限、句柄失效、目标进程退出），调用方必须处理 false 返回值。
/// </summary>
public interface IWindowService
{
    /// <summary>当前进程是否具备管理员权限。</summary>
    bool IsAdmin { get; }

    /// <summary>枚举当前可见的顶层窗口。</summary>
    IReadOnlyList<WindowInfo> EnumerateWindows();

    /// <summary>按句柄获取窗口信息，窗口不存在时返回 null。</summary>
    WindowInfo? TryGetWindow(IntPtr handle);

    bool IsWindowAlive(IntPtr handle);

    /// <summary>设置 / 取消置顶。</summary>
    bool SetTopmost(IntPtr handle, bool topmost, bool activate = false);

    /// <summary>
    /// 将目标窗口嵌入到宿主容器中（“套窗口”）。
    /// 失败时返回 false 并给出可读原因，调用方应降级为普通置顶。
    /// </summary>
    bool EmbedWindow(IntPtr target, IntPtr container, out string? error);

    /// <summary>解除嵌入，恢复目标窗口原有的父窗口与样式。</summary>
    void DetachWindow(IntPtr target);

    bool IsEmbedded(IntPtr handle);

    /// <summary>重排嵌入窗口尺寸以适配宿主容器（容器尺寸变化或目标窗口被恢复时调用）。</summary>
    bool ResizeEmbedded(IntPtr target);

    bool Minimize(IntPtr handle);

    bool Restore(IntPtr handle);

    /// <summary>最小化所有可最小化的顶层窗口，返回受影响数量。</summary>
    int MinimizeAll();

    /// <summary>只保留指定进程的窗口（其它窗口最小化），返回受影响数量。</summary>
    int ShowOnlyProcess(string processName);

    /// <summary>按进程名枚举出正在运行、且有可见窗口的进程名（去重、已排序）。</summary>
    IReadOnlyList<string> GetVisibleProcessNames();
}
