namespace ClassBox.Services;

/// <summary>应用数据路径集中管理。</summary>
public static class AppPaths
{
    public const string AppName = "Class Box";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassBox");

    /// <summary>数据根目录（与 Root 一致，供界面直接展示）。</summary>
    public static string DataRoot => Root;

    public static string ConfigDirectory { get; } = Path.Combine(Root, "config");

    public static string LogDirectory { get; } = Path.Combine(Root, "logs");

    public static string PluginDataDirectory { get; } = Path.Combine(Root, "plugins");

    public static string SettingsFile { get; } = Path.Combine(ConfigDirectory, "settings.json");

    public static string PluginsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Plugins");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(PluginDataDirectory);
    }

    public static string GetPluginDataDirectory(string pluginId)
    {
        var safe = string.Concat(pluginId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(PluginDataDirectory, safe);
        Directory.CreateDirectory(path);
        return path;
    }
}
