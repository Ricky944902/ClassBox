namespace ClassBox.Plugins.NetworkToolkit.Net;

/// <summary>公网出口 IP 查询结果（失败时给出可读原因，不抛异常）。</summary>
public sealed class PublicIpResult
{
    public bool Success { get; init; }

    public string Ip { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public string Describe()
        => Success ? $"公网出口 IP：{Ip}（来源 {Source}）" : "公网 IP 查询不可用：" + Error;
}

/// <summary>公网出口 IP 查询：多源回退，任一步失败均优雅降级。</summary>
public static class PublicIpQuery
{
    private static readonly (string Url, string Name)[] Sources =
    {
        ("https://api.ipify.org", "ipify.org"),
        ("https://ifconfig.me/ip", "ifconfig.me"),
        ("https://icanhazip.com", "icanhazip.com")
    };

    /// <summary>串行尝试多个公开服务，全部失败时返回失败原因。</summary>
    public static async Task<PublicIpResult> QueryAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        foreach (var (url, name) in Sources)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new PublicIpResult { Error = "已取消", Source = name };
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassBox/1.0");

                var text = (await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false)).Trim();
                var firstLine = text.Split('\n')[0].Trim();

                if (IPAddress.TryParse(firstLine, out var ip))
                {
                    return new PublicIpResult { Success = true, Ip = ip.ToString(), Source = name };
                }

                errors.Add($"{name} 返回内容无法解析");
            }
            catch (OperationCanceledException)
            {
                return new PublicIpResult { Error = "查询超时或已取消", Source = name };
            }
            catch (Exception ex)
            {
                errors.Add($"{name}：{ex.Message}");
            }
        }

        var message = errors.Count == 0
            ? "无网络连接或系统未开放出站访问"
            : string.Join("；", errors);

        return new PublicIpResult { Error = message + "（离线或代理环境下此功能不可用，属预期行为）" };
    }
}
