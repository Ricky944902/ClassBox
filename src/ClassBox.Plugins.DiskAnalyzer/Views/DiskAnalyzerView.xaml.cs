using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassBox.Plugins.DiskAnalyzer.Analysis;
using Microsoft.Win32;

namespace ClassBox.Plugins.DiskAnalyzer.Views;

/// <summary>磁盘空间分析页面。</summary>
public partial class DiskAnalyzerView : UserControl
{
    private readonly DiskAnalyzerPlugin _plugin;
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _closed;

    public DiskAnalyzerView(DiskAnalyzerPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
    }

    private int TopCount => TopCombo.SelectedIndex switch
    {
        0 => 10,
        2 => 50,
        _ => 20
    };

    // ===================== 目录选择 =====================

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要分析的目录",
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        PathText.Text = dialog.FolderName;
        ProgressText.Text = "已选择目录，点击「开始分析」开始统计。";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var root = PathText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root))
        {
            ProgressText.Text = "请先选择一个存在的目录。";
            return;
        }

        _running = true;
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.IsIndeterminate = true;
        FolderList.ItemsSource = null;
        FileList.ItemsSource = null;
        EmptyFolderHint.Visibility = Visibility.Visible;
        EmptyFileHint.Visibility = Visibility.Visible;
        TotalSizeText.Text = "-";
        TotalFilesText.Text = "-";
        TotalFoldersText.Text = "-";
        ElapsedText.Text = "-";

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        ProgressText.Text = $"正在分析 {root} …";

        try
        {
            var progress = new Progress<AnalyzeProgress>(p =>
            {
                ProgressText.Text =
                    $"已扫描 {p.FileCount} 个文件 / 累计 {SizeFormatter.Format(p.TotalSize)}" +
                    $"\n当前：{p.CurrentDirectory}";
            });

            var result = await DiskAnalyzerEngine.AnalyzeAsync(root, TopCount, progress, token);

            FolderList.ItemsSource = result.Folders;
            FileList.ItemsSource = result.Files;
            EmptyFolderHint.Visibility = result.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyFileHint.Visibility = result.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            TotalSizeText.Text = result.TotalSizeText;
            TotalFilesText.Text = $"{result.FileCount:N0}";
            TotalFoldersText.Text = $"{result.FolderCount:N0}";
            ElapsedText.Text = result.ElapsedText;

            ProgressText.Text = $"分析完成：{result.RootPath}";
            FooterText.Text = result.SkippedCount > 0
                ? $"提示：{result.SkippedCount} 个目录/文件因权限不足或占用被跳过。双击条目可在资源管理器中打开。"
                : "提示：双击条目可在资源管理器中打开所在位置。";

            _plugin.LogInfo($"目录分析完成：{result.RootPath}，总大小 {result.TotalSizeText}，文件 {result.FileCount} 个，耗时 {result.ElapsedText}");
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消分析。";
            FooterText.Text = "分析已取消，未产生任何文件变更。";
            _plugin.LogInfo("用户取消了目录分析");
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"分析失败：{ex.Message}";
            FooterText.Text = $"目录分析失败：{ex.Message}";
            _plugin.LogError("目录分析异常", ex);
        }
        finally
        {
            _running = false;
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            Progress.IsIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        try
        {
            _cts?.Cancel();
            ProgressText.Text = "正在取消…";
        }
        catch (ObjectDisposedException)
        {
            // 已完成
        }
    }

    // ===================== 打开位置 / 复制路径 =====================

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedFolder();

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedFile();

    private void OpenFolderLocation_Click(object sender, RoutedEventArgs e) => OpenSelectedFolder();

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e) => OpenSelectedFile();

    private void OpenSelectedFolder()
    {
        if (FolderList.SelectedItem is not FolderUsage item) return;
        OpenInExplorer(item.Path, false);
    }

    private void OpenSelectedFile()
    {
        if (FileList.SelectedItem is not FileUsage item) return;
        OpenInExplorer(item.Path, true);
    }

    private void OpenInExplorer(string path, bool selectFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var arguments = selectFile && System.IO.File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{path}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            FooterText.Text = $"打开资源管理器失败：{ex.Message}";
            _plugin.LogWarn($"打开资源管理器失败：{path} - {ex.Message}");
        }
    }

    private void CopyFolderPath_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is FolderUsage item) CopyPath(item.Path);
    }

    private void CopyFilePath_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileUsage item) CopyPath(item.Path);
    }

    private void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
            FooterText.Text = $"已复制路径：{path}";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>页面卸载：取消正在进行的分析。</summary>
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
            _plugin.LogWarn($"关闭分析页面时取消任务异常：{ex.Message}");
        }
    }
}
