using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;

namespace ClassBox.Services;

/// <summary>已加载插件的运行时包装（含启用状态与错误信息）。</summary>
public sealed class PluginRuntime : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isEnabled;
    private string? _error;

    public PluginRuntime(IPlugin plugin)
    {
        Plugin = plugin;
    }

    public IPlugin Plugin { get; }

    public PluginMetadata Metadata => Plugin.Metadata;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            Raise(nameof(IsEnabled));
        }
    }

    public bool IsRunning => Plugin.IsRunning;

    public string? Error
    {
        get => _error;
        set
        {
            _error = value;
            Raise(nameof(Error));
            Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_error);

    public string StatusText => Error is not null ? "异常" : IsEnabled ? (IsRunning ? "运行中" : "已启用") : "已禁用";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void RefreshStatus()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(StatusText));
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>插件页面注册项。</summary>
public sealed class PluginPageEntry
{
    public required string PluginId { get; init; }
    public required PluginPageDescriptor Descriptor { get; init; }
}

/// <summary>
/// 插件导航注册表。页面在插件初始化时注册，导航列表按"插件是否启用"过滤显示，
/// 因此禁用插件无需卸载程序集，重新启用可立即恢复入口。
/// </summary>
public sealed class PluginNavigationStore
{
    private readonly Dictionary<string, PluginPageEntry> _pages = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    /// <summary>插件请求宿主切换页面。</summary>
    public event Action<string>? NavigateRequested;

    internal void RequestNavigate(string pageId) => NavigateRequested?.Invoke(pageId);

    public IReadOnlyCollection<PluginPageEntry> Pages
    {
        get
        {
            lock (_pages)
            {
                return _pages.Values.ToList();
            }
        }
    }

    /// <summary>通知导航集合发生变化（供管理器在启用/禁用插件后刷新）。</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Register(PluginPageEntry entry)
    {
        lock (_pages)
        {
            _pages[entry.Descriptor.PageId] = entry;
        }

        NotifyChanged();
    }

    public void Unregister(string pageId)
    {
        lock (_pages)
        {
            _pages.Remove(pageId);
        }

        NotifyChanged();
    }

    public void RemoveByPlugin(string pluginId)
    {
        lock (_pages)
        {
            foreach (var key in _pages.Where(p => p.Value.PluginId == pluginId).Select(p => p.Key).ToList())
            {
                _pages.Remove(key);
            }
        }

        NotifyChanged();
    }
}

/// <summary>绑定到某个插件的导航代理。</summary>
public sealed class PluginNavigation : IPluginNavigation
{
    private readonly PluginNavigationStore _store;
    private readonly Func<string> _pluginIdProvider;

    public PluginNavigation(PluginNavigationStore store, Func<string> pluginIdProvider)
    {
        _store = store;
        _pluginIdProvider = pluginIdProvider;
    }

    public void RegisterPage(PluginPageDescriptor page)
        => _store.Register(new PluginPageEntry { PluginId = _pluginIdProvider(), Descriptor = page });

    public void RemovePage(string pageId) => _store.Unregister(pageId);

    public void NavigateTo(string pageId) => _store.RequestNavigate(pageId);
}

/// <summary>
/// 插件管理器：扫描 Plugins 目录、通过反射发现 IPlugin 实现、调用生命周期方法。
/// </summary>
public sealed class PluginManager
{
    private readonly IAppLogger _logger;
    private readonly IWindowService _windowService;
    private readonly ISideAlertService _alertService;
    private readonly PluginNavigationStore _navigation;
    private readonly List<PluginRuntime> _plugins = new();
    private readonly HashSet<string> _resolvingDirectories = new(StringComparer.OrdinalIgnoreCase);

    public PluginManager(IAppLogger logger, IWindowService windowService, ISideAlertService alertService,
        PluginNavigationStore navigation)
    {
        _logger = logger;
        _windowService = windowService;
        _alertService = alertService;
        _navigation = navigation;

        AssemblyLoadContext.Default.Resolving += OnResolvingDependency;
    }

    public ReadOnlyCollection<PluginRuntime> Plugins => _plugins.AsReadOnly();

    public string PluginsDirectory => AppPaths.PluginsDirectory;

    /// <summary>扫描并加载全部插件，同时根据用户设置启动已启用插件。</summary>
    public async Task LoadAllAsync(IEnumerable<string> enabledIds)
    {
        var enabled = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(AppPaths.PluginsDirectory))
        {
            _logger.Warn($"插件目录不存在：{AppPaths.PluginsDirectory}", "Plugin");
            return;
        }

