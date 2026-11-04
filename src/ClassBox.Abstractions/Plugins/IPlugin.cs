using System.Windows;
using ClassBox.Abstractions.Services;

namespace ClassBox.Abstractions.Plugins;

/// <summary>
/// 插件元数据。所有插件（内置与第三方）都必须提供。
/// </summary>
public sealed class PluginMetadata
{
    /// <summary>插件唯一标识，建议使用反向域名风格，例如 "classbox.builtin.topmost"。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>一句话描述。</summary>
    public string Description { get; init; } = string.Empty;

    public string Author { get; init; } = "Unknown";

    public Version Version { get; init; } = new(1, 0, 0);

    /// <summary>分类标签，仅用于界面展示。</summary>
    public string Category { get; init; } = "通用";

    /// <summary>Segoe MDL2 / Segoe Fluent Icons 字形。</summary>
    public string Glyph { get; init; } = "\uE8B7";

    /// <summary>是否为内置插件（内置插件不可从磁盘卸载）。</summary>
    public bool IsBuiltIn { get; init; } = true;
}

/// <summary>
/// 插件生命周期接口。宿主导入插件目录后按 Initialize -> Start 的顺序调用。
/// </summary>
public interface IPlugin : IAsyncDisposable
{
    PluginMetadata Metadata { get; }

    /// <summary>插件是否处于运行状态。</summary>
    bool IsRunning { get; }

    /// <summary>侧边栏排序权重，值越小越靠前。</summary>
    int SortOrder => 100;

    /// <summary>
    /// 初始化：注入宿主服务、注册导航页面。此方法只应执行一次。
    /// 不得在此方法内执行耗时的硬件操作。
    /// </summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>启动插件（用户启用插件时调用）。</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>停止插件并释放运行时资源（用户禁用插件时调用）。</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 插件页面注册描述符。插件通过 <see cref="IPluginNavigation.RegisterPage"/> 把自己的页面挂到侧边栏。
/// </summary>
public sealed class PluginPageDescriptor
{
    public string PageId { get; init; } = Guid.NewGuid().ToString("N");

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>Segoe MDL2 字形。</summary>
    public string Glyph { get; init; } = "\uE8B7";

    public int Order { get; init; } = 100;

    /// <summary>视图工厂，返回一个 UserControl。宿主负责缓存与复用。</summary>
    public required Func<FrameworkElement> ViewFactory { get; init; }
}

/// <summary>插件导航注册表，由宿主实现。</summary>
public interface IPluginNavigation
{
    void RegisterPage(PluginPageDescriptor page);

    void RemovePage(string pageId);

    /// <summary>请求宿主导航到指定页面（可用于插件内部按钮跳转）。</summary>
    void NavigateTo(string pageId);
}

/// <summary>插件运行上下文，宿主在 Initialize 时注入。</summary>
public interface IPluginContext
{
    /// <summary>当前插件 Id。</summary>
    string PluginId { get; }

    /// <summary>插件专属数据目录（%AppData%\Class Box\plugins\{pluginId}），已创建。</summary>
    string DataDirectory { get; }

    IConfigStore Configuration { get; }

    IAppLogger Logger { get; }

    IWindowService Windows { get; }

    ISideAlertService Alerts { get; }

    IPluginNavigation Navigation { get; }

    /// <summary>宿主当前是否处于深色主题。</summary>
    bool IsDarkTheme { get; }
}

/// <summary>
/// 插件基类：提供默认的启停状态管理，降低第三方插件开发成本。
/// </summary>
public abstract class PluginBase : IPlugin
{
    protected IPluginContext Context { get; private set; } = null!;

    public abstract PluginMetadata Metadata { get; }

    public bool IsRunning { get; private set; }

    public virtual int SortOrder => 100;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        Context = context;
        try
        {
            await OnInitializeAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            context.Logger.Error($"插件 {Metadata.Id} 初始化失败", ex);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        try
        {
            await OnStartAsync(cancellationToken).ConfigureAwait(true);
            IsRunning = true;
            Context.Logger.Info($"插件已启动：{Metadata.Name}");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Context.Logger.Error($"插件 {Metadata.Name} 启动失败", ex);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return;
        try
        {
            await OnStopAsync(cancellationToken).ConfigureAwait(true);
            Context.Logger.Info($"插件已停止：{Metadata.Name}");
        }
        catch (Exception ex)
        {
            Context.Logger.Error($"插件 {Metadata.Name} 停止时发生异常", ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // 释放阶段吞掉异常，避免影响宿主退出
        }
        GC.SuppressFinalize(this);
    }

    protected virtual Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected virtual Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected virtual Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
