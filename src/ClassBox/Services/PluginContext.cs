using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;

namespace ClassBox.Services;

/// <summary>注入给插件的宿主上下文实现。</summary>
public sealed class PluginContext : IPluginContext
{
    public PluginContext(string pluginId, string dataDirectory, IConfigStore configuration, IAppLogger logger,
        IWindowService windows, ISideAlertService alerts, PluginNavigationStore navigationStore)
    {
        PluginId = pluginId;
        DataDirectory = dataDirectory;
        Configuration = configuration;
        Logger = logger;
        Windows = windows;
        Alerts = alerts;
        Navigation = new PluginNavigation(navigationStore, () => pluginId);
    }

    public string PluginId { get; }

    public string DataDirectory { get; }

    public IConfigStore Configuration { get; }

    public IAppLogger Logger { get; }

    public IWindowService Windows { get; }

    public ISideAlertService Alerts { get; }

    public IPluginNavigation Navigation { get; }

    public bool IsDarkTheme => ThemeManager.IsDark;
}
