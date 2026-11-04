using System.Windows;
using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;

namespace ClassBox.SamplePlugin;

/// <summary>
/// 示例插件：演示第三方插件的最小实现。
/// 复制本目录即可作为新插件的起点，编译后把 DLL 放入程序目录下的 Plugins 文件夹即可被宿主加载。
/// </summary>
public sealed class SamplePlugin : PluginBase
{
    private int _clickCount;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.sample.hello",
        Name = "示例插件",
        Description = "演示插件接口、页面注册、配置读写与侧边提示的最小用法。",
        Author = "Class Box Team",
        Version = new Version(1, 0, 0),
        Category = "开发示例",
        Glyph = "\uE943",
        IsBuiltIn = false
    };

    public override int SortOrder => 900;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        var page = new SamplePage(this);
        Context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = "classbox.page.sample",
            Title = "示例插件",
            Description = "第三方插件开发模板",
            Glyph = "\uE943",
            Order = 900,
            ViewFactory = () => page
        });

        return Task.CompletedTask;
    }

    /// <summary>供示例页面调用：弹出一条侧边提示并累计计数。</summary>
    public int ShowHello()
    {
        _clickCount++;
        Context.Configuration.Save("sample-state", new SampleState { ClickCount = _clickCount });

        Context.Alerts.Show(new SideAlertRequest
        {
            Title = "示例插件",
            Message = $"这是第 {_clickCount} 次提示，配置已保存到插件目录。",
            AutoCloseSeconds = 4
        });

        Context.Logger.Info($"示例插件被点击 {_clickCount} 次", "Sample");
        return _clickCount;
    }
}

/// <summary>示例插件持久化状态。</summary>
public sealed class SampleState
{
    public int ClickCount { get; set; }
}
