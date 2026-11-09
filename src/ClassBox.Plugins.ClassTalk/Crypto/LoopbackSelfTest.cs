using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ClassBox.Plugins.ClassTalk.Net;

namespace ClassBox.Plugins.ClassTalk.Crypto;

/// <summary>
/// 真实 TCP 回环自检：主机 + 两个客户端，加密通道与明文通道各跑一遍多语言样本，
/// 端到端比对字节是否一致，用于验证"传输不产生乱码"。
/// </summary>
public static class LoopbackSelfTest
{
    public static string Run()
    {
        var samples = UnicodeSelfTest.Samples;
        var master = new byte[32];
        for (int i = 0; i < master.Length; i++) master[i] = (byte)(i * 13 + 5);

        int port = FreePort();
        using var server = new ChatServer("主机Host", new Lrnc1024Session(master), port);
        var serverGot = new List<(string Nick, string Text, bool Plain)>();
        server.MessageReceived += (n, t) => { lock (serverGot) serverGot.Add((n, t, false)); };
        server.PlainMessageReceived += (n, t) => { lock (serverGot) serverGot.Add((n, t, true)); };
        server.Start();

        using var a = new ChatClient();
        var aGot = new List<(string, string, bool)>();
        a.MessageReceived += (n, t) => { lock (aGot) aGot.Add((n, t, false)); };
        a.PlainMessageReceived += (n, t) => { lock (aGot) aGot.Add((n, t, true)); };
        a.ConnectAsync("127.0.0.1", port, "客户端A", new Lrnc1024Session(master)).GetAwaiter().GetResult();

        using var b = new ChatClient();
        var bGot = new List<(string, string, bool)>();
        b.MessageReceived += (n, t) => { lock (bGot) bGot.Add((n, t, false)); };
        b.PlainMessageReceived += (n, t) => { lock (bGot) bGot.Add((n, t, true)); };
        b.ConnectAsync("127.0.0.1", port, "客户端B", new Lrnc1024Session(master)).GetAwaiter().GetResult();

        Thread.Sleep(300); // 等 JOIN 与成员表就绪

        var expected = new List<(string Text, bool Plain)>();
        foreach (var s in samples)
        {
            a.SendAsync(s, plain: false).GetAwaiter().GetResult();
            expected.Add((s, false));
        }
        foreach (var s in samples)
        {
            a.SendAsync(s, plain: true).GetAwaiter().GetResult();
            expected.Add((s, true));
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            lock (bGot) if (bGot.Count >= expected.Count) break;
            Thread.Sleep(50);
        }
        Thread.Sleep(200);

        List<(string Nick, string Text, bool Plain)> bSnapshot, sSnapshot;
        lock (bGot) bSnapshot = new List<(string, string, bool)>(bGot);
        lock (serverGot) sSnapshot = new List<(string Nick, string Text, bool Plain)>(serverGot);

        if (bSnapshot.Count != expected.Count)
            throw new Exception($"客户端B 收到 {bSnapshot.Count} 条，期望 {expected.Count} 条");
        for (int i = 0; i < expected.Count; i++)
        {
            string channel = expected[i].Plain ? "明文" : "加密";
            if (bSnapshot[i].Item3 != expected[i].Plain)
                throw new Exception($"第 {i + 1} 条通道标识错误（{channel}）");
            if (!string.Equals(bSnapshot[i].Item1, "客户端A", StringComparison.Ordinal))
                throw new Exception($"第 {i + 1} 条发送者错乱：{bSnapshot[i].Item1}");
            if (!string.Equals(bSnapshot[i].Item2, expected[i].Text, StringComparison.Ordinal))
                throw new Exception($"第 {i + 1} 条正文不一致（{channel}通道）：收到「{bSnapshot[i].Item2}」");
        }

        if (sSnapshot.Count != expected.Count)
            throw new Exception($"主机收到 {sSnapshot.Count} 条，期望 {expected.Count} 条");
        for (int i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(sSnapshot[i].Text, expected[i].Text, StringComparison.Ordinal))
                throw new Exception($"主机第 {i + 1} 条正文不一致：收到「{sSnapshot[i].Text}」");
        }

        return $"PASS（TCP 回环 2 客户端 / 加密与明文各 {samples.Length} 条 = {expected.Count} 条消息，主机与对端逐条一致）";
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
