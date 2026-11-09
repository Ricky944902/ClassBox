using System.Diagnostics;

namespace ClassBox.Plugins.NetworkToolkit.Net;

/// <summary>Ping 单次结果。</summary>
public sealed class PingProbe
{
    public int Sequence { get; init; }

    public bool Success { get; init; }

    public long RoundtripMs { get; init; }

    public string Message { get; init; } = string.Empty;

    public string Display => Success
        ? $"#{Sequence} 来自响应：{RoundtripMs} ms"
        : $"#{Sequence} 超时 / 失败：{Message}";
}

/// <summary>Ping 汇总结果。</summary>
public sealed class PingSummary
{
    public string Host { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public int Sent { get; init; }

    public int Received { get; init; }

    public List<PingProbe> Probes { get; init; } = new();

    public int Lost => Math.Max(0, Sent - Received);

    public double LossPercent => Sent == 0 ? 0 : Math.Round(Lost * 100.0 / Sent, 1);

    public long MinMs => Received == 0 ? 0 : Probes.Where(p => p.Success).Min(p => p.RoundtripMs);

    public long MaxMs => Received == 0 ? 0 : Probes.Where(p => p.Success).Max(p => p.RoundtripMs);

    public double AvgMs => Received == 0 ? 0 : Math.Round(Probes.Where(p => p.Success).Average(p => (double)p.RoundtripMs), 1);

    public string Describe()
        => $"目标 {Host}{(string.IsNullOrWhiteSpace(Address) ? string.Empty : " (" + Address + ")")}："
           + $"发送 {Sent}，接收 {Received}，丢包 {Lost}（{LossPercent}%）"
           + (Received > 0 ? $"，往返 min/avg/max = {MinMs}/{AvgMs}/{MaxMs} ms" : string.Empty);
}

/// <summary>Ping 连通性测试。</summary>
public static class PingRunner
{
    /// <summary>连续 ping 指定主机；不抛异常。</summary>
    public static async Task<PingSummary> RunAsync(string host, int count, int timeoutMs,
        Action<PingProbe>? onProbe, CancellationToken cancellationToken = default)
    {
        host = (host ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return new PingSummary { Host = "(空)", Probes = new List<PingProbe>() };
        }

        count = Math.Clamp(count, 1, 20);
        timeoutMs = Math.Clamp(timeoutMs, 200, 10_000);

        var probes = new List<PingProbe>(count);
        var received = 0;
        var address = string.Empty;

        using var ping = new Ping();

        for (var i = 1; i <= count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            PingProbe probe;
            var buffer = Encoding.ASCII.GetBytes("classbox-ping");

            try
            {
                var reply = await ping.SendPingAsync(host, timeoutMs, buffer).ConfigureAwait(false);

                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    address = reply.Address?.ToString() ?? address;
                    probe = new PingProbe { Sequence = i, Success = true, RoundtripMs = reply.RoundtripTime };
                }
                else
                {
                    probe = new PingProbe { Sequence = i, Success = false, Message = reply.Status.ToString() };
                }
            }
            catch (Exception ex)
            {
                probe = new PingProbe { Sequence = i, Success = false, Message = ex.Message };
            }

            probes.Add(probe);
            onProbe?.Invoke(probe);

            if (i < count) await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return new PingSummary
        {
            Host = host,
            Address = address,
            Sent = probes.Count,
            Received = received,
            Probes = probes
        };
    }
}
