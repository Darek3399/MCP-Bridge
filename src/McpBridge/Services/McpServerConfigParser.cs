using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using McpBridge.Models;

namespace McpBridge.Services;

public static class McpServerConfigParser
{
    public static List<CustomMcpServerEntry> ParseJsonConfiguration(string json)
    {
        McpServerConfigJson.ValidateJsonDocument(json);

        if (DeserializeJson(json) is not IDictionary<string, object?> root)
            throw new FormatException(I18n.Tr(
                "Корнем JSON должен быть объект конфигурации MCP.",
                "The JSON root must be an MCP configuration object."));

        var results = new List<CustomMcpServerEntry>();

        if (TryGetDictionary(root, "mcpServers", out var mcpServers) ||
            TryGetDictionary(root, "servers", out mcpServers))
        {
            if (mcpServers == null || mcpServers.Count == 0)
                throw new FormatException(I18n.Tr(
                    "Список MCP-серверов пуст.",
                    "The MCP server collection is empty."));

            foreach (var pair in mcpServers)
            {
                if (pair.Value is not IDictionary<string, object?> definition ||
                    !TryParseServerDictionary(pair.Key, definition, out var entry))
                {
                    throw new FormatException(I18n.Tr(
                        "Запись сервера «" + pair.Key + "» не содержит поддерживаемого command или url.",
                        "Server entry '" + pair.Key + "' does not contain a supported command or url."));
                }

                results.Add(entry!);
            }

            return results;
        }

        if (root.ContainsKey("command") ||
            root.ContainsKey("url") ||
            root.ContainsKey("serverUrl") ||
            root.ContainsKey("endpoint"))
        {
            var keyName = TryGetString(root, "name") ?? "custom-mcp";
            if (!TryParseServerDictionary(keyName, root, out var directEntry))
                throw new FormatException(I18n.Tr(
                    "JSON не содержит корректного определения MCP-сервера.",
                    "The JSON does not contain a valid MCP server definition."));

            results.Add(directEntry!);
            return results;
        }

        if (root.Count == 0)
            throw new FormatException(I18n.Tr(
                "JSON-объект пуст и не содержит MCP-серверов.",
                "The JSON object is empty and contains no MCP servers."));

        foreach (var pair in root)
        {
            if (pair.Value is not IDictionary<string, object?> definition ||
                !TryParseServerDictionary(pair.Key, definition, out var entry))
            {
                throw new FormatException(I18n.Tr(
                    "JSON содержит неизвестное поле или некорректное описание MCP-сервера: «" + pair.Key + "».",
                    "The JSON contains an unknown property or invalid MCP server definition: '" + pair.Key + "'."));
            }

            results.Add(entry!);
        }

        if (results.Count == 0)
            throw new FormatException(I18n.Tr(
                "В JSON не найдено ни одного определения MCP-сервера.",
                "No MCP server definitions were found in the JSON."));

        return results;
    }

    public static List<CustomMcpServerEntry> ParseSmartInput(string rawInput, out string parseError)
    {
        parseError = string.Empty;
        var results = new List<CustomMcpServerEntry>();
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            parseError = I18n.Tr("Буфер обмена или текст для импорта пуст.", "Clipboard or import text is empty.");
            return results;
        }

        var trimmed = rawInput.Trim();

