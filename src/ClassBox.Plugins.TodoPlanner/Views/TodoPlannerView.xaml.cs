using System.Windows;
using System.Windows.Controls;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.TodoPlanner.Planning;

namespace ClassBox.Plugins.TodoPlanner.Views;

/// <summary>待办清单页面。</summary>
public partial class TodoPlannerView : UserControl
{
    private readonly TodoPlannerPlugin _plugin;
    private readonly TodoStore _store;
    private List<TodoItem> _shown = new();
    private TodoItem? _editing;
    private bool _ready;
    private bool _closed;

    public TodoPlannerView(TodoPlannerPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        // 先填充下拉项：此时 _ready 仍为 false，SelectionChanged 会被事件守卫忽略。
        PriorityBox.Items.Add("高");
        PriorityBox.Items.Add("中");
        PriorityBox.Items.Add("低");
        PriorityBox.SelectedIndex = 1;

        StatusBox.Items.Add("全部");
        StatusBox.Items.Add("未完成");
        StatusBox.Items.Add("已完成");
        StatusBox.Items.Add("逾期");
        StatusBox.SelectedIndex = 1;

        PriorityFilterBox.Items.Add("全部优先级");
        PriorityFilterBox.Items.Add("高");
        PriorityFilterBox.Items.Add("中");
        PriorityFilterBox.Items.Add("低");
        PriorityFilterBox.SelectedIndex = 0;

        SortBox.Items.Add("按截止日期");
        SortBox.Items.Add("按优先级");
        SortBox.Items.Add("按创建时间");
        SortBox.SelectedIndex = 0;

        _store = new TodoStore(plugin.Host.DataDirectory, plugin.Host.Logger);
        _store.Load();

        _ready = true;
        Reload();
    }

    /// <summary>页面关闭时由插件调用，阻止后续回调。</summary>
    public void Shutdown()
    {
        _closed = true;
        _ready = false;
    }

    // ===== 筛选 =====

    private void OnFilterChanged(object sender, EventArgs e)
    {
        if (!_ready || _closed) return;
        Reload();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        // 选中变化不影响列表内容，这里仅保持选中项可用。
    }

