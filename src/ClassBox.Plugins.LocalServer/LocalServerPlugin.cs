using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.LocalServer.Server;
using ClassBox.Plugins.LocalServer.Views;

namespace ClassBox.Plugins.LocalServer;

/// <summary>
/// 「电脑变服务器」插件：在本机临时开启一个 HTTP 文件服务器，
/// 局域网内手机或其他电脑用浏览器即可浏览、下载共享目录文件。
/// </summary>
public sealed class LocalServerPlugin : PluginBase
{
    internal const string PageId = "classbox.page.localserver";

    private LocalServerView? _view;
    private HttpFileServer? _server;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.localserver",
        Name = "电脑变服务器",
        Description = "一键把本机目录变成局域网文件服务器：手机、平板或另一台电脑用浏览器打开地址即可浏览与下载文件，支持端口设置、并发限制与请求日志。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "网络通讯",
        Glyph = "\uE968",
        IsBuiltIn = false
    };

    public override int SortOrder => 340;

    /// <summary>供页面使用的宿主上下文。</summary>
    internal IPluginContext Host => Context;

    /// <summary>当前服务器实例（首次访问时创建）。</summary>
    internal HttpFileServer Server => _server ??= new HttpFileServer();

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
                var view = new LocalServerView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("电脑变服务器页面已注册");
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
                LogError("关闭服务器页面时出现异常", ex);
            }
        }

        try
        {
            _server?.Dispose();
        }
        catch (Exception ex)
        {
            LogError("停止 HTTP 服务时出现异常", ex);
        }

        _server = null;
        return Task.CompletedTask;
    }

    internal void LogInfo(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Info(message, "LocalServer");
    }

    internal void LogWarn(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Warn(message, "LocalServer");
    }

    internal void LogError(string message, Exception? exception = null)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Error(message, exception, "LocalServer");
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
