using System.Security.Cryptography;
using System.Text;

namespace ClassBox.Plugins.PasswordGen.Generating;

/// <summary>密码生成选项。</summary>
public sealed class PasswordOptions
{
    public int Length { get; set; } = 16;

    public bool UseUpper { get; set; } = true;

    public bool UseLower { get; set; } = true;

    public bool UseDigits { get; set; } = true;

    public bool UseSymbols { get; set; }

    public bool ExcludeAmbiguous { get; set; } = true;
}

/// <summary>密码强度评估结果。</summary>
public sealed class StrengthReport
{
    public string Password { get; init; } = string.Empty;

    public int EntropyBits { get; init; }

    public string Level { get; init; } = string.Empty;

    public string CrackTimeText { get; init; } = string.Empty;

    public string CharsetText { get; init; } = string.Empty;

    /// <summary>0~1 的强度比例，用于绘制强度条。</summary>
    public double Ratio { get; init; }
}

/// <summary>基于密码学安全随机数（RandomNumberGenerator）的密码生成与强度评估。</summary>
public static class PasswordEngine
{
    private const string UpperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string LowerChars = "abcdefghijklmnopqrstuvwxyz";
    private const string DigitChars = "0123456789";
    private const string SymbolChars = "!@#$%^&*()-_=+[]{}<>:;,.?/~";

    /// <summary>易混淆字符：0 O o 1 l I | ` ' " 等。</summary>
    private const string AmbiguousChars = "0Oo1lI|`'\"";

    /// <summary>离线暴力破解速率假设：每秒 100 亿次（GPU 集群级别）。</summary>
    private const double GuessesPerSecond = 1e10;

    public static string[] BuildPools(PasswordOptions options, out string? error)
    {
        var pools = new List<string>();

        if (options.UseUpper) pools.Add(Filter(UpperChars, options.ExcludeAmbiguous));
        if (options.UseLower) pools.Add(Filter(LowerChars, options.ExcludeAmbiguous));
        if (options.UseDigits) pools.Add(Filter(DigitChars, options.ExcludeAmbiguous));
        if (options.UseSymbols) pools.Add(Filter(SymbolChars, options.ExcludeAmbiguous));

        if (pools.Count == 0)
        {
            error = "请至少勾选一种字符类型。";
            return Array.Empty<string>();
        }

        if (pools.Any(p => p.Length == 0))
        {
            error = "排除易混淆字符后可用的字符集为空，请调整选项。";
            return Array.Empty<string>();
        }

        error = null;
        return pools.ToArray();
    }

    /// <summary>按选项生成一条密码，保证每种已选字符类型至少出现一次。</summary>
    public static string Generate(PasswordOptions options, out string? error)
    {
        var pools = BuildPools(options, out error);
        if (error is not null) return string.Empty;

        var length = Math.Clamp(options.Length, pools.Length, 128);
        var all = string.Concat(pools);
        var chars = new char[length];

        // 先保证每类字符至少出现一次
        for (var i = 0; i < pools.Length; i++)
        {
            chars[i] = pools[i][RandomNumberGenerator.GetInt32(pools[i].Length)];
        }

        for (var i = pools.Length; i < length; i++)
        {
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }

        // Fisher-Yates 洗牌，避免必选字符固定在开头
        for (var i = length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    public static List<string> GenerateBatch(PasswordOptions options, int count, out string? error)
    {
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var password = Generate(options, out error);
            if (error is not null) return result;
            result.Add(password);
        }

        error = null;
        return result;
    }

    /// <summary>评估密码强度：按出现的字符类别估算字符集大小，再计算熵与平均破解时间。</summary>
    public static StrengthReport Evaluate(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return new StrengthReport
            {
                Password = string.Empty,
                Level = "无",
                CharsetText = "—",
                CrackTimeText = "—",
                Ratio = 0
            };
        }

        var hasUpper = password.Any(c => UpperChars.Contains(c));
        var hasLower = password.Any(c => LowerChars.Contains(c));
        var hasDigit = password.Any(c => DigitChars.Contains(c));
        var hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));

        var charsetSize = 0;
        var categories = new List<string>();
        if (hasUpper) { charsetSize += UpperChars.Length; categories.Add("大写"); }
        if (hasLower) { charsetSize += LowerChars.Length; categories.Add("小写"); }
        if (hasDigit) { charsetSize += DigitChars.Length; categories.Add("数字"); }
        if (hasSymbol) { charsetSize += SymbolChars.Length; categories.Add("符号"); }

        // 密码含非 ASCII 字符时按更宽的字符集估算
        if (password.Any(c => c > 127)) charsetSize += 200;

        charsetSize = Math.Max(charsetSize, 1);
        var entropy = password.Length * Math.Log2(charsetSize);
        var averageGuesses = Math.Pow(2, entropy) / 2;
        var seconds = averageGuesses / GuessesPerSecond;

        var (level, ratio) = entropy switch
        {
            < 28 => ("极弱", 0.12),
            < 40 => ("弱", 0.3),
            < 60 => ("一般", 0.55),
            < 80 => ("强", 0.78),
            _ => ("极强", 1.0)
        };

        return new StrengthReport
        {
            Password = password,
            EntropyBits = (int)Math.Round(entropy),
            Level = level,
            CharsetText = $"{charsetSize} 种字符（{string.Join("+", categories)}）",
            CrackTimeText = FormatDuration(seconds),
            Ratio = ratio
        };
    }

    /// <summary>把秒数格式化为可读的中文时长。</summary>
    public static string FormatDuration(double seconds)
    {
        if (double.IsInfinity(seconds) || seconds > 31557600.0 * 1e6) return "远超百万年";
        if (seconds < 1) return "不足 1 秒";
        if (seconds < 60) return $"{seconds:0} 秒";
        if (seconds < 3600) return $"{seconds / 60:0.#} 分钟";
        if (seconds < 86400) return $"{seconds / 3600:0.#} 小时";
        if (seconds < 31557600) return $"{seconds / 86400:0.#} 天";
        if (seconds < 31557600.0 * 1000) return $"{seconds / 31557600:0.#} 年";
        if (seconds < 31557600.0 * 1e6) return $"{seconds / 31557600 / 1000:0.#} 千年";
        return "百万年以上";
    }

    public static StringBuilder Describe(PasswordOptions options, string? error)
    {
        var sb = new StringBuilder();
        if (error is not null)
        {
            sb.Append(error);
            return sb;
        }

        var pools = BuildPools(options, out _);
        sb.Append($"字符集 {string.Concat(pools).Length} 种 · 长度 {options.Length}");
        return sb;
    }

    private static string Filter(string source, bool excludeAmbiguous)
    {
        if (!excludeAmbiguous) return source;
        var sb = new StringBuilder(source.Length);
        foreach (var c in source)
        {
            if (!AmbiguousChars.Contains(c)) sb.Append(c);
        }

        return sb.ToString();
    }
}
