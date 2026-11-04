using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using ClassBox.Abstractions.Mvvm;
using ClassBox.Abstractions.Services;

namespace ClassBox.Plugins.WindowTopmost.ViewModels;

/// <summary>窗口置顶 / 嵌入页面的视图模型。</summary>
public sealed class WindowTopmostViewModel : ObservableObject
{
    private readonly IWindowService _windows;
    private readonly IAppLogger _logger;
    private readonly ISideAlertService _alerts;

    private readonly HashSet<long> _pinned = new();

    private WindowInfo? _selected;
    private string _searchText = string.Empty;
    private IntPtr _embedded = IntPtr.Zero;
    private string _embedHint = "选择一个窗口后点击「嵌入到此处」，即可在本页内集中查看。";

    public WindowTopmostViewModel(IWindowService windows, IAppLogger logger, ISideAlertService alerts)
    {
        _windows = windows;
        _logger = logger;
        _alerts = alerts;

        WindowsView = CollectionViewSource.GetDefaultView(Windows);
        WindowsView.Filter = FilterWindow;

        RefreshCommand = new RelayCommand(Refresh);
        ToggleTopmostCommand = new RelayCommand(_ => ToggleTopmost(), _ => SelectedWindow is not null);
        DetachCommand = new RelayCommand(_ => DetachCurrent(), _ => _embedded != IntPtr.Zero);
        ClearAllTopmostCommand = new RelayCommand(ClearAllTopmost, () => _pinned.Count > 0);
        MinimizeCommand = new RelayCommand(_ => MinimizeSelected(), _ => SelectedWindow is not null);
        RestoreCommand = new RelayCommand(_ => RestoreSelected(), _ => SelectedWindow is not null);

        Refresh();
    }

    public ObservableCollection<WindowInfo> Windows { get; } = new();

