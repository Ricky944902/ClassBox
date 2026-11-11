using System.Windows;
using System.Windows.Threading;
using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.KeyboardHijack.Core;
using ClassBox.Plugins.KeyboardHijack.Views;

namespace ClassBox.Plugins.KeyboardHijack;

/// <summary>
/// 键盘接管插件：注册一个全局快捷键，按下后最小化除白名单外的所有可见窗口，
/// 并可选记录被最小化的窗口以便一键还原。实现完全基于 Win32，无第三方依赖。
/// </summary>
public sealed class KeyboardHijackPlugin : PluginBase
{
    private const string ConfigKey = "keyboard-hijack";
    private const string PageId = "classbox.page.keyboardhijack";

    private HotkeyRegistrar? _registrar;
    private DispatcherTimer? _autoRestoreTimer;

    /// <summary>当前配置（修改后调用 <see cref="SaveSettings"/> 落盘）。</summary>
    public HijackSettings Settings { get; private set; } = new();

    /// <summary>接管引擎。</summary>
    internal WindowHijackEngine Engine { get; private set; } = null!;

    /// <summary>热键状态描述，供界面展示。</summary>
    public string HotkeyStatus { get; private set; } = "未注册";

    /// <summary>是否成功注册了全局热键。</summary>
    public bool IsHotkeyRegistered => _registrar?.IsRegistered == true;

