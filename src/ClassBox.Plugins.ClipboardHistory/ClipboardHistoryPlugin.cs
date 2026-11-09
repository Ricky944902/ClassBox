using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.ClipboardHistory.Clipboard;
using ClassBox.Plugins.ClipboardHistory.Views;

namespace ClassBox.Plugins.ClipboardHistory;

/// <summary>
/// 「剪贴板历史」插件：后台 STA 线程定时监听剪贴板文本，保留最近 100 条去重记录，
/// 支持搜索、回填、收藏置顶、暂停监听与清除。
/// </summary>
public sealed class ClipboardHistoryPlugin : PluginBase
{
    internal const string PageId = "classbox.page.clipboardhistory";

    private ClipboardHistoryEngine? _engine;
    private ClipboardMonitor? _monitor;
    private ClipboardHistoryView? _view;
    private DateTime _lastSave = DateTime.MinValue;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.clipboardhistory",
        Name = "剪贴板历史",
        Description = "后台记录最近 100 条剪贴板文本并自动去重，支持搜索、一键回填、收藏置顶与暂停监听。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "效率工具",
        Glyph = "\uE77F",
        IsBuiltIn = false
    };

    public override int SortOrder => 470;

    internal IPluginContext Host => Context;

    internal ClipboardHistoryEngine Engine =>
        _engine ?? throw new InvalidOperationException("剪贴板历史引擎尚未初始化");

    internal ClipboardMonitor Monitor =>
        _monitor ?? throw new InvalidOperationException("剪贴板监听器尚未初始化");

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        var context = Context;
        var persistPath = Path.Combine(context.DataDirectory, "clipboard-history.json");

        _engine = new ClipboardHistoryEngine(persistPath);
        _engine.Load();

        _monitor = new ClipboardMonitor(_engine);
        _monitor.Captured += OnClipboardCaptured;
        _monitor.Start();

        context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = PageId,
            Title = Metadata.Name,
            Description = Metadata.Description,
            Glyph = Metadata.Glyph,
            Order = SortOrder,
            ViewFactory = () =>
            {
                var view = new ClipboardHistoryView(this);
                _view = view;
                return view;
            }
        });

        context.Logger.Info($"剪贴板历史已启动，已载入 {_engine.Count} 条记录", "ClipboardHistory");
        return Task.CompletedTask;
    }

    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        _monitor?.Start();
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        if (_monitor is not null)
        {
            _monitor.Captured -= OnClipboardCaptured;
            _monitor.Dispose();
            _monitor = null;
        }

        _engine?.Save();

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
                TryGetContext()?.Logger.Error("关闭剪贴板历史页面失败", ex, "ClipboardHistory");
            }
        }

        return Task.CompletedTask;
    }

    internal void PersistIfDue(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastSave).TotalSeconds < 2) return;
        _lastSave = now;
        _engine?.Save();
    }

    private void OnClipboardCaptured(object? sender, ClipboardEntry entry)
    {
        PersistIfDue();
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "ClipboardHistory");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "ClipboardHistory");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
