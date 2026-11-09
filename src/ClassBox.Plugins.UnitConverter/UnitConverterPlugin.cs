using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.UnitConverter.Views;

namespace ClassBox.Plugins.UnitConverter;

/// <summary>
/// 「单位换算」插件：覆盖长度、重量、温度、面积、体积、速度、数据存储、时间
/// 八大类单位，任一侧输入即时双向换算，并给出同类单位对照表。
/// </summary>
public sealed class UnitConverterPlugin : PluginBase
{
    internal const string PageId = "classbox.page.unitconverter";

    private UnitConverterView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.unitconverter",
        Name = "单位换算",
        Description = "长度、重量、温度、面积、体积、速度、数据存储、时间八大类单位双向即时换算，附同类单位对照表，支持一键复制结果。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "效率工具",
        Glyph = "\uE8EF",
        IsBuiltIn = false
    };

    public override int SortOrder => 460;

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
                var view = new UnitConverterView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("单位换算页面已注册");
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
                LogError("关闭单位换算页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "UnitConverter");

    internal void LogWarn(string message) => TryGetContext()?.Logger.Warn(message, "UnitConverter");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "UnitConverter");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
