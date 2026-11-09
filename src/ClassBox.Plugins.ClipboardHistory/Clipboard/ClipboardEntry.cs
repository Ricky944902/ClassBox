using System.Text.Json.Serialization;
using ClassBox.Abstractions.Mvvm;

namespace ClassBox.Plugins.ClipboardHistory.Clipboard;

/// <summary>一条剪贴板文本记录。</summary>
public sealed class ClipboardEntry : ObservableObject
{
    private bool _isFavorite;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>原始文本内容。</summary>
    public string Text { get; set; } = string.Empty;

    public DateTime CapturedAt { get; set; } = DateTime.Now;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                Raise(nameof(FavoriteMark));
                Raise(nameof(FavoriteText));
            }
        }
    }

    [JsonIgnore]
    public string TimeText => CapturedAt.ToString("MM-dd HH:mm:ss");

    [JsonIgnore]
    public string FavoriteMark => IsFavorite ? "★" : "☆";

    [JsonIgnore]
    public string FavoriteText => IsFavorite ? "已收藏" : "收藏";

    /// <summary>列表摘要：压平换行并截断。</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var flat = (Text ?? string.Empty)
                .Replace("\r\n", " ")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            return flat.Length > 80 ? flat[..80] + "…" : flat;
        }
    }

    [JsonIgnore]
    public string LengthText => $"{(Text?.Length ?? 0)} 字符";
}
