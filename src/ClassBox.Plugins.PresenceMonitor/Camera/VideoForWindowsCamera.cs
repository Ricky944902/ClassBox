using System.Runtime.InteropServices;

namespace ClassBox.Plugins.PresenceMonitor.Camera;

/// <summary>一帧 RGB24 位图数据，行序由 <see cref="BottomUp"/> 指示。</summary>
public sealed class CameraFrame
{
    public CameraFrame(int width, int height, int stride, byte[] pixels, bool bottomUp)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
        BottomUp = bottomUp;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }

    /// <summary>DIB 通常自底向上存储。</summary>
    public bool BottomUp { get; }

    /// <summary>读取指定坐标的像素（BGR 顺序，来自 DIB）。</summary>
    public (byte B, byte G, byte R) GetPixel(int x, int y)
    {
        var row = BottomUp ? Height - 1 - y : y;
        var offset = (row * Stride) + (x * 3);

        if (offset < 0 || offset + 2 >= Pixels.Length) return (0, 0, 0);

        return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2]);
    }
}

/// <summary>
/// 基于 Video for Windows（avicap32）的摄像头抓帧器。
/// 使用系统自带组件，不引入任何第三方依赖。
/// </summary>
public sealed class VideoForWindowsCamera : IDisposable
{
    private const int WM_CAP_START = 0x0400;
    private const int WM_CAP_DRIVER_CONNECT = WM_CAP_START + 10;
    private const int WM_CAP_DRIVER_DISCONNECT = WM_CAP_START + 11;
    private const int WM_CAP_GET_FRAME = WM_CAP_START + 14;
    private const int WM_CAP_SET_PREVIEW = WM_CAP_START + 50;
    private const int WM_CAP_SET_PREVIEWRATE = WM_CAP_START + 52;
    private const int WM_CAP_GRAB_FRAME_NOSTOP = WM_CAP_START + 61;

    private const int WS_POPUP = unchecked((int)0x80000000);

    private IntPtr _hwnd;
    private bool _connected;

    public string? LastError { get; private set; }

    public bool IsConnected => _connected;

    /// <summary>连接指定索引的摄像头设备。</summary>
    public bool Open(int deviceIndex = 0)
    {
        if (_connected) return true;

        try
        {
            // 窗口放在屏幕外并保持极小尺寸：既能连接驱动，又不会打扰用户。
            _hwnd = NativeMethods.capCreateCaptureWindowW(
                "ClassBoxPresence", WS_POPUP, -32000, -32000, 160, 120, IntPtr.Zero, 0);

            if (_hwnd == IntPtr.Zero)
            {
                LastError = "无法创建摄像头捕获窗口（avicap32 不可用）。";
                return false;
            }

            var result = NativeMethods.SendMessage(_hwnd, WM_CAP_DRIVER_CONNECT, new IntPtr(deviceIndex), IntPtr.Zero);
            if (result == IntPtr.Zero)
            {
                LastError = $"未能连接第 {deviceIndex + 1} 个摄像头，请检查设备是否被其它程序占用。";
                Dispose();
                return false;
            }

            _connected = true;

            // 关闭预览画面，仅保留抓帧能力。
            NativeMethods.SendMessage(_hwnd, WM_CAP_SET_PREVIEWRATE, new IntPtr(1000), IntPtr.Zero);
            NativeMethods.SendMessage(_hwnd, WM_CAP_SET_PREVIEW, IntPtr.Zero, IntPtr.Zero);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>抓取一帧；失败返回 null。</summary>
    public CameraFrame? GrabFrame()
    {
        if (!_connected || _hwnd == IntPtr.Zero) return null;

        var hDib = IntPtr.Zero;
        try
        {
            // 先请求抓取，再取回 DIB 句柄。
            NativeMethods.SendMessage(_hwnd, WM_CAP_GRAB_FRAME_NOSTOP, IntPtr.Zero, IntPtr.Zero);
            hDib = NativeMethods.SendMessage(_hwnd, WM_CAP_GET_FRAME, IntPtr.Zero, IntPtr.Zero);
            if (hDib == IntPtr.Zero) return null;

            var pointer = NativeMethods.GlobalLock(hDib);
            if (pointer == IntPtr.Zero) return null;

            try
            {
                var header = Marshal.PtrToStructure<BitmapInfoHeader>(pointer);
                if (header.biBitCount != 24 || header.biWidth <= 0 || header.biHeight == 0) return null;

                var width = header.biWidth;
                var height = Math.Abs(header.biHeight);
                var stride = ((width * 3) + 3) / 4 * 4;
                var dataSize = stride * height;

                var total = (long)NativeMethods.GlobalSize(hDib);
                if (total < Marshal.SizeOf<BitmapInfoHeader>() + dataSize) return null;

                var pixels = new byte[dataSize];
                Marshal.Copy(pointer + Marshal.SizeOf<BitmapInfoHeader>(), pixels, 0, dataSize);

                return new CameraFrame(width, height, stride, pixels, header.biHeight > 0);
            }
            finally
            {
                NativeMethods.GlobalUnlock(hDib);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hDib != IntPtr.Zero)
            {
                NativeMethods.GlobalFree(hDib);
            }
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                if (_connected)
                {
                    NativeMethods.SendMessage(_hwnd, WM_CAP_SET_PREVIEW, IntPtr.Zero, IntPtr.Zero);
                    NativeMethods.SendMessage(_hwnd, WM_CAP_DRIVER_DISCONNECT, IntPtr.Zero, IntPtr.Zero);
                }

                NativeMethods.DestroyWindow(_hwnd);
            }
            catch
            {
                // 释放阶段忽略异常
            }
        }

        _connected = false;
        _hwnd = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    private static class NativeMethods
    {
        [DllImport("avicap32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr capCreateCaptureWindowW(
            string lpszWindowName, int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, int nID);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern UIntPtr GlobalSize(IntPtr hMem);
    }
}
