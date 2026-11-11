using System.Text;

namespace ClassBox.Plugins.KeyboardHijack.Core;

/// <summary>快捷键修饰键与虚拟键的展示、校验辅助。</summary>
internal static class HotkeyDefinition
{
    /// <summary>默认快捷键：Ctrl + Alt + K。</summary>
    internal const int DefaultModifiers = (int)(NativeMethods.ModControl | NativeMethods.ModAlt);

    internal const int DefaultVirtualKey = 0x4B;

    /// <summary>是否至少包含一个修饰键（避免抢占普通按键）。</summary>
    internal static bool HasModifier(int modifiers)
        => (modifiers & (int)(NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModWin)) != 0;

    /// <summary>格式化为 "Ctrl + Alt + K" 形式。</summary>
    internal static string Format(int modifiers, int virtualKey)
    {
        var builder = new StringBuilder();
        if ((modifiers & (int)NativeMethods.ModControl) != 0) builder.Append("Ctrl + ");
        if ((modifiers & (int)NativeMethods.ModAlt) != 0) builder.Append("Alt + ");
        if ((modifiers & (int)NativeMethods.ModShift) != 0) builder.Append("Shift + ");
        if ((modifiers & (int)NativeMethods.ModWin) != 0) builder.Append("Win + ");

        builder.Append(FormatKey(virtualKey));
        return builder.ToString();
    }

    /// <summary>虚拟键名（字母 / 数字 / F1-F24 / 常用功能键）。</summary>
    internal static string FormatKey(int virtualKey)
    {
        if (virtualKey >= 0x41 && virtualKey <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey >= 0x30 && virtualKey <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey >= 0x70 && virtualKey <= 0x87) return "F" + (virtualKey - 0x6F);

        return virtualKey switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x08 => "Backspace",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => "VK_" + virtualKey.ToString("X2")
        };
    }
}
