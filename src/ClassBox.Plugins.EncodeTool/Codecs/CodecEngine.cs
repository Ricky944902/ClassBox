using System.Globalization;
using System.Text;

namespace ClassBox.Plugins.EncodeTool.Codecs;

/// <summary>支持的编码类型。</summary>
public enum CodecKind
{
    Base64,
    Base64Url,
    Url,
    HtmlEntity,
    UnicodeEscape,
    Hex
}

/// <summary>编码/解码引擎：Base64、URL、HTML 实体、Unicode 转义与 Hex，全部基于 BCL 实现。</summary>
public static class CodecEngine
{
    public static readonly string[] KindNames =
    {
        "Base64",
        "Base64（URL 安全）",
        "URL 编码",
        "HTML 实体",
        "Unicode 转义",
        "Hex (UTF-8)"
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding LooseUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static CodecKind FromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return CodecKind.Base64;
        if (name.Contains("URL 安全", StringComparison.Ordinal)) return CodecKind.Base64Url;
        if (name.StartsWith("Base64", StringComparison.Ordinal)) return CodecKind.Base64;
        if (name.StartsWith("URL", StringComparison.Ordinal)) return CodecKind.Url;
        if (name.StartsWith("HTML", StringComparison.Ordinal)) return CodecKind.HtmlEntity;
        if (name.StartsWith("Unicode", StringComparison.Ordinal)) return CodecKind.UnicodeEscape;
        if (name.StartsWith("Hex", StringComparison.Ordinal)) return CodecKind.Hex;
        return CodecKind.Base64;
    }

    // ===================== 编码 =====================

    public static string Encode(string text, CodecKind kind) => EncodeBytes(LooseUtf8.GetBytes(text), kind);

    public static string EncodeBytes(byte[] bytes, CodecKind kind)
    {
        switch (kind)
        {
            case CodecKind.Base64:
                return Convert.ToBase64String(bytes);

            case CodecKind.Base64Url:
                return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            case CodecKind.Hex:
                return Convert.ToHexString(bytes);

            default:
            {
                var text = LooseUtf8.GetString(bytes);
                return EncodeText(text, kind);
            }
        }
    }

    private static string EncodeText(string text, CodecKind kind)
    {
        switch (kind)
        {
            case CodecKind.Url:
                return Uri.EscapeDataString(text);

            case CodecKind.HtmlEntity:
            {
                var sb = new StringBuilder(text.Length + 16);
                foreach (var c in text)
                {
                    switch (c)
                    {
                        case '&': sb.Append("&amp;"); break;
                        case '<': sb.Append("&lt;"); break;
                        case '>': sb.Append("&gt;"); break;
                        case '"': sb.Append("&quot;"); break;
                        case '\'': sb.Append("&#39;"); break;
                        default:
                            if (c > 0x7E || c < 0x20)
                            {
                                if (c == '\n' || c == '\r' || c == '\t') sb.Append(c);
                                else sb.Append("&#").Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(';');
                            }
                            else
                            {
                                sb.Append(c);
                            }

                            break;
                    }
                }

                return sb.ToString();
            }

            case CodecKind.UnicodeEscape:
            {
                var sb = new StringBuilder(text.Length * 2);
                foreach (var c in text)
                {
                    if (c >= 0x20 && c <= 0x7E && c != '\\')
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                }

                return sb.ToString();
            }

            default:
                return text;
        }
    }

    // ===================== 解码 =====================

    public static string Decode(string text, CodecKind kind)
    {
        switch (kind)
        {
            case CodecKind.Base64:
                return BytesToText(Convert.FromBase64String(NormalizeBase64(text, urlSafe: false)));

            case CodecKind.Base64Url:
                return BytesToText(Convert.FromBase64String(NormalizeBase64(text, urlSafe: true)));

            case CodecKind.Hex:
                return BytesToText(ParseHex(text));

            default:
                return DecodeText(text, kind);
        }
    }

