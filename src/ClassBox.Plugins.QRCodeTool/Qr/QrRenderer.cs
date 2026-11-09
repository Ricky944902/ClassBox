using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassBox.Plugins.QRCodeTool.Qr;

/// <summary>把二维码矩阵渲染为 WPF 位图并导出 PNG。</summary>
public static class QrRenderer
{
    public const int DefaultModuleSize = 8;
    public const int DefaultQuietZone = 4;

    /// <summary>渲染为位图（黑白色，硬边，保证可被扫码识别）。</summary>
    public static BitmapSource Render(QrCode code, int moduleSize = DefaultModuleSize, int quietZone = DefaultQuietZone)
    {
        if (moduleSize < 1) moduleSize = 1;
        if (quietZone < 0) quietZone = 0;

        var size = code.Size;
        var pixels = (size + quietZone * 2) * moduleSize;
        var stride = pixels * 4;
        var buffer = new byte[stride * pixels];

        // 背景填充为白色
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = 255;
            buffer[i + 1] = 255;
            buffer[i + 2] = 255;
            buffer[i + 3] = 255;
        }

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!code.GetModule(x, y)) continue;

                var startX = (x + quietZone) * moduleSize;
                var startY = (y + quietZone) * moduleSize;

                for (var dy = 0; dy < moduleSize; dy++)
                {
                    var rowStart = (startY + dy) * stride + startX * 4;
                    for (var dx = 0; dx < moduleSize; dx++)
                    {
                        var offset = rowStart + dx * 4;
                        buffer[offset] = 0;
                        buffer[offset + 1] = 0;
                        buffer[offset + 2] = 0;
                        buffer[offset + 3] = 255;
                    }
                }
            }
        }

        var bitmap = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>保存为 PNG，返回实际路径。</summary>
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

    /// <summary>构造默认文件名。</summary>
    public static string BuildFileName(DateTime now, string prefix = "QRCode")
        => $"{prefix}_{now:yyyyMMdd_HHmmss}.png";

    /// <summary>写入剪贴板（带重试）。</summary>
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

    /// <summary>根据文本估算模块尺寸，避免小内容生成过小图片。</summary>
    public static int SuggestModuleSize(QrCode code) => code.Size <= 25 ? 12 : code.Size <= 41 ? 10 : 8;
}
