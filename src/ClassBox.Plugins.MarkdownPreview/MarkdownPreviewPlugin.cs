using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.MarkdownPreview.Views;

namespace ClassBox.Plugins.MarkdownPreview;

/// <summary>
/// 「Markdown 预览」插件：左侧编辑、右侧基于手写极简渲染器实时预览，
/// 支持标题、强调、行内代码、代码块、列表、引用、水平线、链接与表格，
/// 并可打开/保存 .md 文件、导出纯文本。
/// </summary>
public sealed class MarkdownPreviewPlugin : PluginBase
{
    internal const string PageId = "classbox.page.markdownpreview";

    private MarkdownPreviewView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.markdownpreview",
        Name = "Markdown 预览",
        Description = "左写右看：内置极简 Markdown 渲染器支持标题、粗体斜体、代码块、列表、引用、表格与链接，可打开、保存 .md 并导出纯文本。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "文档工具",
        Glyph = "\uE8A5",
        IsBuiltIn = false
    };

    public override int SortOrder => 440;

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
                var view = new MarkdownPreviewView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("Markdown 预览页面已注册");
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
                LogError("关闭 Markdown 预览页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "MarkdownPreview");

    internal void LogWarn(string message) => TryGetContext()?.Logger.Warn(message, "MarkdownPreview");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "MarkdownPreview");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
