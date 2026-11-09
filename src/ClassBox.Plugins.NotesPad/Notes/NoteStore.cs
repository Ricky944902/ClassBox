using System.Text.Encodings.Web;
using System.Text.Json;
using ClassBox.Abstractions.Services;

namespace ClassBox.Plugins.NotesPad.Notes;

/// <summary>便笺持久化：插件数据目录下的 notes.json。</summary>
public sealed class NoteStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly IAppLogger _logger;
    private readonly List<NoteItem> _notes = new();

    public NoteStore(string dataDirectory, IAppLogger logger)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "notes.json");
    }

    public string FilePath => _filePath;

    public IReadOnlyList<NoteItem> Notes => _notes;

    public void Load()
    {
        _notes.Clear();
        if (!File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return;
            var list = JsonSerializer.Deserialize<List<NoteItem>>(json, Options);
            if (list is null) return;
            foreach (var note in list.Where(n => n is not null))
            {
                if (string.IsNullOrWhiteSpace(note.Id)) note.Id = Guid.NewGuid().ToString("N");
                _notes.Add(note);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("便笺数据读取失败，已重置", ex, "NotesPad");
            try
            {
                File.Copy(_filePath, _filePath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            }
            catch
            {
                // 备份失败不影响页面可用性。
            }

            _notes.Clear();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_notes, Options);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _filePath, true);
        }
        catch (Exception ex)
        {
            _logger.Error("便笺数据保存失败", ex, "NotesPad");
            throw;
        }
    }

    public NoteItem Create(string title = "")
    {
        var note = new NoteItem
        {
            Title = string.IsNullOrWhiteSpace(title) ? "新便笺" : title.Trim(),
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        _notes.Add(note);
        Save();
        return note;
    }

    public void Delete(NoteItem note)
    {
        _notes.Remove(note);
        Save();
    }

    /// <summary>撤销删除：把便笺重新写回集合。</summary>
    public void Restore(NoteItem note)
    {
        if (_notes.Any(n => n.Id == note.Id)) return;
        _notes.Add(note);
        Save();
    }

    public void Touch(NoteItem note)
    {
        note.UpdatedAt = DateTime.Now;
    }

    /// <summary>按搜索关键字过滤并按「置顶优先 + 修改时间倒序」排序。</summary>
    public List<NoteItem> Query(string keyword)
    {
        IEnumerable<NoteItem> query = _notes;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var key = keyword.Trim();
            query = query.Where(n =>
                n.Title.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                n.Content.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.UpdatedAt)
            .ToList();
    }

    /// <summary>导出单篇便笺为 txt，返回写入路径。</summary>
    public static string Export(NoteItem note, string targetPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {note.DisplayTitle}");
        builder.AppendLine($"创建时间：{note.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"修改时间：{note.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(new string('-', 32));
        builder.AppendLine(note.Content ?? string.Empty);
        File.WriteAllText(targetPath, builder.ToString(), new UTF8Encoding(true));
        return targetPath;
    }

    /// <summary>把便笺标题转换为合法的文件名。</summary>
    public static string SafeFileName(string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "便笺" : title.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 40) name = name[..40];
        return name + ".txt";
    }
}
