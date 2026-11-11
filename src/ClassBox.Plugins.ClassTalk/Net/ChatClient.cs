using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ClassBox.Plugins.ClassTalk.Crypto;

namespace ClassBox.Plugins.ClassTalk.Net;

/// <summary>
/// 加入（客户端）模式：连接主机，收发 LRNC 加密消息。
/// 支持两种入群方式：口令派生密钥入群（ConnectAsync）与 /admin 免密钥入群（ConnectPasswordlessAsync）。
/// </summary>
public sealed class ChatClient : IDisposable
{
    private TcpClient? _client;
    private readonly CancellationTokenSource _cts = new();
    private Lrnc1024Session? _session;
    private bool _disconnectedRaised;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<ChatFrame> _pending = new();   // 免密钥入群时在拿到房间密钥前暂存的帧
    private RSA? _rsa;
    private TaskCompletionSource<bool>? _keyArrived;

    public string Nick { get; private set; } = "";

    /// <summary>本次连接是否为 /admin 免密钥入群。</summary>
    public bool Passwordless { get; private set; }

    /// <summary>是否已取得房间密钥（免密钥入群完成握手的标志）。</summary>
    public bool HasSession => _session != null;

    public event Action<string, string>? MessageReceived;   // sender, 明文（加密通道解密后）
    public event Action<string, string>? PlainMessageReceived; // sender, 明文（PLAIN 通道）
    public event Action<IReadOnlyList<string>>? MembersUpdated;
    public event Action<string>? SystemMessage;
    public event Action<string>? Disconnected;              // 断线原因
    public event Action<bool, string>? AdminResult;         // 申请管理员的结果（granted, 现有管理员昵称）
    public event Action<IReadOnlyList<string>>? AdminsChanged; // 管理员集合变化

    private volatile string[] _admins = Array.Empty<string>();

    /// <summary>最近一次收到的管理员集合快照（视图重建时可回填本地状态）。</summary>
    public IReadOnlyList<string> Admins => _admins;
    public event Action<IReadOnlyList<MemberInfo>>? RosterUpdated; // 成员名册（昵称/在线/身份）
    public event Action<bool, string>? AdminOpResult;        // 群聊管理操作结果（ok, 说明）
    public event Action<string>? Kicked;                    // 被管理员移出群聊
    public event Action<string, string>? CommandReceived;   // 管理员下发命令：adminNick, command
    public event Action<string, string>? CommandOutputReceived; // 目标回传输出：target, output

