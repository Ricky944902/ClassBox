using System.Windows;
using System.Windows.Media;

namespace ClassBox.Services;

/// <summary>
/// 主题管理器：通过替换 Application.Resources 中的调色板字典实现深浅色切换。
/// 使用 DynamicResource 的控件（含插件程序集中的页面）会自动跟随。
/// </summary>
public static class ThemeManager
{
    private const string LightUri = "pack://application:,,,/ClassBox.Abstractions;component/Themes/Palette.Light.xaml";
    private const string DarkUri = "pack://application:,,,/ClassBox.Abstractions;component/Themes/Palette.Dark.xaml";

    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Apply(bool dark)
    {
        IsDark = dark;

        var app = Application.Current;
        if (app is null) return;

        try
        {
            var palette = new ResourceDictionary
            {
                Source = new Uri(dark ? DarkUri : LightUri, UriKind.Absolute)
            };

            var dictionaries = app.Resources.MergedDictionaries;
            var replaced = false;

            for (var i = 0; i < dictionaries.Count; i++)
            {
                var source = dictionaries[i].Source?.OriginalString;
                if (source is null) continue;
                if (!source.Contains("Palette.", StringComparison.OrdinalIgnoreCase)) continue;

                dictionaries[i] = palette;
                replaced = true;
                break;
            }

            if (!replaced && dictionaries.Count > 0)
            {
                dictionaries.Insert(0, palette);
            }
        }
        catch
        {
            // 主题切换失败不应影响应用运行
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static Brush AccentBrush
        => (Application.Current?.TryFindResource("AppAccentBrush") as Brush) ?? Brushes.DodgerBlue;
}
