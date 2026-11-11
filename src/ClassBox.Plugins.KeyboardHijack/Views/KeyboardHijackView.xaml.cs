using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassBox.Plugins.KeyboardHijack.Core;

namespace ClassBox.Plugins.KeyboardHijack.Views;

/// <summary>键盘接管插件主页面。</summary>
public partial class KeyboardHijackView : UserControl
{
    private readonly KeyboardHijackPlugin _plugin;
    private bool _ready;
    private bool _suppress;
    private bool _recording;

    public KeyboardHijackView(KeyboardHijackPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

        // InitializeComponent 期间控件字段尚未赋值，所有业务逻辑必须等 _ready 置位后执行
        InitializeComponent();
        _ready = true;

        RefreshAll();
    }

    private void RefreshAll()
    {
        if (!_ready) return;

        _suppress = true;
        try
        {
            var settings = _plugin.Settings;

            ToggleEnabled.IsChecked = settings.HotkeyEnabled;
            ToggleTrackRestore.IsChecked = settings.TrackRestore;
            TxtAutoRestore.Text = settings.AutoRestoreSeconds.ToString();
            TxtHotkey.Text = HotkeyDefinition.Format(settings.HotkeyModifiers, settings.HotkeyVirtualKey);

            LstRules.ItemsSource = null;
            LstRules.ItemsSource = settings.Rules;
        }
        finally
        {
            _suppress = false;
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_ready) return;

        TxtHotkeyStatus.Text = _plugin.HotkeyStatus;
        TxtSummary.Text = $"白名单规则 {_plugin.Settings.Rules.Count} 条 · 上次接管可还原窗口 {_plugin.PendingRestoreCount} 个";
    }

