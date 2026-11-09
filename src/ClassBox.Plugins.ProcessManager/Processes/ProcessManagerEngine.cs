namespace ClassBox.Plugins.ProcessManager.Processes;

/// <summary>进程枚举与结束操作的引擎封装（全部基于 <see cref="System.Diagnostics.Process"/>）。</summary>
public static class ProcessManagerEngine
{
    /// <summary>关键系统进程名单：结束它们会导致系统不稳定，因此禁止操作。</summary>
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "csrss", "wininit", "winlogon",
        "services", "lsass", "smss", "svchost", "dwm", "fontdrvhost", "sihost"
    };

    /// <summary>枚举当前全部进程，读取失败的字段以占位文本代替，不抛异常。</summary>
    public static Task<ProcessSnapshot> CaptureAsync(CancellationToken token) => Task.Run(() =>
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var items = new List<ProcessItem>(256);
        long totalMemory = 0;
        var skipped = 0;
        var currentPid = Environment.ProcessId;

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var pid = process.Id;
                var name = SafeGet(() => process.ProcessName, $"进程 {pid}");

                string startTime;
                try
                {
                    startTime = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
                }
                catch (Exception)
                {
                    startTime = "无权限读取";
                }

                string path;
                try
                {
                    path = process.MainModule?.FileName ?? "无法获取";
                }
                catch (Exception)
                {
                    path = "无权限读取";
                }

                string title;
                try
                {
                    title = process.MainWindowTitle ?? string.Empty;
                }
                catch (Exception)
                {
                    title = string.Empty;
                }

                long memory = 0;
                try
                {
                    memory = process.WorkingSet64;
                }
                catch (Exception)
                {
                    memory = 0;
                }

                totalMemory += memory;

                items.Add(new ProcessItem
                {
                    Pid = pid,
                    Name = name,
                    StartTimeText = startTime,
                    Path = path,
                    Title = title,
                    IsProtected = IsProtected(pid, name, currentPid),
                    MemoryBytes = memory
                });
            }
            catch (Exception)
            {
                skipped++;
            }
            finally
            {
                process.Dispose();
            }
        }

        stopwatch.Stop();

        return new ProcessSnapshot
        {
            Items = items,
            TotalMemoryBytes = totalMemory,
            SkippedCount = skipped,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }, token);

    /// <summary>结束指定 PID 的进程，返回面向用户的结果说明。</summary>
    public static async Task<(bool Success, string Message)> KillAsync(ProcessItem item)
    {
        if (item.IsProtected)
        {
            return (false, $"「{item.Name}」是系统关键进程或宿主自身，已拒绝结束操作。");
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(item.Pid);
            process.Kill();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return (true, $"已结束进程「{item.Name}」(PID {item.Pid})。");
        }
        catch (ArgumentException)
        {
            return (false, $"进程「{item.Name}」(PID {item.Pid}) 已不存在。");
        }
        catch (OperationCanceledException)
        {
            return (false, $"结束进程「{item.Name}」超时，可能仍在退出过程中。");
        }
        catch (Exception ex)
        {
            return (false, $"结束进程「{item.Name}」失败：{ex.Message}（通常需要管理员权限）");
        }
    }

    /// <summary>判断是否为不可结束的进程。</summary>
    public static bool IsProtected(int pid, string name, int currentPid) =>
        pid <= 4 ||
        pid == currentPid ||
        CriticalNames.Contains(name);

    /// <summary>字节数格式化。</summary>
    public static string FormatMemory(long bytes)
    {
        if (bytes <= 0) return "0 MB";

        double mb = bytes / 1024d / 1024d;
        return mb >= 1024 ? $"{mb / 1024:0.00} GB" : $"{mb:0.0} MB";
    }

    private static string SafeGet(Func<string> getter, string fallback)
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
