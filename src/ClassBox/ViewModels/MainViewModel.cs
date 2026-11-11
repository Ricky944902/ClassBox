using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using ClassBox.Abstractions.Mvvm;
using ClassBox.Abstractions.Services;
using ClassBox.Services;
using ClassBox.Views.Pages;

namespace ClassBox.ViewModels;

/// <summary>侧边栏导航项。</summary>
public sealed class NavItem : ObservableObject
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Glyph { get; init; }

    public string Badge { get; init; } = string.Empty;

    public string? PluginId { get; init; }

    public bool IsPluginPage => PluginId is not null;

    public FrameworkElement? Page { get; set; }
}

/// <summary>主窗口视图模型：导航、页面缓存、主题与插件开关。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IAppLogger _logger;
    private readonly SettingsService _settings;
    private readonly PluginManager _pluginManager;
    private readonly PluginNavigationStore _navigation;
    private readonly IWindowService _windows;
    private readonly ISideAlertService _alerts;

    private readonly Dictionary<string, FrameworkElement> _pageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<PluginRuntime> _visiblePlugins = new();

    private NavItem? _selectedNavItem;
    private FrameworkElement? _currentPage;
    private string _currentPageTitle = "首页";
    private string _currentPageSubtitle = "总览与快捷操作";
    private bool _rebuilding;

    public MainViewModel(ServiceContainer container, IAppLogger logger, SettingsService settings,
        PluginManager pluginManager, PluginNavigationStore navigation, IWindowService windows,
        ISideAlertService alerts)
    {
        _logger = logger;
        _settings = settings;
        _pluginManager = pluginManager;
        _navigation = navigation;
        _windows = windows;
        _alerts = alerts;

        Container = container;
        VersionText = $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        TogglePluginCommand = new AsyncRelayCommand(parameter => TogglePluginAsync(parameter as PluginRuntime));
        RefreshPluginsCommand = new AsyncRelayCommand(RefreshPluginsAsync);
        MinimizeAllCommand = new RelayCommand(MinimizeAllWindows);
        OpenPluginsFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.PluginsDirectory));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.DataRoot));
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.LogDirectory));
        SetThemeCommand = new RelayCommand(parameter => SetTheme(parameter as string));
        SaveLogLevelCommand = new RelayCommand(SaveLogLevel);

        _navigation.Changed += (_, _) => RebuildNavigation();
        _navigation.NavigateRequested += NavigateTo;
        ThemeManager.ThemeChanged += (_, _) => Raise(nameof(IsDarkTheme));
    }

    public ServiceContainer Container { get; }

    public ObservableCollection<NavItem> NavItems { get; } = new();

    /// <summary>当前可见的插件集合（受「显示隐藏内容」开关过滤）。</summary>
    public ObservableCollection<PluginRuntime> Plugins => _visiblePlugins;

    /// <summary>全部已加载插件（含默认隐藏项），仅供宿主内部逻辑使用。</summary>
    public IReadOnlyList<PluginRuntime> AllPlugins => _pluginManager.Plugins;

    /// <summary>是否展示默认隐藏的插件；开关状态持久化到 settings.json。</summary>
    public bool ShowHiddenPlugins
    {
        get => _settings.Current.ShowHiddenPlugins;
        set
        {
            if (value == _settings.Current.ShowHiddenPlugins) return;

            _settings.Current.ShowHiddenPlugins = value;
            _settings.Save();

            Raise();
            RefreshVisiblePlugins();
            RebuildNavigation();
        }
    }

    /// <summary>是否存在被默认隐藏的插件。</summary>
    public bool HasHiddenPlugins => AllPlugins.Any(p => p.IsHidden);

    /// <summary>插件市场标题栏下方的可见性说明。</summary>
    public string HiddenPluginSummary
    {
        get
        {
            var hidden = AllPlugins.Count(p => p.IsHidden);
            if (hidden == 0) return $"共 {AllPlugins.Count} 个插件，当前无默认隐藏项";

            return ShowHiddenPlugins
                ? $"共 {AllPlugins.Count} 个插件（已显示 {hidden} 个默认隐藏项）"
                : $"共 {AllPlugins.Count} 个插件，已隐藏 {hidden} 个（默认仅展示 4 个核心插件）";
        }
    }

    public string VersionText { get; }

    public string PluginsDirectory => AppPaths.PluginsDirectory;

    public string DataRoot => AppPaths.DataRoot;

    public string LogDirectory => AppPaths.LogDirectory;

    public bool IsDarkTheme => ThemeManager.IsDark;

    public IReadOnlyList<string> LogLevels { get; } = new[] { "Debug", "Info", "Warn", "Error" };

    public string SelectedLogLevel
    {
        get => _settings.Current.MinimumLogLevel.ToString();
        set
        {
            if (Enum.TryParse<LogLevel>(value, out var level))
            {
                _settings.Current.MinimumLogLevel = level;
                _logger.MinimumLevel = level;
                _settings.Save();
                Raise();
            }
        }
    }

    public int PluginCount => Plugins.Count;

    public int EnabledPluginCount => Plugins.Count(p => p.IsEnabled);

    public int RunningPluginCount => Plugins.Count(p => p.IsRunning);

    public RelayCommand ToggleThemeCommand { get; }

    public AsyncRelayCommand TogglePluginCommand { get; }

    public AsyncRelayCommand RefreshPluginsCommand { get; }

    public RelayCommand MinimizeAllCommand { get; }

    public RelayCommand OpenPluginsFolderCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand SetThemeCommand { get; }

    public RelayCommand SaveLogLevelCommand { get; }

    public NavItem? SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (!SetProperty(ref _selectedNavItem, value)) return;
            if (_rebuilding) return;

            if (value?.Page is not null)
            {
                CurrentPage = value.Page;
                CurrentPageTitle = value.Title;
                CurrentPageSubtitle = value.IsPluginPage ? "插件页面" : string.Empty;
            }
        }
    }

    public FrameworkElement? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentPageTitle
    {
        get => _currentPageTitle;
        private set => SetProperty(ref _currentPageTitle, value);
    }

    public string CurrentPageSubtitle
    {
        get => _currentPageSubtitle;
        private set => SetProperty(ref _currentPageSubtitle, value);
    }

    public void Initialize()
    {
        RefreshVisiblePlugins();
        RebuildNavigation("dashboard");

        var failed = Plugins.Where(p => p.HasError).ToList();
        if (failed.Count > 0)
        {
            _alerts.Show(new SideAlertRequest
            {
                Title = "部分插件未能加载",
                Message = string.Join("、", failed.Select(p => p.Metadata.Name)),
                AutoCloseSeconds = 8,
                IsUrgent = true
            });
        }
    }

    public void PersistState() => _settings.Save();

    public void NavigateTo(string pageKey)
    {
        var target = NavItems.FirstOrDefault(n => string.Equals(n.Key, pageKey, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            SelectedNavItem = target;
        }
    }

    private void RebuildNavigation(string? selectKey = null)
    {
        selectKey ??= SelectedNavItem?.Key ?? "dashboard";

        _rebuilding = true;
        try
        {
            NavItems.Clear();

            NavItems.Add(CreateCoreItem("dashboard", "首页", "\uE80F"));

            foreach (var entry in _navigation.Pages
                         .OrderBy(p => p.Descriptor.Order)
                         .ThenBy(p => p.Descriptor.Title, StringComparer.OrdinalIgnoreCase))
            {
                var runtime = AllPlugins.FirstOrDefault(p => p.Metadata.Id == entry.PluginId);
                if (runtime is null || !runtime.IsEnabled) continue;
                if (runtime.IsHidden && !ShowHiddenPlugins) continue;

                NavItems.Add(new NavItem
                {
                    Key = entry.Descriptor.PageId,
                    Title = entry.Descriptor.Title,
                    Glyph = entry.Descriptor.Glyph,
                    PluginId = entry.PluginId,
                    Page = GetPluginPage(entry)
                });
            }

            NavItems.Add(CreateCoreItem("plugins", "插件中心", "\uE74C"));
            NavItems.Add(CreateCoreItem("settings", "设置", "\uE713"));
        }
        finally
        {
            _rebuilding = false;
        }

        var target = NavItems.FirstOrDefault(n => string.Equals(n.Key, selectKey, StringComparison.OrdinalIgnoreCase))
                     ?? NavItems.FirstOrDefault();

        if (target is not null)
        {
            SelectedNavItem = target;
            CurrentPage = target.Page;
            CurrentPageTitle = target.Title;
            CurrentPageSubtitle = target.IsPluginPage ? "插件页面" : string.Empty;
        }

        Raise(nameof(PluginCount));
        Raise(nameof(EnabledPluginCount));
        Raise(nameof(RunningPluginCount));
        Raise(nameof(HiddenPluginSummary));
        Raise(nameof(HasHiddenPlugins));
    }

    /// <summary>
    /// 按「显示隐藏内容」开关重建可见插件集合。
    /// 默认隐藏机制在宿主侧完成，不影响插件加载、启用与页面注册。
    /// </summary>
    private void RefreshVisiblePlugins()
    {
        var showHidden = ShowHiddenPlugins;

        _visiblePlugins.Clear();
        foreach (var runtime in AllPlugins)
        {
            if (runtime.IsHidden && !showHidden) continue;
            _visiblePlugins.Add(runtime);
        }

        Raise(nameof(PluginCount));
        Raise(nameof(EnabledPluginCount));
        Raise(nameof(RunningPluginCount));
        Raise(nameof(HiddenPluginSummary));
        Raise(nameof(HasHiddenPlugins));
    }

    private NavItem CreateCoreItem(string key, string title, string glyph)
    {
        if (!_pageCache.TryGetValue(key, out var page))
        {
            FrameworkElement? created = key switch
            {
                "dashboard" => new DashboardView(),
                "plugins" => new PluginManagerView(),
                "settings" => new SettingsView(),
                _ => null
            };

            page = created!;
            if (created is not null) _pageCache[key] = created;
        }

        return new NavItem { Key = key, Title = title, Glyph = glyph, Page = page };
    }

    private FrameworkElement GetPluginPage(PluginPageEntry entry)
    {
        var pageId = entry.Descriptor.PageId;
        if (_pageCache.TryGetValue(pageId, out var cached)) return cached;

        try
        {
            var view = entry.Descriptor.ViewFactory();
            _pageCache[pageId] = view;
            return view;
        }
        catch (Exception ex)
        {
            _logger.Error($"插件页面创建失败：{entry.Descriptor.Title}", ex, "UI");
            _pageCache[pageId] = new ErrorPageView($"插件页面「{entry.Descriptor.Title}」创建失败：{ex.Message}");
            return _pageCache[pageId];
        }
    }

    private void ToggleTheme()
    {
        ThemeManager.Apply(!ThemeManager.IsDark);
        _settings.Current.IsDarkTheme = ThemeManager.IsDark;
        _settings.Save();
    }

    private void SetTheme(string? mode)
    {
        var dark = string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase);
        if (dark == ThemeManager.IsDark) return;

        ThemeManager.Apply(dark);
        _settings.Current.IsDarkTheme = dark;
        _settings.Save();
    }

    private void SaveLogLevel() => _settings.Save();

    private async Task TogglePluginAsync(PluginRuntime? runtime)
    {
        if (runtime is null) return;

        var target = !runtime.IsEnabled;
        var ok = await _pluginManager.SetEnabledAsync(runtime, target).ConfigureAwait(true);

        if (ok)
        {
            _settings.SetPluginEnabled(runtime.Metadata.Id, target);
            RefreshVisiblePlugins();

            _alerts.Show(new SideAlertRequest
            {
                Title = runtime.Metadata.Name,
                Message = target ? "插件已启用" : "插件已停用",
                AutoCloseSeconds = 3
            });
        }
        else
        {
            _alerts.Show(new SideAlertRequest
            {
                Title = $"{runtime.Metadata.Name} 操作失败",
                Message = runtime.Error ?? "请查看日志了解详情",
                AutoCloseSeconds = 6,
                IsUrgent = true
            });
        }
    }

    private Task RefreshPluginsAsync()
    {
        _alerts.Show(new SideAlertRequest
        {
            Title = "插件目录已变更？",
            Message = "新增或删除插件后，重启 Class Box 生效。",
            AutoCloseSeconds = 5
        });

        return Task.CompletedTask;
    }

    private void MinimizeAllWindows()
    {
        var count = _windows.MinimizeAll();
        _alerts.Show(new SideAlertRequest
        {
            Title = "已最小化全部窗口",
            Message = $"共处理 {count} 个窗口",
            AutoCloseSeconds = 3
        });
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"打开目录失败：{path} - {ex.Message}", "UI");
        }
    }
}
