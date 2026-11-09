using Microsoft.Win32;

namespace ClassBox.Plugins.WebBrowser.Infrastructure;

/// <summary>
/// 让宿主进程里的 WPF WebBrowser（IE 内核）使用较新的文档模式。
/// 仅写 HKCU（当前用户，无需管理员），写入失败时静默降级为默认文档模式。
/// </summary>
internal static class BrowserHost
{
    private const string EmulationKeyPath =
        @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";

    private static bool _applied;

    /// <summary>为当前进程写入 IE11 文档模式（值 11001），只执行一次。</summary>
    public static void EnableModernEmulation()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            var exeName = Path.GetFileName(Environment.ProcessPath ?? "ClassBox.exe");
            if (string.IsNullOrWhiteSpace(exeName)) return;

            using var key = Registry.CurrentUser.CreateSubKey(EmulationKeyPath, writable: true);
            if (key is null) return;

            if (key.GetValue(exeName) is null)
            {
                key.SetValue(exeName, 11001, RegistryValueKind.DWord);
            }
        }
        catch
        {
            // 受策略限制时忽略：WebBrowser 仍可用，只是文档模式较旧
        }
    }
}
