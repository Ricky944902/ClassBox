using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ClassBox.Plugins.ClassTalk.Crypto;

namespace ClassBox.Plugins.ClassTalk.Net;

/// <summary>
/// 主机（服务器）模式：监听 TCP 端口，管理在线成员与管理员名册，
/// 转发消息密文（端到端加密语义），并以主机视角解密显示。
/// 管理员身份由主机服务端统一裁决：成员管理（移除成员、授予/取消管理员）
/// 必须经 <see cref="TryAdminOp"/> 鉴权通过后才广播生效。
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

    // 管理员集合（可多人）：/admin 首个申请者、免密钥入群者、被授予者
    private readonly HashSet<string> _admins = new(StringComparer.Ordinal);

    // 成员名册：含已离线成员，供管理员控制台的“在线状态”列展示
    private readonly List<string> _rosterOrder = new();
    private readonly Dictionary<string, bool> _rosterOnline = new(StringComparer.Ordinal);
    private const int MaxRosterEntries = 60;

    public string HostNick { get; }
    public int Port { get; }

    /// <summary>会话名称（广播给局域网检测用，也可为空自动取“昵称 的聊天室”）。</summary>
    public string RoomName { get; }

    /// <summary>
    /// 房间主密钥（32 字节）。仅用于 /admin 免密钥入群时通过 RSA-OAEP 下发给申请者，
    /// 不参与任何明文传输；为 null 时表示该房间不接受免密钥入群。
    /// </summary>
    public byte[]? MasterKey { get; }

    public List<string> Members { get; } = new(); // 含主机自身（在线成员）

    /// <summary>在线人数（含主机）。局域网公告与状态栏使用，需加锁读取。</summary>
    public int MemberCount { get { lock (_lock) return Members.Count; } }

    /// <summary>当前管理员昵称列表（仅服务端内部与管理员可见）。</summary>
    public IReadOnlyList<string> Admins { get { lock (_lock) return _rosterOrder.Where(_admins.Contains).ToList(); } }

    public event Action<string, string>? MessageReceived;   // sender, 明文（解密后）
    public event Action<string, string>? PlainMessageReceived; // sender, 明文（PLAIN 通道）
    public event Action<string>? SystemMessage;             // 系统消息
    public event Action<IReadOnlyList<string>>? MembersUpdated;
    public event Action<string>? Stopped;                   // 服务器异常停止
    public event Action<IReadOnlyList<string>>? AdminsChanged; // 管理员集合变化
    public event Action<IReadOnlyList<MemberInfo>>? RosterUpdated; // 成员名册（昵称/在线/身份）
    public event Action<string, string>? CommandIssued;     // 管理员下发命令：target, command
    public event Action<string, string>? CommandOutput;     // 目标回传命令输出：target, output

    public ChatServer(string hostNick, Lrnc1024Session session, int port, string roomName = "", byte[]? masterKey = null)
    {
        HostNick = hostNick;
        _session = session;
        Port = port;
        RoomName = string.IsNullOrWhiteSpace(roomName) ? $"{hostNick} 的聊天室" : roomName.Trim();
        MasterKey = masterKey is { Length: 32 } ? masterKey : null;
        _listener = new TcpListener(IPAddress.Any, port);
        Members.Add(hostNick);
        _rosterOrder.Add(hostNick);
        _rosterOnline[hostNick] = true;
    }

    public void Start()
    {
        _listener.Start();

        // 房主天然拥有群聊管理权限：不依赖 /admin 抢占，避免他人先申请管理员后房主失去管理能力
        lock (_lock) _admins.Add(HostNick);
        BroadcastAdmins();
        BroadcastMembers();

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

                // JOIN 载荷：普通入群为昵称说明文本；免密钥入群为 BYPASS|base64(公钥)
                string joinPayload = Encoding.UTF8.GetString(first.Payload ?? Array.Empty<byte>());
                bool bypass = joinPayload.StartsWith(ChatProtocol.BypassJoinPrefix, StringComparison.Ordinal);
                string clientPublicKey = bypass ? joinPayload.Substring(ChatProtocol.BypassJoinPrefix.Length) : "";

                lock (_lock)
                {
                    _clients[client] = nick;
                    if (!Members.Contains(nick)) Members.Add(nick);
                    AddToRosterLocked(nick, online: true);
                }
                NotifySystem($"{nick} 加入了聊天室");

                if (bypass)
                {
                    // 免密钥入群：先安全下发房间主密钥（RSA-OAEP 封装），客户端拿到即可解密；
                    // 再直接授予管理员身份（需求 B：/admin 免密钥入群后自动成为管理员）
                    if (!await SendMasterKeyAsync(client, clientPublicKey))
                    {
                        await TryWriteAsync(client, ChatProtocol.FrameControl("SYS", "",
                            Encoding.UTF8.GetBytes("免密钥入群失败：当前房间未开放免密钥加入")));
                        return;
                    }

                    lock (_lock) _admins.Add(nick);
                    await TryWriteAsync(client, ChatProtocol.FrameControl("ADMIN_RESULT", "",
                        Encoding.UTF8.GetBytes("OK")));
                }

                BroadcastMembers();
                BroadcastAdmins();

                if (IsAdmin(nick)) SendRosterTo(nick);

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
                        // 房间内还没有管理员时，第一个申请者取得管理员；此后新增管理员只能由现有管理员授予
                        bool granted;
                        string holder;
                        lock (_lock)
                        {
                            granted = _admins.Count == 0;
                            if (granted) _admins.Add(nick);
                            holder = _admins.Count == 0 ? "" : _rosterOrder.FirstOrDefault(_admins.Contains) ?? "";
                        }

                        if (granted)
                        {
                            await TryWriteAsync(client, ChatProtocol.FrameControl("ADMIN_RESULT", "",
                                Encoding.UTF8.GetBytes("OK")));
                            // 管理员身份属于隐藏机制：不向房间广播任何提示文案，只同步管理员可见状态
                            BroadcastAdmins();
                            SendRosterTo(nick);
                        }
                        else
                        {
                            await TryWriteAsync(client, ChatProtocol.FrameControl("ADMIN_RESULT", "",
                                Encoding.UTF8.GetBytes("DENY|" + holder)));
                            await TryWriteAsync(client, ChatProtocol.FrameControl("SYS", "",
                                Encoding.UTF8.GetBytes($"管理员已由 {holder} 取得，你无法重复获取")));
                        }
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "MGR")
                    {
                        // 群聊管理操作（移除成员 / 授予管理员 / 取消管理员）：一律由服务端鉴权裁决
                        string payload = Encoding.UTF8.GetString(frame.Payload ?? Array.Empty<byte>());
                        int sep = payload.IndexOf(ChatProtocol.FieldSeparator);
                        string action = sep > 0 ? payload.Substring(0, sep) : payload;
                        string target = sep > 0 ? payload.Substring(sep + 1) : "";

                        bool ok = TryAdminOp(frame.Nick, action, target, out string message);

                        await TryWriteAsync(client, ChatProtocol.FrameControl("MGR_RESULT", "",
                            Encoding.UTF8.GetBytes((ok ? "OK|" : "DENY|") + message)));
                        if (!ok)
                        {
                            // 鉴权失败要在发起端有明确反馈，而不是静默丢弃
                            await TryWriteAsync(client, ChatProtocol.FrameControl("SYS", "",
                                Encoding.UTF8.GetBytes(message)));
                        }
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "MGR_LIST")
                    {
                        if (IsAdmin(frame.Nick)) SendRosterTo(frame.Nick);
                        else
                            await TryWriteAsync(client, ChatProtocol.FrameControl("MGR_RESULT", "",
                                Encoding.UTF8.GetBytes("DENY|只有管理员可以查看群聊成员名册")));
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "CMD")
                    {
                        // 远程命令只允许管理员下发，主机负责鉴权与转发
                        if (!IsAdmin(frame.Nick))
                        {
                            await TryWriteAsync(client, ChatProtocol.FrameControl("SYS", "",
                                Encoding.UTF8.GetBytes("无管理员权限，命令未执行")));
                            continue;
                        }

                        string payload = Encoding.UTF8.GetString(frame.Payload);
                        int sep = payload.IndexOf(ChatProtocol.FieldSeparator);
                        if (sep <= 0) continue;
                        string target = payload.Substring(0, sep);
                        string command = payload.Substring(sep + 1);

                        CommandIssued?.Invoke(target, command);

                        if (target == HostNick)
                        {
                            // 目标是主机自己：主机本地执行并把输出交回发起的管理员
                            string requester = frame.Nick;
                            _ = Task.Run(async () =>
                            {
                                string output = await CommandRunner.RunAsync(command);
                                if (requester == HostNick) CommandOutput?.Invoke(HostNick, output);
                                else
                                {
                                    var ac = FindClient(requester);
                                    if (ac != null)
                                        await TryWriteAsync(ac, ChatProtocol.FrameControl("CMD_OUT", "",
                                            Encoding.UTF8.GetBytes(HostNick + ChatProtocol.FieldSeparator + output)));
                                }
                            });
                        }
                        else
                        {
                            var targetClient = FindClient(target);
                            if (targetClient == null)
                            {
                                await TryWriteAsync(client, ChatProtocol.FrameControl("CMD_OUT", "",
                                    Encoding.UTF8.GetBytes(target + ChatProtocol.FieldSeparator + "[目标已离线] 命令未执行")));
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
                        int sep = payload.IndexOf(ChatProtocol.FieldSeparator);
                        if (sep <= 0) continue;
                        string target = payload.Substring(0, sep);
                        string output = payload.Substring(sep + 1);

                        // 命令输出回送给所有管理员（多管理员场景），主机为管理员时本地直接回显
                        bool hostIsAdmin;
                        lock (_lock) hostIsAdmin = _admins.Contains(HostNick);
                        if (hostIsAdmin) CommandOutput?.Invoke(target, output);

                        var outFrame = ChatProtocol.FrameControl("CMD_OUT", "", frame.Payload);
                        foreach (var ac in GetAdminClients(excludingHost: true))
                            await TryWriteAsync(ac, outFrame);
                    }
                    else if (frame.Kind == "CTRL" && frame.Type == "WHO")
                    {
                        var list = GetMembers();
                        var payload = Encoding.UTF8.GetBytes(string.Join(ChatProtocol.RecordSeparator, list));
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
                    if (_rosterOnline.ContainsKey(nick)) _rosterOnline[nick] = false;
                }
                if (changed)
                {
                    bool adminLeft;
                    lock (_lock) adminLeft = _admins.Remove(nick);

                    NotifySystem($"{nick} 离开了聊天室");
                    if (adminLeft)
                    {
                        // 静默释放管理员身份，不广播任何提示文案
                        BroadcastAdmins(nick);
                    }
                    BroadcastMembers();
                    BroadcastRoster();
                }
            }
        }
    }

    // ===================== 成员名册与管理员 =====================

    private void AddToRosterLocked(string nick, bool online)
    {
        if (!_rosterOnline.ContainsKey(nick)) _rosterOrder.Add(nick);
        _rosterOnline[nick] = online;

        if (_rosterOrder.Count <= MaxRosterEntries) return;

        // 名册超限时优先淘汰最早的离线成员，避免长期运行无限膨胀
        string? victim = _rosterOrder.FirstOrDefault(n =>
            !string.Equals(n, HostNick, StringComparison.Ordinal) &&
            _rosterOnline.TryGetValue(n, out bool on) && !on);
        if (victim != null)
        {
            _rosterOrder.Remove(victim);
            _rosterOnline.Remove(victim);
            _admins.Remove(victim);
        }
    }

    private List<MemberInfo> GetRoster()
    {
        lock (_lock)
        {
            var list = new List<MemberInfo>(_rosterOrder.Count);
            foreach (var n in _rosterOrder)
            {
                bool online = _rosterOnline.TryGetValue(n, out bool on) && on;
                string role = string.Equals(n, HostNick, StringComparison.Ordinal) ? "H"
                    : _admins.Contains(n) ? "A" : "M";
                list.Add(new MemberInfo(n, online, role));
            }
            return list;
        }
    }

    private bool IsAdmin(string? nick)
    {
        if (string.IsNullOrEmpty(nick)) return false;
        lock (_lock) return _admins.Contains(nick);
    }

    private List<TcpClient> GetAdminClients(bool excludingHost)
    {
        lock (_lock)
        {
            return _clients
                .Where(kv => _admins.Contains(kv.Value))
                .Where(kv => !excludingHost || !string.Equals(kv.Value, HostNick, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    /// <summary>把成员名册推送给指定成员（仅管理员可收到，普通成员看不到管理员身份）。</summary>
    private void SendRosterTo(string nick)
    {
        var roster = GetRoster();
        var frame = ChatProtocol.FrameControl("MGR_MEMBERS", "",
            Encoding.UTF8.GetBytes(ChatProtocol.EncodeRoster(roster)));

        var target = FindClient(nick);
        if (target != null)
        {
            _ = TryWriteAsync(target, frame);
            if (string.Equals(nick, HostNick, StringComparison.Ordinal)) RosterUpdated?.Invoke(roster);
            return;
        }

        if (string.Equals(nick, HostNick, StringComparison.Ordinal)) RosterUpdated?.Invoke(roster);
    }

    /// <summary>向所有管理员推送最新名册，并同步主机本地视图。</summary>
    private void BroadcastRoster()
    {
        var roster = GetRoster();
        var frame = ChatProtocol.FrameControl("MGR_MEMBERS", "",
            Encoding.UTF8.GetBytes(ChatProtocol.EncodeRoster(roster)));
        foreach (var c in GetAdminClients(excludingHost: false)) _ = TryWriteAsync(c, frame);
        RosterUpdated?.Invoke(roster);
    }

    /// <summary>主机端主动刷新名册（打开群聊管理页时调用）。</summary>
    public void RefreshRoster() => BroadcastRoster();

    private void BroadcastAdmins(params string[] extraRecipients)
    {
        List<string> admins;
        lock (_lock) admins = _rosterOrder.Where(_admins.Contains).ToList();

        var frame = ChatProtocol.FrameControl("ADMINS", "",
            Encoding.UTF8.GetBytes(string.Join(ChatProtocol.RecordSeparator, admins)));

        foreach (var c in GetAdminClients(excludingHost: false)) _ = TryWriteAsync(c, frame);

        // 被取消管理员的一方也要收到最新集合（其中已不含自己），以便关闭控制台
        foreach (var nick in extraRecipients)
        {
            if (string.Equals(nick, HostNick, StringComparison.Ordinal)) continue;
            var c = FindClient(nick);
            if (c != null) _ = TryWriteAsync(c, frame);
        }

        AdminsChanged?.Invoke(admins);
    }

    /// <summary>
    /// 群聊管理唯一入口：服务端鉴权 + 执行 + 广播。
    /// action 取值 KICK（移除成员）/ GRANT（授予管理员）/ REVOKE（取消管理员）。
    /// 非管理员或非法目标一律拒绝，返回 false 并给出可读原因。
    /// </summary>
    public bool TryAdminOp(string adminNick, string action, string target, out string message)
    {
        message = "";
        target = (target ?? "").Trim();
        bool kickNow;

        lock (_lock)
        {
            if (string.IsNullOrEmpty(adminNick) || !_admins.Contains(adminNick))
            {
                message = "无管理员权限，操作已被主机拒绝";
                return false;
            }

            if (target.Length == 0)
            {
                message = "未指定要操作的成员";
                return false;
            }

            switch (action)
            {
                case "KICK":
                    if (string.Equals(target, HostNick, StringComparison.Ordinal))
                    {
                        message = "不能移除房主";
                        return false;
                    }
                    if (string.Equals(target, adminNick, StringComparison.Ordinal))
                    {
                        message = "不能移除自己";
                        return false;
                    }
                    if (!Members.Contains(target) && !_rosterOnline.ContainsKey(target))
                    {
                        message = "成员不存在或已不在群聊中";
                        return false;
                    }
                    kickNow = true;
                    message = $"已移除成员 {target}";
                    break;

                case "GRANT":
                    if (!Members.Contains(target))
                    {
                        message = "该成员不在线，无法授予管理员";
                        return false;
                    }
                    if (_admins.Contains(target))
                    {
                        message = "该成员已是管理员";
                        return false;
                    }
                    _admins.Add(target);
                    kickNow = false;
                    message = $"已授予 {target} 管理员";
                    break;

                case "REVOKE":
                    if (!_admins.Contains(target))
                    {
                        message = "该成员不是管理员";
                        return false;
                    }
                    _admins.Remove(target);
                    kickNow = false;
                    message = $"已取消 {target} 的管理员";
                    break;

                default:
                    message = "未知的管理操作";
                    return false;
            }
        }

        if (kickNow)
        {
            KickClient(target, "你已被管理员移出群聊");
            return true;
        }

        // 被取消管理员者已不在管理员通道内，必须单独补发，才能让其界面立即收回管理入口
        BroadcastAdmins(action == "REVOKE" ? new[] { target } : Array.Empty<string>());
        BroadcastRoster();

        // 被授予者需要立刻收到名册，才能打开群聊管理面板
        if (action == "GRANT") SendRosterTo(target);
        return true;
    }

    /// <summary>移除成员：通知被移除端并断开其连接，随后刷新名册与在线列表。</summary>
    private void KickClient(string target, string reason)
    {
        TcpClient? targetClient;
        lock (_lock)
        {
            targetClient = _clients.FirstOrDefault(kv => kv.Value == target).Key;
            if (targetClient != null) _clients.Remove(targetClient);
            Members.Remove(target);
            _admins.Remove(target);
            if (_rosterOnline.ContainsKey(target)) _rosterOnline[target] = false;
        }

        if (targetClient != null)
        {
            var frame = ChatProtocol.FrameControl("KICKED", "", Encoding.UTF8.GetBytes(reason));
            _ = Task.Run(async () =>
            {
                await TryWriteAsync(targetClient, frame);
                try { targetClient.Close(); } catch { }
            });
        }

        NotifySystem($"{target} 已被管理员移出群聊");
        BroadcastAdmins(target);
        BroadcastMembers();
        BroadcastRoster();
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

    /// <summary>用客户端提交的 RSA 公钥封装房间主密钥并下发（免密钥入群）。</summary>
    private async Task<bool> SendMasterKeyAsync(TcpClient client, string publicKeyBase64)
    {
        if (MasterKey == null || string.IsNullOrWhiteSpace(publicKeyBase64)) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            byte[] wrapped = rsa.Encrypt(MasterKey, RSAEncryptionPadding.OaepSHA256);
            await TryWriteAsync(client, ChatProtocol.FrameControl("KEY", "", wrapped));
            return true;
        }
        catch (Exception ex)
        {
            SystemMessage?.Invoke($"免密钥入群密钥下发失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>主机输入 /admin 时申请管理员身份（房间内还没有管理员时成功）。</summary>
    public bool TryClaimAdminAsHost()
    {
        lock (_lock)
        {
            if (_admins.Count > 0) return false;
            _admins.Add(HostNick);
        }
        // 隐藏机制：不广播提示，仅同步管理员可见状态
        BroadcastAdmins();
        SendRosterTo(HostNick);
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
        var payload = Encoding.UTF8.GetBytes(targetNick + ChatProtocol.FieldSeparator + command);
        _ = TryWriteAsync(c, ChatProtocol.FrameControl("CMD", HostNick, payload));
        return true;
    }

    /// <summary>主机以管理员身份执行群聊管理操作（本地直接调用服务端鉴权通道）。</summary>
    public bool TryAdminOpFromHost(string action, string target, out string message)
        => TryAdminOp(HostNick, action, target, out message);

    private void BroadcastMembers()
    {
        var list = GetMembers();
        var payload = Encoding.UTF8.GetBytes(string.Join(ChatProtocol.RecordSeparator, list));
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
