using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ClassBox.Plugins.LocalServer.Server;

/// <summary>一条 HTTP 请求日志。</summary>
public sealed class HttpLogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Client { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long Bytes { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");
    public string SizeText => Bytes > 0 ? FormatSize(Bytes) : "-";

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// 基于 TcpListener 手写的最小 HTTP/1.1 文件服务器：
/// 目录浏览、文件下载（含 Range 断点续传）、MIME 推断、中文文件名 URL 解码、目录穿越防护。
/// 绑定 IPAddress.Any，无需管理员权限。
/// </summary>
public sealed class HttpFileServer : IDisposable
{
    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".rar"] = "application/vnd.rar",
        [".7z"] = "application/x-7z-compressed",
        [".gz"] = "application/gzip",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".mp4"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
        [".exe"] = "application/octet-stream",
        [".dll"] = "application/octet-stream",
        [".apk"] = "application/vnd.android.package-archive",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private SemaphoreSlim? _gate;
    private int _activeConnections;
    private int _totalRequests;

    public string RootDirectory { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public int MaxConnections { get; private set; } = 8;
    public bool IsRunning => _listener is not null;
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public int TotalRequests => Volatile.Read(ref _totalRequests);

    public event Action<HttpLogEntry>? RequestLogged;
    public event Action<string>? ErrorOccurred;

    /// <summary>启动服务；port 传 0 时由系统自动分配可用端口。</summary>
    public int Start(string rootDirectory, int port, int maxConnections)
    {
        if (IsRunning) throw new InvalidOperationException("服务已在运行");

        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"共享目录不存在：{root}");

        MaxConnections = Math.Clamp(maxConnections, 1, 64);
        RootDirectory = root.TrimEnd(Path.DirectorySeparatorChar);
        _gate = new SemaphoreSlim(MaxConnections, MaxConnections);
        _cts = new CancellationTokenSource();

        _listener = new TcpListener(IPAddress.Any, port <= 0 ? 0 : port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Port;
    }

    public void Stop()
    {
        var listener = _listener;
        _listener = null;
        try { _cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { _gate?.Dispose(); } catch { }
        _gate = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    public void Dispose() => Stop();

    // ===================== 连接处理 =====================

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                var listener = _listener;
                if (listener is null) break;
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"接受连接失败：{ex.Message}");
                try { await Task.Delay(200, CancellationToken.None); } catch { }
                continue;
            }

            var gate = _gate;
            if (gate is null)
            {
                client.Dispose();
                break;
            }

            try
            {
                await gate.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, token));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        Interlocked.Increment(ref _activeConnections);
        var clientIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "未知";
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 20000;
                var stream = client.GetStream();

                var header = await ReadHeaderAsync(stream, token);
                if (string.IsNullOrEmpty(header)) return;

                var lines = header.Split("\r\n", StringSplitOptions.None);
                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    await WriteSimpleAsync(stream, "400 Bad Request", "请求格式错误");
                    Report(clientIp, "?", "/", "400", 0);
                    return;
                }

                var method = requestLine[0].ToUpperInvariant();
                var target = requestLine[1];

                string? rangeHeader = null;
                foreach (var line in lines)
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    if (line[..idx].Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
                    {
                        rangeHeader = line[(idx + 1)..].Trim();
                    }
                }

