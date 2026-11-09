using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.QRCodeTool.Qr;
using ClassBox.Plugins.QRCodeTool.Views;

namespace ClassBox.Plugins.QRCodeTool;

/// <summary>插件持久化设置。</summary>
public sealed class QrCodeSettings
{
    public string EccLevel { get; set; } = "M";

    public int ModuleSize { get; set; } = 8;

    public string SaveDirectory { get; set; } = string.Empty;
}

/// <summary>「二维码工具」插件：手写 QR 编码器，支持文本 / 网址 / WiFi 配置生成与 PNG 导出。</summary>
public sealed class QRCodeToolPlugin : PluginBase
{
    internal const string PageId = "classbox.page.qrcodetool";
    private const string ConfigKey = "qrcode-settings";

    private QrCodeSettings _settings = new();
    private QRCodeToolView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.qrcodetool",
        Name = "二维码工具",
        Description = "纯手写 QR 编码器，支持文本、网址、WiFi 配置生成二维码，可导出 PNG 或复制图片。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "效率工具",
        Glyph = "\uE8B0",
        IsBuiltIn = false
    };

    public override int SortOrder => 490;

    internal IPluginContext Host => Context;

    internal QrCodeSettings Settings => _settings;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        var context = Context;
        _settings = context.Configuration.Load(ConfigKey, new QrCodeSettings());
        if (string.IsNullOrWhiteSpace(_settings.SaveDirectory))
        {
            _settings.SaveDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(_settings.SaveDirectory))
            {
                _settings.SaveDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
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
                var view = new QRCodeToolView(this);
                _view = view;
                return view;
            }
        });

        context.Logger.Info("二维码工具已就绪，编码器版本上限 " + QrEncoder.MaxVersion, "QRCodeTool");
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        _view = null;
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
            LogError("保存二维码设置失败", ex);
        }
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "QRCodeTool");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "QRCodeTool");

    internal void Notify(string title, string message)
    {
        try
        {
            TryGetContext()?.Alerts.Show(new SideAlertRequest
            {
                Title = title,
                Message = message,
                Glyph = "\uE8B0",
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
