using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClassBox.Plugins.SystemInfo.Collectors;

/// <summary>单个磁盘卷信息。</summary>
public sealed class DiskInfo
{
    public string Name { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public double UsedPercent => TotalBytes > 0 ? (TotalBytes - FreeBytes) * 100d / TotalBytes : 0d;

    public string TotalText => SystemInfoCollector.FormatSize(TotalBytes);

    public string FreeText => SystemInfoCollector.FormatSize(FreeBytes);

    public string UsageText => $"{TotalText} · 可用 {FreeText} · 已用 {UsedPercent:0.0}%";
}

/// <summary>单块网卡信息。</summary>
public sealed class NetworkInfo
{
    public string Name { get; init; } = string.Empty;

    public string TypeText { get; init; } = string.Empty;

    public string Mac { get; init; } = "-";

    public string IpText { get; init; } = "-";

    public string SpeedText { get; init; } = "-";
}

/// <summary>系统信息快照。</summary>
public sealed class SystemInfoSnapshot
{
    public DateTime CollectedAt { get; init; } = DateTime.Now;

    public string OsName { get; init; } = "-";

    public string OsVersion { get; init; } = "-";

    public string OsBuild { get; init; } = "-";

    public string OsArchitecture { get; init; } = "-";

    public string RuntimeVersion { get; init; } = "-";

    public string MachineName { get; init; } = "-";

    public string UserName { get; init; } = "-";

    public string UptimeText { get; init; } = "-";

    public string CpuName { get; init; } = "-";

    public int PhysicalCores { get; init; }

    public int LogicalCores { get; init; }

    public string CpuText => $"{PhysicalCores} 物理核心 / {LogicalCores} 逻辑处理器";

    public long MemoryTotalBytes { get; init; }

    public long MemoryAvailableBytes { get; init; }

    public string MemoryTotalText => SystemInfoCollector.FormatSize(MemoryTotalBytes);

    public string MemoryAvailableText => SystemInfoCollector.FormatSize(MemoryAvailableBytes);

    public double MemoryUsagePercent =>
        MemoryTotalBytes > 0 ? (MemoryTotalBytes - MemoryAvailableBytes) * 100d / MemoryTotalBytes : 0d;

    public string MemoryUsageText => $"{MemoryUsagePercent:0.0}%";

    public string GpuName { get; init; } = "-";

    public string ResolutionText { get; init; } = "-";

    public IReadOnlyList<DiskInfo> Disks { get; init; } = Array.Empty<DiskInfo>();

    public IReadOnlyList<NetworkInfo> Networks { get; init; } = Array.Empty<NetworkInfo>();

