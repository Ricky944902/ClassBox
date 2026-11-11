using ClassBox.Abstractions.Services;

namespace ClassBox.Services;

/// <summary>应用级设置（持久化到 %AppData%\Class Box\config\settings.json）。</summary>
public sealed class AppSettings
{
    public bool IsDarkTheme { get; set; } = true;

    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Info;

    /// <summary>已启用插件 Id 列表。首次运行时默认启用两个内置插件。</summary>
    public List<string> EnabledPluginIds { get; set; } = new()
    {
        "classbox.builtin.topmost",
        "classbox.builtin.presence"
    };

    public bool ShowSideAlertSound { get; set; }

    /// <summary>是否在插件中心 / 侧边栏展示默认隐藏的插件（开关状态持久化）。</summary>
    public bool ShowHiddenPlugins { get; set; }
}

/// <summary>设置读写服务。</summary>
public sealed class SettingsService
{
    private readonly JsonConfigStore _store;
    private readonly IAppLogger _logger;

    public SettingsService(IAppLogger logger)
    {
        _logger = logger;
        _store = new JsonConfigStore(AppPaths.ConfigDirectory);
        Current = _store.Load("settings", new AppSettings());
    }

    public AppSettings Current { get; private set; }

    public void Save()
    {
        try
        {
            _store.Save("settings", Current);
        }
        catch (Exception ex)
        {
            _logger.Warn($"保存设置失败：{ex.Message}", "Settings");
        }
    }

    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        var list = Current.EnabledPluginIds;
        var exists = list.Any(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (enabled && !exists) list.Add(pluginId);
        if (!enabled && exists) list.RemoveAll(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));

        Save();
    }
}
