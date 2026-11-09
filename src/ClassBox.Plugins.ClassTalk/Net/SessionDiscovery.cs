using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ClassBox.Plugins.ClassTalk.Net;

/// <summary>
/// 一条被检测到的局域网会话（主机周期广播的公告）。
/// </summary>
public sealed record LanSession(
    string Name,
    string HostNick,
    string HostIp,
    int Port,
    int Members,
    bool Encrypted,
    string InstanceId,
    DateTime LastSeen)
{
    public string Endpoint => $"{HostIp}:{Port}";
}

/// <summary>
/// 会话发现协议：主机每 2 秒向局域网 UDP 端口广播一行文本，
/// 形如 CTALK1|会话名|主机昵称|主机内网IP|TCP端口|人数|是否加密|实例标识。
/// 加入方无需事先知道对方 IP，即可列出局域网内所有正在进行的会话。
/// </summary>
public static class SessionDiscoveryProtocol
{
    /// <summary>发现协议固定使用 UDP 55211，与实际聊天端口无关。</summary>
    public const int Port = 55211;

    private const string Magic = "CTALK1";
    private const char Sep = '|';

    /// <summary>清理字段中的分隔符与换行，避免破坏单行公告的解析。</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c == Sep || c == '\n' || c == '\r' || char.IsControl(c)) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    public static byte[] Serialize(LanSession session)
    {
        string line = string.Join(Sep,
            Magic,
            Clean(session.Name),
            Clean(session.HostNick),
            Clean(session.HostIp),
            session.Port.ToString(),
            session.Members.ToString(),
            session.Encrypted ? "1" : "0",
            Clean(session.InstanceId));
        return Encoding.UTF8.GetBytes(line);
    }

    public static LanSession? TryParse(ReadOnlySpan<byte> data, DateTime now)
    {
        if (data.Length == 0 || data.Length > 2048) return null;
        string line;
        try { line = Encoding.UTF8.GetString(data); }
        catch { return null; }

        var parts = line.Split(Sep);
        if (parts.Length != 8) return null;
        if (!string.Equals(parts[0], Magic, StringComparison.Ordinal)) return null;
        if (!int.TryParse(parts[4], out int port) || port < 1 || port > 65535) return null;
        if (!int.TryParse(parts[5], out int members) || members < 1 || members > 9999) return null;

        string ip = parts[3];
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return null;

        return new LanSession(
            Name: parts[1].Length == 0 ? "未命名会话" : parts[1],
            HostNick: parts[2],
            HostIp: ip,
            Port: port,
            Members: members,
            Encrypted: parts[6] == "1",
            InstanceId: parts[7],
            LastSeen: now);
    }

    /// <summary>广播目标：255.255.255.255 + 各网卡定向广播地址 + 本机回环（同机多实例互相可见）。</summary>
    public static IReadOnlyList<IPAddress> GetBroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = ua.IPv4Mask;
                    if (mask is null) continue;

                    byte[] ip = ua.Address.GetAddressBytes();
                    byte[] m = mask.GetAddressBytes();
                    if (ip.Length != 4 || m.Length != 4) continue;

                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | (byte)~m[i]);
                    var address = new IPAddress(bc);
                    if (!targets.Contains(address)) targets.Add(address);
                }
            }
        }
        catch
        {
            // 网卡枚举失败时至少还有 255.255.255.255
        }

        targets.Add(IPAddress.Loopback);
        return targets;
    }
}

/// <summary>
/// 会话公告广播器：主机模式启动后周期广播当前会话；停止主机时释放。
/// </summary>
public sealed class SessionAnnouncer : IDisposable
{
    private const int IntervalMs = 2000;

    private readonly Func<LanSession?> _snapshot;
    private readonly CancellationTokenSource _cts = new();
    private readonly UdpClient _udp;
    private Task? _loop;

    public SessionAnnouncer(Func<LanSession?> snapshot)
    {
        _snapshot = snapshot;
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = LoopAsync();
    }

    /// <summary>立即广播一次（点“刷新”时触发，让其他机器更快看到）。</summary>
    public void PublishOnce()
    {
        var session = _snapshot();
        if (session is null) return;

        byte[] payload;
        try { payload = SessionDiscoveryProtocol.Serialize(session); }
        catch { return; }

        foreach (var target in SessionDiscoveryProtocol.GetBroadcastTargets())
        {
            try
            {
                _udp.Send(payload, payload.Length, new IPEndPoint(target, SessionDiscoveryProtocol.Port));
            }
            catch
            {
                // 某个网卡不可用就跳过，不影响其他地址
            }
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                PublishOnce();
                await Task.Delay(IntervalMs, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception)
        {
            // 广播失败不应影响聊天主流程
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// 会话扫描器：监听发现端口，汇总局域网内所有正在进行的会话（9 秒无公告即视为结束）。
/// </summary>
public sealed class SessionScanner : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(9);

    private readonly UdpClient _udp;
    private readonly ConcurrentDictionary<string, LanSession> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    /// <summary>会话集合发生变化（收到公告或超时清理）。可能在后台线程触发。</summary>
    public event Action? SessionsChanged;

    public SessionScanner(int port = SessionDiscoveryProtocol.Port)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        // 同机多实例必须共享同一端口，否则第二个实例绑定失败
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _udp.EnableBroadcast = true;
    }

    public void Start()
    {
        if (_receiveLoop is not null) return;
        _receiveLoop = ReceiveLoopAsync();
    }

    private async Task ReceiveLoopAsync()
    {
        _ = Task.Run(PurgeLoopAsync);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                var session = SessionDiscoveryProtocol.TryParse(result.Buffer, DateTime.Now);
                if (session is null) continue;

                string key = session.Endpoint;
                _map[key] = session;
                SessionsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException)
        {
            // 端口被占用或网络栈异常：扫描不可用，但不影响聊天
        }
        catch (Exception) { }
    }

    private async Task PurgeLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(3000, _cts.Token).ConfigureAwait(false);
                if (Purge()) SessionsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private bool Purge()
    {
        var now = DateTime.Now;
        bool removed = false;
        foreach (var kv in _map)
        {
            if (now - kv.Value.LastSeen > Ttl)
                removed |= _map.TryRemove(kv.Key, out _);
        }
        return removed;
    }

    /// <summary>当前所有存活会话（按最近公告时间倒序）。</summary>
    public IReadOnlyList<LanSession> Snapshot()
    {
        Purge();
        return _map.Values
            .OrderByDescending(s => s.LastSeen)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
        _cts.Dispose();
    }
}
