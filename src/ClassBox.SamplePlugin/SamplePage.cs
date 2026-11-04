using System.Windows;
using System.Windows.Controls;

namespace ClassBox.SamplePlugin;

/// <summary>示例插件页面：纯代码构建，方便直接阅读最少依赖的写法。</summary>
public sealed class SamplePage : UserControl
{
    private readonly SamplePlugin _plugin;
    private readonly TextBlock _counter;

    public SamplePage(SamplePlugin plugin)
    {
        _plugin = plugin;

        var title = new TextBlock
        {
            Text = "示例插件",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var description = new TextBlock
        {
            Text = "这是第三方插件模板。你可以复制 ClassBox.SamplePlugin 目录，改个名字就是自己的插件。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 16)
        };

        _counter = new TextBlock { Margin = new Thickness(0, 12, 0, 0), Opacity = 0.75 };

        var button = new Button
        {
            Content = "弹出一条侧边提示",
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => _counter.Text = $"已被点击 {_plugin.ShowHello()} 次";

        var panel = new StackPanel { Margin = new Thickness(28, 4, 28, 20) };
        panel.Children.Add(title);
        panel.Children.Add(description);
        panel.Children.Add(button);
        panel.Children.Add(_counter);

        Content = panel;
        Background = System.Windows.Media.Brushes.Transparent;
    }
}
