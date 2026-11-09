using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ClassBox.Plugins.PasswordGen.Generating;

namespace ClassBox.Plugins.PasswordGen.Views;

/// <summary>密码生成页面：可配置字符集批量生成密码，并给出强度与破解时间评估。</summary>
public partial class PasswordGenView : UserControl
{
    private enum StatusKind
    {
        Info,
        Ok,
        Warn,
        Error
    }

    /// <summary>生成记录条目。</summary>
    public sealed class HistoryItem
    {
        public string Time { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        public int Entropy { get; init; }

        public string EntropyText => $"{Entropy} bit";
    }

    private const int HistoryLimit = 20;

    private readonly PasswordGenPlugin _plugin;
    private readonly ObservableCollection<HistoryItem> _history = new();
    private bool _ready;
    private bool _closed;
    private bool _suppressHistorySelection;
    private double _strengthRatio;

    public PasswordGenView(PasswordGenPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        HistoryList.ItemsSource = _history;

        _ready = true;
        GenerateOne(addToHistory: true);
    }

    public void Shutdown()
    {
        _closed = true;
    }

    // ===================== 选项 =====================

    private void LengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LengthText is not null) LengthText.Text = $"{(int)LengthSlider.Value} 位";
        if (!_ready || _closed) return;
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;

        var options = BuildOptions();
        var pools = PasswordEngine.BuildPools(options, out var error);
        if (error is not null)
        {
            SetStatus(error, StatusKind.Warn);
            return;
        }

        SetStatus($"当前字符集可用字符 {string.Concat(pools).Length} 种，长度 {options.Length} 位。", StatusKind.Info);
    }

    private PasswordOptions BuildOptions() => new()
    {
        Length = (int)Math.Round(LengthSlider.Value),
        UseUpper = UpperBox.IsChecked == true,
        UseLower = LowerBox.IsChecked == true,
        UseDigits = DigitBox.IsChecked == true,
        UseSymbols = SymbolBox.IsChecked == true,
        ExcludeAmbiguous = AmbiguousBox.IsChecked == true
    };

    // ===================== 生成 =====================

    private void Generate_Click(object sender, RoutedEventArgs e) => GenerateOne(addToHistory: true);

    private void Batch_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;

        var options = BuildOptions();
        var list = PasswordEngine.GenerateBatch(options, 10, out var error);
        if (error is not null)
        {
            SetStatus(error, StatusKind.Warn);
            return;
        }

        foreach (var password in list) AddHistory(password);

        ShowPassword(list[0]);
        SetStatus($"已批量生成 {list.Count} 条密码，最新一条显示在左栏，其余见右侧记录。", StatusKind.Ok);
        _plugin.LogInfo($"批量生成 {list.Count} 条密码");
    }

    private void GenerateOne(bool addToHistory)
    {
        if (!_ready || _closed) return;

        var options = BuildOptions();
        var password = PasswordEngine.Generate(options, out var error);
        if (error is not null)
        {
            SetStatus(error, StatusKind.Warn);
            return;
        }

        if (addToHistory) AddHistory(password);
        ShowPassword(password);
    }

    private void ShowPassword(string password)
    {
        var report = PasswordEngine.Evaluate(password);

        PasswordBox.Text = password;
        StrengthLabel.Text = $"强度：{report.Level}";
        EntropyLabel.Text = $"熵值 {report.EntropyBits} bit";
        CharsetText.Text = report.CharsetText;
        CrackText.Text = report.CrackTimeText;
        _strengthRatio = report.Ratio;

        StrengthLabel.SetResourceReference(TextBlock.ForegroundProperty, report.Ratio switch
        {
            >= 0.78 => "AppSuccessBrush",
            >= 0.5 => "AppAccentBrush",
            >= 0.25 => "AppWarningBrush",
            _ => "AppDangerBrush"
        });

        StrengthFill.SetResourceReference(Border.BackgroundProperty, report.Ratio switch
        {
            >= 0.78 => "AppSuccessBrush",
            >= 0.5 => "AppAccentBrush",
            >= 0.25 => "AppWarningBrush",
            _ => "AppDangerBrush"
        });

        UpdateStrengthWidth();

        SetStatus($"已生成 {password.Length} 位密码 · 强度 {report.Level} · 平均破解时间约 {report.CrackTimeText}", StatusKind.Ok);
    }

    private void UpdateStrengthWidth()
    {
        if (StrengthTrack is null || StrengthFill is null) return;
        var width = StrengthTrack.ActualWidth;
        if (width <= 0) return;
        StrengthFill.Width = Math.Max(6, width * Math.Clamp(_strengthRatio, 0.03, 1.0));
    }

    private void StrengthTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateStrengthWidth();

    // ===================== 记录 =====================

    private void AddHistory(string password)
    {
        var report = PasswordEngine.Evaluate(password);
        _history.Insert(0, new HistoryItem
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Password = password,
            Entropy = report.EntropyBits
        });

        while (_history.Count > HistoryLimit) _history.RemoveAt(_history.Count - 1);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed || _suppressHistorySelection) return;
        if (HistoryList.SelectedItem is not HistoryItem item) return;

        ShowPassword(item.Password);

        try
        {
            Clipboard.SetText(item.Password);
            SetStatus($"已复制记录中的密码（{item.Time}）到剪贴板。", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("复制记录密码失败", ex);
        }

        _suppressHistorySelection = true;
        HistoryList.SelectedItem = null;
        _suppressHistorySelection = false;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        SetStatus("已清空生成记录。", StatusKind.Info);
    }

    // ===================== 复制 =====================

    private void CopyCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Text))
        {
            SetStatus("当前没有可复制的密码。", StatusKind.Warn);
            return;
        }

        try
        {
            Clipboard.SetText(PasswordBox.Text);
            SetStatus("已复制当前密码到剪贴板。", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, StatusKind.Error);
            _plugin.LogError("复制密码失败", ex);
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
