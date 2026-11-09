using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.JsonTool.Views;

namespace ClassBox.Plugins.JsonTool;

/// <summary>
/// 「JSON 工具」插件：把原始 JSON 文本格式化（2/4 空格或 Tab 缩进）、压缩、
/// 校验并定位错误行列、按键名排序、转义/反转义，以及生成 C# 类骨架。
/// </summary>
public sealed class JsonToolPlugin : PluginBase
{
    internal const string PageId = "classbox.page.jsontool";

    private JsonToolView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.jsontool",
        Name = "JSON 工具",
        Description = "左右双栏的 JSON 处理台：格式化（2/4 空格、Tab）、压缩、语法校验并定位错误行列、键排序、转义反转义，一键生成 C# 类骨架。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "开发工具",
        Glyph = "\uE943",
        IsBuiltIn = false
    };

    public override int SortOrder => 400;

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
                var view = new JsonToolView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("JSON 工具页面已注册");
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
                LogError("关闭 JSON 工具页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "JsonTool");

    internal void LogWarn(string message) => TryGetContext()?.Logger.Warn(message, "JsonTool");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "JsonTool");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
