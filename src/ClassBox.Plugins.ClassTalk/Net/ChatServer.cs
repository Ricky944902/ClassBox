using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassBox.Plugins.ClassTalk.Crypto;

namespace ClassBox.Plugins.ClassTalk.Net;

/// <summary>
/// 主机（服务器）模式：监听 TCP 端口，管理在线成员，
/// 转发消息密文（端到端加密语义），并以主机视角解密显示。
/// </summary>
public sealed class ChatServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Lrnc1024Session _session;
    private readonly Dictionary<TcpClient, string> _clients = new();
    // 每个连接一把写锁：并发广播时若多线程同时写同一 stream，帧字节会交错，接收端解析错位，表现为“自己发的对方收不到”
    private readonly Dictionary<TcpClient, SemaphoreSlim> _writeLocks = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private string? _adminNick;   // 当前管理员昵称；null 表示还没有人取得管理员身份

    public string HostNick { get; }
    public int Port { get; }

    /// <summary>会话名称（广播给局域网检测用，也可为空自动取“昵称 的聊天室”）。</summary>
    public string RoomName { get; }

    public List<string> Members { get; } = new(); // 含主机自身

    /// <summary>在线人数（含主机）。局域网公告与状态栏使用，需加锁读取。</summary>
    public int MemberCount { get { lock (_lock) return Members.Count; } }

    public event Action<string, string>? MessageReceived;   // sender, 明文（解密后）
    public event Action<string, string>? PlainMessageReceived; // sender, 明文（PLAIN 通道）
    public event Action<string>? SystemMessage;             // 系统消息
    public event Action<IReadOnlyList<string>>? MembersUpdated;
    public event Action<string>? Stopped;                   // 服务器异常停止
    public event Action<string?>? AdminChanged;             // 管理员变化（null = 暂无管理员）
    public event Action<string, string>? CommandIssued;     // 管理员下发命令：target, command
    public event Action<string, string>? CommandOutput;     // 目标回传命令输出：target, output

    /// <summary>当前管理员昵称（null 表示还没有人取得）。</summary>
    public string? AdminNick { get { lock (_lock) return _adminNick; } }

    public ChatServer(string hostNick, Lrnc1024Session session, int port, string roomName = "")
    {
        HostNick = hostNick;
        _session = session;
        Port = port;
        RoomName = string.IsNullOrWhiteSpace(roomName) ? $"{hostNick} 的聊天室" : roomName.Trim();
        _listener = new TcpListener(IPAddress.Any, port);
        Members.Add(hostNick);
    }

    public void Start()
    {
        _listener.Start();
        SystemMessage?.Invoke($"主机模式已启动，监听端口 {Port}");
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Stopped?.Invoke($"服务器异常停止：{ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        string? nick = null;
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var first = await ChatProtocol.ReadFrameAsync(stream, _cts.Token);
                if (first == null || first.Kind != "CTRL" || first.Type != "JOIN")
                    return; // 首帧必须是 JOIN
                nick = first.Nick.Trim();
                if (nick.Length == 0 || nick.Length > 32) return;

                lock (_lock)
                {
                    _clients[client] = nick;
                    Members.Add(nick);
                }
                NotifySystem($"{nick} 加入了聊天室");

                // 新加入者补发一次当前管理员状态，避免后进房间的人看不到“谁是管理员”
                string currentAdmin;
                lock (_lock) currentAdmin = _adminNick ?? "";
                await TryWriteAsync(client, ChatProtocol.FrameControl("ADMIN_CHANGED", "",
                    Encoding.UTF8.GetBytes(currentAdmin)));
                BroadcastMembers();

                while (!_cts.IsCancellationRequested)
                {
                    var frame = await ChatProtocol.ReadFrameAsync(stream, _cts.Token);
                    if (frame == null) break;
                    if (frame.Kind == "MSG")
                    {
                        string plain = _session.DecryptString(frame.Payload);
                        MessageReceived?.Invoke(frame.Nick, plain);
                        BroadcastFrame(ChatProtocol.FrameMessage(frame.Nick, frame.Payload), client);
                    }
                    else if (frame.Kind == "PLAIN")
                    {
                        // 明文通道：按 UTF-8 解析并以 UTF-8 原样转发
                        string plain = Encoding.UTF8.GetString(frame.Payload);
                        PlainMessageReceived?.Invoke(frame.Nick, plain);
                        BroadcastFrame(ChatProtocol.FramePlainMessage(frame.Nick, plain), client);
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "LEAVE")
                    {
                        break;
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "ADMIN_REQ")
                    {
                        // 只有第一个提出申请的人才能成为管理员，之后一律拒绝
                        bool granted;
                        string holder;
                        lock (_lock)
                        {
                            granted = _adminNick == null;
                            if (granted) _adminNick = nick;
                            holder = _adminNick ?? "";
                        }

                        if (granted)
                        {
                            await TryWriteAsync(client, ChatProtocol.FrameControl("ADMIN_RESULT", "",
                                Encoding.UTF8.GetBytes("OK")));
                            // 管理员身份属于隐藏机制：不向房间广播任何提示，只同步内部状态，
                            // 避免普通用户界面出现“管理员”相关文案
                            BroadcastAdmin();
                        }
                        else
                        {
                            await TryWriteAsync(client, ChatProtocol.FrameControl("ADMIN_RESULT", "",
                                Encoding.UTF8.GetBytes("DENY|" + holder)));
                            await TryWriteAsync(client, ChatProtocol.FrameControl("SYS", "",
                                Encoding.UTF8.GetBytes($"管理员已由 {holder} 取得，你无法重复获取")));
                        }
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "CMD")
                    {
                        // 远程命令只允许当前管理员下发，主机负责鉴权与转发
                        bool isAdmin;
                        lock (_lock) isAdmin = _adminNick != null && _adminNick == frame.Nick;

                        if (!isAdmin)
                        {
                            await TryWriteAsync(client, ChatProtocol.FrameControl("SYS", "",
                                Encoding.UTF8.GetBytes("无管理员权限，命令未执行")));
                            continue;
                        }

                        string payload = Encoding.UTF8.GetString(frame.Payload);
                        int sep = payload.IndexOf('\x1f');
                        if (sep <= 0) continue;
                        string target = payload.Substring(0, sep);
                        string command = payload.Substring(sep + 1);

                        CommandIssued?.Invoke(target, command);

                        if (target == HostNick)
                        {
                            // 目标是主机自己：主机本地执行并把输出交回管理员
                            _ = Task.Run(async () =>
                            {
                                string output = await CommandRunner.RunAsync(command);
                                CommandOutput?.Invoke(HostNick, output);

                                string? admin;
                                lock (_lock) admin = _adminNick;
                                if (admin != null && admin != HostNick)
                                {
                                    var ac = FindClient(admin);
                                    if (ac != null)
                                        await TryWriteAsync(ac, ChatProtocol.FrameControl("CMD_OUT", "",
                                            Encoding.UTF8.GetBytes(HostNick + '\x1f' + output)));
                                }
                            });
                        }
                        else
                        {
                            var targetClient = FindClient(target);
                            if (targetClient == null)
                            {
                                await TryWriteAsync(client, ChatProtocol.FrameControl("CMD_OUT", "",
                                    Encoding.UTF8.GetBytes(target + "\x1f[目标已离线] 命令未执行")));
                            }
                            else
                            {
                                await TryWriteAsync(targetClient, ChatProtocol.FrameControl("CMD", frame.Nick, frame.Payload));
                            }
                        }
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "CMD_OUT")
                    {
                        string payload = Encoding.UTF8.GetString(frame.Payload);
                        int sep = payload.IndexOf('\x1f');
                        if (sep <= 0) continue;
                        string target = payload.Substring(0, sep);
                        string output = payload.Substring(sep + 1);

                        string? admin;
                        lock (_lock) admin = _adminNick;
                        if (admin == null) continue;

                        if (admin == HostNick) CommandOutput?.Invoke(target, output);
                        else
                        {
                            var adminClient = FindClient(admin);
                            if (adminClient != null)
                                await TryWriteAsync(adminClient, ChatProtocol.FrameControl("CMD_OUT", "", frame.Payload));
                        }
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "WHO")
                    {
                        var list = GetMembers();
                        var payload = Encoding.UTF8.GetBytes(string.Join('\x1e', list));
                        await TryWriteAsync(client, ChatProtocol.FrameControl("MEMBERS", "", payload));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 解密失败（口令不一致）、帧错乱等都要让主机看得见，不能静默吞掉
            if (nick != null)
                SystemMessage?.Invoke($"与 {nick} 的连接异常中断：{ex.Message}");
        }
        finally
        {
            if (nick != null)
            {
                bool changed;
                lock (_lock)
                {
                    changed = _clients.Remove(client) | Members.Remove(nick);
                    if (_writeLocks.TryGetValue(client, out var gate))
                    {
                        _writeLocks.Remove(client);
                        gate.Dispose();
                    }
                }
                if (changed)
                {
                    bool adminLeft;
                    lock (_lock)
                    {
                        adminLeft = _adminNick == nick;
                        if (adminLeft) _adminNick = null;
                    }

                    NotifySystem($"{nick} 离开了聊天室");
                    if (adminLeft)
                    {
                        // 静默释放管理员位置，不广播任何提示文案
                        BroadcastAdmin();
                    }
                    BroadcastMembers();
                }
            }
        }
    }

    private TcpClient? FindClient(string nick)
    {
        lock (_lock)
        {
            foreach (var kv in _clients)
                if (kv.Value == nick) return kv.Key;
            return null;
        }
    }

    private void BroadcastAdmin()
    {
        string admin;
        lock (_lock) admin = _adminNick ?? "";
        BroadcastFrame(ChatProtocol.FrameControl("ADMIN_CHANGED", "", Encoding.UTF8.GetBytes(admin)), null);
        AdminChanged?.Invoke(admin.Length == 0 ? null : admin);
    }

    /// <summary>主机输入 /admin 时申请管理员身份（与客户端同样的“先到先得”规则）。</summary>
    public bool TryClaimAdminAsHost()
    {
        lock (_lock)
        {
            if (_adminNick != null) return false;
            _adminNick = HostNick;
        }
        // 隐藏机制：不广播提示，仅同步内部状态
        BroadcastAdmin();
        return true;
    }

    /// <summary>主机以管理员身份直接下发命令（不经网络回环）。</summary>
    public bool SendCommandFromHost(string targetNick, string command)
    {
        if (targetNick == HostNick)
        {
            _ = Task.Run(async () =>
            {
                string output = await CommandRunner.RunAsync(command);
                CommandOutput?.Invoke(HostNick, output);
            });
            return true;
        }

        var c = FindClient(targetNick);
        if (c == null) return false;
        var payload = Encoding.UTF8.GetBytes(targetNick + '\x1f' + command);
        _ = TryWriteAsync(c, ChatProtocol.FrameControl("CMD", HostNick, payload));
        return true;
    }

    private void BroadcastMembers()
    {
        var list = GetMembers();
        var payload = Encoding.UTF8.GetBytes(string.Join('\x1e', list));
        BroadcastFrame(ChatProtocol.FrameControl("MEMBERS", "", payload), null);
        MembersUpdated?.Invoke(list);
    }

    private List<string> GetMembers()
    {
        lock (_lock) return new List<string>(Members);
    }

    private void NotifySystem(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        BroadcastFrame(ChatProtocol.FrameControl("SYS", "", payload), null);
        SystemMessage?.Invoke(text);
    }

    /// <summary>主机主动发送消息：向所有客户端广播密文帧。</summary>
    public void BroadcastFromHost(byte[] frame) => BroadcastFrame(frame, null);

    private void BroadcastFrame(byte[] frame, TcpClient? excluded)
    {
        List<TcpClient> targets;
        lock (_lock) targets = new List<TcpClient>(_clients.Keys);
        foreach (var t in targets)
        {
            if (t == excluded) continue;
            _ = TryWriteAsync(t, frame);
        }
    }

    private async Task TryWriteAsync(TcpClient client, byte[] frame)
    {
        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_writeLocks.TryGetValue(client, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                _writeLocks[client] = gate;
            }
        }

        try { await gate.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (client.Connected)
            {
                var stream = client.GetStream();
                await stream.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 写失败交由读循环断线清理
        }
        finally
        {
            try { gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private static string SafeEndpoint(TcpClient client)
    {
        try { return client.Client.RemoteEndPoint?.ToString() ?? "未知地址"; }
        catch { return "未知地址"; }
    }

    /// <summary>当前在线客户端的来源地址（不含主机）。</summary>
    public IReadOnlyList<string> GetClientEndpoints()
    {
        lock (_lock)
        {
            return _clients.Keys.Select(c => $"{SafeEndpoint(c)}（{_clients[c]}）").ToList();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        lock (_lock)
        {
            foreach (var c in _clients.Keys) { try { c.Close(); } catch { } }
            _clients.Clear();
        }
    }
}
