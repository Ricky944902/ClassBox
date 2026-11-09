using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.HashCheck.Views;

namespace ClassBox.Plugins.HashCheck;

/// <summary>
/// 「文件哈希校验」插件：一次读取同时计算 MD5 / SHA1 / SHA256 / SHA512，
/// 支持拖放、批量、进度与取消，并可粘贴期望哈希自动比对。
/// </summary>
public sealed class HashCheckPlugin : PluginBase
{
    internal const string PageId = "classbox.page.hashcheck";

    private HashCheckView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.hashcheck",
        Name = "文件哈希校验",
        Description = "拖入或选择文件即可同时计算 MD5、SHA1、SHA256、SHA512，支持批量与进度显示，粘贴期望哈希自动比对通过与否。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "开发工具",
        Glyph = "\uE9D5",
        IsBuiltIn = false
    };

    public override int SortOrder => 360;

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
                var view = new HashCheckView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("文件哈希校验页面已注册");
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
                LogError("关闭哈希校验页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Info(message, "HashCheck");
    }

    internal void LogWarn(string message)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Warn(message, "HashCheck");
    }

    internal void LogError(string message, Exception? exception = null)
    {
        if (TryGetContext() is not { } ctx) return;
        ctx.Logger.Error(message, exception, "HashCheck");
    }

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
