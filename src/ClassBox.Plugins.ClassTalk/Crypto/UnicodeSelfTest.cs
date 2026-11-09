using System.IO;
using System.Text;
using ClassBox.Plugins.ClassTalk.Net;

namespace ClassBox.Plugins.ClassTalk.Crypto;

/// <summary>
/// Unicode 全链路自检：UTF-8 编码 → 协议帧 → LRNC 加解密 → 还原，
/// 覆盖中/英/阿拉伯/韩/泰/印地/希伯来/emoji/组合字符，验证任意 Unicode 文本不产生乱码。
/// </summary>
public static class UnicodeSelfTest
{
    public static readonly string[] Samples =
    {
        "Hello, ClassTalk! 0123456789",
        "你好，世界！局域网加密交流测试。",
        "مرحبا بالعالم! هذا اختبار للغة العربية.",
        "السلام عليكم ورحمة الله وبركاته",
        "안녕하세요. 반갑습니다.",
        "สวัสดีครับ ภาษาไทย",
        "नमस्ते, यह हिन्दी परीक्षण है।",
        "שלום עולם — בדיקת עברית",
        "混合 Mixed 多语言: 中文 123 العربية 한국어 🎉🚀",
        "😀😃😄😁😆😅😂🤣🥰🤩",
        "e\u0301 a\u0308 n\u0303 组合字符",
        "RTL\u202Bعربي\u202C嵌入",
        new string('测', 500),
        new string('م', 500)
    };

    public static string RunAll()
    {
        int checks = 0;

        // 1) UTF-8 编解码往返
        foreach (var s in Samples)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            string back = Encoding.UTF8.GetString(bytes);
            if (!string.Equals(back, s, StringComparison.Ordinal))
                throw new Exception($"UTF-8 往返不一致：{Head(s)}");
            if (Encoding.UTF8.GetByteCount(s) != bytes.Length)
                throw new Exception($"UTF-8 字节数不一致：{Head(s)}");
            checks++;
        }

        // 2) LRNC 加解密往返（同一口令两端会话）
        var master = new byte[32];
        for (int i = 0; i < master.Length; i++) master[i] = (byte)(i * 7 + 1);
        var encSession = new Lrnc1024Session(master);
        var decSession = new Lrnc1024Session(master);
        foreach (var s in Samples)
        {
            string back = decSession.DecryptString(encSession.Encrypt(s));
            if (!string.Equals(back, s, StringComparison.Ordinal))
                throw new Exception($"加解密往返不一致：{Head(s)}");
            checks++;
        }

        // 3) PLAIN 明文帧往返（UTF-8 帧头 + 按字节长度精确截取）
        foreach (var s in Samples)
        {
            foreach (var nick in new[] { "Alice", "用户名", "أحمد", "混合_🚀" })
            {
                byte[] frame = ChatProtocol.FramePlainMessage(nick, s);
                using var ms = new MemoryStream(frame, writable: false);
                var read = ChatProtocol.ReadFrameAsync(ms, CancellationToken.None).GetAwaiter().GetResult();
                if (read == null || read.Kind != "PLAIN")
                    throw new Exception("PLAIN 帧读取失败");
                if (!string.Equals(Encoding.UTF8.GetString(read.Payload), s, StringComparison.Ordinal))
                    throw new Exception($"PLAIN 帧正文不一致：{Head(s)}");
                if (!string.Equals(read.Nick, nick, StringComparison.Ordinal))
                    throw new Exception($"PLAIN 帧昵称不一致：{read.Nick}");
                checks++;
            }
        }

        // 4) MSG 密文帧往返（密文二进制不得被当文本破坏）
        foreach (var s in Samples)
        {
            byte[] cipher = encSession.Encrypt(s);
            byte[] frame = ChatProtocol.FrameMessage("发送者", cipher);
            using var ms = new MemoryStream(frame, writable: false);
            var read = ChatProtocol.ReadFrameAsync(ms, CancellationToken.None).GetAwaiter().GetResult();
            if (read == null || read.Kind != "MSG")
                throw new Exception("MSG 帧读取失败");
            if (!string.Equals(decSession.DecryptString(read.Payload), s, StringComparison.Ordinal))
                throw new Exception($"MSG 帧往返不一致：{Head(s)}");
            checks++;
        }

