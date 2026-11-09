using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClassBox.Plugins.HashCheck.Hashing;

/// <summary>文件列表行：承载单个文件的路径、大小、计算状态与哈希结果。</summary>
public sealed class HashFileItem : INotifyPropertyChanged
{
    private string _stateText = "待计算";
    private string _sizeText = "-";
    private HashResult? _result;

    public HashFileItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        try
        {
            var info = new FileInfo(filePath);
            _sizeText = info.Exists ? HashEngine.FormatSize(info.Length) : "文件不存在";
        }
        catch (Exception)
        {
            _sizeText = "无法读取大小";
        }
    }

    public string FilePath { get; }

    public string FileName { get; }

    public string SizeText
    {
        get => _sizeText;
        private set => SetField(ref _sizeText, value);
    }

    public string StateText
    {
        get => _stateText;
        set => SetField(ref _stateText, value);
    }

    public HashResult? Result
    {
        get => _result;
        set
        {
            _result = value;
            OnPropertyChanged();
            if (value is not null)
            {
                SizeText = value.SizeText;
                StateText = $"已完成 · {value.ElapsedText}";
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
