using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassBox.Abstractions.Services;
using ClassBox.Interop;

namespace ClassBox.Services;

/// <summary>
/// Win32 窗口能力实现。所有 P/Invoke 只出现在本类中。
/// 任何可能失败的操作都返回 bool 并附带可读错误，绝不抛异常给调用方。
/// </summary>
public sealed class WindowService : IWindowService
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        // 桌面 / 任务栏 / 开始菜单 / 任务视图等 shell 容器窗口：由 explorer.exe 持有，
        // 最小化它们只会让桌面与任务栏"消失"，因此按窗口类精确排除（不按进程名排除 explorer，
        // 否则 CabinetWClass 文件资源管理器窗口会被一并放弃，导致"键盘接管"无法最小化资源管理器）。
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Shell_InputSwitchTopLevelWindow", "MultitaskingViewFrame", "ForegroundStaging",
        "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow",
        "Windows.Internal.Shell.TabProxyWindow", "TopLevelWindowForOverflowXamlIsland",
        "ApplicationManager_DesktopShellWindow", "EdgeUiInputTopWndClass",
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow_Cloaked",
        "Button", "SysShadow"
    };

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClassBox", "SearchHost", "StartMenuExperienceHost",
        "TextInputHost", "ShellExperienceHost", "LockApp", "dwm"
    };

    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<IntPtr, EmbedState> _embeds = new();
    private readonly ConcurrentDictionary<IntPtr, System.Windows.Threading.DispatcherTimer> _topmostGuards = new();
    private readonly int _selfProcessId = Environment.ProcessId;

    public WindowService(IAppLogger logger)
    {
        _logger = logger;
        IsAdmin = DetectAdmin();
    }

    public bool IsAdmin { get; }

    public IReadOnlyList<WindowInfo> EnumerateWindows()
    {
        var result = new List<WindowInfo>();

        try
        {
            NativeMethods.EnumWindows((handle, _) =>
            {
                try
                {
                    var info = BuildWindowInfo(handle);
                    if (info is not null) result.Add(info);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"枚举窗口跳过：{ex.Message}");
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _logger.Error("枚举窗口失败", ex);
        }

        return result
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WindowInfo? TryGetWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle)) return null;
        return BuildWindowInfo(handle, requireVisible: false);
    }

    public bool IsWindowAlive(IntPtr handle)
        => handle != IntPtr.Zero && NativeMethods.IsWindow(handle) && !IsCloaked(handle);

    public bool SetTopmost(IntPtr handle, bool topmost, bool activate = false)
    {
        if (!NativeMethods.IsWindow(handle))
        {
            _logger.Warn($"置顶失败：窗口句柄无效 (0x{handle.ToInt64():X})", "Window");
            return false;
        }

        try
        {
            var flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE;
            if (!activate) flags |= NativeMethods.SWP_NOACTIVATE;

            var ok = NativeMethods.SetWindowPos(handle,
                topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0, flags);

            if (!ok)
            {
                _logger.Warn($"SetWindowPos 调用失败，句柄 0x{handle.ToInt64():X}（目标进程可能以更高权限运行）", "Window");
            }

            return ok;
        }
        catch (Exception ex)
        {
            _logger.Error("设置置顶状态时发生异常", ex);
            return false;
        }
    }

    /// <summary>
    /// 为窗口开启"强制置顶"守卫：每秒静默重申一次 HWND_TOPMOST（不抢焦点、不改大小位置），
    /// 避免被其它置顶窗口或全屏应用压到下层。依赖进程已提权（app.manifest 声明管理员）。
    /// </summary>
    public void StartTopmostGuard(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        try
        {
            _topmostGuards.AddOrUpdate(handle, CreateTopmostGuard, (_, existing) =>
            {
                if (!existing.IsEnabled) existing.Start();
                return existing;
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"启动置顶守卫失败：{ex.Message}", "Window");
        }
    }

    public void StopTopmostGuard(IntPtr handle)
    {
        if (_topmostGuards.TryRemove(handle, out var timer))
        {
            timer.Stop();
        }
    }

    private System.Windows.Threading.DispatcherTimer CreateTopmostGuard(IntPtr handle)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            System.Windows.Threading.Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        timer.Tick += (_, _) =>
        {
            if (!NativeMethods.IsWindow(handle))
            {
                StopTopmostGuard(handle);
                return;
            }

            NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        };

        timer.Start();
        return timer;
    }

    public bool EmbedWindow(IntPtr target, IntPtr container, out string? error)
    {
        error = null;

        if (!NativeMethods.IsWindow(target))
        {
            error = "目标窗口已关闭或句柄无效。";
            return false;
        }

        if (container == IntPtr.Zero)
        {
            error = "宿主容器尚未创建，请稍后重试。";
            return false;
        }

        if (_embeds.ContainsKey(target))
        {
            error = "该窗口已处于嵌入状态。";
            return false;
        }

        try
        {
            var originalStyle = NativeMethods.GetWindowLongPtr(target, NativeMethods.GWL_STYLE).ToInt64();
            var originalParent = NativeMethods.GetParent(target);

            NativeMethods.SetParent(target, container);
            if (NativeMethods.GetParent(target) != container)
            {
                error = "SetParent 被目标窗口拒绝（部分游戏、UWP 应用或受保护进程不允许重设父窗口）。";
                return false;
            }

            var style = (uint)originalStyle;
            style &= ~0x80000000u;  // WS_POPUP
            style &= ~0x00C00000u;  // WS_CAPTION
            style &= ~0x00040000u;  // WS_THICKFRAME
            style |= 0x40000000u;   // WS_CHILD
            style |= 0x10000000u;   // WS_VISIBLE

            NativeMethods.SetWindowLongPtr(target, NativeMethods.GWL_STYLE, new IntPtr(unchecked((int)style)));
            ResizeToContainer(target, container);
            NativeMethods.SetWindowPos(target, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            _embeds[target] = new EmbedState(originalStyle, originalParent, container);
            _logger.Info($"窗口已嵌入：0x{target.ToInt64():X}", "Window");
            return true;
        }
        catch (Exception ex)
        {
            error = $"嵌入失败：{ex.Message}";
            _logger.Error("嵌入窗口时发生异常", ex);
            return false;
        }
    }

    public void DetachWindow(IntPtr target)
    {
        if (!_embeds.TryRemove(target, out var state)) return;

        try
        {
            if (!NativeMethods.IsWindow(target)) return;

            NativeMethods.SetParent(target, state.OriginalParent);
            NativeMethods.SetWindowLongPtr(target, NativeMethods.GWL_STYLE, new IntPtr(state.OriginalStyle));
            NativeMethods.SetWindowPos(target, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            _logger.Info($"已解除窗口嵌入：0x{target.ToInt64():X}", "Window");
        }
        catch (Exception ex)
        {
            _logger.Warn($"解除嵌入失败：{ex.Message}", "Window");
        }
    }

    public bool IsEmbedded(IntPtr handle) => _embeds.ContainsKey(handle);

    public bool ResizeEmbedded(IntPtr target)
    {
        if (!_embeds.TryGetValue(target, out var state)) return false;
        if (!NativeMethods.IsWindow(target) || !NativeMethods.IsWindow(state.Container)) return false;

        return ResizeToContainer(target, state.Container);
    }

    /// <summary>退出前释放所有嵌入窗口，避免目标窗口"卡"在已销毁的宿主里。</summary>
    public void DetachAll()
    {
        foreach (var handle in _embeds.Keys.ToList())
        {
            DetachWindow(handle);
        }

        foreach (var handle in _topmostGuards.Keys.ToList())
        {
            StopTopmostGuard(handle);
        }
    }

    /// <summary>当前是否存在嵌入窗口。</summary>
    public int EmbeddedCount => _embeds.Count;

    public bool Minimize(IntPtr handle)
    {
        if (!NativeMethods.IsWindow(handle)) return false;
        if (NativeMethods.IsIconic(handle)) return true;

        // ① 同步最小化且不激活目标窗口：不抢焦点，也不会把目标窗口先提到前台
        if (NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWMINNOACTIVE) && NativeMethods.IsIconic(handle))
        {
            return true;
        }

        // ② 兜底：目标窗口（资源管理器、浏览器等）消息循环繁忙时，同步 ShowWindow 可能被忽略，
        //    改为投递异步消息并短暂等待，避免热键线程被目标进程阻塞。
        NativeMethods.ShowWindowAsync(handle, NativeMethods.SW_SHOWMINNOACTIVE);
        for (var i = 0; i < 10; i++)
        {
            if (NativeMethods.IsIconic(handle)) return true;
            System.Threading.Thread.Sleep(20);
        }

        _logger.Warn($"最小化窗口失败：句柄 0x{handle.ToInt64():X}（目标窗口可能以更高完整性级别运行或被系统保护）", "Window");
        return false;
    }

    public bool Restore(IntPtr handle)
    {
        if (!NativeMethods.IsWindow(handle)) return false;
        return NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
    }

    public int MinimizeAll()
    {
        var count = 0;
        foreach (var window in EnumerateWindows())
        {
            if (NativeMethods.IsIconic(window.Handle)) continue;
            if (Minimize(window.Handle)) count++;
        }

        _logger.Info($"已最小化 {count} 个窗口", "Window");
        return count;
    }

    public int ShowOnlyProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return MinimizeAll();

        var count = 0;
        foreach (var window in EnumerateWindows())
        {
            if (string.Equals(window.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                if (NativeMethods.IsIconic(window.Handle))
                {
                    NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_RESTORE);
                }

                NativeMethods.SetForegroundWindow(window.Handle);
            }
            else
            {
                if (Minimize(window.Handle)) count++;
            }
        }

        _logger.Info($"仅显示 {processName}，最小化其它 {count} 个窗口", "Window");
        return count;
    }

    public IReadOnlyList<string> GetVisibleProcessNames()
        => EnumerateWindows()
            .Select(w => w.ProcessName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool ResizeToContainer(IntPtr target, IntPtr container)
    {
        if (!NativeMethods.GetClientRect(container, out var rect)) return false;

        var flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED;
        return NativeMethods.SetWindowPos(target, IntPtr.Zero, 0, 0,
            Math.Max(rect.Width, 1), Math.Max(rect.Height, 1), flags);
    }

    private WindowInfo? BuildWindowInfo(IntPtr handle, bool requireVisible = true)
    {
        if (requireVisible && !NativeMethods.IsWindowVisible(handle)) return null;
        if (IsCloaked(handle)) return null;

        var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        var isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

        var className = GetClassName(handle);
        if (IgnoredClasses.Contains(className)) return null;

        var title = GetTitle(handle);
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (requireVisible && isToolWindow) return null;

        NativeMethods.GetWindowThreadProcessId(handle, out var pid);
        if ((int)pid == _selfProcessId) return null;

        string processName;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch
        {
            processName = "unknown";
        }

        if (IgnoredProcesses.Contains(processName)) return null;

        return new WindowInfo
        {
            Handle = handle,
            Title = title,
            ProcessName = processName,
            ProcessId = (int)pid,
            IsTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0,
            IsAdmin = IsAdminProcess((int)pid),
            Icon = TryGetIcon(handle)
        };
    }

    private static string GetTitle(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLengthW(handle);
        if (length <= 0) return string.Empty;

        var builder = new StringBuilder(length + 2);
        var copied = NativeMethods.GetWindowTextW(handle, builder, builder.Capacity);
        return copied <= 0 ? string.Empty : builder.ToString();
    }

    private static string GetClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        var copied = NativeMethods.GetClassNameW(handle, builder, builder.Capacity);
        return copied <= 0 ? string.Empty : builder.ToString();
    }

    private static bool IsCloaked(IntPtr handle)
    {
        try
        {
            var hr = NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static ImageSource? TryGetIcon(IntPtr handle)
    {
        try
        {
            var hIcon = IntPtr.Zero;

            foreach (var size in new[] { NativeMethods.ICON_BIG, NativeMethods.ICON_SMALL2, NativeMethods.ICON_SMALL })
            {
                if (NativeMethods.SendMessageTimeout(handle, NativeMethods.WM_GETICON, new IntPtr(size), IntPtr.Zero,
                        NativeMethods.SMTO_ABORTIFHUNG, 200, out var result) != IntPtr.Zero && result != IntPtr.Zero)
                {
                    hIcon = result;
                    break;
                }
            }

            if (hIcon == IntPtr.Zero)
            {
                hIcon = NativeMethods.GetClassLongPtr(handle, NativeMethods.GCLP_HICON);
            }

            if (hIcon == IntPtr.Zero)
            {
                hIcon = NativeMethods.GetClassLongPtr(handle, NativeMethods.GCLP_HICONSM);
            }

            if (hIcon == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAdminProcess(int pid)
    {
        // 跨进程查询完整性级别需要 OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + 令牌解析，
        // 对普通窗口操作并非必需，这里保守返回 false，避免引入额外权限风险。
        _ = pid;
        return false;
    }

    private static bool DetectAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private sealed record EmbedState(long OriginalStyle, IntPtr OriginalParent, IntPtr Container);
}
