using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.PortScanner.Views;

namespace ClassBox.Plugins.PortScanner;

/// <summary>
/// 「端口扫描与连通检测」插件：对目标主机做 TCP Connect 端口扫描与 ICMP 连通检测，
/// 全部基于 .NET 内置 TcpClient / Ping 实现，无需任何第三方库。
/// </summary>
public sealed class PortScannerPlugin : PluginBase
{
    internal const string PageId = "classbox.page.portscanner";

    private PortScannerView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.portscanner",
        Name = "端口扫描与连通检测",
        Description = "输入目标 IP 或主机名即可探测哪些 TCP 端口开放，支持端口段与列表、超时与并发调节、Ping 连通检测，并可将结果导出为文本。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "网络通讯",
        Glyph = "\uE968",
        IsBuiltIn = false
    };

    public override int SortOrder => 350;

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
                var view = new PortScannerView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("端口扫描页面已注册");
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
                LogError("关闭端口扫描页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Info(message, "PortScanner");
    }

    internal void LogWarn(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Warn(message, "PortScanner");
    }

    internal void LogError(string message, Exception? exception = null)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Error(message, exception, "PortScanner");
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
