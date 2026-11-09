using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.WebBrowser.Views;

namespace ClassBox.Plugins.WebBrowser;

/// <summary>
/// 简易浏览器插件：使用 WPF 内置 WebBrowser 控件（无第三方依赖）提供地址栏导航、
/// 前进 / 后退 / 刷新 / 主页、关键词自动补全为必应搜索、外部浏览器打开。
/// </summary>
public sealed class WebBrowserPlugin : PluginBase
{
    internal const string PageId = "classbox.page.webbrowser";

    private WebBrowserView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.webbrowser",
        Name = "简易浏览器",
        Description = "内置网页浏览器：地址栏直接输入网址或关键词，自动补全为必应搜索，支持前进后退刷新与外部浏览器打开。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "网络通讯",
        Glyph = "\uE774",
        IsBuiltIn = false
    };

    public override int SortOrder => 330;

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
                var view = new WebBrowserView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("简易浏览器页面已注册");
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
                LogError("关闭简易浏览器页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Info(message, "WebBrowser");
    }

    internal void LogError(string message, Exception? exception = null)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Error(message, exception, "WebBrowser");
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
