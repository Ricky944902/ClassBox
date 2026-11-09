using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.NotesPad.Notes;
using Microsoft.Win32;

namespace ClassBox.Plugins.NotesPad.Views;

/// <summary>速记便笺页面。</summary>
public partial class NotesPadView : UserControl
{
    private readonly NotesPadPlugin _plugin;
    private readonly NoteStore _store;
    private readonly DispatcherTimer _autoSaveTimer;
    private NoteItem? _current;
    private NoteItem? _lastDeleted;
    private bool _ready;
    private bool _closed;
    private bool _loading;
    private bool _dirty;

    public NotesPadView(NotesPadPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        _store = new NoteStore(plugin.Host.DataDirectory, plugin.Host.Logger);
        _store.Load();

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _autoSaveTimer.Tick += OnAutoSaveTick;

        _ready = true;
        ReloadList(null);
        StatusText.Text = $"共 {_store.Notes.Count} 条便笺 · 数据文件：{_store.FilePath}";
    }

    /// <summary>页面关闭时调用：立即落盘并停止定时器。</summary>
    public void Shutdown()
    {
        _closed = true;
        _ready = false;
        _autoSaveTimer.Stop();
        try
        {
            FlushSave(silent: true);
        }
        catch
        {
            // 关闭阶段不再抛出。
        }
    }

    // ===== 便笺列表 =====

    private void ReloadList(NoteItem? select)
    {
        if (!_ready || _closed) return;

        var notes = _store.Query(SearchBox.Text ?? string.Empty);
        NoteList.ItemsSource = null;
        NoteList.ItemsSource = notes;

        if (EmptyHint is not null)
        {
            EmptyHint.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = _store.Notes.Count == 0
                ? "暂无便笺，点击「新建便笺」开始。"
                : "没有匹配的便笺。";
        }

        var target = select ?? _current;
        if (target is not null && notes.Contains(target))
        {
            NoteList.SelectedItem = target;
        }
        else if (notes.Count > 0)
        {
            NoteList.SelectedIndex = 0;
        }
        else
        {
            LoadNoteIntoEditor(null);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        ReloadList(null);
    }

    private void OnNoteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;

        // 切换前先保存上一条，避免内容丢失。
        FlushSave(silent: true);
        LoadNoteIntoEditor(NoteList.SelectedItem as NoteItem);
    }

    private void LoadNoteIntoEditor(NoteItem? note)
    {
        _current = note;
        _loading = true;
        try
        {
            if (note is null)
            {
                TitleBox.Text = string.Empty;
                ContentBox.Text = string.Empty;
                PinCheck.IsChecked = false;
                TitleBox.IsEnabled = false;
                ContentBox.IsEnabled = false;
                ExportButton.IsEnabled = false;
                EditorHint.Text = "当前没有可用便笺。";
            }
            else
            {
                TitleBox.Text = note.Title;
                ContentBox.Text = note.Content;
                PinCheck.IsChecked = note.IsPinned;
                TitleBox.IsEnabled = true;
                ContentBox.IsEnabled = true;
                ExportButton.IsEnabled = true;
                EditorHint.Text = $"创建于 {note.CreatedAt:yyyy-MM-dd HH:mm:ss} · 修改于 {note.UpdatedAt:yyyy-MM-dd HH:mm:ss}";
            }
        }
        finally
        {
            _loading = false;
        }

        _dirty = false;
        SaveStateText.Text = note is null ? string.Empty : "已保存";
        UpdateCharCount();
        PathText.Text = Path.GetFileName(_store.FilePath);
    }

