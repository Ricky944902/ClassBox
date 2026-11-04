using System.Windows;
using ClassBox.Abstractions.Services;
using ClassBox.Views;

namespace ClassBox.Services;

/// <summary>
/// 侧边提示服务实现：串行展示提示条，右侧滑入、自动消失。
/// 失败时静默降级为日志，不影响触发动作。
/// </summary>
public sealed class SideAlertService : ISideAlertService
{
    private readonly IAppLogger _logger;
    private readonly Queue<SideAlertRequest> _queue = new();
    private SideAlertWindow? _current;

    public SideAlertService(IAppLogger logger)
    {
        _logger = logger;
    }

    public void Show(SideAlertRequest request)
    {
        var app = Application.Current;
        if (app is null) return;

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => Show(request)));
            return;
        }

        _queue.Enqueue(request);
        TryShowNext();
    }

    private void TryShowNext()
    {
        if (_current is not null) return;
        if (_queue.Count == 0) return;

        var request = _queue.Dequeue();

        try
        {
            var window = new SideAlertWindow();
            window.Apply(request);
            window.Closed2 += (_, _) =>
            {
                _current = null;
                TryShowNext();
            };
            window.Closed += (_, _) => _current = null;

            _current = window;
            Position(window);
            window.Show();
            window.PlayEnterAnimation();
        }
        catch (Exception ex)
        {
            _current = null;
            _logger.Warn($"侧边提示展示失败：{ex.Message}", "Alert");
        }
    }

    private static void Position(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        window.UpdateLayout();

        var height = window.ActualHeight > 0 ? window.ActualHeight : 120;
        window.Left = workArea.Right - window.Width - 4;
        window.Top = workArea.Top + Math.Max(60, workArea.Height * 0.28);

        _ = height;
    }
}
