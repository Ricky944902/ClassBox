using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.WindowTopmost.ViewModels;
using ClassBox.Plugins.WindowTopmost.Views;

namespace ClassBox.Plugins.WindowTopmost;

/// <summary>窗口置顶与嵌入插件（内置）。</summary>
public sealed class WindowTopmostPlugin : PluginBase
{
    private WindowTopmostViewModel? _viewModel;
    private IPluginContext? _context;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.builtin.topmost",
        Name = "窗口置顶与嵌入",
        Description = "把任意窗口钉在最上层，或将其嵌入 Class Box 内集中查看。",
        Author = "Class Box",
        Version = new Version(1, 0, 0),
        Category = "窗口",
        Glyph = "\uE840",
        IsBuiltIn = true
    };

    public override int SortOrder => 10;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        _context = Context;
        _viewModel = new WindowTopmostViewModel(Context.Windows, Context.Logger, Context.Alerts);

        Context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = "classbox.page.topmost",
            Title = "窗口置顶",
            Description = "置顶或嵌入任意窗口",
            Glyph = "\uE840",
            Order = 10,
            ViewFactory = () =>
            {
                var view = new WindowTopmostView();
                view.DataContext = _viewModel;
                return view;
            }
        });

        return Task.CompletedTask;
    }

    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        _viewModel?.Refresh();
        return Task.CompletedTask;
    }

    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _viewModel?.DetachAll();
        }
        catch (Exception ex)
        {
            _context?.Logger.Warn($"停止置顶插件时清理失败：{ex.Message}");
        }

        await Task.CompletedTask;
    }
}
