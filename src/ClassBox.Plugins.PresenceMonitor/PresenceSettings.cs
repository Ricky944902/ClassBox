namespace ClassBox.Plugins.PresenceMonitor;

/// <summary>检测到人像后的触发动作。</summary>
public enum PresenceAction
{
    /// <summary>仅弹出侧边提示，不打断当前操作。</summary>
    AlertOnly = 0,

    /// <summary>提示并最小化所有窗口（一键回到桌面）。</summary>
    AlertAndMinimizeAll = 1,

    /// <summary>提示并只保留指定进程的窗口（如教室管理软件）。</summary>
    AlertAndShowOnlyProcess = 2
}

/// <summary>人像监测插件配置项，持久化到插件独立目录。</summary>
public sealed class PresenceSettings
{
    /// <summary>应用启动后自动开始监测。</summary>
    public bool StartOnLaunch { get; set; } = true;

    /// <summary>摄像头设备索引（0 为第一个）。</summary>
    public int DeviceIndex { get; set; }

    /// <summary>检测灵敏度 0~1，越高越容易判定为有人。</summary>
    public double Sensitivity { get; set; } = 0.5;

    /// <summary>抓帧间隔（秒）。</summary>
    public double IntervalSeconds { get; set; } = 1.5;

    /// <summary>连续检测到人像多少秒后才触发，避免误报。</summary>
    public double PresenceSeconds { get; set; } = 2;

    /// <summary>连续多少秒未检测到人像才判定为离开。</summary>
    public double AbsenceSeconds { get; set; } = 5;

    /// <summary>两次提示之间的最小间隔（秒），防止刷屏。</summary>
    public double CooldownSeconds { get; set; } = 60;

    /// <summary>触发动作。</summary>
    public PresenceAction Action { get; set; } = PresenceAction.AlertOnly;

    /// <summary>触发动作中需要保留的进程名。</summary>
    public string TargetProcess { get; set; } = string.Empty;
}
