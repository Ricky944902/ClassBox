using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.DiskAnalyzer.Views;

namespace ClassBox.Plugins.DiskAnalyzer;

/// <summary>
/// 「磁盘空间分析」插件：后台递归统计目录占用，列出占用最大的文件夹与文件。
/// </summary>
public sealed class DiskAnalyzerPlugin : PluginBase
{
    internal const string PageId = "classbox.page.diskanalyzer";

    private DiskAnalyzerView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.diskanalyzer",
        Name = "磁盘空间分析",
        Description = "选择任意目录，递归统计各子项占用并按大小排序，快速找出占满磁盘的文件夹与大文件。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "系统工具",
        Glyph = "\uEDA2",
        IsBuiltIn = false
    };

    public override int SortOrder => 370;

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
                var view = new DiskAnalyzerView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("磁盘空间分析页面已注册");
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
                LogError("关闭磁盘分析页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Info(message, "DiskAnalyzer");
    }

    internal void LogWarn(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Warn(message, "DiskAnalyzer");
    }

    internal void LogError(string message, Exception? exception = null)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Error(message, exception, "DiskAnalyzer");
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
