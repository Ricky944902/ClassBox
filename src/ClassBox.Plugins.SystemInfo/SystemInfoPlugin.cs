using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.SystemInfo.Views;

namespace ClassBox.Plugins.SystemInfo;

/// <summary>
/// 「系统信息面板」插件：汇总操作系统、CPU、内存、磁盘、显卡、网络与开机时长，并支持一键复制。
/// </summary>
public sealed class SystemInfoPlugin : PluginBase
{
    internal const string PageId = "classbox.page.systeminfo";

    private SystemInfoView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.systeminfo",
        Name = "系统信息面板",
        Description = "一屏查看操作系统版本、CPU、内存、磁盘容量、显卡分辨率、网络地址与开机时长，并可一键复制全部信息。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "系统工具",
        Glyph = "\uE7F8",
        IsBuiltIn = false
    };

    public override int SortOrder => 390;

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
                var view = new SystemInfoView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("系统信息面板页面已注册");
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
                LogError("停止系统信息页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Info(message, "SystemInfo");
    }

    internal void LogWarn(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Warn(message, "SystemInfo");
    }

    internal void LogError(string message, Exception? exception = null)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Error(message, exception, "SystemInfo");
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
