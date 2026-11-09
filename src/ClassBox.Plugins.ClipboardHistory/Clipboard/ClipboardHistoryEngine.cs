using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClassBox.Plugins.ClipboardHistory.Clipboard;

/// <summary>
/// 剪贴板历史引擎（不依赖 WPF，可独立测试）：
/// 去重、上限 100 条、收藏置顶、搜索过滤与清除。
/// </summary>
public sealed class ClipboardHistoryEngine
{
    public const int MaxEntries = 100;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<ClipboardEntry> _entries = new();
    private readonly string? _persistPath;

    public ClipboardHistoryEngine(string? persistPath = null)
    {
        _persistPath = persistPath;
    }

    public IReadOnlyList<ClipboardEntry> Entries => _entries;

    public int Count => _entries.Count;

    public int FavoriteCount => _entries.Count(e => e.IsFavorite);

    /// <summary>
    /// 写入新文本；与最新一条相同或与已有内容重复时仅上移并刷新时间。
    /// 返回 true 表示历史发生实质变化。
    /// </summary>
    public bool Capture(string? text, DateTime? capturedAt = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = text;
        var existing = _entries.FirstOrDefault(e => string.Equals(e.Text, normalized, StringComparison.Ordinal));
        if (existing is not null)
        {
            // 已存在：刷新时间并置顶（收藏项本身已置顶，但同样刷新时间即可）。
            existing.CapturedAt = capturedAt ?? DateTime.Now;
            _entries.Remove(existing);
            _entries.Insert(0, existing);
            return true;
        }

        _entries.Insert(0, new ClipboardEntry
        {
            Text = normalized,
            CapturedAt = capturedAt ?? DateTime.Now
        });

        Trim();
        return true;
    }

    /// <summary>超出上限时优先丢弃最早的、未收藏的记录。</summary>
    private void Trim()
    {
        while (_entries.Count > MaxEntries)
        {
            var victim = _entries.LastOrDefault(e => !e.IsFavorite);
            if (victim is null) break;
            _entries.Remove(victim);
        }
    }

    public void ToggleFavorite(ClipboardEntry entry)
    {
        entry.IsFavorite = !entry.IsFavorite;
    }

    public bool Remove(ClipboardEntry entry) => _entries.Remove(entry);

    /// <summary>清除全部记录；keepFavorites 为 true 时保留收藏项。</summary>
    public int ClearAll(bool keepFavorites = false)
    {
        if (!keepFavorites)
        {
            var count = _entries.Count;
            _entries.Clear();
            return count;
        }

        return _entries.RemoveAll(e => !e.IsFavorite);
    }

    /// <summary>按关键字过滤，收藏项永远排在最前，其余按时间倒序。</summary>
    public List<ClipboardEntry> Query(string keyword)
    {
        IEnumerable<ClipboardEntry> query = _entries;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var key = keyword.Trim();
            query = query.Where(e => e.Text.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderByDescending(e => e.IsFavorite)
            .ThenByDescending(e => e.CapturedAt)
            .ToList();
    }

    public void Load()
    {
        if (string.IsNullOrWhiteSpace(_persistPath) || !File.Exists(_persistPath)) return;
        try
        {
            var json = File.ReadAllText(_persistPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return;
            var list = JsonSerializer.Deserialize<List<ClipboardEntry>>(json, Options);
            if (list is null) return;
            _entries.Clear();
            foreach (var entry in list.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Text)).Take(MaxEntries))
            {
                _entries.Add(entry);
            }
        }
        catch
        {
            // 历史文件损坏时直接忽略，保持空历史可用。
            _entries.Clear();
        }
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(_persistPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_entries, Options);
            var temp = _persistPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _persistPath!, true);
        }
        catch
        {
            // 保存失败不应影响剪贴板监听。
        }
    }
}
