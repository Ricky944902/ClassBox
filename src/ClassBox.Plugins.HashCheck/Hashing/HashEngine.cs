using System.Diagnostics;
using System.Security.Cryptography;

namespace ClassBox.Plugins.HashCheck.Hashing;

/// <summary>单个文件的哈希计算结果。</summary>
public sealed class HashResult
{
    public string FilePath { get; init; } = string.Empty;

    public long FileSize { get; init; }

    public long ElapsedMs { get; init; }

    public string Md5 { get; init; } = string.Empty;

    public string Sha1 { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string Sha512 { get; init; } = string.Empty;

    public string SizeText => HashEngine.FormatSize(FileSize);

    public string ElapsedText => HashEngine.FormatDuration(ElapsedMs);
}

/// <summary>哈希比对结果。</summary>
public sealed class HashCompareResult
{
    public bool Matched { get; init; }

    public string Algorithm { get; init; } = "未知";

    public string Expected { get; init; } = string.Empty;

    /// <summary>命中的实际值；未命中时为对应算法计算出的值。</summary>
    public string Actual { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

/// <summary>基于 .NET 内置 <see cref="IncrementalHash"/> 的多算法单遍哈希引擎。</summary>
public static class HashEngine
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// 单次读取文件同时计算 MD5 / SHA1 / SHA256 / SHA512，避免反复读盘。
    /// </summary>
    public static async Task<HashResult> ComputeAsync(
        string filePath,
        IProgress<double>? progress,
        CancellationToken token)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("文件不存在", filePath);

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var total = stream.Length;
        var buffer = new byte[BufferSize];
        long processed = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = -1d;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read <= 0) break;

            md5.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
            sha512.AppendData(buffer, 0, read);

            processed += read;
            if (progress is not null && total > 0)
            {
                var percent = processed * 100d / total;
                if (percent - lastReport >= 1)
                {
                    lastReport = percent;
                    progress.Report(percent);
                }
            }
        }

        progress?.Report(100);
        stopwatch.Stop();

        return new HashResult
        {
            FilePath = filePath,
            FileSize = total,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Md5 = ToHex(md5.GetHashAndReset()),
            Sha1 = ToHex(sha1.GetHashAndReset()),
            Sha256 = ToHex(sha256.GetHashAndReset()),
            Sha512 = ToHex(sha512.GetHashAndReset())
        };
    }

    /// <summary>
    /// 依据十六进制长度识别算法，并把期望值与计算结果比对（忽略大小写与空白）。
    /// </summary>
    public static HashCompareResult Compare(HashResult result, string expectedInput)
    {
        var expected = Normalize(expectedInput);
        if (expected.Length == 0)
        {
            return new HashCompareResult { Matched = false, Message = "请先粘贴期望的哈希值" };
        }

        if (!IsHex(expected))
        {
            return new HashCompareResult
            {
                Matched = false,
                Expected = expected,
                Message = "期望值包含非十六进制字符，请检查是否复制完整"
            };
        }

        var (algorithm, actual) = expected.Length switch
        {
            32 => ("MD5", result.Md5),
            40 => ("SHA1", result.Sha1),
            64 => ("SHA256", result.Sha256),
            128 => ("SHA512", result.Sha512),
            _ => (string.Empty, string.Empty)
        };

        if (algorithm.Length == 0)
        {
            return new HashCompareResult
            {
                Matched = false,
                Expected = expected,
                Message = $"长度 {expected.Length} 不符合 MD5(32)/SHA1(40)/SHA256(64)/SHA512(128) 任一格式"
            };
        }

        var matched = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        return new HashCompareResult
        {
            Matched = matched,
            Algorithm = algorithm,
            Expected = expected,
            Actual = actual,
            Message = matched
                ? $"校验通过：{algorithm} 与期望值完全一致"
                : $"校验不通过：{algorithm} 实际值与期望值不同（文件可能已损坏或被修改）"
        };
    }

    /// <summary>MD5 与 SHA1 之外的常见文本摘要格式（大写/空格/连字符）清洗。</summary>
    public static string Normalize(string? input) =>
        (input ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

    private static bool IsHex(string text) => text.All(c => Uri.IsHexDigit(c));

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>把字节数格式化为易读文本。</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>把毫秒格式化为易读耗时。</summary>
    public static string FormatDuration(long milliseconds) =>
        milliseconds < 1000
            ? $"{milliseconds} ms"
            : $"{milliseconds / 1000.0:0.00} s";
}
