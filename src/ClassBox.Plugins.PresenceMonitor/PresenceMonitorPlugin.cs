using ClassBox.Abstractions.Plugins;
using ClassBox.Plugins.PresenceMonitor.ViewModels;
using ClassBox.Plugins.PresenceMonitor.Views;

namespace ClassBox.Plugins.PresenceMonitor;

/// <summary>人像监测插件（内置）：摄像头存在性检测 + 可配置触发动作。</summary>
public sealed class PresenceMonitorPlugin : PluginBase
{
    private PresenceMonitorViewModel? _viewModel;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.builtin.presence",
        Name = "人像监测",
        Description = "调用本机摄像头判断是否有人在前，可按配置弹出侧边提示或收敛桌面窗口。",
        Author = "Class Box",
        Version = new Version(1, 0, 0),
        Category = "课堂辅助",
        Glyph = "\uE7B8",
        IsBuiltIn = true
    };

    public override int SortOrder => 20;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        _viewModel = new PresenceMonitorViewModel(
            Context.Windows, Context.Logger, Context.Alerts, Context.Configuration);

        Context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = "classbox.page.presence",
            Title = "人像监测",
            Description = "摄像头人像检测与自动提醒",
            Glyph = "\uE7B8",
            Order = 25,
            ViewFactory = () => new PresenceMonitorView { DataContext = _viewModel }
        });

        return Task.CompletedTask;
    }

    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        _viewModel?.StartIfConfigured();
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        _viewModel?.Dispose();
        return Task.CompletedTask;
    }
}
