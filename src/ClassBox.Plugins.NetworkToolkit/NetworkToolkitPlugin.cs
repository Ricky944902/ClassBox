using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.NetworkToolkit.Views;

namespace ClassBox.Plugins.NetworkToolkit;

/// <summary>插件持久化设置。</summary>
public sealed class NetworkSettings
{
    public string PingHost { get; set; } = "127.0.0.1";

    public int PingCount { get; set; } = 4;

    public string DnsQuery { get; set; } = "www.microsoft.com";

    public string PortQuery { get; set; } = "443";
}

/// <summary>「网络工具箱」插件：网卡信息、Ping、DNS 解析、端口占用与公网 IP 查询。</summary>
public sealed class NetworkToolkitPlugin : PluginBase
{
    internal const string PageId = "classbox.page.networktoolkit";
    private const string ConfigKey = "network-settings";

    private NetworkSettings _settings = new();
    private NetworkToolkitView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.networktoolkit",
        Name = "网络工具箱",
        Description = "查询本机 IP / 网关 / DNS / MAC、Ping 连通性、DNS 解析、端口占用与公网出口 IP。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "网络通讯",
        Glyph = "\uE968",
        IsBuiltIn = false
    };

    public override int SortOrder => 520;

    internal IPluginContext Host => Context;

    internal NetworkSettings Settings => _settings;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        var context = Context;
        _settings = context.Configuration.Load(ConfigKey, new NetworkSettings());

        context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = PageId,
            Title = Metadata.Name,
            Description = Metadata.Description,
            Glyph = Metadata.Glyph,
            Order = SortOrder,
            ViewFactory = () =>
            {
                var view = new NetworkToolkitView(this);
                _view = view;
                return view;
            }
        });

        context.Logger.Info("网络工具箱已就绪", "NetworkToolkit");
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        var view = _view;
        _view = null;
        try
        {
            view?.Shutdown();
        }
        catch (Exception ex)
        {
            LogError("关闭网络工具箱页面失败", ex);
        }

        return Task.CompletedTask;
    }

    internal void PersistSettings()
    {
        try
        {
            Context.Configuration.Save(ConfigKey, _settings);
        }
        catch (Exception ex)
        {
            LogError("保存网络工具箱设置失败", ex);
        }
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "NetworkToolkit");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "NetworkToolkit");

    internal void Notify(string title, string message)
    {
        try
        {
            TryGetContext()?.Alerts.Show(new SideAlertRequest
            {
                Title = title,
                Message = message,
                Glyph = "\uE968",
                AutoCloseSeconds = 3
            });
        }
        catch (Exception ex)
        {
            LogError("侧边提示显示失败", ex);
        }
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
