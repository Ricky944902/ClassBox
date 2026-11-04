using System.Windows.Controls;

namespace ClassBox.Views.Pages;

/// <summary>插件页面创建失败时的兜底页面，避免整个窗口白屏。</summary>
public partial class ErrorPageView : UserControl
{
    public ErrorPageView(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }
}
