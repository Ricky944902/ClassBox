using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.TextDiff.Views;

namespace ClassBox.Plugins.TextDiff;

/// <summary>
/// 「文本对比」插件：左右两栏文本行级 LCS 差异对比，标记新增/删除/修改，
/// 统计增删行数，支持忽略大小写与空白、从文件载入、结果复制。
/// </summary>
public sealed class TextDiffPlugin : PluginBase
{
    internal const string PageId = "classbox.page.textdiff";

    private TextDiffView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.textdiff",
        Name = "文本对比",
        Description = "左右粘贴或载入两段文本，按行做 LCS 差异对比，逐行标注新增/删除/修改并着色，可忽略大小写与空白，统计增删行数。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "开发工具",
        Glyph = "\uE8A6",
        IsBuiltIn = false
    };

    public override int SortOrder => 430;

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
                var view = new TextDiffView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("文本对比页面已注册");
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
                LogError("关闭文本对比页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "TextDiff");

    internal void LogWarn(string message) => TryGetContext()?.Logger.Warn(message, "TextDiff");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "TextDiff");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