        // 1. Check if it is a direct HTTP URL
        if ((trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            !trimmed.Contains("\n") && !trimmed.Contains("{"))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var id = "http_" + (uri.Port > 0 ? uri.Port.ToString() : "srv");
                results.Add(new CustomMcpServerEntry
                {
                    Id = id,
                    Name = "HTTP MCP (:" + uri.Port + ")",
                    Transport = "http",
                    Url = trimmed,
                    Enabled = true
                });
                return results;
            }
        }

        // 2. Try parsing as JSON (full object or fragment like "github": { "command": ... })
        var jsonCandidate = trimmed;
        if (!jsonCandidate.StartsWith("{", StringComparison.Ordinal) && jsonCandidate.Contains("{") && jsonCandidate.Contains(":"))
        {
            jsonCandidate = "{" + jsonCandidate.TrimEnd(',') + "}";
        }

        if (jsonCandidate.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                if (DeserializeJson(jsonCandidate) is IDictionary<string, object?> rootDict)
                {
                    // Case A: { "mcpServers": { "srv1": { ... }, "srv2": { ... } } }
                    if (TryGetDictionary(rootDict, "mcpServers", out var serversDict) ||
                        TryGetDictionary(rootDict, "servers", out serversDict))
                    {
                        foreach (var pair in serversDict!)
                        {
                            if (pair.Value is IDictionary<string, object?> srvObj &&
                                TryParseServerDictionary(pair.Key, srvObj, out var entry))
                            {
                                results.Add(entry!);
                            }
                        }
                    }
                    // Case B: Direct server definition { "command": "...", "args": [...] } or { "url": "..." }
                    else if (rootDict.ContainsKey("command") || rootDict.ContainsKey("url") || rootDict.ContainsKey("serverUrl") || rootDict.ContainsKey("endpoint"))
                    {
                        var name = TryGetString(rootDict, "name") ?? "custom-mcp";
                        if (TryParseServerDictionary(name, rootDict, out var singleEntry))
                        {
                            results.Add(singleEntry!);
                        }
                    }
                    // Case C: Map of server names directly { "github": { "command": ... } }
                    else
                    {
                        foreach (var pair in rootDict)
                        {
                            if (pair.Value is IDictionary<string, object?> srvObj &&
                                TryParseServerDictionary(pair.Key, srvObj, out var entry))
                            {
                                results.Add(entry!);
                            }
                        }
                    }

                    if (results.Count > 0)
                        return results;
                }
            }
            catch (Exception ex)
            {
                parseError = I18n.Tr("Ошибка разбора JSON: ", "JSON parse error: ") + ex.Message;
                return results;
            }
        }

        // 3. Fallback: Parse as CLI command line (e.g. "npx -y @modelcontextprotocol/server-memory")
        var tokens = SplitCommandLine(trimmed);
        if (tokens.Count > 0)
        {
            var command = tokens[0];
            var args = tokens.Skip(1).ToList();
            var guessedName = GuessServerNameFromCommand(command, args);
            results.Add(new CustomMcpServerEntry
            {
                Id = SettingsStore.SanitizeServerId(guessedName),
                Name = guessedName,
                Transport = "stdio",
                Command = command,
                Args = args,
                Enabled = true
            });
            return results;
        }

        parseError = I18n.Tr("Не удалось распознать JSON-конфигурацию, URL или команду запуска.", "Could not recognize a JSON configuration, URL, or launch command.");
        return results;
    }

    private static bool TryParseServerDictionary(
        string keyName,
        IDictionary<string, object?> dict,
        out CustomMcpServerEntry? entry)
    {
        entry = null;
        var url = TryGetString(dict, "url") ?? TryGetString(dict, "serverUrl") ?? TryGetString(dict, "endpoint");
        var command = TryGetString(dict, "command");

        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(command))
            return false;

        var cleanId = SettingsStore.SanitizeServerId(keyName);
        if (string.IsNullOrWhiteSpace(cleanId))
            cleanId = "srv_" + Guid.NewGuid().ToString("N").Substring(0, 6);

        var argsList = new List<string>();
        if (dict.TryGetValue("args", out var rawArgs) && rawArgs is IEnumerable enumerableArgs && rawArgs is not string)
        {
            foreach (var item in enumerableArgs)
            {
                if (item != null)
                    argsList.Add(Convert.ToString(item) ?? string.Empty);
            }
        }

        var envDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetDictionary(dict, "env", out var rawEnv))
        {
            foreach (var kv in rawEnv!)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                    envDict[kv.Key.Trim()] = Convert.ToString(kv.Value) ?? string.Empty;
            }
        }

        var cwd = TryGetString(dict, "cwd") ?? TryGetString(dict, "workingDirectory");
        var declaredTransport = TryGetString(dict, "transport") ?? TryGetString(dict, "type");
        var isExplicitStdio = string.Equals(declaredTransport, "stdio", StringComparison.OrdinalIgnoreCase);
        var isHttp = !isExplicitStdio && !string.IsNullOrWhiteSpace(url) &&
                     (string.IsNullOrWhiteSpace(command) ||
                      string.Equals(declaredTransport, "external", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(declaredTransport, "http", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(declaredTransport, "sse", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(declaredTransport, "streamableHttp", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(declaredTransport, "streamable-http", StringComparison.OrdinalIgnoreCase));

        var headersDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetDictionary(dict, "headers", out var rawHeaders))
        {
            foreach (var kv in rawHeaders!)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                    headersDict[kv.Key.Trim()] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var additionalProperties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var knownProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "url", "serverUrl", "endpoint", "command", "args", "env",
            "cwd", "workingDirectory", "headers", "type", "transport", "trustedExternalOrigin"
        };
        foreach (var pair in dict)
            if (!knownProperties.Contains(pair.Key))
                additionalProperties[pair.Key] = pair.Value;

        entry = new CustomMcpServerEntry
        {
            Id = cleanId,
            Name = !string.IsNullOrWhiteSpace(TryGetString(dict, "name"))
                ? TryGetString(dict, "name")!.Trim()
                : (string.IsNullOrWhiteSpace(keyName) ? cleanId : keyName.Trim()),
            Transport = isHttp ? "http" : "stdio",
            Url = url?.Trim(),
            Command = command?.Trim(),
            Args = argsList,
            Env = envDict,
            WorkingDirectory = cwd?.Trim(),
            Headers = headersDict,
            AdditionalJson = SerializeJson(additionalProperties),
            Enabled = true
        };
        return true;
    }

    private static string GuessServerNameFromCommand(string command, List<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("-", StringComparison.Ordinal)) continue;
            if (arg.IndexOf("server-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
                arg.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                arg.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
            {
                var slash = Math.Max(arg.LastIndexOf('/'), arg.LastIndexOf('\\'));
                var leaf = slash >= 0 ? arg.Substring(slash + 1) : arg;
                return leaf;
            }
        }
        var cmdSlash = Math.Max(command.LastIndexOf('/'), command.LastIndexOf('\\'));
        return cmdSlash >= 0 ? command.Substring(cmdSlash + 1) : command;
    }

    public static List<string> SplitCommandLine(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '"';

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (inQuotes)
            {
                if (ch == quoteChar)
                {
                    inQuotes = false;
                }
                else if (ch == '\\' && i + 1 < input.Length && input[i + 1] == quoteChar)
                {
                    current.Append(quoteChar);
                    i++;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else
            {
                if (ch == '"' || ch == '\'')
                {
                    inQuotes = true;
                    quoteChar = ch;
                }
                else if (char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    public static string FormatArguments(IEnumerable<string>? args)
    {
        if (args == null) return string.Empty;
        return string.Join(" ", args.Select(a =>
            string.IsNullOrEmpty(a) ? "\"\"" : (a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a)));
    }

    public static string FormatEnvLines(IDictionary<string, string>? env)
    {
        if (env == null || env.Count == 0) return string.Empty;
        return string.Join(Environment.NewLine, env.Select(kv => kv.Key + "=" + kv.Value));
    }

    public static Dictionary<string, string> ParseEnvLines(string? text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return dict;

        var lines = text!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(key))
                dict[key] = val;
        }
        return dict;
    }

    public static string FormatHeaderLines(IDictionary<string, string>? headers) => FormatEnvLines(headers);

    public static Dictionary<string, string> ParseHeaderLines(string? text) => ParseEnvLines(text);

    public static string NormalizeAdditionalJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        var parsed = DeserializeJson(json!);
        if (parsed is not IDictionary<string, object?> properties)
            throw new FormatException(I18n.Tr("Дополнительные параметры должны быть JSON-объектом вида { ... }.", "Additional parameters must be a JSON object: { ... }."));

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "url", "serverUrl", "endpoint", "command", "args", "env",
            "cwd", "workingDirectory", "headers", "type", "transport", "trustedExternalOrigin"
        };
        var conflicts = properties.Keys.Where(reserved.Contains).ToList();
        if (conflicts.Count > 0)
            throw new FormatException(I18n.Tr(
                "Поля " + string.Join(", ", conflicts) + " уже редактируются отдельными элементами формы.",
                "The fields " + string.Join(", ", conflicts) + " are already edited by dedicated form controls."));

        return SerializeJson(properties);
    }

    public static string SerializeJson(object? value)
    {
        var builder = new StringBuilder();
        WriteJsonValue(builder, value);
        return builder.ToString();
    }

    private static void WriteJsonValue(StringBuilder builder, object? value)
    {
        if (value == null)
        {
            builder.Append("null");
            return;
        }

        if (value is string stringValue)
        {
            WriteJsonString(builder, stringValue);
            return;
        }

        if (value is bool boolValue)
        {
            builder.Append(boolValue ? "true" : "false");
            return;
        }

        if (value is IDictionary dictionary)
        {
            builder.Append('{');
            var first = true;
            foreach (DictionaryEntry item in dictionary)
            {
                if (item.Key == null) continue;
                if (!first) builder.Append(',');
                first = false;
                WriteJsonString(builder, Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                builder.Append(':');
                WriteJsonValue(builder, item.Value);
            }
            builder.Append('}');
            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            builder.Append('[');
            var first = true;
            foreach (var item in enumerable)
            {
                if (!first) builder.Append(',');
                first = false;
                WriteJsonValue(builder, item);
            }
            builder.Append(']');
            return;
        }

        if (value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong ||
            value is float || value is double || value is decimal)
        {
            builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        WriteJsonString(builder, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static void WriteJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(ch);
                    break;
            }
        }
        builder.Append('"');
    }

    private static bool TryGetDictionary(IDictionary<string, object?> parent, string key, out IDictionary<string, object?>? child)
    {
        child = null;
        foreach (var pair in parent)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                pair.Value is IDictionary<string, object?> dict)
            {
                child = dict;
                return true;
            }
        }
        return false;
    }

    private static string? TryGetString(IDictionary<string, object?> dict, string key)
    {
        foreach (var pair in dict)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && pair.Value != null)
                return Convert.ToString(pair.Value);
        }
        return null;
    }

    // =========================================================================
    // НАТИВНЫЙ ПАРСЕР JSON БЕЗ ЗАВИСИМОСТИ ОТ SYSTEM.WEB
    // =========================================================================

    public static object? DeserializeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var index = 0;
        return ParseValue(json, ref index);
    }

    private static object? ParseValue(string s, ref int i)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length) return null;

        var ch = s[i];
        return ch switch
        {
            '{' => ParseObject(s, ref i),
            '[' => ParseArray(s, ref i),
            '"' => ParseString(s, ref i),
            't' or 'T' => ParseLiteral(s, ref i, "true", true),
            'f' or 'F' => ParseLiteral(s, ref i, "false", false),
            'n' or 'N' => ParseLiteral(s, ref i, "null", null),
            _ => ParseNumber(s, ref i)
        };
    }

    private static Dictionary<string, object?> ParseObject(string s, ref int i)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        i++; // skip '{'

        while (true)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Незакрытый JSON-объект.");
            if (s[i] == '}')
            {
                i++;
                return map;
            }
            if (s[i] == ',')
            {
                i++;
                continue;
            }

            var key = s[i] == '"' ? ParseString(s, ref i) : ParseIdentifier(s, ref i);
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != ':')
                throw new FormatException("Ожидалось двоеточие после ключа '" + key + "'.");
            i++; // skip ':'

            var val = ParseValue(s, ref i);
            map[key] = val;

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ',')
            {
                i++;
            }
            else if (i < s.Length && s[i] == '}')
            {
                i++;
                return map;
            }
        }
    }

    private static List<object?> ParseArray(string s, ref int i)
    {
        var list = new List<object?>();
        i++; // skip '['

        while (true)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Незакрытый JSON-массив.");
            if (s[i] == ']')
            {
                i++;
                return list;
            }
            if (s[i] == ',')
            {
                i++;
                continue;
            }

            list.Add(ParseValue(s, ref i));

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ',')
            {
                i++;
            }
            else if (i < s.Length && s[i] == ']')
            {
                i++;
                return list;
            }
        }
    }

    private static string ParseString(string s, ref int i)
    {
        var quote = s[i++];
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i++];
            if (c == quote) return sb.ToString();
            if (c == '\\' && i < s.Length)
            {
                var esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= s.Length &&
                            int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default:
                        sb.Append(esc);
                        break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        throw new FormatException("Незакрытая строковая константа в JSON.");
    }

    private static string ParseIdentifier(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && s[i] != ':' && !char.IsWhiteSpace(s[i]))
            i++;
        return s.Substring(start, i - start).Trim();
    }

    private static object? ParseLiteral(string s, ref int i, string literal, object? result)
    {
        if (i + literal.Length <= s.Length &&
            string.Compare(s, i, literal, 0, literal.Length, StringComparison.OrdinalIgnoreCase) == 0)
        {
            i += literal.Length;
            return result;
        }
        throw new FormatException("Неожиданный токен в JSON.");
    }

    private static object ParseNumber(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && "-+0123456789.eE".IndexOf(s[i]) >= 0)
            i++;
        var numStr = s.Substring(start, i - start);
        if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return numStr;
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                i++;
            }
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                i += 2;
                while (i < s.Length && s[i] != '\n') i++;
            }
            else
            {
                break;
            }
        }
    }
}
