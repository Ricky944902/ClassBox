using ClassBox.Abstractions.Mvvm;

namespace ClassBox.Plugins.TodoPlanner.Planning;

/// <summary>待办优先级。</summary>
public enum TodoPriority
{
    High = 0,
    Medium = 1,
    Low = 2
}

/// <summary>单条待办事项。</summary>
public sealed class TodoItem : ObservableObject
{
    private string _title = string.Empty;
    private string _notes = string.Empty;
    private TodoPriority _priority = TodoPriority.Medium;
    private string _dueDate = string.Empty;
    private string _tags = string.Empty;
    private bool _isDone;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? string.Empty);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value ?? string.Empty);
    }

    public TodoPriority Priority
    {
        get => _priority;
        set
        {
            if (SetProperty(ref _priority, value)) Raise(nameof(PriorityText));
        }
    }

    /// <summary>截止日期，格式 yyyy-MM-dd，空字符串表示未设置。</summary>
    public string DueDate
    {
        get => _dueDate;
        set
        {
            if (SetProperty(ref _dueDate, value ?? string.Empty))
            {
                Raise(nameof(DueDateText));
                Raise(nameof(IsOverdue));
            }
        }
    }

    /// <summary>标签，逗号分隔。</summary>
    public string Tags
    {
        get => _tags;
        set
        {
            if (SetProperty(ref _tags, value ?? string.Empty)) Raise(nameof(TagsText));
        }
    }

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (SetProperty(ref _isDone, value))
            {
                Raise(nameof(StateText));
                Raise(nameof(IsOverdue));
            }
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? CompletedAt { get; set; }

    // ===== 展示派生属性 =====

    public string PriorityText => Priority switch
    {
        TodoPriority.High => "高",
        TodoPriority.Medium => "中",
        _ => "低"
    };

    public string DueDateText => string.IsNullOrWhiteSpace(DueDate) ? "无截止" : DueDate;

    public string StateText => IsDone ? "已完成" : "未完成";

    public string TagsText => string.IsNullOrWhiteSpace(Tags) ? "无标签" : Tags;

    public bool IsOverdue
    {
        get
        {
            if (IsDone || string.IsNullOrWhiteSpace(DueDate)) return false;
            return DateTime.TryParse(DueDate, out var due) && due.Date < DateTime.Today;
        }
    }

    public DateOnly? ParsedDueDate =>
        DateTime.TryParse(DueDate, out var due) ? DateOnly.FromDateTime(due) : null;
}