    public ICollectionView WindowsView { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ToggleTopmostCommand { get; }

    public RelayCommand DetachCommand { get; }

    public RelayCommand ClearAllTopmostCommand { get; }

    public RelayCommand MinimizeCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                WindowsView.Refresh();
            }
        }
    }

    public WindowInfo? SelectedWindow
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                RefreshSelectionState();
            }
        }
    }

    public bool HasSelection => SelectedWindow is not null;

    public bool IsSelectedPinned
        => SelectedWindow is not null && _pinned.Contains(SelectedWindow.Handle.ToInt64());

    public string TopmostButtonText => IsSelectedPinned ? "取消置顶" : "置顶该窗口";

    public bool IsAnyEmbedded => _embedded != IntPtr.Zero;

    public string EmbedHint
    {
        get => _embedHint;
        private set => SetProperty(ref _embedHint, value);
    }

    public string PinnedSummary => _pinned.Count == 0 ? "当前没有置顶窗口" : $"已置顶 {_pinned.Count} 个窗口";

    /// <summary>刷新窗口列表（同时把已置顶状态同步到内存）。</summary>
    public void Refresh()
    {
        var previous = SelectedWindow?.Handle ?? IntPtr.Zero;

        try
        {
            var list = _windows.EnumerateWindows();
            Windows.Clear();

            foreach (var window in list)
            {
                Windows.Add(window);
                if (window.IsTopmost) _pinned.Add(window.Handle.ToInt64());
            }

            if (previous != IntPtr.Zero)
            {
                SelectedWindow = Windows.FirstOrDefault(w => w.Handle == previous);
            }

            SelectedWindow ??= Windows.FirstOrDefault();
            _logger.Debug($"窗口列表已刷新，共 {list.Count} 个", "Topmost");
        }
        catch (Exception ex)
        {
            _logger.Error("刷新窗口列表失败", ex, "Topmost");
        }

        Raise(nameof(PinnedSummary));
        RefreshSelectionState();
    }

    /// <summary>把目标窗口嵌入到宿主提供的子窗口容器中。</summary>
    public void EmbedInto(IntPtr container)
    {
        var target = SelectedWindow;
        if (target is null) return;

        if (container == IntPtr.Zero)
        {
            _alerts.Show(new SideAlertRequest
            {
                Title = "嵌入容器尚未就绪",
                Message = "请稍候重试，或改用置顶方式。",
                AutoCloseSeconds = 5,
                IsUrgent = true
            });
            return;
        }

        if (_embedded != IntPtr.Zero) DetachCurrent();

        if (_windows.EmbedWindow(target.Handle, container, out var error))
        {
            _embedded = target.Handle;
            EmbedHint = $"已嵌入：{target.DisplayName}";
            _windows.ResizeEmbedded(target.Handle);

            _alerts.Show(new SideAlertRequest
            {
                Title = "窗口已嵌入",
                Message = "点击「解除嵌入」可把窗口还原到桌面。",
                AutoCloseSeconds = 4
            });
        }
        else
        {
            _logger.Warn($"嵌入窗口失败：{error}", "Topmost");
            _alerts.Show(new SideAlertRequest
            {
                Title = "嵌入失败，已尝试置顶",
                Message = error ?? "目标窗口可能带有系统保护，无法作为子窗口。",
                AutoCloseSeconds = 7,
                IsUrgent = true
            });

            // 降级：至少保证窗口在最上层
            if (_windows.SetTopmost(target.Handle, true, activate: true))
            {
                _pinned.Add(target.Handle.ToInt64());
                EmbedHint = "该窗口不支持嵌入，已改为置顶显示。";
            }
        }

        Raise(nameof(IsAnyEmbedded));
        Raise(nameof(PinnedSummary));
        RefreshSelectionState();
    }

    /// <summary>容器尺寸变化时重排嵌入窗口。</summary>
    public void OnContainerResized()
    {
        if (_embedded == IntPtr.Zero) return;
        _windows.ResizeEmbedded(_embedded);
    }

    /// <summary>插件停止或应用退出时清理所有副作用。</summary>
    public void DetachAll()
    {
        if (_embedded != IntPtr.Zero) DetachCurrent();

        foreach (var handle in _pinned.ToList())
        {
            _windows.SetTopmost(new IntPtr(handle), false);
        }

        _pinned.Clear();
        Raise(nameof(PinnedSummary));
    }

    private void ToggleTopmost()
    {
        var target = SelectedWindow;
        if (target is null) return;

        var handle = target.Handle.ToInt64();
        var next = !_pinned.Contains(handle);

        if (_windows.SetTopmost(target.Handle, next, activate: true))
        {
            if (next) _pinned.Add(handle);
            else _pinned.Remove(handle);

            _alerts.Show(new SideAlertRequest
            {
                Title = target.DisplayName,
                Message = next ? "已置顶" : "已取消置顶",
                AutoCloseSeconds = 2
            });
        }
        else
        {
            _alerts.Show(new SideAlertRequest
            {
                Title = "操作失败",
                Message = "该窗口可能以管理员权限运行，请以管理员身份启动 Class Box 后重试。",
                AutoCloseSeconds = 6,
                IsUrgent = true
            });
        }

        Raise(nameof(PinnedSummary));
        RefreshSelectionState();
    }

    private void DetachCurrent()
    {
        if (_embedded == IntPtr.Zero) return;

        _windows.DetachWindow(_embedded);
        _embedded = IntPtr.Zero;
        EmbedHint = "已解除嵌入，窗口已恢复到桌面。";

        Raise(nameof(IsAnyEmbedded));
        RefreshSelectionState();
    }

    private void ClearAllTopmost()
    {
        foreach (var handle in _pinned.ToList())
        {
            _windows.SetTopmost(new IntPtr(handle), false);
        }

        _pinned.Clear();
        Raise(nameof(PinnedSummary));
        RefreshSelectionState();
    }

    private void MinimizeSelected()
    {
        if (SelectedWindow is null) return;
        _windows.Minimize(SelectedWindow.Handle);
        Refresh();
    }

    private void RestoreSelected()
    {
        if (SelectedWindow is null) return;
        _windows.Restore(SelectedWindow.Handle);
        Refresh();
    }

    private void RefreshSelectionState()
    {
        Raise(nameof(HasSelection));
        Raise(nameof(IsSelectedPinned));
        Raise(nameof(TopmostButtonText));
    }

    private bool FilterWindow(object item)
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        if (item is not WindowInfo window) return false;

        return window.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || window.ProcessName.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }
}