    /// <summary>供"一键复制"使用的纯文本报告。</summary>
    public string ToPlainText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("========== 系统信息 ==========");
        builder.AppendLine($"采集时间：{CollectedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine("[操作系统]");
        builder.AppendLine($"名称：{OsName}");
        builder.AppendLine($"版本：{OsVersion}");
        builder.AppendLine($"构建号：{OsBuild}");
        builder.AppendLine($"架构：{OsArchitecture}");
        builder.AppendLine($"运行时：{RuntimeVersion}");
        builder.AppendLine($"主机名：{MachineName}");
        builder.AppendLine($"当前用户：{UserName}");
        builder.AppendLine($"已开机：{UptimeText}");
        builder.AppendLine();
        builder.AppendLine("[处理器与内存]");
        builder.AppendLine($"CPU：{CpuName}");
        builder.AppendLine($"核心：{CpuText}");
        builder.AppendLine($"物理内存总量：{MemoryTotalText}");
        builder.AppendLine($"物理内存可用：{MemoryAvailableText}（已用 {MemoryUsageText}）");
        builder.AppendLine();
        builder.AppendLine("[显卡与显示]");
        builder.AppendLine($"显卡：{GpuName}");
        builder.AppendLine($"分辨率：{ResolutionText}");
        builder.AppendLine();
        builder.AppendLine("[磁盘]");
        if (Disks.Count == 0) builder.AppendLine("无可读磁盘卷");
        foreach (var disk in Disks)
        {
            builder.AppendLine($"{disk.Name} {disk.Label} {disk.Format}：{disk.UsageText}");
        }

        builder.AppendLine();
        builder.AppendLine("[网络]");
        if (Networks.Count == 0) builder.AppendLine("无活动网络适配器");
        foreach (var nic in Networks)
        {
            builder.AppendLine($"{nic.Name}（{nic.TypeText}）MAC {nic.Mac} 速率 {nic.SpeedText}");
            builder.AppendLine($"    IP：{nic.IpText}");
        }

        return builder.ToString();
    }
}

/// <summary>系统信息采集器：全部使用 .NET 内置 API 与 Win32 只读查询，无第三方依赖。</summary>
public static class SystemInfoCollector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    /// <summary>采集系统信息（同步方法内部无阻塞等待，页面用 Task.Run 调用）。</summary>
    public static SystemInfoSnapshot Capture()
    {
        var (totalMemory, availableMemory) = ReadMemory();
        var (physicalCores, logicalCores) = ReadCpuCores();

        return new SystemInfoSnapshot
        {
            OsName = ReadOsName(),
            OsVersion = ReadOsVersion(),
            OsBuild = ReadOsBuild(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            UptimeText = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)),
            CpuName = ReadCpuName(),
            PhysicalCores = physicalCores,
            LogicalCores = logicalCores,
            MemoryTotalBytes = (long)totalMemory,
            MemoryAvailableBytes = (long)availableMemory,
            GpuName = ReadGpuName(),
            ResolutionText = ReadResolution(),
            Disks = ReadDisks(),
            Networks = ReadNetworks()
        };
    }

    // ===================== 操作系统 =====================

    private static string ReadOsName()
    {
        var productName = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
        var edition = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID");

        if (!string.IsNullOrWhiteSpace(productName)) return productName;

        return string.IsNullOrWhiteSpace(edition)
            ? RuntimeInformation.OSDescription
            : $"Windows {edition}";
    }

    private static string ReadOsVersion()
    {
        var displayVersion = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var build = ReadOsBuild();

        if (!string.IsNullOrWhiteSpace(displayVersion)) return $"{displayVersion}（Build {build}）";

        return Environment.OSVersion.VersionString;
    }

    private static string ReadOsBuild()
    {
        var build = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber");
        var ubr = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");

        if (string.IsNullOrWhiteSpace(build)) return Environment.OSVersion.Version.Build.ToString();
        return string.IsNullOrWhiteSpace(ubr) ? build : $"{build}.{ubr}";
    }

    // ===================== CPU / 内存 =====================

    private static string ReadCpuName()
    {
        var name = ReadRegistryString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();

        var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return string.IsNullOrWhiteSpace(identifier) ? "未知处理器" : identifier.Trim();
    }

    private static (int Physical, int Logical) ReadCpuCores()
    {
        var logical = Environment.ProcessorCount;

        try
        {
            // PROCESSOR_COUNT 之外的物理核心数需从注册表读取每个核心的标识，取最大值作为物理核心数估计。
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor");
            if (key is not null)
            {
                var count = key.GetSubKeyNames().Length;
                if (count > 0) return (Math.Min(count, logical), logical);
            }
        }
        catch (Exception)
        {
            // 忽略，回退为逻辑核心数
        }

        return (logical, logical);
    }

    private static (ulong Total, ulong Available) ReadMemory()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status)) return (status.TotalPhys, status.AvailPhys);
        }
        catch (Exception)
        {
            // 忽略，回退 GC 信息
        }

        var fallback = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return ((ulong)Math.Max(fallback, 0), 0);
    }

    // ===================== 显卡 / 分辨率 =====================

    private static string ReadGpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (key is not null)
            {
                var names = new List<string>();
                foreach (var subName in key.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
                    {
                        names.Add(desc.Trim());
                    }
                }

                if (names.Count > 0) return string.Join(" / ", names.Distinct());
            }
        }
        catch (Exception)
        {
            // 忽略
        }

        return "未知显卡";
    }

    private static string ReadResolution()
    {
        try
        {
            var width = GetSystemMetrics(SmCxScreen);
            var height = GetSystemMetrics(SmCyScreen);
            if (width > 0 && height > 0) return $"{width} × {height}（主显示器）";
        }
        catch (Exception)
        {
            // 忽略
        }

        return "无法获取";
    }

    // ===================== 磁盘 =====================

    private static List<DiskInfo> ReadDisks()
    {
        var list = new List<DiskInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!drive.IsReady) continue;

                list.Add(new DiskInfo
                {
                    Name = drive.Name,
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel,
                    Format = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.TotalFreeSpace
                });
            }
            catch (Exception)
            {
                // 单个卷读取失败不影响其他卷
            }
        }

        return list.OrderBy(d => d.Name).ToList();
    }

    // ===================== 网络 =====================

    private static List<NetworkInfo> ReadNetworks()
    {
        var list = new List<NetworkInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;

                    var properties = nic.GetIPProperties();
                    var ipv4 = properties.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString())
                        .ToList();

                    var ipv6 = properties.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        .Select(a => a.Address.ToString())
                        .Where(a => !a.StartsWith("fe80", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var allIp = ipv4.Concat(ipv6).ToList();

                    var mac = nic.GetPhysicalAddress()?.ToString();
                    if (!string.IsNullOrWhiteSpace(mac) && mac.Length == 12)
                    {
                        mac = string.Join("-", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
                    }

                    long speed = 0;
                    try
                    {
                        speed = nic.Speed;
                    }
                    catch (Exception)
                    {
                        speed = 0;
                    }

                    list.Add(new NetworkInfo
                    {
                        Name = nic.Name,
                        TypeText = DescribeType(nic.NetworkInterfaceType),
                        Mac = string.IsNullOrWhiteSpace(mac) ? "-" : mac,
                        IpText = allIp.Count == 0 ? "-" : string.Join("  |  ", allIp),
                        SpeedText = speed > 0 ? $"{(speed >= 1000L * 1000 * 1000 ? $"{speed / 1000d / 1000d / 1000d:0.##} Gbps" : $"{speed / 1000d / 1000d:0.##} Mbps")}" : "-"
                    });
                }
                catch (Exception)
                {
                    // 单块网卡读取失败忽略
                }
            }
        }
        catch (Exception)
        {
            // 网络信息不可用时返回空列表
        }

        return list;
    }

    private static string DescribeType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "有线以太网",
        NetworkInterfaceType.Wireless80211 => "无线网络",
        NetworkInterfaceType.Ppp => "拨号/VPN",
        NetworkInterfaceType.Tunnel => "隧道",
        _ => type.ToString()
    };

    // ===================== 工具方法 =====================

    private static string? ReadRegistryString(string path, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(valueName)?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatUptime(TimeSpan span) =>
        $"{(int)span.TotalDays} 天 {span.Hours} 小时 {span.Minutes} 分 {span.Seconds} 秒";

    /// <summary>字节数格式化。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
