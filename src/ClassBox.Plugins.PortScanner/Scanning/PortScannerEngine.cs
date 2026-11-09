using System.Diagnostics;
using System.Net.NetworkInformation;

namespace ClassBox.Plugins.PortScanner.Scanning;

/// <summary>Ping 连通检测结果。</summary>
public sealed class PingCheckResult
{
    public bool Success { get; init; }

    public string Address { get; init; } = "-";

    public long RoundtripMs { get; init; }

    public int Ttl { get; init; }

    public string Message { get; init; } = string.Empty;

    public string ToLine() => Success
        ? $"Ping {Address} 成功：{RoundtripMs} ms / TTL {Ttl}"
        : $"Ping {Address} 失败：{Message}";
}

/// <summary>纯 .NET 实现的 TCP 端口扫描与 Ping 检测引擎。</summary>
public static class PortScannerEngine
{
    /// <summary>单次扫描的端口数量上限，避免误操作导致长时间占用。</summary>
    public const int MaxPortCount = 8192;

    /// <summary>解析主机名或 IP 字面量。</summary>
    public static async Task<IPAddress> ResolveHostAsync(string host, CancellationToken token)
    {
        var trimmed = host.Trim();
        if (IPAddress.TryParse(trimmed, out var parsed)) return parsed;

        var addresses = await Dns.GetHostAddressesAsync(trimmed, token).ConfigureAwait(false);
        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        return v4 ?? addresses.FirstOrDefault()
               ?? throw new InvalidOperationException($"无法解析主机：{trimmed}");
    }

    /// <summary>
    /// 解析端口表达式，支持 <c>1-1024</c>、<c>80,443,8080</c>、<c>80,8000-8010,443</c>。
    /// </summary>
    public static IReadOnlyList<int> ParsePorts(string expression, out string error)
    {
        error = string.Empty;
        var result = new SortedSet<int>();
        var text = (expression ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "请输入端口范围，例如 1-1024 或 80,443,8080";
            return Array.Empty<int>();
        }

        foreach (var rawToken in text.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            if (token.Length == 0) continue;

            var dashIndex = token.IndexOf('-');
            if (dashIndex > 0)
            {
                var startText = token[..dashIndex].Trim();
                var endText = token[(dashIndex + 1)..].Trim();
                if (!int.TryParse(startText, out var start) || !int.TryParse(endText, out var end))
                {
                    error = $"端口段「{token}」格式不正确";
                    return Array.Empty<int>();
                }

                if (start > end) (start, end) = (end, start);
                if (start < 1 || end > 65535)
                {
                    error = $"端口段「{token}」超出 1-65535 范围";
                    return Array.Empty<int>();
                }

                for (var port = start; port <= end; port++)
                {
                    result.Add(port);
                    if (result.Count > MaxPortCount)
                    {
                        error = $"一次最多扫描 {MaxPortCount} 个端口，请缩小范围";
                        return Array.Empty<int>();
                    }
                }
            }
            else
            {
                if (!int.TryParse(token, out var port) || port < 1 || port > 65535)
                {
                    error = $"端口「{token}」不是 1-65535 之间的整数";
                    return Array.Empty<int>();
                }

                result.Add(port);
            }
        }

        if (result.Count == 0)
        {
            error = "未解析出有效端口";
            return Array.Empty<int>();
        }

        return result.ToList();
    }

    /// <summary>
    /// 并发 TCP Connect 扫描。<paramref name="onResult"/> 会在任务线程回调（调用方需自行切回 UI 线程）。
    /// 返回全部结果按端口升序排列。
    /// </summary>
    public static async Task<IReadOnlyList<PortScanResult>> ScanAsync(
        IPAddress address,
        IReadOnlyList<int> ports,
        int timeoutMs,
        int concurrency,
        Action<PortScanResult>? onResult,
        CancellationToken token)
    {
        if (ports.Count == 0) return Array.Empty<PortScanResult>();
        if (timeoutMs < 50) timeoutMs = 50;
        if (concurrency < 1) concurrency = 1;
        if (concurrency > 512) concurrency = 512;

        var results = new List<PortScanResult>(ports.Count);
        var gate = new object();
        using var semaphore = new SemaphoreSlim(concurrency);

        var tasks = ports.Select(async port =>
        {
            await semaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var result = await ScanSinglePortAsync(address, port, timeoutMs, token).ConfigureAwait(false);
                lock (gate) results.Add(result);
                onResult?.Invoke(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个端口异常不影响整体扫描
            }
            finally
            {
                semaphore.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户取消：返回已完成部分
        }

        lock (gate)
        {
            return results.OrderBy(r => r.Port).ToList();
        }
    }

    private static async Task<PortScanResult> ScanSinglePortAsync(
        IPAddress address,
        int port,
        int timeoutMs,
        CancellationToken token)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new PortScanResult
            {
                Port = port,
                Service = ServiceNames.Guess(port),
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                IsOpen = true
            };
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            token.ThrowIfCancellationRequested();
            stopwatch.Stop();
            return new PortScanResult
            {
                Port = port,
                Service = ServiceNames.Guess(port),
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                IsOpen = false
            };
        }
    }

    /// <summary>Ping 检测（ICMP）。</summary>
    public static async Task<PingCheckResult> PingAsync(string host, int timeoutMs, CancellationToken token)
    {
        if (timeoutMs < 100) timeoutMs = 100;

        try
        {
            var address = await ResolveHostAsync(host, token).ConfigureAwait(false);
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            return new PingCheckResult
            {
                Success = reply.Status == IPStatus.Success,
                Address = address.ToString(),
                RoundtripMs = reply.RoundtripTime,
                Ttl = reply.Options?.Ttl ?? 0,
                Message = reply.Status == IPStatus.Success
                    ? "连通"
                    : $"{reply.Status}（可能被防火墙拦截 ICMP，不代表端口不通）"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PingCheckResult
            {
                Success = false,
                Address = host,
                Message = ex.Message
            };
        }
    }
}
