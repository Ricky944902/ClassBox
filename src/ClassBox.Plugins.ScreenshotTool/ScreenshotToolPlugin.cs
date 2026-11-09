using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.ScreenshotTool.Capture;
using ClassBox.Plugins.ScreenshotTool.Views;

namespace ClassBox.Plugins.ScreenshotTool;

/// <summary>插件持久化设置。</summary>
public sealed class ScreenshotSettings
{
    public string SaveDirectory { get; set; } = string.Empty;

    public bool CopyAfterSave { get; set; } = true;
}

/// <summary>
/// 「截图工具」插件：全屏 / 当前屏幕抓取，页面内框选裁剪，
/// 预览后可复制到剪贴板或另存为 PNG，支持 3 秒延时与保存目录设置。
/// </summary>
public sealed class ScreenshotToolPlugin : PluginBase
{
    internal const string PageId = "classbox.page.screenshottool";
    private const string ConfigKey = "screenshot-settings";

    private ScreenshotSettings _settings = new();
    private ScreenshotToolView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.screenshottool",
        Name = "截图工具",
        Description = "全屏或当前屏幕截图，页面内框选裁剪，支持 3 秒延时、复制到剪贴板与另存 PNG。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "效率工具",
        Glyph = "\uE722",
        IsBuiltIn = false
    };

    public override int SortOrder => 480;

    internal IPluginContext Host => Context;

    internal ScreenshotSettings Settings => _settings;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        var context = Context;
        _settings = context.Configuration.Load(ConfigKey, new ScreenshotSettings());
        if (string.IsNullOrWhiteSpace(_settings.SaveDirectory))
        {
            _settings.SaveDirectory = ImageCropper.GetDefaultDirectory();
        }

        context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = PageId,
            Title = Metadata.Name,
            Description = Metadata.Description,
            Glyph = Metadata.Glyph,
            Order = SortOrder,
            ViewFactory = () =>
            {
                var view = new ScreenshotToolView(this);
                _view = view;
                return view;
            }
        });

        context.Logger.Info("截图工具已就绪，默认保存目录：" + _settings.SaveDirectory, "ScreenshotTool");
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
                LogError("关闭截图页面失败", ex);
            }
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
            LogError("保存截图设置失败", ex);
        }
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "ScreenshotTool");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "ScreenshotTool");

    internal void Notify(string title, string message)
    {
        try
        {
            TryGetContext()?.Alerts.Show(new SideAlertRequest
            {
                Title = title,
                Message = message,
                Glyph = "\uE722",
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
