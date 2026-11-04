using ClassBox.Plugins.PresenceMonitor.Camera;

namespace ClassBox.Plugins.PresenceMonitor.Detection;

/// <summary>单次检测结果。</summary>
public sealed class DetectionResult
{
    public bool PersonDetected { get; init; }

    /// <summary>整幅画面的肤色占比。</summary>
    public double SkinRatio { get; init; }

    /// <summary>中央区域肤色占比（人像通常位于画面中央）。</summary>
    public double CenterRatio { get; init; }

    /// <summary>置信度 0~1。</summary>
    public double Confidence { get; init; }

    public int SampledPixels { get; init; }
}

/// <summary>
/// 轻量人像检测：基于 YCbCr 肤色模型统计画面中的肤色像素比例。
/// 不依赖第三方视觉库，适合"是否有人坐在电脑前"这类存在性判断。
/// </summary>
public static class SkinToneDetector
{
    private const int Step = 4;

    public static DetectionResult Detect(CameraFrame frame, double sensitivity)
    {
        var width = frame.Width;
        var height = frame.Height;
        if (width <= 0 || height <= 0) return new DetectionResult();

        var total = 0;
        var skin = 0;
        var centerTotal = 0;
        var centerSkin = 0;

        var centerLeft = (int)(width * 0.22);
        var centerRight = (int)(width * 0.78);
        var centerTop = (int)(height * 0.12);
        var centerBottom = (int)(height * 0.78);

        for (var y = 0; y < height; y += Step)
        {
            for (var x = 0; x < width; x += Step)
            {
                var (b, g, r) = frame.GetPixel(x, y);
                var isSkin = IsSkin(r, g, b);

                total++;
                if (isSkin) skin++;

                if (x >= centerLeft && x <= centerRight && y >= centerTop && y <= centerBottom)
                {
                    centerTotal++;
                    if (isSkin) centerSkin++;
                }
            }
        }

        if (total == 0) return new DetectionResult();

        var skinRatio = (double)skin / total;
        var centerRatio = centerTotal == 0 ? 0 : (double)centerSkin / centerTotal;

        // 灵敏度越高，判定阈值越低（默认 0.5 时约为 3.5%）。
        var threshold = 0.055 - (0.035 * Math.Clamp(sensitivity, 0, 1));

        var detected = skinRatio >= threshold && centerRatio >= threshold * 0.55;

        var confidence = threshold <= 0 ? 0 : Math.Clamp(centerRatio / threshold * 0.7 + skinRatio / threshold * 0.3, 0, 1);

        return new DetectionResult
        {
            PersonDetected = detected,
            SkinRatio = skinRatio,
            CenterRatio = centerRatio,
            Confidence = confidence,
            SampledPixels = total
        };
    }

    /// <summary>经典 YCbCr 肤色区间判定。</summary>
    private static bool IsSkin(byte r, byte g, byte b)
    {
        var cb = 128 - (0.168736 * r) - (0.331264 * g) + (0.5 * b);
        var cr = 128 + (0.5 * r) - (0.418688 * g) - (0.081312 * b);

        return cb is >= 77 and <= 127 && cr is >= 133 and <= 173;
    }
}
