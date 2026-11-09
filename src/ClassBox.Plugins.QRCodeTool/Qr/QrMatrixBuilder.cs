namespace ClassBox.Plugins.QRCodeTool.Qr;

/// <summary>QR 矩阵布局：功能图形、数据放置、掩码与罚分择优。</summary>
internal static class QrMatrixBuilder
{
    public static QrCode Build(int version, QrEccLevel level, byte[] allCodewords)
    {
        var size = version * 4 + 17;
        var modules = new bool?[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(modules, isFunction, version, size);
        DrawCodewords(modules, isFunction, size, allCodewords);

        var bestMask = 0;
        var minPenalty = int.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, isFunction, size, mask);
            DrawFormatBits(modules, isFunction, size, level, mask);

            var penalty = GetPenalty(modules, size);
            if (penalty < minPenalty)
            {
                minPenalty = penalty;
                bestMask = mask;
            }

            ApplyMask(modules, isFunction, size, mask); // 通过再次异或还原
        }

        ApplyMask(modules, isFunction, size, bestMask);
        DrawFormatBits(modules, isFunction, size, level, bestMask);

        var matrix = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                matrix[x, y] = modules[x, y] ?? false;
            }
        }

        return new QrCode(version, level, bestMask, matrix);
    }

    // ===== 基础写入 =====

    private static void SetFunctionModule(bool?[,] modules, bool[,] isFunction, int x, int y, bool isDark)
    {
        modules[x, y] = isDark;
        isFunction[x, y] = true;
    }

    private static bool GetBit(int value, int index) => ((value >> index) & 1) != 0;

    // ===== 功能图形 =====

    private static void DrawFunctionPatterns(bool?[,] modules, bool[,] isFunction, int version, int size)
    {
        // 定时图形
        for (var i = 0; i < size; i++)
        {
            SetFunctionModule(modules, isFunction, 6, i, i % 2 == 0);
            SetFunctionModule(modules, isFunction, i, 6, i % 2 == 0);
        }

        // 三个定位图形（含分隔区）
        DrawFinderPattern(modules, isFunction, 3, 3);
        DrawFinderPattern(modules, isFunction, size - 4, 3);
        DrawFinderPattern(modules, isFunction, 3, size - 4);

        // 校正图形
        var positions = GetAlignmentPatternPositions(version, size);
        var last = positions.Length - 1;
        for (var i = 0; i < positions.Length; i++)
        {
            for (var j = 0; j < positions.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0)) continue;
                DrawAlignmentPattern(modules, isFunction, positions[i], positions[j]);
            }
        }

        // 先占位格式信息（掩码 0），后续按最优掩码重绘
        DrawFormatBits(modules, isFunction, size, QrEccLevel.L, 0);

        // 版本信息（版本 7 起）
        DrawVersionBits(modules, isFunction, version, size);
    }

    private static void DrawFinderPattern(bool?[,] modules, bool[,] isFunction, int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                var x = cx + dx;
                var y = cy + dy;
                if (x < 0 || y < 0 || x >= modules.GetLength(0) || y >= modules.GetLength(0)) continue;
                SetFunctionModule(modules, isFunction, x, y, dist != 2 && dist != 4);
            }
        }
    }

    private static void DrawAlignmentPattern(bool?[,] modules, bool[,] isFunction, int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x < 0 || y < 0 || x >= modules.GetLength(0) || y >= modules.GetLength(0)) continue;
                SetFunctionModule(modules, isFunction, x, y, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    private static int[] GetAlignmentPatternPositions(int version, int size)
    {
        if (version == 1) return Array.Empty<int>();

        var numAlign = version / 7 + 2;
        var step = (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;

        for (int i = numAlign - 1, pos = size - 7; i >= 1; i--, pos -= step)
        {
            result[i] = pos;
        }

        return result;
    }

    private static void DrawFormatBits(bool?[,] modules, bool[,] isFunction, int size, QrEccLevel level, int mask)
    {
        var eccBits = level switch
        {
            QrEccLevel.L => 1,
            QrEccLevel.M => 0,
            QrEccLevel.Q => 3,
            _ => 2
        };

        var data = (eccBits << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        var bits = ((data << 10) | rem) ^ 0x5412;

        // 第一份：环绕左上定位图形
        for (var i = 0; i <= 5; i++) SetFunctionModule(modules, isFunction, 8, i, GetBit(bits, i));
        SetFunctionModule(modules, isFunction, 8, 7, GetBit(bits, 6));
        SetFunctionModule(modules, isFunction, 8, 8, GetBit(bits, 7));
        SetFunctionModule(modules, isFunction, 7, 8, GetBit(bits, 8));
        for (var i = 9; i < 15; i++) SetFunctionModule(modules, isFunction, 14 - i, 8, GetBit(bits, i));

        // 第二份：右上 / 左下
        for (var i = 0; i < 8; i++) SetFunctionModule(modules, isFunction, size - 1 - i, 8, GetBit(bits, i));
        for (var i = 8; i < 15; i++) SetFunctionModule(modules, isFunction, 8, size - 15 + i, GetBit(bits, i));

        // 固定深色模块
        SetFunctionModule(modules, isFunction, 8, size - 8, true);
    }

    private static void DrawVersionBits(bool?[,] modules, bool[,] isFunction, int version, int size)
    {
        if (version < 7) return;

        var rem = version;
        for (var i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }

        var bits = (version << 12) | rem;

        for (var i = 0; i < 18; i++)
        {
            var bit = GetBit(bits, i);
            var a = size - 11 + i % 3;
            var b = i / 3;
            SetFunctionModule(modules, isFunction, a, b, bit);
            SetFunctionModule(modules, isFunction, b, a, bit);
        }
    }

    // ===== 数据放置 =====

    private static void DrawCodewords(bool?[,] modules, bool[,] isFunction, int size, byte[] data)
    {
        var i = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5; // 跳过竖向定时图形

            for (var vert = 0; vert < size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vert : vert;

                    if (isFunction[x, y] || i >= data.Length * 8) continue;

                    modules[x, y] = GetBit(data[i >> 3], 7 - (i & 7));
                    i++;
                }
            }
        }
    }

    // ===== 掩码 =====

    private static void ApplyMask(bool?[,] modules, bool[,] isFunction, int size, int mask)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (isFunction[x, y]) continue;

                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0
                };

                if (invert) modules[x, y] = !(modules[x, y] ?? false);
            }
        }
    }

    // ===== 罚分 =====

    private static int GetPenalty(bool?[,] modules, int size)
    {
        var penalty = 0;

        // 规则 1：同色连续 5 个以上
        for (var y = 0; y < size; y++)
        {
            penalty += RowRunPenalty(modules, size, y, true);
        }

        for (var x = 0; x < size; x++)
        {
            penalty += RowRunPenalty(modules, size, x, false);
        }

        // 规则 2：2×2 同色块
        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var color = modules[x, y] ?? false;
                if (color == (modules[x + 1, y] ?? false)
                    && color == (modules[x, y + 1] ?? false)
                    && color == (modules[x + 1, y + 1] ?? false))
                {
                    penalty += 3;
                }
            }
        }

        // 规则 3：类似定位图形的 1:1:3:1:1 序列
        penalty += FinderLikePenalty(modules, size);

        // 规则 4：深色模块占比偏离 50%
        var dark = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (modules[x, y] ?? false) dark++;
            }
        }

        var total = size * size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        penalty += k * 10;

        return penalty;
    }

    private static int RowRunPenalty(bool?[,] modules, int size, int index, bool horizontal)
    {
        var penalty = 0;
        var runLength = 1;
        var color = horizontal ? (modules[0, index] ?? false) : (modules[index, 0] ?? false);

        for (var i = 1; i < size; i++)
        {
            var current = horizontal ? (modules[i, index] ?? false) : (modules[index, i] ?? false);
            if (current == color)
            {
                runLength++;
                if (runLength == 5) penalty += 3;
                else if (runLength > 5) penalty++;
            }
            else
            {
                color = current;
                runLength = 1;
            }
        }

        return penalty;
    }

    private static int FinderLikePenalty(bool?[,] modules, int size)
    {
        // 11 位窗口：1011101 0000 或 0000 1011101
        var penalty = 0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x + 11 <= size; x++)
            {
                if (MatchesFinderWindow(modules, size, x, y, true)) penalty += 40;
            }
        }

        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y + 11 <= size; y++)
            {
                if (MatchesFinderWindow(modules, size, x, y, false)) penalty += 40;
            }
        }

        return penalty;
    }

    private static bool MatchesFinderWindow(bool?[,] modules, int size, int x, int y, bool horizontal)
    {
        ReadOnlySpan<int> first = stackalloc int[] { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
        ReadOnlySpan<int> second = stackalloc int[] { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };

        var matchFirst = true;
        var matchSecond = true;

        for (var i = 0; i < 11; i++)
        {
            var value = horizontal ? (modules[x + i, y] ?? false) : (modules[x, y + i] ?? false);
            var bit = value ? 1 : 0;
            if (matchFirst && bit != first[i]) matchFirst = false;
            if (matchSecond && bit != second[i]) matchSecond = false;
            if (!matchFirst && !matchSecond) return false;
        }

        return matchFirst || matchSecond;
    }
}
