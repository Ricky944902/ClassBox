using System.Windows;
using System.Windows.Media.Imaging;

namespace ClassBox.Plugins.ScreenshotTool.Capture;

/// <summary>截图裁剪、保存与剪贴板复制。</summary>
public static class ImageCropper
{
    /// <summary>按像素矩形裁剪；矩形会自动收敛到图像范围内。</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var x = Math.Max(0, rect.X);
        var y = Math.Max(0, rect.Y);
        var w = Math.Min(rect.Width, source.PixelWidth - x);
        var h = Math.Min(rect.Height, source.PixelHeight - y);

        if (w <= 0 || h <= 0)
        {
            throw new ArgumentException("裁剪区域落空，请重新框选。");
        }

        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>保存为 PNG，返回实际保存路径。</summary>
    public static string SavePng(BitmapSource image, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        return filePath;
    }

    /// <summary>生成形如 Screenshot_20261106_193000.png 的默认文件名。</summary>
    public static string BuildFileName(DateTime now, string? prefix = null)
    {
        var name = string.IsNullOrWhiteSpace(prefix) ? "Screenshot" : prefix!.Trim();
        return $"{name}_{now:yyyyMMdd_HHmmss}.png";
    }

    /// <summary>写入剪贴板（带重试），失败返回 false。</summary>
    public static bool TryCopyToClipboard(BitmapSource image)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                System.Windows.Clipboard.SetImage(image);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    /// <summary>取系统图片目录作为默认保存位置。</summary>
    public static string GetDefaultDirectory()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return pictures;
    }
}
