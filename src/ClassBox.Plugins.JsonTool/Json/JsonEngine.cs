using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClassBox.Plugins.JsonTool.Json;

/// <summary>JSON 语法错误位置信息。</summary>
public sealed class JsonError
{
    public int Line { get; init; }

    public int Column { get; init; }

    public string Message { get; init; } = string.Empty;

    public string PositionText => Line > 0 ? $"第 {Line} 行 第 {Column} 列" : "（位置未知）";
}

/// <summary>解析 JSON 失败时抛出，携带行列信息。</summary>
public sealed class JsonInputException : Exception
{
    public JsonInputException(JsonError error) : base(error.Message)
    {
        Error = error;
    }

    public JsonError Error { get; }
}

/// <summary>
/// JSON 处理引擎：格式化（2/4 空格或 Tab）、压缩、校验定位、键排序、
/// 转义/反转义、生成 C# 类骨架。全部基于 System.Text.Json 自行实现，无第三方依赖。
/// </summary>
public static class JsonEngine
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>校验使用严格模式：不忽略冗余逗号与注释，确保能报出真正的格式错误。</summary>
    private static readonly JsonDocumentOptions StrictDocOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    public const string IndentTwoSpaces = "2 空格";
    public const string IndentFourSpaces = "4 空格";
    public const string IndentTab = "Tab";

    /// <summary>把缩进下拉项映射成实际缩进字符串。</summary>
    public static string ResolveIndent(string? selected)
    {
        if (string.IsNullOrEmpty(selected)) return "  ";
        if (selected.Contains("4")) return "    ";
        if (selected.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) >= 0) return "\t";
        return "  ";
    }

    // ===================== 校验 =====================

    public static bool TryValidate(string text, out JsonError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = new JsonError { Message = "内容为空，没有可校验的 JSON。" };
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, StrictDocOptions);
            return true;
        }
        catch (JsonException ex)
        {
            error = BuildError(text, ex);
            return false;
        }
    }

    public static string? ValidateMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "内容为空";
        if (TryValidate(text, out var error))
        {
            using var doc = JsonDocument.Parse(text, DocOptions);
            var structural = Describe(doc.RootElement);
            return $"JSON 合法 · {structural}";
        }

        return $"JSON 非法 · {error!.PositionText} · {error.Message}";
    }

    private static string Describe(JsonElement root)
    {
        var kind = root.ValueKind switch
        {
            JsonValueKind.Object => "对象",
            JsonValueKind.Array => "数组",
            JsonValueKind.String => "字符串",
            JsonValueKind.Number => "数字",
            JsonValueKind.True or JsonValueKind.False => "布尔",
            _ => "空值"
        };

        var count = root.ValueKind switch
        {
            JsonValueKind.Object => $"{root.EnumerateObject().Count()} 个键",
            JsonValueKind.Array => $"{root.GetArrayLength()} 个元素",
            _ => $"{EstimateSize(root)} 字符"
        };

        return $"根为{kind}，{count}";
    }

    private static int EstimateSize(JsonElement element)
    {
        try { return (element.GetRawText() ?? string.Empty).Length; }
        catch { return 0; }
    }

    private static JsonError BuildError(string text, JsonException ex)
    {
        var lineNumber = ex.LineNumber ?? -1;
        var bytePosition = ex.BytePositionInLine ?? 0;
        var column = (int)bytePosition + 1;

        if (lineNumber >= 0)
        {
            var lines = SplitLines(text);
            if (lineNumber < lines.Length)
            {
                var raw = lines[lineNumber];
                var bytes = Encoding.UTF8.GetBytes(raw);
                var take = Math.Min((int)bytePosition, bytes.Length);
                column = Encoding.UTF8.GetString(bytes, 0, take).Length + 1;
            }
        }

        return new JsonError
        {
            Line = (int)lineNumber + 1,
            Column = column,
            Message = ex.Message
        };
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // ===================== 格式化 / 压缩 / 排序 =====================

    public static string Format(string text, string indentUnit) => Write(text, indentUnit, sortKeys: false, pretty: true);

    public static string SortKeys(string text, string indentUnit) => Write(text, indentUnit, sortKeys: true, pretty: true);

    public static string Minify(string text) => Write(text, "  ", sortKeys: false, pretty: false);

    private static string Write(string text, string indentUnit, bool sortKeys, bool pretty)
    {
        using var doc = ParseOrThrow(text);
        var sb = new StringBuilder(Math.Max(64, text.Length * 2));
        WriteElement(doc.RootElement, sb, 0, indentUnit, sortKeys, pretty);
        return sb.ToString();
    }

    private static JsonDocument ParseOrThrow(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonInputException(new JsonError { Message = "内容为空，没有可处理的 JSON。" });
        }

        try
        {
            return JsonDocument.Parse(text, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new JsonInputException(BuildError(text, ex));
        }
    }

    private static void WriteElement(JsonElement element, StringBuilder sb, int depth, string indentUnit, bool sortKeys, bool pretty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var props = sortKeys
                    ? element.EnumerateObject()
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(p => p.Name, StringComparer.Ordinal)
                        .ToList()
                    : element.EnumerateObject().ToList();

                if (props.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }

                sb.Append('{');
                if (pretty) sb.Append("\r\n");
                for (var i = 0; i < props.Count; i++)
                {
                    if (pretty) AppendIndent(sb, depth + 1, indentUnit);
                    AppendString(sb, props[i].Name);
                    sb.Append(pretty ? ": " : ":");
                    WriteElement(props[i].Value, sb, depth + 1, indentUnit, sortKeys, pretty);
                    if (i < props.Count - 1) sb.Append(',');
                    if (pretty) sb.Append("\r\n");
                }

                if (pretty) AppendIndent(sb, depth, indentUnit);
                sb.Append('}');
                return;
            }

            case JsonValueKind.Array:
            {
                var items = element.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }

                sb.Append('[');
                if (pretty) sb.Append("\r\n");
                for (var i = 0; i < items.Count; i++)
                {
                    if (pretty) AppendIndent(sb, depth + 1, indentUnit);
                    WriteElement(items[i], sb, depth + 1, indentUnit, sortKeys, pretty);
                    if (i < items.Count - 1) sb.Append(',');
                    if (pretty) sb.Append("\r\n");
                }

                if (pretty) AppendIndent(sb, depth, indentUnit);
                sb.Append(']');
                return;
            }

            case JsonValueKind.String:
                AppendString(sb, element.GetString() ?? string.Empty);
                return;

            case JsonValueKind.Number:
                sb.Append(element.GetRawText());
                return;

            case JsonValueKind.True:
                sb.Append("true");
                return;

            case JsonValueKind.False:
                sb.Append("false");
                return;

            default:
                sb.Append("null");
                return;
        }
    }

    private static void AppendIndent(StringBuilder sb, int depth, string indentUnit)
    {
        for (var i = 0; i < depth; i++) sb.Append(indentUnit);
    }

    private static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }

    // ===================== 转义 / 反转义 =====================

    /// <summary>把纯文本转成 JSON 字符串内部形式（不含外层引号）。</summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length + 16);
        AppendString(sb, text);
        return sb.ToString(1, sb.Length - 2);
    }

    /// <summary>把含有 \uXXXX、\n 等转义的文本还原为原始文本。</summary>
    public static string Unescape(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i == text.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            var next = text[++i];
            switch (next)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                {
                    if (i + 4 >= text.Length)
                    {
                        throw new JsonInputException(new JsonError { Message = "\\u 转义不足 4 位十六进制数字。" });
                    }

                    var hex = text.Substring(i + 1, 4);
                    if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        throw new JsonInputException(new JsonError { Message = $"非法的 \\u 转义：\\u{hex}" });
                    }

                    sb.Append((char)code);
                    i += 4;
                    break;
                }

                default:
                    throw new JsonInputException(new JsonError { Message = $"无法识别的转义序列：\\{next}" });
            }
        }

        return sb.ToString();
    }

    // ===================== 转 C# 类骨架 =====================

    /// <summary>由 JSON 生成 C# 类骨架（含嵌套类、数组列表、JsonPropertyName）。</summary>
    public static string ToCSharp(string text, string rootName)
    {
        using var doc = ParseOrThrow(text);
        var root = doc.RootElement;
        var rootClass = SanitizeClassName(string.IsNullOrWhiteSpace(rootName) ? "RootObject" : rootName.Trim());

        var sb = new StringBuilder();
        sb.AppendLine("// 由 Class Box「JSON 工具」自动生成");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();

        var pending = new List<(string Name, JsonElement Element)>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                EmitClass(sb, rootClass, root, pending, emitted);
                break;

            case JsonValueKind.Array:
            {
                var first = FirstElement(root);
                var wrapper = new StringBuilder();
                wrapper.Append("public class ").AppendLine(rootClass);
                wrapper.AppendLine("{");
                if (first.ValueKind == JsonValueKind.Object)
                {
                    var itemName = rootClass + "Item";
                    wrapper.Append("    public List<").Append(itemName).AppendLine("> Items { get; set; } = new();");
                    pending.Add((itemName, first));
                }
                else
                {
                    wrapper.Append("    public List<").Append(ResolveType(first, rootClass, "Item", pending)).AppendLine("> Items { get; set; } = new();");
                }

                wrapper.AppendLine("}");
                sb.Append(wrapper);
                break;
            }

            default:
                sb.Append("public class ").AppendLine(rootClass);
                sb.AppendLine("{");
                sb.Append("    public ").Append(ResolveType(root, rootClass, "Value", pending)).AppendLine(" Value { get; set; }");
                sb.AppendLine("}");
                break;
        }

        var guard = 0;
        while (pending.Count > 0 && guard++ < 200)
        {
            var (name, element) = pending[0];
            pending.RemoveAt(0);
            if (!emitted.Add(name)) continue;
            sb.AppendLine();
            EmitClass(sb, name, element, pending, emitted);
        }

        return sb.ToString().TrimEnd();
    }

    private static void EmitClass(
        StringBuilder sb,
        string className,
        JsonElement element,
        List<(string Name, JsonElement Element)> pending,
        HashSet<string> emitted)
    {
        sb.Append("public class ").AppendLine(className);
        sb.AppendLine("{");

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var propName = SanitizePropertyName(property.Name);
            if (!used.Add(propName))
            {
                var index = 2;
                while (!used.Add(propName + index)) index++;
                propName += index.ToString(CultureInfo.InvariantCulture);
            }

            var type = ResolveType(property.Value, className, property.Name, pending);
            if (!string.Equals(propName, property.Name, StringComparison.Ordinal))
            {
                var literal = property.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");
                sb.Append("    [JsonPropertyName(\"").Append(literal).AppendLine("\")]");
            }

            sb.Append("    public ").Append(type).Append(' ').Append(propName).AppendLine(" { get; set; }");
        }

        sb.AppendLine("}");
    }

    private static string ResolveType(JsonElement element, string parentClass, string hint, List<(string Name, JsonElement Element)> pending)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return "string";

            case JsonValueKind.True:
            case JsonValueKind.False:
                return "bool";

            case JsonValueKind.Number:
            {
                var raw = element.GetRawText();
                if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0) return "double";
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && l > int.MaxValue) return "long";
                return "int";
            }

            case JsonValueKind.Object:
            {
                var name = parentClass + SanitizeClassName(hint);
                pending.Add((name, element));
                return name;
            }

            case JsonValueKind.Array:
            {
                var first = FirstElement(element);
                if (first.ValueKind == JsonValueKind.Undefined) return "List<object>";
                if (first.ValueKind == JsonValueKind.Object)
                {
                    var name = parentClass + SanitizeClassName(hint) + "Item";
                    pending.Add((name, first));
                    return "List<" + name + ">";
                }

                return "List<" + ResolveType(first, parentClass, hint, pending) + ">";
            }

            default:
                return "object";
        }
    }

    private static JsonElement FirstElement(JsonElement array)
    {
        foreach (var item in array.EnumerateArray()) return item;
        return default;
    }

    private static string SanitizeClassName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "RootObject";
        var sb = new StringBuilder(raw.Length);
        var upperNext = true;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }

        var result = sb.Length == 0 ? "RootObject" : sb.ToString();
        if (char.IsDigit(result[0])) result = "C" + result;
        return result;
    }

    private static string SanitizePropertyName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var upperNext = true;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }

        var result = sb.Length == 0 ? "Value" : sb.ToString();
        if (char.IsDigit(result[0])) result = "P" + result;
        return result;
    }
}
