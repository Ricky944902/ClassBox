namespace ClassBox.Plugins.PortScanner.Scanning;

/// <summary>单个端口的扫描结果。</summary>
public sealed class PortScanResult
{
    public int Port { get; init; }

    public string Service { get; init; } = "-";

    public long ElapsedMs { get; init; }

    public bool IsOpen { get; init; }

    public string StateText => IsOpen ? "开放" : "关闭/过滤";

    public string ElapsedText => $"{ElapsedMs} ms";

    /// <summary>导出为纯文本行。</summary>
    public string ToLine() => $"{Port}\t{StateText}\t{Service}\t{ElapsedMs} ms";
}
