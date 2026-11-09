using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.TodoPlanner.Views;

namespace ClassBox.Plugins.TodoPlanner;

/// <summary>
/// 「待办清单」插件：新增/编辑/删除/勾选待办，支持优先级、截止日期、标签，
/// 按状态与优先级筛选排序，数据持久化到插件数据目录的 JSON 文件。
/// </summary>
public sealed class TodoPlannerPlugin : PluginBase
{
    internal const string PageId = "classbox.page.todoplanner";

    private TodoPlannerView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.todoplanner",
        Name = "待办清单",
        Description = "支持优先级、截止日期与标签的待办管理，可按状态筛选排序，未完成与逾期数量一目了然，数据保存在本地。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "效率工具",
        Glyph = "\uE9D5",
        IsBuiltIn = false
    };

    public override int SortOrder => 500;

    /// <summary>供页面使用的宿主上下文。</summary>
    internal IPluginContext Host => Context;

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
                var view = new TodoPlannerView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("待办清单页面已注册");
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
                LogError("关闭待办清单页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "TodoPlanner");

    internal void LogWarn(string message) => TryGetContext()?.Logger.Warn(message, "TodoPlanner");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "TodoPlanner");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
