using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ClassBox.Plugins.ClassTalk.Net;

/// <summary>
/// 本机局域网信息与防火墙放行工具。
/// 作用：主机模式对外展示的必须是真实内网 IPv4（如 192.168.x.x），
/// 而不是 127.0.0.1 —— 后者只在本机内有效，其他设备永远连不上。
/// </summary>
public static class LocalNetwork
{
    /// <summary>
    /// 取本机最可能被同网段设备访问的 IPv4 地址。
    /// 优先私有网段（192.168 / 10 / 172.16-31），并对常见虚拟网卡降权。
    /// 全部失败时返回 127.0.0.1。
    /// </summary>
    public static string GetLanIpv4()
    {
        var candidates = new List<(string Ip, int Score)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
            bool virtualNic = desc.Contains("vmware") || desc.Contains("virtualbox") || desc.Contains("hyper-v")
                              || desc.Contains("vethernet") || desc.Contains("virtual") || desc.Contains("tap-")
                              || desc.Contains("tun") || desc.Contains("wsl") || desc.Contains("docker")
                              || desc.Contains("zerotier") || desc.Contains("tailscale") || desc.Contains("vpn");

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = ua.Address.ToString();
                if (ip.StartsWith("127.") || ip.StartsWith("169.254.") || ip.StartsWith("0.")) continue;

                int score = 100;
                if (IsPrivate(ip)) score -= 60;
                if (ip.StartsWith("192.168.")) score -= 10;   // 家用最常见
                if (virtualNic) score += 80;                   // 虚拟网卡靠后
                candidates.Add((ip, score));
            }
        }

        if (candidates.Count > 0)
            return candidates.OrderBy(c => c.Score).ThenByDescending(c => c.Ip).First().Ip;

        // 兜底：主机名解析
        try
        {
            foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("127."))
                    return a.ToString();
            }
        }
        catch { }

        return "127.0.0.1";
    }

    private static bool IsPrivate(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return false;
        if (!byte.TryParse(parts[0], out byte a) || !byte.TryParse(parts[1], out byte b)) return false;
        if (a == 192 && b == 168) return true;
        if (a == 10) return true;
        if (a == 172 && b >= 16 && b <= 31) return true;
        return false;
    }

    /// <summary>
    /// 确保本机 TCP 端口有入站放行规则（进程已 requireAdministrator，可直接改防火墙）。
    /// 返回 true 表示规则可用；message 为给用户看的说明。
    /// </summary>
    public static bool EnsureFirewallRule(int port, out string message)
    {
        string ruleName = $"ClassTalk-TCP-{port}";
        try
        {
            if (RuleExists(ruleName))
            {
                message = $"防火墙已放行 TCP {port}";
                return true;
            }

            Run($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port} profile=any enable=yes");
            if (RuleExists(ruleName))
            {
                message = $"已自动放行防火墙 TCP {port}";
                return true;
            }

            message = $"防火墙未能自动放行，请手动执行：netsh advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}";
            return false;
        }
        catch (Exception ex)
        {
            message = $"防火墙设置失败（{ex.Message}），其他设备可能连不上";
            return false;
        }
    }

    private static bool RuleExists(string ruleName)
    {
        string output = Run($"advfirewall firewall show rule name=\"{ruleName}\"");
        // 中文系统"没有与指定条件相符的规则"，英文系统 "No rules match"
        if (output.Contains("没有与指定条件相符") || output.Contains("No rules match", StringComparison.OrdinalIgnoreCase))
            return false;
        return output.Contains(ruleName);
    }

    private static string Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(6000);
            return stdout + stderr;
        }
        catch
        {
            return "";
        }
    }
}
