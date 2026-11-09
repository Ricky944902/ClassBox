using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.ProcessManager.Views;

namespace ClassBox.Plugins.ProcessManager;

/// <summary>
/// 「进程管理」插件：列出进程名称、PID、内存、启动时间与路径，支持搜索、排序、自动刷新，
/// 结束进程前在页面内二次确认。
/// </summary>
public sealed class ProcessManagerPlugin : PluginBase
{
    internal const string PageId = "classbox.page.processmanager";

    private ProcessManagerView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.processmanager",
        Name = "进程管理",
        Description = "查看全部进程的名称、PID、内存占用、启动时间与可执行文件路径，支持搜索过滤、按内存排序与结束进程。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "系统工具",
        Glyph = "\uE9F5",
        IsBuiltIn = false
    };

    public override int SortOrder => 380;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = PageId,
            Title = Metadata.Name,
            Description = Metadata.Description,
            Glyph = Metadata.Glyph,
            Order = SortOrder,
            ViewFactory = () =>
            {
                var view = new ProcessManagerView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("进程管理页面已注册");
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        var view = _view;
        _view = null;
        if (view is not null)
        {
            try
            {
                if (view.Dispatcher.CheckAccess()) view.Shutdown();
                else view.Dispatcher.Invoke(view.Shutdown);
            }
            catch (Exception ex)
            {
                LogError("停止进程管理页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Info(message, "ProcessManager");
    }

    internal void LogWarn(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Warn(message, "ProcessManager");
    }

    internal void LogError(string message, Exception? exception = null)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Error(message, exception, "ProcessManager");
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
