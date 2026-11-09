using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.HashCheck.Hashing;
using Microsoft.Win32;

namespace ClassBox.Plugins.HashCheck.Views;

/// <summary>文件哈希校验页面：拖放/选择文件，批量计算四种摘要并支持期望值比对。</summary>
public partial class HashCheckView : UserControl
{
    private const int MaxFiles = 200;

    private readonly HashCheckPlugin _plugin;
    private readonly ObservableCollection<HashFileItem> _files = new();
    private CancellationTokenSource? _cts;
    private bool _computing;
    private bool _closed;

    public HashCheckView(HashCheckPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        FileList.ItemsSource = _files;
        _files.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();
    }

    // ===================== 文件收集 =====================

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要计算哈希的文件",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        AddFiles(dialog.FileNames);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var expanded = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    expanded.AddRange(Directory.EnumerateFiles(path));
                }
                catch (Exception ex)
                {
                    _plugin.LogWarn($"读取目录失败：{path} - {ex.Message}");
                }
            }
            else if (File.Exists(path))
            {
                expanded.Add(path);
            }
        }

        AddFiles(expanded);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        foreach (var path in paths)
        {
            if (_files.Count >= MaxFiles)
            {
                skipped++;
                continue;
            }

            if (_files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            _files.Add(new HashFileItem(path));
            added++;
        }

        if (added > 0)
        {
            ProgressText.Text = $"已添加 {added} 个文件，列表共 {_files.Count} 个。点击「开始计算」开始校验。";
            if (FileList.SelectedItem is null) FileList.SelectedIndex = 0;
        }

        if (skipped > 0)
        {
            ProgressText.Text += $"（{skipped} 个重复或超出上限的文件已忽略，单次最多 {MaxFiles} 个）";
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_computing) return;

        _files.Clear();
        ClearDetail();
        CompareBar.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
        ProgressText.Text = "列表已清空。可拖入文件或点击「添加文件」。";
        StatusText.Text = "就绪";
        FooterText.Text = "提示：哈希计算为本地只读操作，不会修改文件内容。";
    }

    // ===================== 计算 =====================

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_computing) return;
        if (_files.Count == 0)
        {
            ProgressText.Text = "请先添加要计算的文件。";
            return;
        }

        _computing = true;
        ToggleBusy(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var completed = 0;
        var failed = 0;
        var totalBytes = 0L;
        var totalMs = 0L;

        try
        {
            foreach (var item in _files.ToList())
            {
                token.ThrowIfCancellationRequested();

                item.StateText = "计算中…";
                StatusText.Text = $"正在计算：{item.FileName}";
                Progress.Value = 0;

                try
                {
                    var progress = new Progress<double>(percent =>
                    {
                        Progress.Value = percent;
                        ProgressText.Text =
                            $"正在计算 {item.FileName}（{percent:0}%）· 已完成 {completed}/{_files.Count}";
                    });

                    var result = await HashEngine.ComputeAsync(item.FilePath, progress, token);
                    item.Result = result;
                    totalBytes += result.FileSize;
                    totalMs += result.ElapsedMs;
                    completed++;

                    if (ReferenceEquals(FileList.SelectedItem, item)) ShowDetail(result);
                }
                catch (OperationCanceledException)
                {
                    item.StateText = "已取消";
                    throw;
                }
                catch (Exception ex)
                {
                    item.StateText = $"失败：{ex.Message}";
                    failed++;
                    _plugin.LogWarn($"计算哈希失败：{item.FilePath} - {ex.Message}");
                }

                Progress.Value = 100;
            }

            ProgressText.Text = $"计算完成：成功 {completed} 个" +
                                (failed > 0 ? $"，失败 {failed} 个" : string.Empty) +
                                $"，累计读取 {HashEngine.FormatSize(totalBytes)}，累计耗时 {HashEngine.FormatDuration(totalMs)}";
            StatusText.Text = "计算完成";
            FooterText.Text = $"本次共处理 {completed} 个文件，累计 {HashEngine.FormatSize(totalBytes)}。";

            if (FileList.SelectedItem is HashFileItem selected && selected.Result is not null)
            {
                ShowDetail(selected.Result);
                if (!string.IsNullOrWhiteSpace(ExpectedBox.Text)) RunCompare(selected.Result);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
            ProgressText.Text = $"已取消，已完成 {completed} 个文件。";
            _plugin.LogInfo("用户取消了哈希计算");
        }
        catch (Exception ex)
        {
            StatusText.Text = "计算失败";
            ProgressText.Text = $"计算过程出错：{ex.Message}";
            _plugin.LogError("哈希计算异常", ex);
        }
        finally
        {
            _computing = false;
            ToggleBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_computing) return;
        try
        {
            _cts?.Cancel();
            StatusText.Text = "正在取消…";
        }
        catch (ObjectDisposedException)
        {
            // 已结束
        }
    }

    private void ToggleBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        StatusDot.Fill = (System.Windows.Media.Brush)FindResource(busy ? "AppAccentBrush" : "AppTextSecondaryBrush");
    }

    // ===================== 详情与比对 =====================

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (FileList.SelectedItem)
        {
            case HashFileItem { Result: not null } item:
                ShowDetail(item.Result);
                if (!string.IsNullOrWhiteSpace(ExpectedBox.Text)) RunCompare(item.Result);
                break;
            default:
                ClearDetail();
                break;
        }
    }

    private void ShowDetail(HashResult result)
    {
        Md5Box.Text = result.Md5;
        Sha1Box.Text = result.Sha1;
        Sha256Box.Text = result.Sha256;
        Sha512Box.Text = result.Sha512;
        FooterText.Text = $"{result.FilePath} · 大小 {result.SizeText} · 耗时 {result.ElapsedText}";
    }

    private void ClearDetail()
    {
        Md5Box.Text = string.Empty;
        Sha1Box.Text = string.Empty;
        Sha256Box.Text = string.Empty;
        Sha512Box.Text = string.Empty;
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string algorithm }) return;

        var value = algorithm switch
        {
            "MD5" => Md5Box.Text,
            "SHA1" => Sha1Box.Text,
            "SHA256" => Sha256Box.Text,
            "SHA512" => Sha512Box.Text,
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            FooterText.Text = "当前没有可复制的哈希值，请先选择并计算文件。";
            return;
        }

        try
        {
            Clipboard.SetText(value);
            FooterText.Text = $"已复制 {algorithm} 值到剪贴板。";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void ExpectedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var normalized = HashEngine.Normalize(ExpectedBox.Text);
        var hint = normalized.Length switch
        {
            0 => "期望哈希（粘贴官方/发布页给出的值，长度自动识别算法）",
            32 => "已识别为 MD5（32 位十六进制）",
            40 => "已识别为 SHA1（40 位十六进制）",
            64 => "已识别为 SHA256（64 位十六进制）",
            128 => "已识别为 SHA512（128 位十六进制）",
            _ => $"长度 {normalized.Length} 位，无法识别算法（需 32/40/64/128 位十六进制）"
        };

        if (CompareBar.Visibility == Visibility.Visible && normalized.Length is 0) CompareBar.Visibility = Visibility.Collapsed;
        FooterText.Text = hint;

        if (FileList.SelectedItem is HashFileItem { Result: not null } item && normalized.Length > 0)
        {
            RunCompare(item.Result);
        }
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not HashFileItem item)
        {
            ShowCompare(false, "请先在左侧选择要比对的文件");
            return;
        }

        if (item.Result is null)
        {
            ShowCompare(false, "该文件尚未计算哈希，请先点击「开始计算」");
            return;
        }

        RunCompare(item.Result);
    }

    private void RunCompare(HashResult result)
    {
        var compare = HashEngine.Compare(result, ExpectedBox.Text);
        var body = $"{compare.Message}\n期望值：{compare.Expected}";
        if (compare.Actual.Length > 0) body += $"\n实际值：{compare.Actual}";
        ShowCompare(compare.Matched, body);
        _plugin.LogInfo($"哈希比对（{compare.Algorithm}）：{(compare.Matched ? "通过" : "不通过")}");
    }

    private void ShowCompare(bool matched, string message)
    {
        CompareText.Text = message;
        CompareText.Foreground = (System.Windows.Media.Brush)FindResource(
            matched ? "AppSuccessBrush" : "AppDangerBrush");
        CompareBar.Visibility = Visibility.Visible;
    }

    private void PasteExpected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) ExpectedBox.Text = Clipboard.GetText().Trim();
            else FooterText.Text = "剪贴板中没有文本内容。";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"读取剪贴板失败：{ex.Message}";
        }
    }

    private void ClearExpected_Click(object sender, RoutedEventArgs e)
    {
        ExpectedBox.Text = string.Empty;
        CompareBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyHint()
    {
        EmptyFileHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>页面卸载：取消计算任务。</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;

        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            _plugin.LogWarn($"关闭哈希校验页面时取消任务异常：{ex.Message}");
        }
    }
}
