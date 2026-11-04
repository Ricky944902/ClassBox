using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClassBox.Plugins.WindowTopmost;

/// <summary>
/// HwndHost 宿主容器：在 WPF 中创建一块原生子窗口区域，用于承载被嵌入的外部窗口。
/// </summary>
public sealed class EmbedHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;

    public IntPtr ContainerHandle { get; private set; }

    /// <summary>容器尺寸变化时触发，宿主据此重排嵌入窗口。</summary>
    public event EventHandler? ContainerResized;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        ContainerHandle = NativeMethods.CreateWindowEx(
            0, "static", string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 100, 100,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        return new HandleRef(this, ContainerHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(hwnd.Handle);
        }

        ContainerHandle = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ContainerResized?.Invoke(this, EventArgs.Empty);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr hWnd);
    }
}