        _resolvingDirectories.Add(AppPaths.PluginsDirectory);

        var dlls = Directory.GetFiles(AppPaths.PluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(d => !Path.GetFileName(d).StartsWith("ClassBox.Abstractions", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.Info($"发现 {dlls.Count} 个插件程序集", "Plugin");

        var loadedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in dlls)
        {
            foreach (var plugin in LoadPluginsFromAssembly(dll))
            {
                if (!loadedIds.Add(plugin.Metadata.Id))
                {
                    _logger.Warn($"插件 Id 重复，已跳过：{plugin.Metadata.Id}", "Plugin");
                    continue;
                }

                var runtime = new PluginRuntime(plugin);
                _plugins.Add(runtime);

                await InitializePluginAsync(runtime).ConfigureAwait(true);

                if (runtime.Error is null && enabled.Contains(plugin.Metadata.Id))
                {
                    await SetEnabledAsync(runtime, true).ConfigureAwait(true);
                }
                else
                {
                    runtime.IsEnabled = false;
                    runtime.RefreshStatus();
                }
            }
        }

        _logger.Info($"插件加载完成，共 {_plugins.Count} 个（内置 {_plugins.Count(p => p.Metadata.IsBuiltIn)} 个）", "Plugin");
    }

    public async Task<bool> SetEnabledAsync(PluginRuntime runtime, bool enabled)
    {
        if (enabled == runtime.IsEnabled && !(enabled && !runtime.IsRunning)) return true;

        try
        {
            if (enabled)
            {
                await runtime.Plugin.StartAsync().ConfigureAwait(true);
                runtime.IsEnabled = true;
                runtime.Error = null;
            }
            else
            {
                await runtime.Plugin.StopAsync().ConfigureAwait(true);
                runtime.IsEnabled = false;
            }

            runtime.RefreshStatus();
            _navigation.NotifyChanged();
            return true;
        }
        catch (Exception ex)
        {
            runtime.Error = ex.Message;
            _logger.Error($"切换插件状态失败：{runtime.Metadata.Name}", ex, "Plugin");
            runtime.RefreshStatus();
            return false;
        }
    }

    public async Task ShutdownAsync()
    {
        foreach (var runtime in _plugins)
        {
            try
            {
                await runtime.Plugin.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"释放插件失败：{runtime.Metadata.Id} - {ex.Message}", "Plugin");
            }
        }

        AssemblyLoadContext.Default.Resolving -= OnResolvingDependency;
    }

    private async Task InitializePluginAsync(PluginRuntime runtime)
    {
        try
        {
            var pluginId = runtime.Metadata.Id;
            var dataDir = AppPaths.GetPluginDataDirectory(pluginId);

            var context = new PluginContext(
                pluginId,
                dataDir,
                new JsonConfigStore(dataDir),
                _logger,
                _windowService,
                _alertService,
                _navigation);

            await runtime.Plugin.InitializeAsync(context).ConfigureAwait(true);
            runtime.RefreshStatus();
            _logger.Info($"插件已就绪：{runtime.Metadata.Name} v{runtime.Metadata.Version}", "Plugin");
        }
        catch (Exception ex)
        {
            runtime.Error = ex.Message;
            runtime.RefreshStatus();
            _logger.Error($"插件初始化失败：{Path.GetFileName(runtime.Metadata.Id)}", ex, "Plugin");
        }
    }

    private IEnumerable<IPlugin> LoadPluginsFromAssembly(string path)
    {
        var results = new List<IPlugin>();
        Assembly assembly;

        try
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        catch (Exception ex)
        {
            _logger.Error($"程序集加载失败：{Path.GetFileName(path)}", ex, "Plugin");
            return results;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
            _logger.Warn($"程序集部分类型无法加载：{Path.GetFileName(path)}", "Plugin");
        }
        catch (Exception ex)
        {
            _logger.Error($"程序集类型枚举失败：{Path.GetFileName(path)}", ex, "Plugin");
            return results;
        }

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || !type.IsClass) continue;
            if (!typeof(IPlugin).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;

            try
            {
                if (Activator.CreateInstance(type) is IPlugin plugin)
                {
                    results.Add(plugin);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"插件实例化失败：{type.FullName}", ex, "Plugin");
            }
        }

        return results;
    }

    private Assembly? OnResolvingDependency(AssemblyLoadContext context, AssemblyName name)
    {
        foreach (var directory in _resolvingDirectories)
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(candidate))
            {
                try
                {
                    return context.LoadFromAssemblyPath(candidate);
                }
                catch
                {
                    // 继续尝试其它目录
                }
            }
        }

        return null;
    }
}
