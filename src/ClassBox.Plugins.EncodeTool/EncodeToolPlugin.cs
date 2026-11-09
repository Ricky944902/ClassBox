using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.EncodeTool.Views;

namespace ClassBox.Plugins.EncodeTool;

/// <summary>
/// 「编码转换台」插件：Base64（含 URL 安全变体）、URL 编码、HTML 实体、
/// Unicode 转义、Hex 的编解码双向转换，输入即时输出，支持二进制文件。
/// </summary>
public sealed class EncodeToolPlugin : PluginBase
{
    internal const string PageId = "classbox.page.encodetool";

    private EncodeToolView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.encodetool",
        Name = "编码转换台",
        Description = "Base64 / URL / HTML 实体 / Unicode 转义 / Hex 五种编码的双向转换，边输边出结果，可解码或生成二进制文件。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "开发工具",
        Glyph = "\uE8AB",
        IsBuiltIn = false
    };

    public override int SortOrder => 410;

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
                var view = new EncodeToolView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("编码转换台页面已注册");
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
                LogError("关闭编码转换台页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "EncodeTool");

    internal void LogWarn(string message) => TryGetContext()?.Logger.Warn(message, "EncodeTool");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "EncodeTool");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