    private void ToggleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppress) return;

        _plugin.Settings.HotkeyEnabled = ToggleEnabled.IsChecked == true;
        _plugin.SaveSettings();
        _plugin.ApplyHotkey();
        UpdateStatus();
    }

    private void ToggleTrackRestore_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppress) return;

        _plugin.Settings.TrackRestore = ToggleTrackRestore.IsChecked == true;
        _plugin.SaveSettings();
        UpdateStatus();
    }

    private void TxtAutoRestore_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppress) return;

        var seconds = 0;
        if (int.TryParse(TxtAutoRestore.Text?.Trim(), out var parsed)) seconds = parsed;
        seconds = Math.Clamp(seconds, 0, 600);

        TxtAutoRestore.Text = seconds.ToString();
        _plugin.Settings.AutoRestoreSeconds = seconds;
        _plugin.SaveSettings();
    }

    private void BtnApplyHotkey_Click(object sender, RoutedEventArgs e)
    {
        _plugin.ApplyHotkey();
        UpdateStatus();
    }

    private void BtnResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        _plugin.ResetHotkey();
        RefreshAll();
    }

    private void BtnHijack_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = _plugin.RunHijack();
            var preserved = result.PreservedWindows.Count;
            TxtSummary.Text = $"本次接管：最小化 {result.MinimizedCount} 个窗口，保留白名单窗口 {preserved} 个"
                              + (preserved > 0 ? $"（{string.Join("；", result.PreservedWindows.Take(3))}）" : string.Empty);
        }
        catch (Exception ex)
        {
            TxtSummary.Text = $"执行接管失败：{ex.Message}";
        }

        UpdateStatus();
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var restored = _plugin.RestoreLast();
            TxtSummary.Text = restored > 0
                ? $"已还原 {restored} 个窗口"
                : "没有可还原的窗口（可能需要先勾选「记录被最小化的窗口」）";
        }
        catch (Exception ex)
        {
            TxtSummary.Text = $"还原失败：{ex.Message}";
        }

        UpdateStatus();
    }

    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
    {
        var value = TxtRuleInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            TxtSummary.Text = "请先输入进程名或标题关键字";
            return;
        }

        var kind = CmbRuleKind.SelectedIndex == 1 ? "title" : "process";
        var added = _plugin.AddRule(kind, value);

        TxtSummary.Text = added
            ? $"已添加白名单：{(kind == "title" ? "标题包含" : "进程名")}：{value}"
            : "该规则已存在";

        TxtRuleInput.Text = string.Empty;
        RefreshAll();
    }

    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        var info = _plugin.CaptureForegroundWindow();

        if (info is null || string.IsNullOrWhiteSpace(info.ProcessName))
        {
            TxtSummary.Text = "未能获取当前活动窗口，请先切换到目标窗口再点击";
            return;
        }

        TxtSummary.Text = $"已把当前活动窗口（{info.ProcessName} · {info.DisplayName}）加入白名单";
        RefreshAll();
    }

    private void BtnClearRules_Click(object sender, RoutedEventArgs e)
    {
        _plugin.ClearRules();
        TxtSummary.Text = "已清空白名单";
        RefreshAll();
    }

    private void BtnRemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not WhitelistRule rule) return;

        _plugin.RemoveRule(rule);
        TxtSummary.Text = $"已移除白名单规则：{rule.Display}";
        RefreshAll();
    }

    // ---------- 快捷键录制 ----------

    private void RecordBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_ready) return;

        RecordBox.Focus();
        Keyboard.Focus(RecordBox);
        BeginRecording();
    }

    private void RecordBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_ready) return;
        BeginRecording();
    }

    private void RecordBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_ready) return;

        _recording = false;
        TxtHotkey.Text = HotkeyDefinition.Format(_plugin.Settings.HotkeyModifiers, _plugin.Settings.HotkeyVirtualKey);
    }

    private void BeginRecording()
    {
        _recording = true;
        TxtHotkey.Text = "请按下组合键…";
        TxtHotkeyStatus.Text = "录制中：按下组合键（需含 Ctrl / Alt / Shift / Win），Esc 取消";
    }

    private void RecordBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_ready || !_recording) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            e.Handled = true;
            _recording = false;
            Keyboard.ClearFocus();
            TxtHotkey.Text = HotkeyDefinition.Format(_plugin.Settings.HotkeyModifiers, _plugin.Settings.HotkeyVirtualKey);
            TxtHotkeyStatus.Text = "已取消录制";
            return;
        }

        if (IsModifierKey(key))
        {
            e.Handled = true;
            TxtHotkey.Text = DescribeModifiers(CurrentModifiers()) + "…";
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0) return;

        var modifiers = CurrentModifiers();
        if (!HotkeyDefinition.HasModifier(modifiers))
        {
            e.Handled = true;
            TxtHotkeyStatus.Text = "快捷键必须包含 Ctrl / Alt / Shift / Win 中的至少一个修饰键";
            return;
        }

        e.Handled = true;
        _recording = false;

        var status = _plugin.ChangeHotkey(modifiers, virtualKey);
        Keyboard.ClearFocus();
        RefreshAll();
        TxtHotkeyStatus.Text = status;
    }

    private static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin
            or Key.System;

    private static int CurrentModifiers()
    {
        var modifiers = 0;
        var live = Keyboard.Modifiers;

        if (live.HasFlag(ModifierKeys.Control)) modifiers |= (int)NativeMethods.ModControl;
        if (live.HasFlag(ModifierKeys.Alt)) modifiers |= (int)NativeMethods.ModAlt;
        if (live.HasFlag(ModifierKeys.Shift)) modifiers |= (int)NativeMethods.ModShift;
        if (live.HasFlag(ModifierKeys.Windows)) modifiers |= (int)NativeMethods.ModWin;

        return modifiers;
    }

    private static string DescribeModifiers(int modifiers)
    {
        var text = string.Empty;
        if ((modifiers & (int)NativeMethods.ModControl) != 0) text += "Ctrl + ";
        if ((modifiers & (int)NativeMethods.ModAlt) != 0) text += "Alt + ";
        if ((modifiers & (int)NativeMethods.ModShift) != 0) text += "Shift + ";
        if ((modifiers & (int)NativeMethods.ModWin) != 0) text += "Win + ";
        return text.TrimEnd(' ', '+');
    }
}