    private static string DecodeText(string text, CodecKind kind)
    {
        switch (kind)
        {
            case CodecKind.Url:
                return Uri.UnescapeDataString(text.Replace("\r\n", "\n"));

            case CodecKind.HtmlEntity:
                return DecodeHtml(text);

            case CodecKind.UnicodeEscape:
                return DecodeUnicodeEscapes(text);

            default:
                return text;
        }
    }

    private static string NormalizeBase64(string text, bool urlSafe)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '\'') continue;
            var ch = c;
            if (urlSafe || ch == '-' || ch == '_')
            {
                if (ch == '-') ch = '+';
                else if (ch == '_') ch = '/';
            }

            sb.Append(ch);
        }

        var clean = sb.ToString();
        var remainder = clean.Length % 4;
        if (remainder == 2) clean += "==";
        else if (remainder == 3) clean += "=";
        return clean;
    }

    private static byte[] ParseHex(string text)
    {
        var digits = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
            {
                i++;
                continue;
            }

            if (Uri.IsHexDigit(c)) digits.Append(c);
        }

        if (digits.Length % 2 != 0)
        {
            throw new FormatException("Hex 长度不是偶数，无法按字节解析。");
        }

        var bytes = new byte[digits.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(digits.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private static string DecodeHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i]);
                continue;
            }

            var end = text.IndexOf(';', i + 1);
            if (end < 0 || end - i > 12)
            {
                sb.Append(text[i]);
                continue;
            }

            var entity = text.Substring(i + 1, end - i - 1);
            var resolved = ResolveEntity(entity);
            if (resolved is null)
            {
                sb.Append(text[i]);
                continue;
            }

            sb.Append(resolved);
            i = end;
        }

        return sb.ToString();
    }

    private static string? ResolveEntity(string entity)
    {
        switch (entity.ToLowerInvariant())
        {
            case "amp": return "&";
            case "lt": return "<";
            case "gt": return ">";
            case "quot": return "\"";
            case "apos": return "'";
            case "nbsp": return "\u00A0";
            case "copy": return "\u00A9";
            case "reg": return "\u00AE";
            case "hellip": return "\u2026";
            case "mdash": return "\u2014";
            case "ndash": return "\u2013";
        }

        if (entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(entity.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? char.ConvertFromUtf32(hex)
                : null;
        }

        if (entity.StartsWith("#", StringComparison.Ordinal))
        {
            return int.TryParse(entity.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
                ? char.ConvertFromUtf32(dec)
                : null;
        }

        return null;
    }

    private static string DecodeUnicodeEscapes(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i == text.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            var next = text[i + 1];
            switch (next)
            {
                case 'u':
                {
                    if (i + 5 >= text.Length)
                    {
                        throw new FormatException("\\u 转义后面不足 4 位十六进制数字。");
                    }

                    var hex = text.Substring(i + 2, 4);
                    if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        throw new FormatException($"非法的 \\u 转义：\\u{hex}");
                    }

                    sb.Append((char)code);
                    i += 5;
                    break;
                }

                case 'U':
                {
                    if (i + 9 >= text.Length)
                    {
                        throw new FormatException("\\U 转义后面不足 8 位十六进制数字。");
                    }

                    var hex = text.Substring(i + 2, 8);
                    if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        throw new FormatException($"非法的 \\U 转义：\\U{hex}");
                    }

                    sb.Append(char.ConvertFromUtf32(code));
                    i += 9;
                    break;
                }

                case 'n': sb.Append('\n'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    // ===================== 字节与文本 =====================

    public static string BytesToText(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return LooseUtf8.GetString(bytes);
        }
    }

    public static bool TryGetBytes(string text, CodecKind kind, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;
        try
        {
            switch (kind)
            {
                case CodecKind.Base64:
                    bytes = Convert.FromBase64String(NormalizeBase64(text, urlSafe: false));
                    return true;
                case CodecKind.Base64Url:
                    bytes = Convert.FromBase64String(NormalizeBase64(text, urlSafe: true));
                    return true;
                case CodecKind.Hex:
                    bytes = ParseHex(text);
                    return true;
                default:
                    bytes = LooseUtf8.GetBytes(Decode(text, kind));
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
