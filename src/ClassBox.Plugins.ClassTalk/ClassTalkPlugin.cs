using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.ClassTalk.Views;

namespace ClassBox.Plugins.ClassTalk;

/// <summary>
/// ClassTalk 插件：把原独立版局域网加密交流软件（.NET8/WPF，LRNC-1024-AEAD）
/// 完整移植为 Class Box 插件页面，保留加密算法、局域网发现、聊天服务器与客户端、
/// 管理员控制台、消息收发与记录等全部功能。窗口置顶 / 悬浮球由宿主统一提供。
/// </summary>
public sealed class ClassTalkPlugin : PluginBase
{
    internal const string PageId = "classbox.page.classtalk";

    private ClassTalkView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.classtalk",
        Name = "ClassTalk 局域网加密交流",
        Description = "LRNC-1024-AEAD 端到端加密的局域网聊天室：主机/加入双模式、局域网 IP 自动探测与防火墙放行、"
                    + "消息收发与记录、明文通道、管理员控制台与远程命令执行。",
        Author = "Class Box",
        Version = new Version(1, 3, 0),
        Category = "网络通讯",
        Glyph = "\uE8BD",
        IsBuiltIn = false
    };

    public override int SortOrder => 320;

    /// <summary>供页面调用的宿主上下文。</summary>
    internal IPluginContext Host => Context;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = PageId,
            Title = "ClassTalk 加密交流",
            Description = "局域网端到端加密聊天与管理员命令台",
            Glyph = "\uE8BD",
            Order = SortOrder,
            ViewFactory = () =>
            {
                var view = new ClassTalkView(this);
                _view = view;
                return view;
            }
        });

        Context.Logger.Info("ClassTalk 插件已初始化（LRNC-1024-AEAD）", "ClassTalk");
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
                Context.Logger.Warn($"关闭 ClassTalk 页面时出现异常：{ex.Message}", "ClassTalk");
            }
        }

        return Task.CompletedTask;
    }

    internal void LogError(string message, Exception? exception = null)
    {
        // Host 尚未注入（如页面提前构造/宿主异常路径）时静默降级，避免 UI 线程抛 NRE
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Error(message, exception, "ClassTalk");
    }

    internal void LogInfo(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Info(message, "ClassTalk");
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
