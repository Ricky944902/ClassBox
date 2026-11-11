using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClassBox.Plugins.KeyboardHijack.Core;

/// <summary>
/// 全局热键注册器：基于消息专用窗口（HWND_MESSAGE）+ RegisterHotKey 实现，
/// 零第三方依赖，必须在具备消息循环的线程（WPF UI 线程）上创建。
/// </summary>
internal sealed class HotkeyRegistrar : IDisposable
{
    private const int HotkeyId = 0x4B48;

    private HwndSource? _source;
    private bool _disposed;

    /// <summary>热键被按下（已在 UI 线程触发）。</summary>
    internal event EventHandler? HotkeyPressed;

    internal bool IsRegistered { get; private set; }

    internal int Modifiers { get; private set; }

    internal int VirtualKey { get; private set; }

    /// <summary>
    /// 注册（或替换）全局热键。失败时返回 false 并给出可读原因，绝不抛异常。
    /// </summary>
    internal bool Register(int modifiers, int virtualKey, out string error)
    {
        error = string.Empty;

        if (_disposed)
        {
            error = "热键组件已释放";
            return false;
        }

        if (!HotkeyDefinition.HasModifier(modifiers))
        {
            error = "快捷键必须包含 Ctrl / Alt / Shift / Win 中的至少一个修饰键";
            return false;
        }

        Unregister();

        if (!EnsureSource())
        {
            error = "创建热键消息窗口失败";
            return false;
        }

        var handle = _source!.Handle;
        if (!NativeMethods.RegisterHotKey(handle, HotkeyId,
                (uint)(modifiers | (int)NativeMethods.ModNoRepeat), (uint)virtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == NativeMethods.ErrorHotkeyAlreadyRegistered
                ? "该快捷键已被其它程序占用，请更换组合"
                : $"系统拒绝注册该快捷键（错误码 {code}）";
            return false;
        }

        Modifiers = modifiers;
        VirtualKey = virtualKey;
        IsRegistered = true;
        return true;
    }

    /// <summary>注销热键并释放消息窗口（可重复调用）。</summary>
    internal void Unregister()
    {
        if (_source is null) return;

        if (IsRegistered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            IsRegistered = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            Unregister();
            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
        catch
        {
            // 退出阶段吞掉异常，避免影响宿主关闭
        }
    }

    private bool EnsureSource()
    {
        if (_source is not null) return true;

        try
        {
            var parameters = new HwndSourceParameters("ClassBoxKeyboardHijackHotkeyWindow")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ParentWindow = NativeMethods.HwndMessage
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            return true;
        }
        catch
        {
            _source = null;
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}
