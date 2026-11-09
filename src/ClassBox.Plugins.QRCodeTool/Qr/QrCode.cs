namespace ClassBox.Plugins.QRCodeTool.Qr;

/// <summary>纠错等级。</summary>
public enum QrEccLevel
{
    L = 0,
    M = 1,
    Q = 2,
    H = 3
}

/// <summary>生成后的二维码矩阵（true 表示深色模块）。</summary>
public sealed class QrCode
{
    private readonly bool[,] _modules;

    internal QrCode(int version, QrEccLevel level, int mask, bool[,] modules)
    {
        Version = version;
        EccLevel = level;
        Mask = mask;
        _modules = modules;
        Size = version * 4 + 17;
    }

    public int Version { get; }

    public int Size { get; }

    public QrEccLevel EccLevel { get; }

    public int Mask { get; }

    public bool GetModule(int x, int y) => _modules[x, y];

    public bool[,] ToArray() => _modules;

    public string Describe()
    {
        var level = EccLevel switch
        {
            QrEccLevel.L => "L(7%)",
            QrEccLevel.M => "M(15%)",
            QrEccLevel.Q => "Q(25%)",
            _ => "H(30%)"
        };

        return $"版本 {Version} · {Size}×{Size} 模块 · 纠错 {level} · 掩码 {Mask}";
    }
}
