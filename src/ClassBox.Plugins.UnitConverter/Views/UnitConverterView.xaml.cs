using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.UnitConverter.Converting;

namespace ClassBox.Plugins.UnitConverter.Views;

/// <summary>单位换算页面：八大类单位双向即时换算，并给出常用换算对照表。</summary>
public partial class UnitConverterView : UserControl
{
    private enum StatusKind
    {
        Info,
        Ok,
        Warn,
        Error
    }

    private readonly UnitConverterPlugin _plugin;
    private bool _ready;
    private bool _closed;
    private bool _suppress;

    public UnitConverterView(UnitConverterPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        CategoryBox.ItemsSource = UnitEngine.Categories;
        CategoryBox.SelectedIndex = 0;

        _ready = true;
        LoadCategory(0);
        FromValueBox.Text = "1";
        ConvertFromLeft();
    }

    public void Shutdown()
    {
        _closed = true;
    }

    private UnitCategory? CurrentCategory =>
        CategoryBox?.SelectedIndex >= 0 && CategoryBox.SelectedIndex < UnitEngine.Categories.Count
            ? UnitEngine.Categories[CategoryBox.SelectedIndex]
            : null;

    private UnitDef? FromUnit =>
        CurrentCategory is not null && FromUnitBox.SelectedIndex >= 0 && FromUnitBox.SelectedIndex < CurrentCategory.Units.Count
            ? CurrentCategory.Units[FromUnitBox.SelectedIndex]
            : null;

    private UnitDef? ToUnit =>
        CurrentCategory is not null && ToUnitBox.SelectedIndex >= 0 && ToUnitBox.SelectedIndex < CurrentCategory.Units.Count
            ? CurrentCategory.Units[ToUnitBox.SelectedIndex]
            : null;

    // ===================== 事件 =====================

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        LoadCategory(CategoryBox.SelectedIndex);
        ConvertFromLeft();
    }

    private void LoadCategory(int index)
    {
        if (index < 0 || index >= UnitEngine.Categories.Count) return;

        var category = UnitEngine.Categories[index];
        var items = category.Units.Select(u => u.Display).ToList();

        _suppress = true;
        FromUnitBox.ItemsSource = items;
        ToUnitBox.ItemsSource = items;
        FromUnitBox.SelectedIndex = 0;
        ToUnitBox.SelectedIndex = items.Count > 1 ? 1 : 0;
        _suppress = false;

        if (CategoryBox is not null)
        {
            SetStatus($"已切换到「{category.Name}」，共 {items.Count} 个单位。", StatusKind.Info);
        }

        UpdateTable();
    }

    private void Unit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed || _suppress) return;
        ConvertFromLeft();
    }

    private void FromValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed || _suppress) return;
        ConvertFromLeft();
    }

    private void ToValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed || _suppress) return;
        ConvertFromRight();
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var fromIndex = FromUnitBox.SelectedIndex;
        var toIndex = ToUnitBox.SelectedIndex;
        var fromText = FromValueBox.Text;
        var toText = ToValueBox.Text;

        _suppress = true;
        FromUnitBox.SelectedIndex = toIndex;
        ToUnitBox.SelectedIndex = fromIndex;
        FromValueBox.Text = toText;
        ToValueBox.Text = fromText;
        _suppress = false;

        ConvertFromLeft();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        _suppress = true;
        FromValueBox.Text = "1";
        _suppress = false;
        ConvertFromLeft();
        SetStatus("已重置换算数值。", StatusKind.Info);
    }

    // ===================== 换算 =====================

    private void ConvertFromLeft()
    {
        var from = FromUnit;
        var to = ToUnit;
        if (from is null || to is null) return;

        if (string.IsNullOrWhiteSpace(FromValueBox.Text))
        {
            _suppress = true;
            ToValueBox.Text = string.Empty;
            _suppress = false;
            UpdateTable();
            return;
        }

        if (!TryParse(FromValueBox.Text, out var value))
        {
            SetStatus("原始数值不是有效数字，请检查输入（支持 1.5、-20、1e3 等格式）。", StatusKind.Warn);
            return;
        }

        var converted = UnitEngine.Convert(from, to, value);
        _suppress = true;
        ToValueBox.Text = UnitEngine.FormatNumber(converted);
        _suppress = false;

        UpdateTable();
        SetStatus($"{UnitEngine.FormatNumber(value)} {from.Name} = {UnitEngine.FormatNumber(converted)} {to.Name}", StatusKind.Ok);
    }

    private void ConvertFromRight()
    {
        var from = FromUnit;
        var to = ToUnit;
        if (from is null || to is null) return;

        if (string.IsNullOrWhiteSpace(ToValueBox.Text))
        {
            _suppress = true;
            FromValueBox.Text = string.Empty;
            _suppress = false;
            return;
        }

        if (!TryParse(ToValueBox.Text, out var value))
        {
            SetStatus("换算结果不是有效数字，请检查输入。", StatusKind.Warn);
            return;
        }

        var converted = UnitEngine.Convert(to, from, value);
        _suppress = true;
        FromValueBox.Text = UnitEngine.FormatNumber(converted);
        _suppress = false;

        UpdateTable();
        SetStatus($"{UnitEngine.FormatNumber(value)} {to.Name} = {UnitEngine.FormatNumber(converted)} {from.Name}", StatusKind.Ok);
    }

    private void UpdateTable()
    {
        if (TableView is null) return;

        var category = CurrentCategory;
        var unit = FromUnit;
        if (category is null || unit is null)
        {
            TableView.Text = string.Empty;
            return;
        }

        var value = TryParse(FromValueBox?.Text ?? "1", out var parsed) ? parsed : 1;
        if (value == 0) value = 1;

        TableView.Text = UnitEngine.BuildTable(category, unit, value);
        if (TableHeader is not null) TableHeader.Text = $"{category.Name} · {UnitEngine.FormatNumber(value)} {unit.Name} 换算对照";
        if (TableHint is not null) TableHint.Text = $"同类共 {category.Units.Count} 个单位";
    }

    private static bool TryParse(string text, out double value)
    {
        var trimmed = text.Trim().Replace(" ", string.Empty).Replace(",", string.Empty);
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    // ===================== 复制 =====================

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        var from = FromUnit;
        var to = ToUnit;
        if (from is null || to is null || string.IsNullOrWhiteSpace(ToValueBox.Text))
        {
            SetStatus("当前没有可复制的换算结果。", StatusKind.Warn);
            return;
        }

        CopyText($"{FromValueBox.Text} {from.Name} = {ToValueBox.Text} {to.Name}", "换算结果");
    }

    private void CopyTable_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TableView.Text))
        {
            SetStatus("当前没有可复制的对照表。", StatusKind.Warn);
            return;
        }

        CopyText(TableView.Text, "换算对照表");
    }

    private void CopyText(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus($"已复制{what}到剪贴板。", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("复制到剪贴板失败", ex);
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        if (StatusText is null) return;
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            StatusKind.Ok => "AppSuccessBrush",
            StatusKind.Warn => "AppWarningBrush",
            StatusKind.Error => "AppDangerBrush",
            _ => "AppTextSecondaryBrush"
        });
    }
}