                await ServeAsync(stream, method, target, rangeHeader, clientIp, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"处理 {clientIp} 请求失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
            try { _gate?.Release(); } catch { }
        }
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), token);
            if (read <= 0) return total > 0 ? Encoding.UTF8.GetString(buffer, 0, total) : null;
            total += read;
            var text = Encoding.UTF8.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n")) return text;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    // ===================== 路由 =====================

    private async Task ServeAsync(NetworkStream stream, string method, string target, string? rangeHeader,
        string clientIp, CancellationToken token)
    {
        if (method != "GET" && method != "HEAD")
        {
            await WriteSimpleAsync(stream, "405 Method Not Allowed", "仅支持 GET / HEAD 请求");
            Report(clientIp, method, target, "405", 0);
            return;
        }

        var queryIndex = target.IndexOf('?');
        var rawPath = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = queryIndex >= 0 ? target[(queryIndex + 1)..] : string.Empty;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(rawPath);
        }
        catch
        {
            await WriteSimpleAsync(stream, "400 Bad Request", "路径编码无法解析");
            Report(clientIp, method, rawPath, "400", 0);
            return;
        }

        if (decoded.Contains('\0'))
        {
            await WriteSimpleAsync(stream, "400 Bad Request", "非法路径");
            Report(clientIp, method, decoded, "400", 0);
            return;
        }

        var relative = decoded.Replace('\\', '/').TrimStart('/');
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            await WriteSimpleAsync(stream, "400 Bad Request", "非法路径");
            Report(clientIp, method, decoded, "400", 0);
            return;
        }

        var rootWithSeparator = RootDirectory + Path.DirectorySeparatorChar;
        var inRoot = fullPath.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase) ||
                     fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        if (!inRoot)
        {
            await WriteSimpleAsync(stream, "403 Forbidden", "禁止访问共享目录之外的路径");
            Report(clientIp, method, decoded, "403", 0);
            return;
        }

        if (Directory.Exists(fullPath))
        {
            if (!decoded.EndsWith('/'))
            {
                var location = decoded + "/" + (query.Length > 0 ? "?" + query : string.Empty);
                await WriteResponseHeadersAsync(stream, "301 Moved Permanently",
                    "text/plain; charset=utf-8", 0, "Location: " + location + "\r\n");
                Report(clientIp, method, decoded, "301", 0);
                return;
            }

            var html = BuildDirectoryPage(fullPath, decoded);
            var bytes = Encoding.UTF8.GetBytes(html);
            await WriteResponseHeadersAsync(stream, "200 OK", "text/html; charset=utf-8", bytes.Length);
            if (method != "HEAD")
            {
                await stream.WriteAsync(bytes);
            }
            Report(clientIp, method, decoded, "200", bytes.Length);
            return;
        }

        if (File.Exists(fullPath))
        {
            await ServeFileAsync(stream, method, fullPath, decoded, query, rangeHeader, clientIp, token);
            return;
        }

        await WriteSimpleAsync(stream, "404 Not Found", "文件或目录不存在");
        Report(clientIp, method, decoded, "404", 0);
    }

    private async Task ServeFileAsync(NetworkStream stream, string method, string filePath, string requestPath,
        string query, string? rangeHeader, string clientIp, CancellationToken token)
    {
        var info = new FileInfo(filePath);
        var mime = MimeMap.TryGetValue(info.Extension, out var mapped) ? mapped : "application/octet-stream";

        long start = 0;
        var length = info.Length;
        var partial = false;

        if (!string.IsNullOrWhiteSpace(rangeHeader) &&
            rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader[6..].Split(',')[0].Trim();
            var dash = spec.Split('-');
            if (dash.Length == 2)
            {
                if (long.TryParse(dash[0], out var s)) start = s;
                if (start < 0 || start >= info.Length)
                {
                    await WriteResponseHeadersAsync(stream, "416 Range Not Satisfiable",
                        "text/plain; charset=utf-8", 0, $"Content-Range: bytes */{info.Length}\r\n");
                    Report(clientIp, method, requestPath, "416", 0);
                    return;
                }

                length = long.TryParse(dash[1], out var e) && e >= start
                    ? Math.Min(e - start + 1, info.Length - start)
                    : info.Length - start;
                partial = true;
            }
        }

        var extra = new StringBuilder();
        if (partial)
        {
            extra.Append($"Content-Range: bytes {start}-{start + length - 1}/{info.Length}\r\n");
        }
        extra.Append("Accept-Ranges: bytes\r\n");

        if (query.Contains("download=1", StringComparison.OrdinalIgnoreCase))
        {
            var encodedName = Uri.EscapeDataString(info.Name);
            extra.Append($"Content-Disposition: attachment; filename*=UTF-8''{encodedName}\r\n");
        }

        await WriteResponseHeadersAsync(stream, partial ? "206 Partial Content" : "200 OK",
            mime, length, extra.ToString());

        if (method == "HEAD") return;

        long sent = 0;
        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fileStream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await fileStream.ReadAsync(buffer.AsMemory(0, want), token);
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), token);
                remaining -= read;
                sent += read;
            }
        }

        Report(clientIp, method, requestPath, partial ? "206" : "200", sent);
    }

    // ===================== 目录页 =====================

    private string BuildDirectoryPage(string directory, string requestPath)
    {
        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<title>").Append(WebUtility.HtmlEncode(requestPath)).Append("</title>");
        builder.Append("<style>");
        builder.Append("body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;margin:0;padding:28px;background:#f6f7f9;color:#1f2328;}");
        builder.Append("h1{font-size:18px;margin:0 0 4px;}p.sub{color:#6b7280;font-size:13px;margin:0 0 20px;}");
        builder.Append("table{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,.08);}");
        builder.Append("th,td{padding:10px 14px;text-align:left;font-size:13px;border-bottom:1px solid #eceef1;}");
        builder.Append("th{background:#fafbfc;color:#6b7280;font-weight:600;font-size:12px;}");
        builder.Append("tr:last-child td{border-bottom:none;}a{color:#2563eb;text-decoration:none;}a:hover{text-decoration:underline;}");
        builder.Append("td.size{width:120px;color:#6b7280;}td.type{width:90px;color:#9ca3af;font-size:12px;}");
        builder.Append("</style></head><body>");
        builder.Append("<h1>").Append(WebUtility.HtmlEncode(requestPath)).Append("</h1>");
        builder.Append("<p class=\"sub\">Class Box 本地文件服务，共享根目录 ")
               .Append(WebUtility.HtmlEncode(RootDirectory)).Append("</p>");
        builder.Append("<table><thead><tr><th>名称</th><th>大小</th><th>类型</th></tr></thead><tbody>");

        if (!string.IsNullOrEmpty(requestPath) && requestPath != "/")
        {
            builder.Append("<tr><td><a href=\"../\">.. 返回上级</a></td><td class=\"size\">-</td><td class=\"type\">目录</td></tr>");
        }

        const int maxEntries = 2000;
        var count = 0;
        var truncated = false;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(directory)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (count++ >= maxEntries) { truncated = true; break; }
                var name = Path.GetFileName(dir);
                builder.Append("<tr><td><a href=\"").Append(Uri.EscapeDataString(name)).Append("/\">")
                       .Append(WebUtility.HtmlEncode(name)).Append("/</a></td>")
                       .Append("<td class=\"size\">-</td><td class=\"type\">目录</td></tr>");
            }

            if (!truncated)
            {
                foreach (var file in Directory.EnumerateFiles(directory)
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    if (count++ >= maxEntries) { truncated = true; break; }
                    var name = Path.GetFileName(file);
                    long size;
                    try { size = new FileInfo(file).Length; }
                    catch { size = 0; }
                    var ext = Path.GetExtension(file).TrimStart('.');
                    builder.Append("<tr><td><a href=\"").Append(Uri.EscapeDataString(name)).Append("\">")
                           .Append(WebUtility.HtmlEncode(name)).Append("</a> ")
                           .Append("<a href=\"").Append(Uri.EscapeDataString(name))
                           .Append("?download=1\" style=\"font-size:12px;color:#9ca3af;\">下载</a></td>")
                           .Append("<td class=\"size\">").Append(HttpLogEntry.FormatSize(size)).Append("</td>")
                           .Append("<td class=\"type\">")
                           .Append(WebUtility.HtmlEncode(string.IsNullOrEmpty(ext) ? "文件" : ext.ToUpperInvariant()))
                           .Append("</td></tr>");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            builder.Append("<tr><td colspan=\"3\">（无权限读取该目录全部内容）</td></tr>");
        }
        catch (Exception ex)
        {
            builder.Append("<tr><td colspan=\"3\">（读取目录失败：")
                   .Append(WebUtility.HtmlEncode(ex.Message)).Append("）</td></tr>");
        }

        builder.Append("</tbody></table>");
        if (truncated)
        {
            builder.Append("<p class=\"sub\" style=\"margin-top:14px;\">条目过多，仅显示前 ")
                   .Append(maxEntries).Append(" 项。</p>");
        }
        builder.Append("</body></html>");
        return builder.ToString();
    }

    // ===================== 响应输出 =====================

    private static async Task WriteResponseHeadersAsync(NetworkStream stream, string status, string contentType,
        long contentLength, string? extraHeaders = null)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        builder.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        builder.Append("Server: ClassBox-LocalServer/1.0\r\n");
        builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
        builder.Append("Content-Length: ").Append(contentLength).Append("\r\n");
        builder.Append("Connection: close\r\n");
        if (!string.IsNullOrEmpty(extraHeaders)) builder.Append(extraHeaders);
        builder.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(builder.ToString());
        await stream.WriteAsync(headerBytes);
    }

    private static async Task WriteSimpleAsync(NetworkStream stream, string status, string message)
    {
        var body = Encoding.UTF8.GetBytes(
            "<html><head><meta charset=\"utf-8\"><title>" + status + "</title></head>" +
            "<body style=\"font-family:'Microsoft YaHei',sans-serif;padding:32px;\"><h2>" + status +
            "</h2><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
        await WriteResponseHeadersAsync(stream, status, "text/html; charset=utf-8", body.Length);
        await stream.WriteAsync(body);
    }

    private void Report(string client, string method, string path, string status, long bytes)
    {
        Interlocked.Increment(ref _totalRequests);
        try
        {
            RequestLogged?.Invoke(new HttpLogEntry
            {
                Client = client,
                Method = method,
                Path = path,
                Status = status,
                Bytes = bytes
            });
        }
        catch
        {
            // 订阅方异常不影响服务
        }
    }
}

/// <summary>本机局域网 IPv4 地址枚举与访问地址拼接。</summary>
public static class LocalAddresses
{
    public static IReadOnlyList<string> GetLanIPv4()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = address.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    if (!result.Contains(ip)) result.Add(ip);
                }
            }
        }
        catch
        {
            // 枚举失败时退回已收集结果
        }

        if (result.Count == 0) result.Add("127.0.0.1");
        return result;
    }

    public static IReadOnlyList<string> BuildAccessUrls(int port)
    {
        var urls = new List<string>();
        foreach (var ip in GetLanIPv4())
        {
            urls.Add($"http://{ip}:{port}/");
        }
        if (!urls.Contains($"http://127.0.0.1:{port}/"))
        {
            urls.Add($"http://127.0.0.1:{port}/");
        }
        return urls;
    }
}
