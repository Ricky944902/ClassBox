using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.NotesPad.Views;

namespace ClassBox.Plugins.NotesPad;

/// <summary>
/// 「速记便笺」插件：多便笺的新建/重命名/删除/搜索，正文自动保存到插件数据目录，
/// 支持置顶、字数统计与单篇导出 txt。
/// </summary>
public sealed class NotesPadPlugin : PluginBase
{
    internal const string PageId = "classbox.page.notespad";

    private NotesPadView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.notespad",
        Name = "速记便笺",
        Description = "多便笺速记，输入即自动保存到本地，支持置顶、搜索、字数统计与导出 txt。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "文档工具",
        Glyph = "\uE70B",
        IsBuiltIn = false
    };

    public override int SortOrder => 510;

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
                var view = new NotesPadView(this);
                _view = view;
                return view;
            }
        });

        TryGetContext()?.Logger.Info("速记便笺页面已注册", "NotesPad");
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
                TryGetContext()?.Logger.Error("关闭速记便笺页面失败", ex, "NotesPad");
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "NotesPad");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "NotesPad");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