    public async Task ConnectAsync(string host, int port, string nick, Lrnc1024Session session)
    {
        _session = session;
        Passwordless = false;
        Nick = ChatProtocol.SanitizeNick(nick);
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, _cts.Token);
        var stream = _client.GetStream();
        var join = ChatProtocol.FrameControl("JOIN", Nick, Encoding.UTF8.GetBytes(Nick));
        await stream.WriteAsync(join, _cts.Token);
        _ = ReceiveLoopAsync(stream);
    }

    /// <summary>
    /// /admin 免密钥入群：提交一次性 RSA 公钥，主机用其封装房间主密钥回传（KEY 帧），
    /// 客户端解密后即拥有与口令入群等同的会话能力，并由主机直接授予管理员身份。
    /// </summary>
    public async Task ConnectPasswordlessAsync(string host, int port, string nick)
    {
        Passwordless = true;
        _session = null;
        Nick = ChatProtocol.SanitizeNick(nick);
        _rsa = RSA.Create(2048);
        _keyArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _client = new TcpClient();
        await _client.ConnectAsync(host, port, _cts.Token);
        var stream = _client.GetStream();

        string pub = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());
        var join = ChatProtocol.FrameControl("JOIN", Nick,
            Encoding.UTF8.GetBytes(ChatProtocol.BypassJoinPrefix + pub));
        await stream.WriteAsync(join, _cts.Token);
        _ = ReceiveLoopAsync(stream);
    }

    /// <summary>等待房间密钥下发完成；超时或失败返回 false。</summary>
    public async Task<bool> WaitForSessionAsync(TimeSpan timeout)
    {
        if (_session != null) return true;
        if (_keyArrived == null) return false;
        var done = await Task.WhenAny(_keyArrived.Task, Task.Delay(timeout));
        return done == _keyArrived.Task && _keyArrived.Task.Result;
    }

    public async Task SendAsync(string message, bool plain = false)
    {
        if (_client == null || !_client.Connected)
            throw new InvalidOperationException("连接已断开，消息没有发出去");
        byte[] frame;
        if (plain)
        {
            frame = ChatProtocol.FramePlainMessage(Nick, message);
        }
        else
        {
            var session = _session ?? throw new InvalidOperationException("尚未取得房间密钥，无法发送加密消息");
            byte[] encrypted = session.Encrypt(message);
            frame = ChatProtocol.FrameMessage(Nick, encrypted);
        }
        await _writeLock.WaitAsync(_cts.Token);
        try
        {
            var stream = _client.GetStream();
            await stream.WriteAsync(frame, _cts.Token);
            await stream.FlushAsync(_cts.Token);
        }
        finally { _writeLock.Release(); }
        if (plain) PlainMessageReceived?.Invoke(Nick, message); // 本地显示自己的消息
        else MessageReceived?.Invoke(Nick, message);
    }

    /// <summary>申请成为管理员（房间内还没有管理员时会被主机批准）。</summary>
    public Task RequestAdminAsync() => SendControlAsync("ADMIN_REQ", Array.Empty<byte>());

    /// <summary>拉取群聊成员名册（仅管理员有效）。</summary>
    public Task RequestRosterAsync() => SendControlAsync("MGR_LIST", Array.Empty<byte>());

    /// <summary>移除成员（主机鉴权后生效）。</summary>
    public Task KickMemberAsync(string targetNick) => SendAdminOpAsync("KICK", targetNick);

    /// <summary>授予管理员（主机鉴权后生效）。</summary>
    public Task GrantAdminAsync(string targetNick) => SendAdminOpAsync("GRANT", targetNick);

    /// <summary>取消管理员（主机鉴权后生效）。</summary>
    public Task RevokeAdminAsync(string targetNick) => SendAdminOpAsync("REVOKE", targetNick);

    private Task SendAdminOpAsync(string action, string targetNick)
        => SendControlAsync("MGR", Encoding.UTF8.GetBytes(action + ChatProtocol.FieldSeparator + targetNick));

    /// <summary>管理员下发命令（经主机鉴权后转发到目标机器）。</summary>
    public Task SendCommandAsync(string targetNick, string command)
        => SendControlAsync("CMD", Encoding.UTF8.GetBytes(targetNick + ChatProtocol.FieldSeparator + command));

    /// <summary>作为被控端，把命令输出回传给主机。</summary>
    public Task SendCommandOutputAsync(string targetNick, string output)
        => SendControlAsync("CMD_OUT", Encoding.UTF8.GetBytes(targetNick + ChatProtocol.FieldSeparator + output));

    private async Task SendControlAsync(string type, byte[] payload)
    {
        if (_client == null || !_client.Connected)
            throw new InvalidOperationException("连接已断开");

        var frame = ChatProtocol.FrameControl(type, Nick, payload);
        await _writeLock.WaitAsync(_cts.Token);
        try
        {
            var stream = _client.GetStream();
            await stream.WriteAsync(frame, _cts.Token);
            await stream.FlushAsync(_cts.Token);
        }
        finally { _writeLock.Release(); }
    }

    public async Task LeaveAsync()
    {
        try
        {
            if (_client != null && _client.Connected)
            {
                var leave = ChatProtocol.FrameControl("LEAVE", Nick, Array.Empty<byte>());
                await _client.GetStream().WriteAsync(leave, _cts.Token);
            }
        }
        catch { }
        finally { Dispose(); }
    }

    private async Task ReceiveLoopAsync(Stream stream)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await ChatProtocol.ReadFrameAsync(stream, _cts.Token);
                if (frame == null) break;
                if (!Dispatch(frame)) return;
            }
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            // 断线：由 finally 统一上报
        }
        finally
        {
            if (!_disconnectedRaised)
            {
                _disconnectedRaised = true;
                Disconnected?.Invoke("连接已断开");
            }
        }
    }

    /// <summary>处理单帧；返回 false 表示连接需要结束（被移出群聊 / 服务端要求离开）。</summary>
    private bool Dispatch(ChatFrame frame)
    {
        if (frame.Kind == "MSG")
        {
            if (frame.Nick == Nick) return true; // 自己已本地显示
            if (_session == null) { Queue(frame); return true; }
            try { MessageReceived?.Invoke(frame.Nick, _session.DecryptString(frame.Payload)); }
            catch (Exception) { /* 密钥不一致导致单条解密失败，不影响后续收发 */ }
            return true;
        }

        if (frame.Kind == "PLAIN")
        {
            if (frame.Nick == Nick) return true;
            PlainMessageReceived?.Invoke(frame.Nick, Encoding.UTF8.GetString(frame.Payload));
            return true;
        }

        if (frame.Kind != "CTRL") return true;

        switch (frame.Type)
        {
            case "KEY":
                InstallSession(frame.Payload);
                break;

            case "MEMBERS":
            {
                string text = Encoding.UTF8.GetString(frame.Payload);
                var list = text.Split(ChatProtocol.RecordSeparator, StringSplitOptions.RemoveEmptyEntries);
                MembersUpdated?.Invoke(list);
                break;
            }
            case "SYS":
                SystemMessage?.Invoke(Encoding.UTF8.GetString(frame.Payload));
                break;
            case "ADMIN_RESULT":
            {
                string text = Encoding.UTF8.GetString(frame.Payload);
                if (text == "OK") AdminResult?.Invoke(true, "");
                else
                {
                    string holder = text.StartsWith("DENY|") ? text.Substring(5) : text;
                    AdminResult?.Invoke(false, holder);
                }
                break;
            }
            case "ADMINS":
            {
                string text = Encoding.UTF8.GetString(frame.Payload);
                var list = text.Length == 0
                    ? Array.Empty<string>()
                    : text.Split(ChatProtocol.RecordSeparator, StringSplitOptions.RemoveEmptyEntries);
                _admins = list;
                AdminsChanged?.Invoke(list);
                break;
            }
            case "ADMIN_CHANGED":
            {
                // 兼容旧版主机：单个管理员昵称
                string text = Encoding.UTF8.GetString(frame.Payload);
                _admins = text.Length == 0 ? Array.Empty<string>() : new[] { text };
                AdminsChanged?.Invoke(_admins);
                break;
            }
            case "MGR_MEMBERS":
                RosterUpdated?.Invoke(ChatProtocol.DecodeRoster(Encoding.UTF8.GetString(frame.Payload)));
                break;
            case "MGR_RESULT":
            {
                string text = Encoding.UTF8.GetString(frame.Payload);
                bool ok = text.StartsWith("OK", StringComparison.Ordinal);
                string detail = text.Length > 3 ? text.Substring(3) : "";
                AdminOpResult?.Invoke(ok, detail);
                break;
            }
            case "KICKED":
                Kicked?.Invoke(Encoding.UTF8.GetString(frame.Payload));
                return false;
            case "CMD":
            {
                string text = Encoding.UTF8.GetString(frame.Payload);
                int sep = text.IndexOf(ChatProtocol.FieldSeparator);
                if (sep > 0)
                    CommandReceived?.Invoke(frame.Nick, text.Substring(sep + 1));
                break;
            }
            case "CMD_OUT":
            {
                string text = Encoding.UTF8.GetString(frame.Payload);
                int sep = text.IndexOf(ChatProtocol.FieldSeparator);
                if (sep > 0)
                    CommandOutputReceived?.Invoke(text.Substring(0, sep), text.Substring(sep + 1));
                break;
            }
            case "LEAVE":
                return false;
        }

        return true;
    }

    private void Queue(ChatFrame frame)
    {
        if (_pending.Count >= 200) _pending.RemoveAt(0);
        _pending.Add(frame);
    }

    /// <summary>解密主密钥并建立会话，随后回放等待中的消息帧。</summary>
    private void InstallSession(byte[] wrappedKey)
    {
        try
        {
            if (_rsa == null) throw new InvalidOperationException("缺少本机私钥");
            byte[] master = _rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
            _session = new Lrnc1024Session(master);
            _keyArrived?.TrySetResult(true);

            var pending = _pending.ToList();
            _pending.Clear();
            foreach (var f in pending) Dispatch(f);
        }
        catch (Exception ex)
        {
            _keyArrived?.TrySetResult(false);
            SystemMessage?.Invoke($"房间密钥解析失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _client?.Close(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        try { _rsa?.Dispose(); } catch { }
        _rsa = null;
    }
}
