using System.Collections.ObjectModel;
using System.Windows.Threading;
using ClassBox.Abstractions.Mvvm;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.PresenceMonitor.Camera;
using ClassBox.Plugins.PresenceMonitor.Detection;

namespace ClassBox.Plugins.PresenceMonitor.ViewModels;

/// <summary>人像监测页面的视图模型：负责抓帧调度、状态机与触发动作。</summary>
public sealed class PresenceMonitorViewModel : ObservableObject, IDisposable
{
    private const string ConfigKey = "presence-monitor";
    private const string LogTag = "Presence";

    private readonly IWindowService _windows;
    private readonly IAppLogger _logger;
    private readonly ISideAlertService _alerts;
    private readonly IConfigStore _config;

    private readonly DispatcherTimer _timer;
    private VideoForWindowsCamera? _camera;

    private PresenceSettings _settings;

    private bool _isMonitoring;
    private bool _isPersonPresent;
    private bool _cameraReady;
    private int _failureCount;
    private int _presentTicks;
    private int _absentTicks;
    private DateTime _lastAlertAt = DateTime.MinValue;

    private string _statusText = "已暂停";
    private string _detailText = "点击「开始监测」后会持续分析摄像头画面，判断是否有人坐在电脑前。";
    private string _metricsText = "—";
    private string _deviceText = "摄像头未初始化";
    private string _targetProcess = string.Empty;

