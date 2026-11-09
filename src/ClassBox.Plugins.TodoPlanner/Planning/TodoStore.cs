using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassBox.Abstractions.Services;

namespace ClassBox.Plugins.TodoPlanner.Planning;

/// <summary>待办清单持久化：插件数据目录下的 todos.json。</summary>
public sealed class TodoStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly IAppLogger _logger;
    private readonly List<TodoItem> _items = new();

    public TodoStore(string dataDirectory, IAppLogger logger)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "todos.json");
    }

    public string FilePath => _filePath;

    public IReadOnlyList<TodoItem> Items => _items;

    /// <summary>从磁盘载入；文件损坏时备份并重置。</summary>
    public void Load()
    {
        _items.Clear();
        if (!File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return;
            var list = JsonSerializer.Deserialize<List<TodoItem>>(json, Options);
            if (list is null) return;
            foreach (var item in list.Where(i => i is not null))
            {
                if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                _items.Add(item);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("待办数据读取失败，已重置并备份原文件", ex, "TodoPlanner");
            try
            {
                var backup = _filePath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(_filePath, backup, true);
            }
            catch
            {
                // 备份失败时忽略，保证页面仍可用。
            }

            _items.Clear();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, Options);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _filePath, true);
        }
        catch (Exception ex)
        {
            _logger.Error("待办数据保存失败", ex, "TodoPlanner");
            throw;
        }
    }

    public TodoItem Add(TodoItem item)
    {
        _items.Add(item);
        Save();
        return item;
    }

    public void Remove(TodoItem item)
    {
        _items.Remove(item);
        Save();
    }

    public int RemoveCompleted()
    {
        var removed = _items.RemoveAll(i => i.IsDone);
        if (removed > 0) Save();
        return removed;
    }

    public void Update(TodoItem item, TodoItem edited)
    {
        // 就地更新，保持列表顺序与引用稳定。
        item.Title = edited.Title;
        item.Notes = edited.Notes;
        item.Priority = edited.Priority;
        item.DueDate = edited.DueDate;
        item.Tags = edited.Tags;
        if (item.IsDone != edited.IsDone)
        {
            item.IsDone = edited.IsDone;
            item.CompletedAt = edited.IsDone ? DateTime.Now : null;
        }

        Save();
    }

    /// <summary>按状态、优先级、关键字过滤，并按排序规则返回快照。</summary>
    public List<TodoItem> Query(TodoStatusFilter status, TodoPriorityFilter priority, string keyword, TodoSortMode sort)
    {
        IEnumerable<TodoItem> query = _items;

        query = status switch
        {
            TodoStatusFilter.Active => query.Where(i => !i.IsDone),
            TodoStatusFilter.Completed => query.Where(i => i.IsDone),
            TodoStatusFilter.Overdue => query.Where(i => i.IsOverdue),
            _ => query
        };

        query = priority switch
        {
            TodoPriorityFilter.High => query.Where(i => i.Priority == TodoPriority.High),
            TodoPriorityFilter.Medium => query.Where(i => i.Priority == TodoPriority.Medium),
            TodoPriorityFilter.Low => query.Where(i => i.Priority == TodoPriority.Low),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var key = keyword.Trim();
            query = query.Where(i =>
                i.Title.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                i.Notes.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                i.Tags.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        var list = query.ToList();
        switch (sort)
        {
            case TodoSortMode.DueDate:
                return list
                    .OrderBy(i => i.IsDone)
                    .ThenBy(i => i.ParsedDueDate ?? DateOnly.MaxValue)
                    .ThenBy(i => (int)i.Priority)
                    .ToList();

            case TodoSortMode.Priority:
                return list
                    .OrderBy(i => i.IsDone)
                    .ThenBy(i => (int)i.Priority)
                    .ThenBy(i => i.ParsedDueDate ?? DateOnly.MaxValue)
                    .ToList();

            case TodoSortMode.Created:
            default:
                return list
                    .OrderBy(i => i.IsDone)
                    .ThenByDescending(i => i.CreatedAt)
                    .ToList();
        }
    }

    public TodoStats GetStats()
    {
        var total = _items.Count;
        var completed = _items.Count(i => i.IsDone);
        var active = total - completed;
        var overdue = _items.Count(i => i.IsOverdue);
        var highActive = _items.Count(i => !i.IsDone && i.Priority == TodoPriority.High);
        return new TodoStats(total, active, completed, overdue, highActive);
    }
}

public enum TodoStatusFilter
{
    All = 0,
    Active = 1,
    Completed = 2,
    Overdue = 3
}

public enum TodoPriorityFilter
{
    All = 0,
    High = 1,
    Medium = 2,
    Low = 3
}

public enum TodoSortMode
{
    DueDate = 0,
    Priority = 1,
    Created = 2
}

public readonly record struct TodoStats(int Total, int Active, int Completed, int Overdue, int HighActive);
