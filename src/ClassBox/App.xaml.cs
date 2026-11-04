using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ClassBox.Abstractions.Services;
using ClassBox.Services;
using ClassBox.ViewModels;
using ClassBox.Views;

namespace ClassBox;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private ServiceContainer _container = new();
    private IAppLogger? _logger;
    private PluginManager? _pluginManager;
    private WindowService? _windowService;
    private SettingsService? _settingsService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：已运行时把已有窗口带到前台后退出
        _singleInstanceMutex = new Mutex(true, "ClassBox.SingleInstance.Mutex", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Class Box 已在运行中。", "Class Box", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            await BootstrapAsync();
        }
        catch (Exception ex)
        {
            _logger?.Error("应用启动失败", ex, "App");
            MessageBox.Show($"启动失败：{ex.Message}", "Class Box", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task BootstrapAsync()
    {
        AppPaths.EnsureCreated();

        var logger = new LogService(AppPaths.LogDirectory);
        _logger = logger;

        var settingsService = new SettingsService(logger);
        _settingsService = settingsService;
        logger.MinimumLevel = settingsService.Current.MinimumLogLevel;

        HookGlobalExceptionHandlers();

        ThemeManager.Apply(settingsService.Current.IsDarkTheme);

        var windowService = new WindowService(logger);
        _windowService = windowService;
        var alertService = new SideAlertService(logger);
        var navigationStore = new PluginNavigationStore();
        var pluginManager = new PluginManager(logger, windowService, alertService, navigationStore);
        _pluginManager = pluginManager;

        _container = new ServiceContainer();
        _container.RegisterInstance<IAppLogger>(logger);
        _container.RegisterInstance<IWindowService>(windowService);
        _container.RegisterInstance<ISideAlertService>(alertService);
        _container.RegisterInstance(settingsService);
        _container.RegisterInstance(navigationStore);
        _container.RegisterInstance(pluginManager);

        logger.Info($"Class Box 启动：v{typeof(App).Assembly.GetName().Version}，管理员权限={(windowService.IsAdmin ? "是" : "否")}", "App");
        logger.Info($"数据目录：{AppPaths.DataRoot}；插件目录：{AppPaths.PluginsDirectory}", "App");

        await pluginManager.LoadAllAsync(settingsService.Current.EnabledPluginIds);

        var mainViewModel = new MainViewModel(_container, logger, settingsService, pluginManager,
            navigationStore, windowService, alertService);

        var mainWindow = new MainWindow { DataContext = mainViewModel };
        MainWindow = mainWindow;
        // 主界面强制置顶：AttachWindowService 会在窗口句柄创建后立即置顶并启动置顶守卫
        mainWindow.AttachWindowService(windowService);
        mainWindow.Show();

        mainViewModel.Initialize();

        if (!windowService.IsAdmin)
        {
            alertService.Show(new SideAlertRequest
            {
                Title = "权限提示",
                Message = "当前未以管理员身份运行，部分以管理员启动的程序窗口无法置顶或嵌入。",
                AutoCloseSeconds = 8
            });
        }
    }

    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _logger?.Error("UI 线程未处理异常", args.Exception, "App");
            args.Handled = true;

            try
            {
                MessageBox.Show($"发生未预期的错误，已记录到日志。\n\n{args.Exception.Message}",
                    "Class Box", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // 忽略
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                _logger?.Error("后台线程未处理异常", ex, "App");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.Error("未观察的任务异常", args.Exception, "App");
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                vm.PersistState();
            }

            _windowService?.DetachAll();
            _pluginManager?.ShutdownAsync().GetAwaiter().GetResult();
            _logger?.Info("Class Box 已退出", "App");
        }
        catch
        {
            // 退出阶段忽略异常
        }
        finally
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
    }
}
