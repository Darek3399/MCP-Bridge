using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McpBridge.Models;

namespace McpBridge.Services;

public static class McpServerConfigJson
{
    private static readonly HashSet<string> CoreProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "url", "serverUrl", "endpoint", "command", "args", "env",
        "cwd", "workingDirectory", "headers", "type", "transport"
    };

    public static string CreateDefault()
    {
        return "{\n" +
               "  \"name\": \"custom-mcp\",\n" +
               "  \"command\": \"\",\n" +
               "  \"args\": [],\n" +
               "  \"env\": {}\n" +
               "}";
    }

    public static string Serialize(CustomMcpServerEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = entry.Name ?? string.Empty
        };

        var isHttp = string.Equals(entry.Transport, "http", StringComparison.OrdinalIgnoreCase);

        if (isHttp)
        {
            properties["url"] = entry.Url ?? string.Empty;
            if (entry.Command != null)
                properties["command"] = entry.Command;
            if (entry.Args != null && entry.Args.Count > 0)
                properties["args"] = new List<string>(entry.Args);
            if (entry.Env != null && entry.Env.Count > 0)
                properties["env"] = new Dictionary<string, string>(entry.Env, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                properties["cwd"] = entry.WorkingDirectory;
        }
        else
        {
            if (entry.Command != null)
                properties["command"] = entry.Command;

            properties["args"] = entry.Args != null
                ? new List<string>(entry.Args)
                : new List<string>();

            properties["env"] = entry.Env != null
                ? new Dictionary<string, string>(entry.Env, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                properties["cwd"] = entry.WorkingDirectory;

            if (!string.IsNullOrWhiteSpace(entry.Url))
                properties["url"] = entry.Url;
        }

        if (entry.Headers != null && entry.Headers.Count > 0)
            properties["headers"] = new Dictionary<string, string>(entry.Headers, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(entry.AdditionalJson))
        {
            var additional = McpServerConfigParser.DeserializeJson(entry.AdditionalJson!);
            if (additional is not IDictionary<string, object?> additionalProperties)
                throw new FormatException("AdditionalJson должен содержать JSON-объект.");

            foreach (var pair in additionalProperties)
            {
                if (!CoreProperties.Contains(pair.Key))
                    properties[pair.Key] = pair.Value;
            }
        }

        return PrettyPrint(McpServerConfigParser.SerializeJson(properties));
    }

    public static void ValidateJsonDocument(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException(I18n.Tr("Буфер обмена пуст.", "Clipboard is empty."));

        ValidateStrictJson(json);
    }

    public static CustomMcpServerEntry ParseSingleServer(string json, string? stableId = null, bool enabled = true)
    {
        var parsed = McpServerConfigParser.ParseJsonConfiguration(json);
        if (parsed.Count > 1)
            throw new FormatException("В редакторе можно изменять один MCP-сервер за раз. Для импорта нескольких серверов используйте кнопку вставки из буфера обмена.");

        var entry = parsed[0];
        if (!string.IsNullOrWhiteSpace(stableId))
            entry.Id = stableId!;

        entry.Name = string.IsNullOrWhiteSpace(entry.Name)
            ? "custom-mcp"
            : entry.Name.Trim();

        entry.Enabled = enabled;
        return entry;
    }

    private static string PrettyPrint(string compactJson)
    {
        var result = new StringBuilder(compactJson.Length + 64);
        var indent = 0;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < compactJson.Length; i++)
        {
            var ch = compactJson[i];

            if (inString)
            {
                result.Append(ch);
                if (escaped)
                    escaped = false;
                else if (ch == '\\')
                    escaped = true;
                else if (ch == '"')
                    inString = false;

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    result.Append(ch);
                    break;

                case '{':
                case '[':
                    result.Append(ch);
                    var closing = ch == '{' ? '}' : ']';
                    if (i + 1 < compactJson.Length && compactJson[i + 1] != closing)
                    {
                        indent++;
                        result.Append('\n').Append(' ', indent * 2);
                    }
                    break;

                case '}':
                case ']':
                    var opening = ch == '}' ? '{' : '[';
                    if (i > 0 && compactJson[i - 1] != opening)
                    {
                        indent = Math.Max(0, indent - 1);
                        result.Append('\n').Append(' ', indent * 2);
                    }
                    result.Append(ch);
                    break;

                case ',':
                    result.Append(",\n").Append(' ', indent * 2);
                    break;

                case ':':
                    result.Append(": ");
                    break;

                default:
                    result.Append(ch);
                    break;
            }
        }

        return result.ToString();
    }

    private static void ValidateStrictJson(string json)
    {
        var index = 0;
        SkipJsonWhitespace(json, ref index);
        ParseJsonValue(json, ref index, 0);
        SkipJsonWhitespace(json, ref index);

        if (index != json.Length)
            throw JsonError("После JSON-значения обнаружены лишние символы", index);
    }

    private static void ParseJsonValue(string json, ref int index, int depth)
    {
        if (depth > 128)
            throw JsonError("Превышена допустимая глубина JSON", index);

        SkipJsonWhitespace(json, ref index);
        if (index >= json.Length)
            throw JsonError("Ожидалось JSON-значение", index);

        switch (json[index])
        {
            case '{':
                ParseJsonObject(json, ref index, depth + 1);
                return;
            case '[':
                ParseJsonArray(json, ref index, depth + 1);
                return;
            case '"':
                ParseJsonString(json, ref index);
                return;
            case 't':
                ParseJsonLiteral(json, ref index, "true");
                return;
            case 'f':
                ParseJsonLiteral(json, ref index, "false");
                return;
            case 'n':
                ParseJsonLiteral(json, ref index, "null");
                return;
            default:
                ParseJsonNumber(json, ref index);
                return;
        }
    }

    private static void ParseJsonObject(string json, ref int index, int depth)
    {
        index++;
        SkipJsonWhitespace(json, ref index);

        if (index < json.Length && json[index] == '}')
        {
            index++;
            return;
        }

        while (true)
        {
            SkipJsonWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '"')
                throw JsonError("Ключ JSON-объекта должен быть строкой в двойных кавычках", index);

            ParseJsonString(json, ref index);
            SkipJsonWhitespace(json, ref index);

            if (index >= json.Length || json[index] != ':')
                throw JsonError("После ключа JSON-объекта ожидалось двоеточие", index);

            index++;
            ParseJsonValue(json, ref index, depth);
            SkipJsonWhitespace(json, ref index);

            if (index >= json.Length)
                throw JsonError("JSON-объект не закрыт", index);

            if (json[index] == '}')
            {
                index++;
                return;
            }

            if (json[index] != ',')
                throw JsonError("Между свойствами JSON-объекта ожидалась запятая", index);

            index++;
        }
    }

    private static void ParseJsonArray(string json, ref int index, int depth)
    {
        index++;
        SkipJsonWhitespace(json, ref index);

        if (index < json.Length && json[index] == ']')
        {
            index++;
            return;
        }

        while (true)
        {
            ParseJsonValue(json, ref index, depth);
            SkipJsonWhitespace(json, ref index);

            if (index >= json.Length)
                throw JsonError("JSON-массив не закрыт", index);

            if (json[index] == ']')
            {
                index++;
                return;
            }

            if (json[index] != ',')
                throw JsonError("Между элементами JSON-массива ожидалась запятая", index);

            index++;
        }
    }

    private static void ParseJsonString(string json, ref int index)
    {
        index++;
        while (index < json.Length)
        {
            var ch = json[index++];

            if (ch == '"')
                return;

            if (ch < 0x20)
                throw JsonError("В JSON-строке обнаружен недопустимый управляющий символ", index - 1);

            if (ch != '\\')
                continue;

            if (index >= json.Length)
                throw JsonError("Незавершённая escape-последовательность", index);

            var escaped = json[index++];
            switch (escaped)
            {
                case '"':
                case '\\':
                case '/':
                case 'b':
                case 'f':
                case 'n':
                case 'r':
                case 't':
                    break;

                case 'u':
                    if (index + 4 > json.Length)
                        throw JsonError("Неполная Unicode-последовательность", index);

                    for (var digit = 0; digit < 4; digit++)
                    {
                        if (!Uri.IsHexDigit(json[index + digit]))
                            throw JsonError("Некорректная Unicode-последовательность", index + digit);
                    }

                    index += 4;
                    break;

                default:
                    throw JsonError("Недопустимая escape-последовательность", index - 1);
            }
        }

        throw JsonError("JSON-строка не закрыта", index);
    }

    private static void ParseJsonNumber(string json, ref int index)
    {
        var start = index;

        if (index < json.Length && json[index] == '-')
            index++;

        if (index >= json.Length)
            throw JsonError("Некорректное число", start);

        if (json[index] == '0')
        {
            index++;
            if (index < json.Length && char.IsDigit(json[index]))
                throw JsonError("В JSON-числе не допускаются ведущие нули", index);
        }
        else if (json[index] >= '1' && json[index] <= '9')
        {
            while (index < json.Length && json[index] >= '0' && json[index] <= '9')
                index++;
        }
        else
        {
            throw JsonError("Некорректное JSON-число", index);
        }

        if (index < json.Length && json[index] == '.')
        {
            index++;
            var fractionStart = index;
            while (index < json.Length && json[index] >= '0' && json[index] <= '9')
                index++;

            if (fractionStart == index)
                throw JsonError("После десятичной точки ожидались цифры", index);
        }

        if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
        {
            index++;
            if (index < json.Length && (json[index] == '+' || json[index] == '-'))
                index++;

            var exponentStart = index;
            while (index < json.Length && json[index] >= '0' && json[index] <= '9')
                index++;

            if (exponentStart == index)
                throw JsonError("В экспоненте ожидались цифры", index);
        }
    }

    private static void ParseJsonLiteral(string json, ref int index, string literal)
    {
        if (index + literal.Length > json.Length ||
            string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0)
            throw JsonError("Некорректное JSON-значение", index);

        index += literal.Length;
    }

    private static void SkipJsonWhitespace(string json, ref int index)
    {
        while (index < json.Length &&
               (json[index] == ' ' || json[index] == '\t' || json[index] == '\r' || json[index] == '\n'))
            index++;
    }

    private static FormatException JsonError(string message, int index)
    {
        return new FormatException(message + " (позиция " + (index + 1).ToString(CultureInfo.InvariantCulture) + ").");
    }
}