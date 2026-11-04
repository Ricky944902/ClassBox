using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ClassBox.Plugins.WindowTopmost.ViewModels;

namespace ClassBox.Plugins.WindowTopmost.Views;

public partial class WindowTopmostView : UserControl
{
    public WindowTopmostView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            EmbedContainer.ContainerResized += (_, _) =>
                (DataContext as WindowTopmostViewModel)?.OnContainerResized();
        };
    }

    private void Embed_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WindowTopmostViewModel vm) return;

        // HwndHost 需要先参与布局才会创建原生子窗口，这里延迟到布局完成后再取句柄。
        EmbedContainer.Visibility = Visibility.Visible;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            EmbedContainer.UpdateLayout();
            var handle = EmbedContainer.ContainerHandle;

            if (handle == IntPtr.Zero)
            {
                Dispatcher.BeginInvoke(new Action(() => vm.EmbedInto(EmbedContainer.ContainerHandle)),
                    DispatcherPriority.ApplicationIdle);
                return;
            }

            vm.EmbedInto(handle);
        }), DispatcherPriority.Loaded);
    }
}