    public PresenceMonitorViewModel(
        IWindowService windows,
        IAppLogger logger,
        ISideAlertService alerts,
        IConfigStore config)
    {
        _windows = windows;
        _logger = logger;
        _alerts = alerts;
        _config = config;

        _settings = _config.Load(ConfigKey, new PresenceSettings());
        _targetProcess = _settings.TargetProcess;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.IntervalSeconds, 0.5, 10))
        };
        _timer.Tick += (_, _) => Sample();

        StartCommand = new RelayCommand(StartMonitoring, () => !IsMonitoring);
        StopCommand = new RelayCommand(StopMonitoring, () => IsMonitoring);
        TestCommand = new RelayCommand(TestOnce);
        SaveCommand = new RelayCommand(SaveSettings);
        ResetCommand = new RelayCommand(ResetStatistics);
        RefreshProcessesCommand = new RelayCommand(LoadProcessNames);

        LoadProcessNames();
    }

    public RelayCommand StartCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand TestCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand RefreshProcessesCommand { get; }

    public ObservableCollection<string> RecentEvents { get; } = new();

    public ObservableCollection<string> ProcessNames { get; } = new();

    public IReadOnlyList<string> ActionOptions { get; } = new[]
    {
        "仅侧边提示（不打断操作）",
        "提示并最小化所有窗口",
        "提示并只保留指定进程窗口"
    };

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                Raise(nameof(MonitoringButtonText));
            }
        }
    }

    public string MonitoringButtonText => IsMonitoring ? "监测中" : "已暂停";

    public bool CameraReady
    {
        get => _cameraReady;
        private set => SetProperty(ref _cameraReady, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string MetricsText
    {
        get => _metricsText;
        private set => SetProperty(ref _metricsText, value);
    }

    public string DeviceText
    {
        get => _deviceText;
        private set => SetProperty(ref _deviceText, value);
    }

    public string TargetProcess
    {
        get => _targetProcess;
        set => SetProperty(ref _targetProcess, value);
    }

    public double Sensitivity
    {
        get => _settings.Sensitivity;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), 0, 1);
            if (Math.Abs(_settings.Sensitivity - clamped) < 0.001) return;

            _settings.Sensitivity = clamped;
            Raise();
            SaveSettings();
        }
    }

    public double IntervalSeconds
    {
        get => _settings.IntervalSeconds;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 1), 0.5, 10);
            if (Math.Abs(_settings.IntervalSeconds - clamped) < 0.001) return;

            _settings.IntervalSeconds = clamped;
            _timer.Interval = TimeSpan.FromSeconds(clamped);
            Raise();
            SaveSettings();
        }
    }

    public double PresenceSeconds
    {
        get => _settings.PresenceSeconds;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 1), 0.5, 10);
            if (Math.Abs(_settings.PresenceSeconds - clamped) < 0.001) return;

            _settings.PresenceSeconds = clamped;
            Raise();
            SaveSettings();
        }
    }

    public double CooldownSeconds
    {
        get => _settings.CooldownSeconds;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 0), 0, 600);
            if (Math.Abs(_settings.CooldownSeconds - clamped) < 0.001) return;

            _settings.CooldownSeconds = clamped;
            Raise();
            SaveSettings();
        }
    }

    public int ActionIndex
    {
        get => (int)_settings.Action;
        set
        {
            var index = Math.Clamp(value, 0, 2);
            if ((int)_settings.Action == index) return;

            _settings.Action = (PresenceAction)index;
            Raise();
            Raise(nameof(NeedsTargetProcess));
            SaveSettings();
        }
    }

    public bool NeedsTargetProcess => _settings.Action == PresenceAction.AlertAndShowOnlyProcess;

    public bool StartOnLaunch
    {
        get => _settings.StartOnLaunch;
        set
        {
            if (_settings.StartOnLaunch == value) return;

            _settings.StartOnLaunch = value;
            Raise();
            SaveSettings();
        }
    }

    public int DeviceIndex
    {
        get => _settings.DeviceIndex;
        set
        {
            var index = Math.Clamp(value, 0, 3);
            if (_settings.DeviceIndex == index) return;

            _settings.DeviceIndex = index;
            Raise();
            SaveSettings();

            if (IsMonitoring)
            {
                StopMonitoring();
                StartMonitoring();
            }
        }
    }

    public IReadOnlyList<int> DeviceOptions { get; } = new[] { 1, 2, 3, 4 };

    /// <summary>插件启动时调用：按配置决定是否自动开始监测。</summary>
    public void StartIfConfigured()
    {
        AddEvent("插件已加载");
        if (_settings.StartOnLaunch)
        {
            StartMonitoring();
        }
    }

    public void StartMonitoring()
    {
        if (IsMonitoring) return;

        _camera ??= new VideoForWindowsCamera();

        if (!_camera.IsConnected && !_camera.Open(DeviceIndex))
        {
            CameraReady = false;
            StatusText = "摄像头不可用";
            DeviceText = _camera.LastError ?? "未检测到可用摄像头";
            AddEvent("连接摄像头失败：" + DeviceText);

            _alerts.Show(new SideAlertRequest
            {
                Title = "人像监测未启动",
                Message = DeviceText,
                AutoCloseSeconds = 7,
                IsUrgent = true
            });
            return;
        }

        CameraReady = true;
        DeviceText = $"摄像头 #{DeviceIndex + 1} 已连接";
        _failureCount = 0;
        _presentTicks = 0;
        _absentTicks = 0;
        _isPersonPresent = false;

        IsMonitoring = true;
        StatusText = "监测中 · 等待画面";
        DetailText = "画面分析中，检测到人像会按配置的动作提醒。";
        _timer.Start();
        AddEvent("开始监测");
    }

    public void StopMonitoring()
    {
        if (!IsMonitoring) return;

        _timer.Stop();
        IsMonitoring = false;
        _isPersonPresent = false;
        StatusText = "已暂停";
        DetailText = "监测已暂停，摄像头占用已释放。";
        _camera?.Dispose();
        _camera = null;
        CameraReady = false;
        DeviceText = "摄像头已释放";
        AddEvent("停止监测");
    }

    /// <summary>释放资源（应用退出或插件停止时调用）。</summary>
    public void Dispose()
    {
        StopMonitoring();
    }

    private void Sample()
    {
        if (_camera is null || !_camera.IsConnected) return;

        var frame = _camera.GrabFrame();
        if (frame is null)
        {
            _failureCount++;
            if (_failureCount >= 3)
            {
                StatusText = "抓帧失败";
                DetailText = "连续三次未能获取画面，可能是摄像头被其它程序独占。";
                AddEvent("抓帧失败，已自动停止");
                _timer.Stop();
                IsMonitoring = false;
                CameraReady = false;
                _camera.Dispose();
                _camera = null;

                _alerts.Show(new SideAlertRequest
                {
                    Title = "人像监测已停止",
                    Message = "摄像头画面获取失败，请关闭占用摄像头的程序后重试。",
                    AutoCloseSeconds = 7,
                    IsUrgent = true
                });
            }

            return;
        }

        _failureCount = 0;

        var result = SkinToneDetector.Detect(frame, Sensitivity);
        MetricsText = $"肤色占比 {result.SkinRatio:P1} · 中央区域 {result.CenterRatio:P1} · 置信度 {result.Confidence:P0}";

        var interval = _timer.Interval.TotalSeconds;

        if (result.PersonDetected)
        {
            _presentTicks++;
            _absentTicks = 0;

            if (!_isPersonPresent && _presentTicks * interval >= PresenceSeconds)
            {
                _isPersonPresent = true;
                StatusText = "检测到人像";
                DetailText = "已确认有人在前，按配置执行提醒动作。";
                OnPersonPresent(result);
            }
            else if (_isPersonPresent)
            {
                StatusText = "检测到人像";
            }
            else
            {
                StatusText = "监测中 · 疑似人像";
            }
        }
        else
        {
            _absentTicks++;
            _presentTicks = 0;

            if (_isPersonPresent && _absentTicks * interval >= _settings.AbsenceSeconds)
            {
                _isPersonPresent = false;
                StatusText = "监测中 · 无人";
                DetailText = "画面中未检测到人像。";
                AddEvent("人像离开画面");
            }
            else if (!_isPersonPresent)
            {
                StatusText = "监测中 · 无人";
            }
        }
    }

    private void OnPersonPresent(DetectionResult result)
    {
        AddEvent($"检测到人像（置信度 {result.Confidence:P0}）");

        if ((DateTime.Now - _lastAlertAt).TotalSeconds < CooldownSeconds)
        {
            _logger.Debug("命中冷却时间，跳过提示", LogTag);
            return;
        }

        _lastAlertAt = DateTime.Now;

        var action = _settings.Action;
        var message = action switch
        {
            PresenceAction.AlertAndMinimizeAll => "已确认有人在前，窗口已最小化。",
            PresenceAction.AlertAndShowOnlyProcess => string.IsNullOrWhiteSpace(_settings.TargetProcess)
                ? "已确认有人在前（未指定要保留的进程）。"
                : $"已确认有人在前，仅保留 {_settings.TargetProcess} 窗口。",
            _ => "已确认有人在前，注意演示内容。"
        };

        _alerts.Show(new SideAlertRequest
        {
            Title = "检测到人像",
            Message = message,
            Glyph = "\uE7B8",
            AutoCloseSeconds = 8,
            IsUrgent = false
        });

        try
        {
            switch (action)
            {
                case PresenceAction.AlertAndMinimizeAll:
                    var count = _windows.MinimizeAll();
                    AddEvent($"已最小化 {count} 个窗口");
                    break;

                case PresenceAction.AlertAndShowOnlyProcess when !string.IsNullOrWhiteSpace(_settings.TargetProcess):
                    var affected = _windows.ShowOnlyProcess(_settings.TargetProcess);
                    AddEvent($"收敛窗口：保留 {_settings.TargetProcess}，处理 {affected} 个窗口");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("执行触发动作失败", ex, LogTag);
        }
    }

    private void TestOnce()
    {
        _camera ??= new VideoForWindowsCamera();

        if (!_camera.IsConnected && !_camera.Open(DeviceIndex))
        {
            CameraReady = false;
            DeviceText = _camera.LastError ?? "未检测到可用摄像头";
            _alerts.Show(new SideAlertRequest
            {
                Title = "摄像头不可用",
                Message = DeviceText,
                AutoCloseSeconds = 6,
                IsUrgent = true
            });
            return;
        }

        CameraReady = true;
        var frame = _camera.GrabFrame();
        if (frame is null)
        {
            MetricsText = "本次测试未取到画面，请稍后重试。";
            return;
        }

        var result = SkinToneDetector.Detect(frame, Sensitivity);
        MetricsText = $"单次测试：肤色占比 {result.SkinRatio:P1} · 中央区域 {result.CenterRatio:P1} · {(result.PersonDetected ? "判定为有人" : "判定为无人")}";
        AddEvent($"单次测试 → {(result.PersonDetected ? "有人" : "无人")}（{result.SkinRatio:P1}）");

        if (!IsMonitoring)
        {
            _camera.Dispose();
            _camera = null;
            CameraReady = false;
            DeviceText = "摄像头已释放";
        }
    }

    private void SaveSettings()
    {
        _settings.TargetProcess = _targetProcess ?? string.Empty;
        _config.Save(ConfigKey, _settings);
        _logger.Debug("人像监测设置已保存", LogTag);
    }

    private void ResetStatistics()
    {
        RecentEvents.Clear();
        AddEvent("统计已清空");
    }

    private void LoadProcessNames()
    {
        try
        {
            var names = _windows.GetVisibleProcessNames();
            ProcessNames.Clear();
            foreach (var name in names) ProcessNames.Add(name);
        }
        catch (Exception ex)
        {
            _logger.Warn("读取进程列表失败：" + ex.Message, LogTag);
        }
    }

    private void AddEvent(string message)
    {
        RecentEvents.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (RecentEvents.Count > 30) RecentEvents.RemoveAt(RecentEvents.Count - 1);
    }
}
