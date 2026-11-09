using System.IO;
using System.Text;

namespace ClassBox.Plugins.ClassTalk.Net;

/// <summary>
/// 简单可靠的二进制帧协议：行头 + 定长载荷。
/// 帧格式：KIND|TYPE|NICK|LEN\n  + payload(LEN 字节)
///   MSG  : TYPE 为空；payload = LRNC-1024-AEAD 封装密文
///   CTRL : TYPE ∈ JOIN / LEAVE / WHO / MEMBERS / SYS；payload = 负载字节
/// 载荷可为任意二进制（密文），LEN 保证精确切分。
/// </summary>
public sealed record ChatFrame(string Kind, string Type, string Nick, byte[] Payload);

public static class ChatProtocol
{
    public const int MaxHeaderBytes = 1024;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 清理昵称中的协议分隔符与控制字符（| \n \r 等），避免帧头被破坏导致解码错位（乱码）。
    /// </summary>
    public static string SanitizeNick(string nick)
    {
        if (string.IsNullOrEmpty(nick)) return "";
        var sb = new StringBuilder(nick.Length);
        foreach (char c in nick)
        {
            if (c == '|' || c == '\n' || c == '\r' || char.IsControl(c)) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    public static byte[] FrameMessage(string nick, byte[] encrypted)
        => Frame("MSG", "", nick, encrypted);

    /// <summary>PLAIN 帧：明文字段（UTF-8 编码，用于明文传输通道）。</summary>
    public static byte[] FramePlainMessage(string nick, string text)
        => Frame("PLAIN", "", nick, Encoding.UTF8.GetBytes(text));

    public static byte[] FrameControl(string type, string nick, byte[] payload)
        => Frame("CTRL", type, nick, payload);

    private static byte[] Frame(string kind, string type, string nick, byte[] payload)
    {
        if (payload.Length > MaxPayloadBytes) throw new InvalidOperationException("载荷超过协议上限");
        nick = SanitizeNick(nick);
        string header = $"{kind}|{type}|{nick}|{payload.Length}\n";
        var result = new byte[Encoding.UTF8.GetByteCount(header) + payload.Length];
        int written = Encoding.UTF8.GetBytes(header, 0, header.Length, result, 0);
        Array.Copy(payload, 0, result, written, payload.Length);
        return result;
    }

    /// <summary>读取一帧；连接正常关闭时返回 null。</summary>
    public static async Task<ChatFrame?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[]? header = await ReadLineBytesAsync(stream, ct);
        if (header == null) return null;
        string line = Encoding.UTF8.GetString(header);
        var parts = line.Split('|');
        if (parts.Length != 4) throw new IOException("帧格式错误");
        string kind = parts[0];
        string type = parts[1];
        string nick = parts[2];
        if (kind != "MSG" && kind != "PLAIN" && kind != "CTRL") throw new IOException("未知帧类型");
        int len;
        if (!int.TryParse(parts[3], out len) || len < 0 || len > MaxPayloadBytes)
            throw new IOException("帧载荷长度异常");
        var payload = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = await stream.ReadAsync(payload.AsMemory(read, len - read), ct);
            if (n == 0) return null;
            read += n;
        }
        return new ChatFrame(kind, type, nick, payload);
    }

    /// <summary>按行读取（到 \n 为止）；EOF 且无数据时返回 null。</summary>
    private static async Task<byte[]?> ReadLineBytesAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var one = new byte[1];
            int n = await stream.ReadAsync(one.AsMemory(), ct);
            if (n == 0)
            {
                if (buffer.Length == 0) return null;
                throw new IOException("连接在帧头中途关闭");
            }
            if (one[0] == (byte)'\n') break;
            buffer.WriteByte(one[0]);
            if (buffer.Length > MaxHeaderBytes) throw new IOException("帧头超长");
        }
        return buffer.ToArray();
    }
}