    // ===== 编辑与自动保存 =====

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed || _loading) return;
        MarkDirty();
    }

    private void OnContentChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed || _loading) return;
        UpdateCharCount();
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_current is null) return;
        _dirty = true;
        SaveStateText.Text = "待保存…";
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void OnAutoSaveTick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (!_ready || _closed) return;
        FlushSave(silent: false);
    }

    private void FlushSave(bool silent)
    {
        if (_current is null || !_dirty) return;

        _current.Title = TitleBox.Text ?? string.Empty;
        _current.Content = ContentBox.Text ?? string.Empty;
        _store.Touch(_current);
        _store.Save();

        _dirty = false;
        if (SaveStateText is not null)
        {
            SaveStateText.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
        }

        if (EditorHint is not null)
        {
            EditorHint.Text = $"创建于 {_current.CreatedAt:yyyy-MM-dd HH:mm:ss} · 修改于 {_current.UpdatedAt:yyyy-MM-dd HH:mm:ss}";
        }

        if (!silent && StatusText is not null)
        {
            StatusText.Text = $"已保存「{_current.DisplayTitle}」";
        }
    }

    private void UpdateCharCount()
    {
        if (CharCountText is null) return;
        var text = ContentBox.Text ?? string.Empty;
        var chars = text.Length;
        var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
        CharCountText.Text = $"字数 {chars} · 行数 {lines}";
    }

    // ===== 便笺操作 =====

    private void OnNewNoteClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        FlushSave(silent: true);
        try
        {
            var index = _store.Notes.Count + 1;
            var note = _store.Create($"新便笺 {index}");
            ReloadList(note);
            LoadNoteIntoEditor(note);
            TitleBox.Focus();
            TitleBox.SelectAll();
            StatusText.Text = $"已新建「{note.DisplayTitle}」";
        }
        catch (Exception ex)
        {
            _plugin.LogError("新建便笺失败", ex);
            StatusText.Text = "新建失败：" + ex.Message;
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed || _current is null) return;
        TitleBox.Focus();
        TitleBox.SelectAll();
        StatusText.Text = "直接修改标题即可重命名，自动保存";
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed || _current is null) return;

        _current.IsPinned = PinCheck.IsChecked == true;
        try
        {
            _store.Touch(_current);
            _store.Save();
            var keep = _current;
            ReloadList(keep);
            StatusText.Text = _current.IsPinned ? "已置顶该便笺" : "已取消置顶";
        }
        catch (Exception ex)
        {
            _plugin.LogError("设置置顶失败", ex);
        }
    }

    private void OnDeleteNoteClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed || _current is null) return;

        var note = _current;
        try
        {
            _dirty = false;
            _autoSaveTimer.Stop();
            _store.Delete(note);
            _lastDeleted = note;
            UndoDeleteButton.Visibility = Visibility.Visible;
            StatusText.Text = $"已删除「{note.DisplayTitle}」，可点击「撤销删除」恢复";
            _current = null;
            ReloadList(null);
            Notify("已删除便笺", note.DisplayTitle);
        }
        catch (Exception ex)
        {
            _plugin.LogError("删除便笺失败", ex);
            StatusText.Text = "删除失败：" + ex.Message;
        }
    }

    private void OnUndoDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed || _lastDeleted is null) return;

        try
        {
            var note = _lastDeleted;
            _store.Restore(note);
            _lastDeleted = null;
            UndoDeleteButton.Visibility = Visibility.Collapsed;
            ReloadList(note);
            LoadNoteIntoEditor(note);
            StatusText.Text = $"已恢复「{note.DisplayTitle}」";
        }
        catch (Exception ex)
        {
            _plugin.LogError("撤销删除失败", ex);
            StatusText.Text = "撤销失败：" + ex.Message;
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed || _current is null) return;

        FlushSave(silent: true);
        var dialog = new SaveFileDialog
        {
            Title = "导出便笺为 txt",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = NoteStore.SafeFileName(_current.DisplayTitle),
            DefaultExt = ".txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var path = NoteStore.Export(_current, dialog.FileName);
            StatusText.Text = "已导出：" + path;
            Notify("便笺已导出", path);
            _plugin.LogInfo("便笺导出成功：" + path);
        }
        catch (Exception ex)
        {
            _plugin.LogError("导出便笺失败", ex);
            StatusText.Text = "导出失败：" + ex.Message;
        }
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
