using System.Runtime.InteropServices;

namespace ClassBox.Plugins.KeyboardHijack;

/// <summary>
/// 插件内部使用的少量 Win32 互操作声明。
/// 窗口操作（枚举 / 最小化 / 还原）统一走宿主 <c>IWindowService</c>，
/// 此处仅保留全局热键与只读窗口状态查询，避免与宿主实现重复。
/// </summary>
internal static class NativeMethods
{
    /// <summary>消息专用窗口的父窗口句柄（HWND_MESSAGE）。</summary>
    internal static readonly IntPtr HwndMessage = new(-3);

    internal const int WmHotkey = 0x0312;

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    /// <summary>避免长按热键时重复触发。</summary>
    internal const uint ModNoRepeat = 0x4000;

    internal const int ErrorHotkeyAlreadyRegistered = 1409;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
}