    // ===== 录入 =====

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (FormHint is null) return;
        FormHint.Text = string.IsNullOrWhiteSpace(TitleBox.Text)
            ? "标题为必填项"
            : (_editing is null ? "按「添加待办」写入本地" : $"正在编辑：{_editing.Title}");
    }

    private void OnAddOrSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var title = (TitleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            FormHint.Text = "标题不能为空";
            TitleBox.Focus();
            return;
        }

        if (title.Length > 120) title = title[..120];

        var dueDate = DueDatePicker.SelectedDate is { } date
            ? date.ToString("yyyy-MM-dd")
            : string.Empty;

        var draft = new TodoItem
        {
            Title = title,
            Notes = (NotesBox.Text ?? string.Empty).Trim(),
            Priority = (TodoPriority)Math.Clamp(PriorityBox.SelectedIndex, 0, 2),
            DueDate = dueDate,
            Tags = (TagsBox.Text ?? string.Empty).Trim()
        };

        try
        {
            if (_editing is null)
            {
                _store.Add(draft);
                Notify("已添加待办", draft.Title);
            }
            else
            {
                _store.Update(_editing, draft);
                Notify("已保存修改", draft.Title);
                EndEditing();
            }
        }
        catch (Exception ex)
        {
            _plugin.LogError("保存待办失败", ex);
            FormHint.Text = "保存失败：" + ex.Message;
            return;
        }

        ClearForm();
        Reload();
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        EndEditing();
        ClearForm();
        Reload();
    }

    private void OnClearFormClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        EndEditing();
        ClearForm();
    }

    // ===== 列表项操作 =====

    private void OnItemCheckClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (sender is not FrameworkElement { DataContext: TodoItem item }) return;

        item.CompletedAt = item.IsDone ? DateTime.Now : null;
        try
        {
            _store.Save();
            Notify(item.IsDone ? "已标记完成" : "已取消完成", item.Title);
        }
        catch (Exception ex)
        {
            _plugin.LogError("更新完成状态失败", ex);
        }

        Reload();
    }

    private void OnEditItemClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (sender is not FrameworkElement { DataContext: TodoItem item }) return;
        BeginEditing(item);
    }

    private void OnDeleteItemClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (sender is not FrameworkElement { DataContext: TodoItem item }) return;
        DeleteItem(item);
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        if (TodoList.SelectedItem is not TodoItem item)
        {
            FormHint.Text = "请先在列表中选择一条待办";
            return;
        }

        DeleteItem(item);
    }

    private void OnClearCompletedClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        try
        {
            var removed = _store.RemoveCompleted();
            Notify(removed > 0 ? "已清理已完成待办" : "没有已完成的待办", $"共清理 {removed} 条");
        }
        catch (Exception ex)
        {
            _plugin.LogError("清空已完成待办失败", ex);
        }

        Reload();
    }

    // ===== 内部 =====

    private void DeleteItem(TodoItem item)
    {
        try
        {
            _store.Remove(item);
            Notify("已删除待办", item.Title);
            if (ReferenceEquals(_editing, item))
            {
                EndEditing();
                ClearForm();
            }
        }
        catch (Exception ex)
        {
            _plugin.LogError("删除待办失败", ex);
        }

        Reload();
    }

    private void BeginEditing(TodoItem item)
    {
        _editing = item;
        TitleBox.Text = item.Title;
        NotesBox.Text = item.Notes;
        TagsBox.Text = item.Tags;
        PriorityBox.SelectedIndex = (int)item.Priority;
        DueDatePicker.SelectedDate = DateTime.TryParse(item.DueDate, out var due) ? due.Date : null;
        AddButton.Content = "保存修改";
        CancelEditButton.IsEnabled = true;
        FormHint.Text = $"正在编辑：{item.Title}";
        TitleBox.Focus();
    }

    private void EndEditing()
    {
        _editing = null;
        AddButton.Content = "添加待办";
        CancelEditButton.IsEnabled = false;
        FormHint.Text = "标题为必填项";
    }

    private void ClearForm()
    {
        TitleBox.Text = string.Empty;
        NotesBox.Text = string.Empty;
        TagsBox.Text = string.Empty;
        PriorityBox.SelectedIndex = 1;
        DueDatePicker.SelectedDate = null;
    }

    private void Reload()
    {
        if (!_ready || _closed) return;

        var status = (TodoStatusFilter)Math.Clamp(StatusBox.SelectedIndex, 0, 3);
        var priority = (TodoPriorityFilter)Math.Clamp(PriorityFilterBox.SelectedIndex, 0, 3);
        var sort = (TodoSortMode)Math.Clamp(SortBox.SelectedIndex, 0, 2);

        _shown = _store.Query(status, priority, SearchBox.Text ?? string.Empty, sort);
        TodoList.ItemsSource = null;
        TodoList.ItemsSource = _shown;

        if (EmptyHint is not null)
        {
            EmptyHint.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = _store.Items.Count == 0
                ? "暂无待办，先在上方添加一条吧。"
                : "当前筛选条件下没有匹配的待办。";
        }

        UpdateStats();
    }

    private void UpdateStats()
    {
        if (!_ready || _closed || StatsText is null) return;

        var stats = _store.GetStats();
        StatsText.Text =
            $"未完成 {stats.Active} 条 · 逾期 {stats.Overdue} 条 · 高优先级未完成 {stats.HighActive} 条 · " +
            $"已完成 {stats.Completed} 条 / 共 {stats.Total} 条 · 数据文件：{_store.FilePath}";
    }

    private void Notify(string title, string message)
    {
        try
        {
            _plugin.Host.Alerts.Show(new SideAlertRequest
            {
                Title = title,
                Message = message,
                Glyph = "\uE73E",
                AutoCloseSeconds = 3
            });
        }
        catch (Exception ex)
        {
            _plugin.LogError("侧边提示显示失败", ex);
        }
    }
}
