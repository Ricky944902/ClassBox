using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.RegexTester.Views;

namespace ClassBox.Plugins.RegexTester;

/// <summary>
/// 「正则测试器」插件：输入正则与待测文本，列出全部匹配（序号/位置/长度/值）
/// 与命名分组，支持替换预览、常用模板库，并带 2 秒超时保护。
/// </summary>
public sealed class RegexTesterPlugin : PluginBase
{
    internal const string PageId = "classbox.page.regextester";

    private RegexTesterView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.regextester",
        Name = "正则测试器",
        Description = "实时测试正则表达式：列出全部匹配的位置、长度与命名分组，替换预览、常用模板一键填入，2 秒超时防止灾难性回溯。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "开发工具",
        Glyph = "\uE71C",
        IsBuiltIn = false
    };

    public override int SortOrder => 420;

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
                var view = new RegexTesterView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("正则测试器页面已注册");
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
                LogError("关闭正则测试器页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "RegexTester");

    internal void LogWarn(string message) => TryGetContext()?.Logger.Warn(message, "RegexTester");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "RegexTester");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
