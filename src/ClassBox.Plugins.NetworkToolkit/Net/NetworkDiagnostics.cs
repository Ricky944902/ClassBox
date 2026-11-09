using System.Diagnostics;

namespace ClassBox.Plugins.NetworkToolkit.Net;

/// <summary>端口占用条目。</summary>
public sealed class PortUsage
{
    public string Protocol { get; init; } = "TCP";

    public string LocalAddress { get; init; } = string.Empty;

    public int Port { get; init; }

    public string State { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    public string Display
        => $"{Protocol,-4} {LocalAddress,-22} :{Port,-6} {State,-12} PID {ProcessId} ({ProcessName})";
}

/// <summary>DNS 解析结果。</summary>
public sealed class DnsLookupResult
{
    public string HostName { get; init; } = string.Empty;

    public List<string> Addresses { get; init; } = new();

    public List<string> Aliases { get; init; } = new();

    public string Error { get; init; } = string.Empty;

    public bool Success => string.IsNullOrEmpty(Error);
}

/// <summary>DNS 解析与端口占用查询。</summary>
public static class NetworkDiagnostics
{
    /// <summary>解析主机名到 IP 地址。</summary>
    public static async Task<DnsLookupResult> ResolveAsync(string hostName, CancellationToken cancellationToken = default)
    {
        hostName = (hostName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return new DnsLookupResult { HostName = hostName, Error = "请输入要解析的域名或主机名。" };
        }

        try
        {
            if (IPAddress.TryParse(hostName, out var direct))
            {
                try
                {
                    var reverse = await Dns.GetHostEntryAsync(direct).ConfigureAwait(false);
                    return new DnsLookupResult
                    {
                        HostName = reverse.HostName,
                        Addresses = new List<string> { direct.ToString() },
                        Aliases = reverse.Aliases?.ToList() ?? new List<string>()
                    };
                }
                catch
                {
                    return new DnsLookupResult { HostName = hostName, Addresses = new List<string> { direct.ToString() } };
                }
            }

            var entry = await Dns.GetHostEntryAsync(hostName, cancellationToken).ConfigureAwait(false);
            return new DnsLookupResult
            {
                HostName = entry.HostName,
                Addresses = entry.AddressList.Select(a => a.ToString()).ToList(),
                Aliases = entry.Aliases?.ToList() ?? new List<string>()
            };
        }
        catch (Exception ex)
        {
            return new DnsLookupResult { HostName = hostName, Error = "解析失败：" + ex.Message };
        }
    }

    /// <summary>枚举当前 TCP 监听端口与已建立连接。</summary>
    public static List<PortUsage> GetTcpUsage()
    {
        var raw = new List<PortUsage>();

        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();

            foreach (var listener in props.GetActiveTcpListeners())
            {
                raw.Add(new PortUsage
                {
                    Protocol = "TCP",
                    LocalAddress = listener.Address.ToString(),
                    Port = listener.Port,
                    State = "LISTEN"
                });
            }

            foreach (var connection in props.GetActiveTcpConnections())
            {
                raw.Add(new PortUsage
                {
                    Protocol = "TCP",
                    LocalAddress = connection.LocalEndPoint.Address.ToString(),
                    Port = connection.LocalEndPoint.Port,
                    State = connection.State.ToString()
                });
            }
        }
        catch
        {
            return raw;
        }

        // 补充进程信息（netstat 不可用时仅缺少 PID，不影响端口列表）
        var owners = TryGetTcpOwners();
        var cache = new Dictionary<int, string>();
        var result = new List<PortUsage>(raw.Count);

        foreach (var item in raw)
        {
            owners.TryGetValue((item.LocalAddress, item.Port), out var pid);
            result.Add(new PortUsage
            {
                Protocol = item.Protocol,
                LocalAddress = item.LocalAddress,
                Port = item.Port,
                State = item.State,
                ProcessId = pid,
                ProcessName = pid > 0 ? ResolveProcessName(pid, cache) : string.Empty
            });
        }

        return result
            .OrderBy(r => r.Port)
            .ThenBy(r => r.Protocol)
            .ToList();
    }

    /// <summary>查询指定端口的占用情况。</summary>
    public static List<PortUsage> QueryPort(int port, IEnumerable<PortUsage>? source = null)
    {
        var list = source?.ToList() ?? GetTcpUsage();
        return list.Where(p => p.Port == port).ToList();
    }

    private static string ResolveProcessName(int pid, Dictionary<int, string> cache)
    {
        if (pid <= 0) return string.Empty;
        if (cache.TryGetValue(pid, out var cached)) return cached;

        var name = string.Empty;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch
        {
            name = "未知";
        }

        cache[pid] = name;
        return name;
    }

    /// <summary>通过 netstat 输出解析端口对应的 PID（IPv4/IPv6 均覆盖）。</summary>
    private static Dictionary<(string, int), int> TryGetTcpOwners()
    {
        var map = new Dictionary<(string, int), int>();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null) return map;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;

                var local = parts[1];
                var pidText = parts[^1];
                if (!int.TryParse(pidText, out var pid)) continue;

                var idx = local.LastIndexOf(':');
                if (idx <= 0) continue;

                var address = local[..idx].Trim('[', ']');
                if (!int.TryParse(local[(idx + 1)..], out var port)) continue;

                var key = (address, port);
                if (!map.ContainsKey(key)) map[key] = pid;
            }
        }
        catch
        {
            // netstat 不可用则忽略（仅丢失进程信息）。
        }

        return map;
    }
}
