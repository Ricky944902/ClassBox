using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassBox.Abstractions.Services;

namespace ClassBox.Views;

/// <summary>屏幕右侧非模态提示条。不抢焦点、不打断当前操作。</summary>
public partial class SideAlertWindow : Window
{
    private readonly DispatcherTimer _timer = new();

    public SideAlertWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            BeginCloseAnimation();
        };
    }

    public event EventHandler? Closed2;

    public void Apply(SideAlertRequest request)
    {
        TitleText.Text = request.Title;
        MessageText.Text = request.Message;
        GlyphText.Text = string.IsNullOrEmpty(request.Glyph) ? "\uE7BA" : request.Glyph;

        if (request.IsUrgent)
        {
            AccentBar.Background = (Brush)FindResource("AppDangerBrush");
            GlyphText.Foreground = (Brush)FindResource("AppDangerBrush");
        }
        else if (!string.IsNullOrWhiteSpace(request.AccentColor))
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(request.AccentColor));
                brush.Freeze();
                AccentBar.Background = brush;
                GlyphText.Foreground = brush;
            }
            catch
            {
                // 颜色非法时保留主题色
            }
        }

        if (request.AutoCloseSeconds > 0)
        {
            _timer.Interval = TimeSpan.FromSeconds(request.AutoCloseSeconds);
            _timer.Start();
        }
    }

    public void PlayEnterAnimation()
    {
        var transform = new TranslateTransform(120, 0);
        RootCard.RenderTransform = transform;
        RootCard.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(120, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        RootCard.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    private void BeginCloseAnimation()
    {
        var transform = RootCard.RenderTransform as TranslateTransform ?? new TranslateTransform();
        RootCard.RenderTransform = transform;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var animation = new DoubleAnimation(transform.X, 140, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = ease
        };
        animation.Completed += (_, _) =>
        {
            try
            {
                Closed2?.Invoke(this, EventArgs.Empty);
                Close();
            }
            catch
            {
                // 关闭阶段忽略异常
            }
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
        RootCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        BeginCloseAnimation();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
