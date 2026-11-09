using System.Diagnostics;

namespace ClassBox.Plugins.DiskAnalyzer.Analysis;

/// <summary>文件夹占用条目（按根目录下的直接子项聚合）。</summary>
public sealed class FolderUsage
{
    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public long Size { get; init; }

    public int FileCount { get; init; }

    /// <summary>该文件夹占根目录总大小的百分比。</summary>
    public double Percent { get; init; }

    public string SizeText => FormatSize(Size);

    public string CountText => $"{FileCount} 个文件";

    internal static string FormatSize(long bytes) => SizeFormatter.Format(bytes);
}

/// <summary>文件占用条目。</summary>
public sealed class FileUsage
{
    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public long Size { get; init; }

    public string SizeText => SizeFormatter.Format(Size);
}

/// <summary>分析进度快照。</summary>
public sealed class AnalyzeProgress
{
    public string CurrentDirectory { get; init; } = string.Empty;

    public int FileCount { get; init; }

    public long TotalSize { get; init; }
}

/// <summary>分析最终结果。</summary>
public sealed class DiskAnalyzeResult
{
    public string RootPath { get; init; } = string.Empty;

    public long TotalSize { get; init; }

    public int FileCount { get; init; }

    public int FolderCount { get; init; }

    public int SkippedCount { get; init; }

    public long ElapsedMs { get; init; }

    public IReadOnlyList<FolderUsage> Folders { get; init; } = Array.Empty<FolderUsage>();

    public IReadOnlyList<FileUsage> Files { get; init; } = Array.Empty<FileUsage>();

    public string TotalSizeText => SizeFormatter.Format(TotalSize);

    public string ElapsedText => SizeFormatter.FormatDuration(ElapsedMs);
}

/// <summary>字节数 / 耗时格式化工具。</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {Units[unit]}";
    }

    public static string FormatDuration(long milliseconds) =>
        milliseconds < 1000 ? $"{milliseconds} ms" : $"{milliseconds / 1000.0:0.00} s";
}

/// <summary>递归统计目录占用的引擎（只读扫描，不修改任何文件）。</summary>
public static class DiskAnalyzerEngine
{
    private const int MaxTopFiles = 300;
    private const int ProgressInterval = 400;

    /// <summary>
    /// 递归遍历 <paramref name="root"/>，聚合根目录下每个直接子文件夹的占用，并收集最大的若干个文件。
    /// 无法访问的目录会被跳过并计入 <see cref="DiskAnalyzeResult.SkippedCount"/>。
    /// </summary>
    public static Task<DiskAnalyzeResult> AnalyzeAsync(
        string root,
        int topFolders,
        IProgress<AnalyzeProgress>? progress,
        CancellationToken token) =>
        Task.Run(() => Analyze(root, topFolders, progress, token), token);

    private static DiskAnalyzeResult Analyze(
        string root,
        int topFolders,
        IProgress<AnalyzeProgress>? progress,
        CancellationToken token)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"目录不存在：{root}");

        var stopwatch = Stopwatch.StartNew();
        var rootInfo = new DirectoryInfo(root);

        var folderSizes = new Dictionary<string, (long Size, int Files)>(StringComparer.OrdinalIgnoreCase);
        var folderPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var topFiles = new List<FileUsage>(MaxTopFiles + 32);

        long totalSize = 0;
        var fileCount = 0;
        var folderCount = 0;
        var skipped = 0;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var current = stack.Pop();
            folderCount++;

            if (folderCount % 40 == 0)
            {
                progress?.Report(new AnalyzeProgress
                {
                    CurrentDirectory = current,
                    FileCount = fileCount,
                    TotalSize = totalSize
                });
            }

            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(current);
                dirs = Directory.EnumerateDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped++;
                continue;
            }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;

                    var size = info.Length;
                    totalSize += size;
                    fileCount++;

                    var key = ResolveTopKey(rootInfo.FullName, file);
                    folderPaths[key] = key.Length == 0 ? rootInfo.FullName : key;
                    folderSizes[key] = folderSizes.TryGetValue(key, out var existing)
                        ? (existing.Size + size, existing.Files + 1)
                        : (size, 1);

                    CollectTopFile(topFiles, new FileUsage
                    {
                        Name = info.Name,
                        Path = info.FullName,
                        Size = size
                    });

                    if (fileCount % ProgressInterval == 0)
                    {
                        progress?.Report(new AnalyzeProgress
                        {
                            CurrentDirectory = current,
                            FileCount = fileCount,
                            TotalSize = totalSize
                        });
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    skipped++;
                }
                catch (TokenCanceled)
                {
                    throw;
                }
            }

            try
            {
                foreach (var dir in dirs) stack.Push(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped++;
            }
        }

        token.ThrowIfCancellationRequested();
        stopwatch.Stop();

        var folders = folderSizes
            .OrderByDescending(kv => kv.Value.Size)
            .Take(topFolders <= 0 ? 10 : topFolders)
            .Select(kv => new FolderUsage
            {
                Name = kv.Key.Length == 0 ? "（根目录直接包含的文件）" : Path.GetFileName(kv.Key.TrimEnd(Path.DirectorySeparatorChar)),
                Path = folderPaths.TryGetValue(kv.Key, out var path) ? path : kv.Key,
                Size = kv.Value.Size,
                FileCount = kv.Value.Files,
                Percent = totalSize > 0 ? kv.Value.Size * 100d / totalSize : 0d
            })
            .ToList();

        var files2 = topFiles.OrderByDescending(f => f.Size).Take(topFolders <= 0 ? 10 : topFolders).ToList();

        return new DiskAnalyzeResult
        {
            RootPath = rootInfo.FullName,
            TotalSize = totalSize,
            FileCount = fileCount,
            FolderCount = folderCount,
            SkippedCount = skipped,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Folders = folders,
            Files = files2
        };
    }

    /// <summary>取文件所在的一级子项路径；直接在根目录下的文件返回空串。</summary>
    private static string ResolveTopKey(string rootFullPath, string filePath)
    {
        var root = rootFullPath.TrimEnd(Path.DirectorySeparatorChar);
        if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return root;

        var relative = filePath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
        var index = relative.IndexOf(Path.DirectorySeparatorChar);
        if (index <= 0) return string.Empty; // 根目录下的文件

        var first = relative[..index];
        return Path.Combine(root, first);
    }

    /// <summary>维护"最大的若干文件"列表，避免把整棵树的所有文件都留在内存。</summary>
    private static void CollectTopFile(List<FileUsage> list, FileUsage candidate)
    {
        if (list.Count < MaxTopFiles)
        {
            list.Add(candidate);
            return;
        }

        var min = list.Min(f => f.Size);
        if (candidate.Size <= min) return;

        var index = list.FindIndex(f => f.Size == min);
        if (index >= 0) list[index] = candidate;
    }

    private sealed class TokenCanceled : Exception
    {
    }
}
