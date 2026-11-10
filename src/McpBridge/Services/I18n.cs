using System;
using System.IO;

namespace McpBridge.Services;

public static class I18n
{
    public static string CurrentLanguage { get; set; } = "ru";

    public static bool IsEnglish => string.Equals(CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);

    public static string Tr(string ru, string en) => IsEnglish ? en : ru;

    private static readonly (string Russian, string English)[] ServerStatusTranslations =
    {
        ("Скачивание и запуск пакета MCP…", "Downloading & starting MCP package…"),
        ("Установка связи MCP…", "Establishing MCP connection…"),
        ("Запуск и проверка MCP…", "Starting & verifying MCP…"),
        ("Подключение к MCP-серверу…", "Connecting to MCP server…"),
        ("Выключен (процесс остановлен)", "Disabled (process stopped)"),
        ("Активен · папок: ", "Active · folders: "),
        ("Ошибка запуска/проверки MCP: ", "MCP startup/verification error: "),
        ("Ошибка удалённого MCP: ", "Remote MCP error: "),
        ("Некорректный URL: требуется HTTP или HTTPS", "Invalid URL: HTTP or HTTPS is required"),
        ("Подключён и активен", "Connected & active"),
        ("Ожидание удалённого MCP: ", "Waiting for remote MCP: "),
        ("Ошибка протокола MCP: ", "MCP protocol error: "),
        ("Ожидание запуска внешней программы (:", "Waiting for external app (:"),
        ("Карантин (Pre-flight): ", "Quarantine (Pre-flight): "),
        ("Карантин (сбой старта): ", "Quarantine (startup failed): "),
        ("Карантин (не прошёл MCP проверку): ", "Quarantine (failed MCP check): "),
        ("Верифицирован и активен", "Verified & active"),
        ("Требуется корректный URL с протоколом HTTP или HTTPS.", "A valid HTTP or HTTPS URL is required."),
        ("URL MCP-сервера должен содержать имя хоста или IP-адрес.", "The MCP URL must include a host name or IP address."),
        ("Учетные данные нельзя указывать непосредственно в URL. Используйте HTTP-заголовки.", "Credentials must not be embedded in the URL. Use HTTP headers instead."),
        ("Конфигурация MCP-сервера отсутствует.", "MCP server configuration is missing."),
        ("Удалённые MCP-серверы должны использовать HTTPS. Обычный HTTP разрешён только для localhost / loopback.", "Remote MCP servers must use HTTPS. Plain HTTP is allowed only for localhost / loopback."),
        ("Удалённый адрес не подтверждён. Откройте редактор сервера и отдельно подтвердите доверие к: ", "Remote endpoint has not been approved. Open the server editor and explicitly trust: "),
        ("Карантин: ", "Quarantine: "),
        ("Выключен", "Disabled")
    };

    public static string TranslateServerStatus(string? status)
    {
        var value = status ?? string.Empty;
        if (value.Length == 0)
            return value;

        foreach (var translation in ServerStatusTranslations)
        {
            if (value.StartsWith(translation.Russian, StringComparison.OrdinalIgnoreCase))
                return Tr(translation.Russian, translation.English) + value.Substring(translation.Russian.Length);

            if (value.StartsWith(translation.English, StringComparison.OrdinalIgnoreCase))
                return Tr(translation.Russian, translation.English) + value.Substring(translation.English.Length);
        }

        return value;
    }

    public static string ResolveLanguage(string? savedLanguage)
    {
        try
        {
            var markerFile = Path.Combine(AppPaths.ConfigDirectory, "installer-language.txt");
            if (File.Exists(markerFile))
            {
                var raw = File.ReadAllText(markerFile).Trim();
                try { File.Delete(markerFile); } catch { }
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw.IndexOf("english", StringComparison.OrdinalIgnoreCase) >= 0 ? "en" : "ru";
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(savedLanguage))
            return string.Equals(savedLanguage!.Trim(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";

        return "ru";
    }
}