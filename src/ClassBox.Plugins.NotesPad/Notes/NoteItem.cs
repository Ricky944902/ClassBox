using ClassBox.Abstractions.Mvvm;

namespace ClassBox.Plugins.NotesPad.Notes;

/// <summary>单条便笺。</summary>
public sealed class NoteItem : ObservableObject
{
    private string _title = string.Empty;
    private string _content = string.Empty;
    private bool _isPinned;
    private DateTime _updatedAt = DateTime.Now;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value ?? string.Empty))
            {
                Raise(nameof(DisplayTitle));
                Raise(nameof(Preview));
            }
        }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value ?? string.Empty))
            {
                Raise(nameof(Preview));
                Raise(nameof(CharCount));
                Raise(nameof(LineCount));
            }
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value)) Raise(nameof(PinText));
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (SetProperty(ref _updatedAt, value)) Raise(nameof(UpdatedText));
        }
    }

    // ===== 展示派生属性 =====

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? "未命名便笺" : Title;

    public string PinText => IsPinned ? "置顶" : string.Empty;

    public string UpdatedText => UpdatedAt.ToString("MM-dd HH:mm");

    public int CharCount => Content?.Length ?? 0;

    public int LineCount => string.IsNullOrEmpty(Content)
        ? 0
        : Content.Count(c => c == '\n') + 1;

    /// <summary>列表用摘要：取首行非空内容并截断。</summary>
    public string Preview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Content)) return "（空白便笺）";
            var firstLine = Content
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            firstLine = firstLine.Replace("\r", string.Empty);
            return firstLine.Length > 42 ? firstLine[..42] + "…" : firstLine;
        }
    }
}