    /// <summary>上次接管中被最小化的窗口数量。</summary>
    public int PendingRestoreCount => Engine?.MinimizedWindows.Count ?? 0;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "classbox.plugin.keyboardhijack",
        Name = "键盘接管",
        Description = "按下自定义全局快捷键，一键最小化除白名单外的所有窗口，支持一键还原。",
        Author = "Class Box",
        Version = new Version(1, 0, 0),
        Category = "效率工具",
        Glyph = "\uE765",
        IsBuiltIn = false
    };

    public override int SortOrder => 530;

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Engine = new WindowHijackEngine(Context.Windows, Context.Logger);

        var loaded = Context.Configuration.Load(ConfigKey, new HijackSettings());
        Settings = loaded ?? new HijackSettings();
        Settings.Rules ??= new List<WhitelistRule>();

        _registrar = new HotkeyRegistrar();
        _registrar.HotkeyPressed += OnHotkeyPressed;

        Context.Navigation.RegisterPage(new PluginPageDescriptor
        {
            PageId = PageId,
            Title = "键盘接管",
            Description = "快捷键一键最小化其它窗口",
            Glyph = "\uE765",
            Order = SortOrder,
            ViewFactory = () => new KeyboardHijackView(this)
        });

        return Task.CompletedTask;
    }

    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        ApplyHotkey();
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        StopAutoRestoreTimer();
        UnregisterHotkey();
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (_registrar is not null)
            {
                _registrar.HotkeyPressed -= OnHotkeyPressed;
                _registrar.Dispose();
                _registrar = null;
            }
        }
        catch
        {
            // 释放阶段吞掉异常
        }
    }

    /// <summary>按当前配置重新注册热键，返回可读状态文本。</summary>
    public string ApplyHotkey()
    {
        if (_registrar is null)
        {
            HotkeyStatus = "热键组件未就绪";
            return HotkeyStatus;
        }

        if (!Settings.HotkeyEnabled)
        {
            UnregisterHotkey();
            HotkeyStatus = "快捷键已关闭（可在下方重新启用）";
            return HotkeyStatus;
        }

        var modifiers = Settings.HotkeyModifiers;
        var virtualKey = Settings.HotkeyVirtualKey;
        var formatted = HotkeyDefinition.Format(modifiers, virtualKey);

        string? error = null;
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        void Work()
        {
            if (!_registrar.Register(modifiers, virtualKey, out var message))
            {
                error = message;
            }
        }

        try
        {
            if (dispatcher.CheckAccess()) Work();
            else dispatcher.Invoke(Work);
        }
        catch (Exception ex)
        {
            error = $"注册热键异常：{ex.Message}";
        }

        if (error is not null)
        {
            HotkeyStatus = error;
            Context.Logger.Warn($"键盘接管热键注册失败：{error}");
            return HotkeyStatus;
        }

        HotkeyStatus = $"已注册：{formatted}";
        Context.Logger.Info($"键盘接管热键已注册：{formatted}");
        return HotkeyStatus;
    }

    /// <summary>注销全局热键（插件停止 / 被禁用时调用）。</summary>
    public void UnregisterHotkey()
    {
        try
        {
            _registrar?.Unregister();
            HotkeyStatus = "未注册";
        }
        catch (Exception ex)
        {
            Context.Logger.Warn($"注销热键失败：{ex.Message}");
        }
    }

    /// <summary>执行一次接管（最小化除白名单外的窗口）。</summary>
    public HijackResult RunHijack(bool showAlert = true)
    {
        var result = Engine.Hijack(Settings.Rules, Settings.TrackRestore);

        if (showAlert)
        {
            Context.Alerts.Show(new SideAlertRequest
            {
                Title = "键盘接管",
                Message = result.MinimizedCount > 0
                    ? $"已最小化 {result.MinimizedCount} 个窗口，保留 {result.PreservedWindows.Count} 个白名单窗口"
                    : "没有可最小化的窗口（可能都被白名单保留）",
                Glyph = "\uE765",
                AutoCloseSeconds = 4
            });
        }

        ScheduleAutoRestore();
        return result;
    }

    /// <summary>还原上次被最小化的窗口。</summary>
    public int RestoreLast()
    {
        StopAutoRestoreTimer();
        return Engine.RestoreAll();
    }

    /// <summary>把当前活动窗口加入白名单（按进程名）。</summary>
    public WindowInfo? CaptureForegroundWindow()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero) return null;

        var info = Context.Windows.TryGetWindow(handle);
        if (info is null) return null;

        AddRule("process", info.ProcessName);
        return info;
    }

    /// <summary>新增白名单规则（自动去重）。</summary>
    public bool AddRule(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalizedKind = string.Equals(kind, "title", StringComparison.OrdinalIgnoreCase) ? "title" : "process";
        var trimmed = value.Trim();

        if (Settings.Rules.Any(r => r.Kind == normalizedKind
                                   && string.Equals(r.Value, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Settings.Rules.Add(new WhitelistRule { Kind = normalizedKind, Value = trimmed });
        SaveSettings();
        return true;
    }

    /// <summary>移除白名单规则。</summary>
    public void RemoveRule(WhitelistRule rule)
    {
        if (rule is null) return;
        if (Settings.Rules.Remove(rule)) SaveSettings();
    }

    /// <summary>清空白名单。</summary>
    public void ClearRules()
    {
        if (Settings.Rules.Count == 0) return;
        Settings.Rules.Clear();
        SaveSettings();
    }

    /// <summary>恢复默认快捷键（Ctrl + Alt + K）并重新注册。</summary>
    public string ResetHotkey()
    {
        Settings.HotkeyModifiers = HotkeyDefinition.DefaultModifiers;
        Settings.HotkeyVirtualKey = HotkeyDefinition.DefaultVirtualKey;
        Settings.HotkeyEnabled = true;
        SaveSettings();
        return ApplyHotkey();
    }

    /// <summary>更换快捷键并立即重新注册。</summary>
    public string ChangeHotkey(int modifiers, int virtualKey)
    {
        Settings.HotkeyModifiers = modifiers;
        Settings.HotkeyVirtualKey = virtualKey;
        Settings.HotkeyEnabled = true;
        SaveSettings();
        return ApplyHotkey();
    }

    /// <summary>把配置写入插件专属配置文件。</summary>
    public void SaveSettings()
    {
        try
        {
            Context.Configuration.Save(ConfigKey, Settings);
        }
        catch (Exception ex)
        {
            Context.Logger.Error("保存键盘接管配置失败", ex);
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        try
        {
            RunHijack();
        }
        catch (Exception ex)
        {
            Context.Logger.Error("处理热键触发失败", ex);
        }
    }

    private void ScheduleAutoRestore()
    {
        StopAutoRestoreTimer();

        if (!Settings.TrackRestore || Settings.AutoRestoreSeconds <= 0) return;
        if (PendingRestoreCount == 0) return;

        _autoRestoreTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Settings.AutoRestoreSeconds)
        };

        _autoRestoreTimer.Tick += (_, _) =>
        {
            StopAutoRestoreTimer();
            var restored = RestoreLast();
            if (restored > 0)
            {
                Context.Alerts.Show(new SideAlertRequest
                {
                    Title = "键盘接管",
                    Message = $"已自动还原 {restored} 个窗口",
                    Glyph = "\uE777",
                    AutoCloseSeconds = 4
                });
            }
        };

        _autoRestoreTimer.Start();
    }

    private void StopAutoRestoreTimer()
    {
        if (_autoRestoreTimer is null) return;
        _autoRestoreTimer.Stop();
        _autoRestoreTimer = null;
    }
}
