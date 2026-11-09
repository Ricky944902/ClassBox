using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.PasswordGen.Views;

namespace ClassBox.Plugins.PasswordGen;

/// <summary>
/// 「密码生成器」插件：可配置长度与字符集，随机源使用加密安全的
/// RandomNumberGenerator，附带强度分档、熵值与破解时间估算、批量生成与历史记录。
/// </summary>
public sealed class PasswordGenPlugin : PluginBase
{
    internal const string PageId = "classbox.page.passwordgen";

    private PasswordGenView? _view;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.passwordgen",
        Name = "密码生成器",
        Description = "加密安全随机源生成强密码，可定制长度与字符集、排除易混淆字符，实时给出熵值、强度分档与破解时间估算，支持批量与历史记录。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "安全工具",
        Glyph = "\uE72E",
        IsBuiltIn = false
    };

    public override int SortOrder => 450;

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
                var view = new PasswordGenView(this);
                _view = view;
                return view;
            }
        });

        LogInfo("密码生成器页面已注册");
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
                LogError("关闭密码生成器页面时出现异常", ex);
            }
        }

        return Task.CompletedTask;
    }

    internal void LogInfo(string message) => TryGetContext()?.Logger.Info(message, "PasswordGen");

    internal void LogWarn(string message) => TryGetContext()?.Logger.Warn(message, "PasswordGen");

    internal void LogError(string message, Exception? exception = null)
        => TryGetContext()?.Logger.Error(message, exception, "PasswordGen");

    private IPluginContext? TryGetContext()
    {
        try { return Context; }
        catch { return null; }
    }
}
