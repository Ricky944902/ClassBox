namespace ClassBox.Plugins.NetworkToolkit.Net;

/// <summary>单块网卡信息。</summary>
public sealed class AdapterInfo
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Mac { get; init; } = string.Empty;

    public string Speed { get; init; } = string.Empty;

    public List<string> IpAddresses { get; init; } = new();

    public List<string> Gateways { get; init; } = new();

    public List<string> DnsServers { get; init; } = new();

    public string IpText => IpAddresses.Count == 0 ? "-" : string.Join(", ", IpAddresses);

    public string GatewayText => Gateways.Count == 0 ? "-" : string.Join(", ", Gateways);

    public string DnsText => DnsServers.Count == 0 ? "-" : string.Join(", ", DnsServers);

    public string Summary => $"{Name}｜{Status}｜IP {IpText}｜网关 {GatewayText}";
}

/// <summary>本机网络信息采集。</summary>
public static class NetworkInfoProvider
{
    /// <summary>枚举全部网卡（含虚拟网卡），不抛异常。</summary>
    public static List<AdapterInfo> GetAdapters()
    {
        var result = new List<AdapterInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var info = new AdapterInfo
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Type = nic.NetworkInterfaceType.ToString(),
                    Status = nic.OperationalStatus.ToString(),
                    Mac = FormatMac(nic.GetPhysicalAddress()),
                    Speed = nic.Speed > 0 ? $"{nic.Speed / 1_000_000} Mbps" : "-"
                };

                try
                {
                    var props = nic.GetIPProperties();

                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            info.IpAddresses.Add(addr.Address + (addr.PrefixLength > 0 ? "/" + addr.PrefixLength : string.Empty));
                        }
                    }

                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address is null) continue;
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork
                            || gw.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            info.Gateways.Add(gw.Address.ToString());
                        }
                    }

                    foreach (var dns in props.DnsAddresses)
                    {
                        info.DnsServers.Add(dns.ToString());
                    }
                }
                catch
                {
                    // 单个网卡读取失败不影响整体枚举。
                }

                result.Add(info);
            }
        }
        catch
        {
            // 枚举整体失败时返回已有结果。
        }

        return result
            .OrderByDescending(a => a.IpAddresses.Count > 0)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address?.GetAddressBytes();
        if (bytes is null || bytes.Length == 0) return "-";
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>汇总本机主要网络参数（用于概览展示）。</summary>
    public static string BuildOverview(IEnumerable<AdapterInfo> adapters)
    {
        var list = adapters.ToList();
        var active = list.Where(a => a.IpAddresses.Count > 0).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"网卡总数：{list.Count}，其中已分配 IPv4 地址：{active.Count}");
        foreach (var adapter in active)
        {
            sb.AppendLine($"{adapter.Name}：IP {adapter.IpText}｜网关 {adapter.GatewayText}｜DNS {adapter.DnsText}｜MAC {adapter.Mac}");
        }

        if (active.Count == 0)
        {
            sb.AppendLine("未检测到已分配 IPv4 的网卡，请检查网络连接。");
        }

        return sb.ToString().TrimEnd();
    }
}