        // 5) 昵称净化：含分隔符/换行不得破坏帧头导致错位乱码
        {
            string dirty = "a|b\nc\r\n态";
            string clean = ChatProtocol.SanitizeNick(dirty);
            if (clean.Contains('|') || clean.Contains('\n') || clean.Contains('\r'))
                throw new Exception("昵称净化失败");
            byte[] frame = ChatProtocol.FramePlainMessage(dirty, "正文");
            using var ms = new MemoryStream(frame, writable: false);
            var read = ChatProtocol.ReadFrameAsync(ms, CancellationToken.None).GetAwaiter().GetResult();
            if (read == null || Encoding.UTF8.GetString(read.Payload) != "正文")
                throw new Exception("含非法字符昵称导致帧解析错位");
            checks++;
        }

        // 6) 连续帧流切分：多帧不间断写入，验证按 LEN 精确切分、不吞多字节字符
        {
            using var ms = new MemoryStream();
            foreach (var s in Samples)
            {
                byte[] f = ChatProtocol.FramePlainMessage("流", s);
                ms.Write(f, 0, f.Length);
            }
            ms.Position = 0;
            foreach (var s in Samples)
            {
                var read = ChatProtocol.ReadFrameAsync(ms, CancellationToken.None).GetAwaiter().GetResult();
                if (read == null || !string.Equals(Encoding.UTF8.GetString(read.Payload), s, StringComparison.Ordinal))
                    throw new Exception($"连续帧流解析错位：{Head(s)}");
                checks++;
            }
            if (ChatProtocol.ReadFrameAsync(ms, CancellationToken.None).GetAwaiter().GetResult() != null)
                throw new Exception("帧流末尾存在残留数据");
        }

        return $"PASS（{Samples.Length} 组多语言样本 / {checks} 项断言：UTF-8 编码 + 加密往返 + 明文帧 + 密文帧 + 帧流切分）";
    }

    private static string Head(string s)
        => s.Substring(0, Math.Min(10, s.Length));

    /// <summary>界面实际使用的字体回退链，逐字符确认每个 Unicode 字符都有可渲染字形（不出现豆腐块）。</summary>
    public static readonly string[] FontChain =
    {
        "Microsoft YaHei UI", "Segoe UI", "Tahoma", "Arial",
        "Nirmala UI", "Malgun Gothic", "Leelawadee UI", "Segoe UI Emoji"
    };

    public static string RunFontCoverage()
    {
        var maps = new List<(string Name, IDictionary<int, ushort> Map)>();
        foreach (var name in FontChain)
        {
            var tf = new System.Windows.Media.Typeface(
                new System.Windows.Media.FontFamily(name),
                System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            if (tf.TryGetGlyphTypeface(out var gt) && gt.CharacterToGlyphMap != null)
                maps.Add((name, gt.CharacterToGlyphMap));
        }
        if (maps.Count == 0) throw new Exception("无法加载任何回退字体");

        var missing = new List<string>();
        int checkedChars = 0;
        foreach (var s in Samples)
        {
            foreach (var rune in s.EnumerateRunes())
            {
                int cp = rune.Value;
                // 跳过空白、组合附加符与不可见的格式控制符（Bidi/变体选择符/零宽连接符）
                if (System.Text.Rune.IsWhiteSpace(rune)) continue;
                if (cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x202A || cp == 0x202B ||
                    cp == 0x202C || cp == 0x200E || cp == 0x200F) continue;
                if (cp >= 0x0300 && cp <= 0x036F) continue;
                checkedChars++;
                if (!maps.Any(m => m.Map.ContainsKey(cp)))
                    missing.Add($"U+{cp:X4}");
            }
        }
        if (missing.Count > 0)
            throw new Exception($"以下码位无可用字形（会显示为方块）：{string.Join(", ", missing.Distinct())}");

        return $"PASS（{checkedChars} 个字符全部命中字形 / 已加载 {maps.Count} 个回退字体：{string.Join("、", maps.Select(m => m.Name))}）";
    }
}
