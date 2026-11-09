using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClassBox.Plugins.ScreenshotTool.Capture;

/// <summary>屏幕区域描述。</summary>
public sealed class ScreenInfo
{
    public int Index { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsPrimary { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string Describe() => $"{Name} {Width}×{Height} @({X},{Y})" + (IsPrimary ? " [主屏]" : string.Empty);

    /// <summary>下拉框显示用文本。</summary>
    public string DescribeText => Describe();
}

/// <summary>
/// 屏幕抓取：基于框架自带的 GDI（Graphics.CopyFromScreen），不依赖任何第三方库。
/// 全部方法在失败时返回 false 并给出可读错误，不抛异常。
/// </summary>
public static class ScreenCapture
{
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>枚举所有显示器。</summary>
    public static List<ScreenInfo> GetScreens()
    {
        var result = new List<ScreenInfo>();
        try
        {
            var all = Forms.Screen.AllScreens;
            for (var i = 0; i < all.Length; i++)
            {
                var screen = all[i];
                result.Add(new ScreenInfo
                {
                    Index = i,
                    Name = screen.DeviceName,
                    IsPrimary = screen.Primary,
                    X = screen.Bounds.X,
                    Y = screen.Bounds.Y,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height
                });
            }
        }
        catch
        {
            // 枚举失败时回退到主屏尺寸。
        }

        if (result.Count == 0)
        {
            result.Add(new ScreenInfo
            {
                Index = 0,
                Name = "Primary",
                IsPrimary = true,
                X = 0,
                Y = 0,
                Width = Math.Max(1, Forms.SystemInformation.PrimaryMonitorSize.Width),
                Height = Math.Max(1, Forms.SystemInformation.PrimaryMonitorSize.Height)
            });
        }

        return result;
    }

    /// <summary>抓取全部显示器组成的桌面（虚拟屏）。</summary>
    public static bool TryCaptureAllScreens(out BitmapSource? image, out string error)
        => TryCaptureRect(Forms.SystemInformation.VirtualScreen, out image, out error);

    /// <summary>抓取主显示器。</summary>
    public static bool TryCapturePrimaryScreen(out BitmapSource? image, out string error)
    {
        var screens = GetScreens();
        var primary = screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
        return TryCaptureScreen(primary.Index, out image, out error);
    }

    /// <summary>抓取指定序号的显示器。</summary>
    public static bool TryCaptureScreen(int index, out BitmapSource? image, out string error)
    {
        var screens = GetScreens();
        if (index < 0 || index >= screens.Count)
        {
            image = null;
            error = $"显示器序号越界：{index}（当前共 {screens.Count} 台）";
            return false;
        }

        var screen = screens[index];
        var rect = new Drawing.Rectangle(screen.X, screen.Y, screen.Width, screen.Height);
        return TryCaptureRect(rect, out image, out error);
    }

    /// <summary>抓取任意屏幕矩形区域。</summary>
    public static bool TryCaptureRect(Drawing.Rectangle rect, out BitmapSource? image, out string error)
    {
        image = null;
        error = string.Empty;

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            error = "抓取区域无效（宽或高为 0）。";
            return false;
        }

        Drawing.Bitmap? bitmap = null;
        try
        {
            bitmap = new Drawing.Bitmap(rect.Width, rect.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Drawing.Size(rect.Width, rect.Height),
                    Drawing.CopyPixelOperation.SourceCopy);
            }

            image = ToBitmapSource(bitmap);
            return image is not null;
        }
        catch (Exception ex)
        {
            error = "抓取屏幕失败：" + ex.Message;
            return false;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static BitmapSource? ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var hBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap();
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
        }
    }
}
