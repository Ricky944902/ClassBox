namespace ClassBox.Plugins.QRCodeTool.Qr;

/// <summary>
/// 手写 QR Code（Model 2）编码器：字节模式 + 自动版本（1~10）+ Reed-Solomon 纠错 + 8 种掩码择优。
/// 不依赖任何第三方库。
/// </summary>
public static class QrEncoder
{
    /// <summary>支持的最高版本（10 = 57×57 模块，字节模式 M 级可容纳 213 字节）。</summary>
    public const int MaxVersion = 10;

    // 每块纠错码字数：[等级, 版本]，版本 1~10 有效。
    private static readonly int[,] EccCodewordsPerBlock =
    {
        { 0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18 },   // L
        { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 },   // M
        { 0, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24 },   // Q
        { 0, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28 }    // H
    };

    // 纠错块数量：[等级, 版本]。
    private static readonly int[,] NumEccBlocks =
    {
        { 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4 },             // L
        { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 },             // M
        { 0, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8 },             // Q
        { 0, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8 }              // H
    };

    /// <summary>编码文本为二维码矩阵；失败时返回 false 并给出原因。</summary>
    public static bool TryEncode(string text, QrEccLevel level, out QrCode? code, out string error)
    {
        code = null;
        error = string.Empty;

        try
        {
            if (string.IsNullOrEmpty(text))
            {
                error = "内容为空，请输入要编码的文本。";
                return false;
            }

            var data = Encoding.UTF8.GetBytes(text);

            var version = -1;
            for (var v = 1; v <= MaxVersion; v++)
            {
                var capacityBits = GetNumDataCodewords(v, level) * 8;
                var neededBits = 4 + CharCountBits(v) + data.Length * 8;
                if (data.Length < (1 << CharCountBits(v)) && neededBits <= capacityBits)
                {
                    version = v;
                    break;
                }
            }

            if (version < 0)
            {
                var maxBytes = GetMaxDataBytes(level);
                error = $"内容过长（{data.Length} 字节 UTF-8），当前最多支持 {maxBytes} 字节，请缩短内容或降低纠错等级。";
                return false;
            }

            var dataCodewords = BuildDataCodewords(data, version, level);
            var allCodewords = AddEccAndInterleave(dataCodewords, version, level);

            code = QrMatrixBuilder.Build(version, level, allCodewords);
            return true;
        }
        catch (Exception ex)
        {
            error = "生成二维码失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>指定纠错等级下可容纳的最大 UTF-8 字节数。</summary>
    public static int GetMaxDataBytes(QrEccLevel level)
    {
        var capacityBits = GetNumDataCodewords(MaxVersion, level) * 8;
        return (capacityBits - 4 - CharCountBits(MaxVersion)) / 8;
    }

    private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

    private static int GetNumRawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7) result -= 36;
        }

        return result;
    }

    private static int GetNumDataCodewords(int version, QrEccLevel level)
        => GetNumRawDataModules(version) / 8
           - EccCodewordsPerBlock[(int)level, version] * NumEccBlocks[(int)level, version];

    // ===== 数据码字 =====

    private static byte[] BuildDataCodewords(byte[] data, int version, QrEccLevel level)
    {
        var bits = new List<bool>(GetNumDataCodewords(version, level) * 8 + 32);

        AppendBits(bits, 0b0100, 4);                       // 字节模式
        AppendBits(bits, data.Length, CharCountBits(version));
        foreach (var b in data) AppendBits(bits, b, 8);

        var capacityBits = GetNumDataCodewords(version, level) * 8;

        // 终止符（最多 4 bit）
        for (var i = 0; i < 4 && bits.Count < capacityBits; i++) bits.Add(false);

        // 补齐到字节边界
        while (bits.Count % 8 != 0 && bits.Count < capacityBits) bits.Add(false);

        // 填充码字 0xEC / 0x11 交替
        var pad = 0xEC;
        while (bits.Count < capacityBits)
        {
            AppendBits(bits, pad, 8);
            pad ^= 0xEC ^ 0x11;
        }

        var result = new byte[capacityBits / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (!bits[i]) continue;
            result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        }

        return result;
    }

    private static void AppendBits(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    // ===== 纠错与交织 =====

    private static byte[] AddEccAndInterleave(byte[] data, int version, QrEccLevel level)
    {
        var numBlocks = NumEccBlocks[(int)level, version];
        var blockEccLen = EccCodewordsPerBlock[(int)level, version];
        var rawCodewords = GetNumRawDataModules(version) / 8;
        var numShortBlocks = numBlocks - rawCodewords % numBlocks;
        var shortBlockLen = rawCodewords / numBlocks;

        var divisor = ReedSolomonComputeDivisor(blockEccLen);
        var blocksData = new List<byte[]>(numBlocks);
        var blocksEcc = new List<byte[]>(numBlocks);

        var offset = 0;
        for (var i = 0; i < numBlocks; i++)
        {
            var dataLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var blockData = new byte[dataLen];
            Array.Copy(data, offset, blockData, 0, dataLen);
            offset += dataLen;

            blocksData.Add(blockData);
            blocksEcc.Add(ReedSolomonComputeRemainder(blockData, divisor));
        }

        var result = new byte[rawCodewords];
        var index = 0;

        var maxDataLen = blocksData.Max(b => b.Length);
        for (var i = 0; i < maxDataLen; i++)
        {
            foreach (var block in blocksData)
            {
                if (i < block.Length) result[index++] = block[i];
            }
        }

        for (var i = 0; i < blockEccLen; i++)
        {
            foreach (var block in blocksEcc)
            {
                result[index++] = block[i];
            }
        }

        return result;
    }

    private static byte[] ReedSolomonComputeDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = (byte)1;

        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = GfMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }

            root = GfMultiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] ^= GfMultiply(divisor[i], factor);
            }
        }

        return result;
    }

    private static byte GfMultiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }
}
