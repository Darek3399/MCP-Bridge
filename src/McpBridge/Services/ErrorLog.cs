using System;
using System.IO;
using System.Management;
using System.Text;
namespace McpBridge.Services;

public static class ErrorLog
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string FilePath => Path.Combine(AppPaths.LogsDirectory, "errors.log");
    public static string ConnectionsFilePath => Path.Combine(AppPaths.LogsDirectory, "connections.log");
    public static string AppFilePath => Path.Combine(AppPaths.LogsDirectory, "app.log");
    public static string VerificationFilePath => Path.Combine(AppPaths.LogsDirectory, "verification.log");

    public static event EventHandler? LogUpdated;

    public static void LogConnection(string message) => AppendTimestampedLine(ConnectionsFilePath, "MCP", message);
    public static void LogApp(string message) => AppendTimestampedLine(AppFilePath, "APP", message);
    public static void LogVerification(string message) => AppendTimestampedLine(VerificationFilePath, "VERIFY", message);

    private static void AppendTimestampedLine(string filePath, string tag, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + tag + "] " + message.Trim() + Environment.NewLine;
            lock (Sync) { File.AppendAllText(filePath, line, Utf8NoBom); }
            LogUpdated?.Invoke(null, EventArgs.Empty);
        }
        catch { }
    }

    public static void ClearCategory(string category)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var target = GetCategoryPath(category);
            lock (Sync) { if (File.Exists(target)) File.WriteAllText(target, string.Empty, Utf8NoBom); }
            LogUpdated?.Invoke(null, EventArgs.Empty);
        }
        catch { }
    }

    public static string ReadCategory(string category)
    {
        try
        {
            var target = GetCategoryPath(category);
            lock (Sync)
            {
                if (!File.Exists(target)) return GetEmptyCategoryPlaceholder(category);
                var content = File.ReadAllText(target, Utf8NoBom);
                return string.IsNullOrWhiteSpace(content) ? GetEmptyCategoryPlaceholder(category) : content;
            }
        }
        catch (Exception ex) { return I18n.Tr("Не удалось прочитать журнал: ", "Failed to read log: ") + ex.Message; }
    }

    private static string GetCategoryPath(string category) => category switch
    {
        "connections" => ConnectionsFilePath,
        "app" => AppFilePath,
        "verification" => VerificationFilePath,
        _ => FilePath
    };

    private static string GetEmptyCategoryPlaceholder(string category) => category switch
    {
        "connections" => I18n.Tr(
            "Журнал подключений пуст.\r\nЗдесь отображаются события запуска Bridge MCP Gateway (:8004), роутера (:8001), проверки MCP-серверов, автоподключения и состояние Tailscale Funnel.",
            "Connections log is empty.\r\nBridge MCP Gateway (:8004) and router (:8001) startup, MCP server checks, auto-attach events, and Tailscale Funnel status are logged here."),
        "app" => I18n.Tr(
            "Журнал приложения пуст.\r\nЗдесь отображаются события запуска программы, переключения пресетов, изменения папок и настроек.",
            "Application log is empty.\r\nApp startup, preset switching, folder changes, and configuration updates are logged here."),
        "verification" => I18n.Tr(
            "Журнал верификации пуст.\r\nЗдесь сохраняются этапы проверки MCP, вывод запускаемых процессов и ошибки установки/запуска серверов.",
            "Verification log is empty.\r\nMCP verification stages, child-process output, and server installation/startup errors are recorded here."),
        _ => I18n.Tr(
            "Ошибок пока нет.\r\nСюда записываются только сбои, исключения и аварийные остановки процессов.",
            "No errors recorded.\r\nOnly failures, exceptions, and unexpected process exits are written here.")
    };

    public static string Describe(Exception exception)
    {
        var description = exception.GetType().Name + " (HRESULT 0x" + exception.HResult.ToString("X8") + "): " + exception.Message;
        if (exception is ManagementException managementException)
        {
            description += Environment.NewLine + "WMI ErrorCode: " + managementException.ErrorCode + " (" + ((int)managementException.ErrorCode).ToString() + ")";
            try
            {
                var errorInformation = managementException.ErrorInformation;
                if (errorInformation != null)
                    foreach (PropertyData property in errorInformation.Properties)
                        description += Environment.NewLine + "WMI Detail " + property.Name + ": " + (property.Value?.ToString() ?? "<null>");
            }
            catch (Exception detailException) { description += Environment.NewLine + "WMI extended details could not be read: " + detailException.Message; }
        }
        if (exception.InnerException != null)
            description += Environment.NewLine + "Внутренняя ошибка: " + exception.InnerException.GetType().Name + ": " + exception.InnerException.Message;
        return description;
    }

    public static void WriteMessage(string source, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var entry = new StringBuilder().AppendLine("============================================================")
                .Append("Time (UTC): ").AppendLine(DateTimeOffset.UtcNow.ToString("O"))
                .Append("Source: ").AppendLine(source).AppendLine(message).AppendLine();
            lock (Sync) { File.AppendAllText(FilePath, entry.ToString(), Utf8NoBom); }
            LogConnection(I18n.Tr("ОШИБКА [", "ERROR [") + source + "]: " + message);
        }
        catch { }
    }

    public static void Write(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var entry = new StringBuilder().AppendLine("============================================================")
                .Append("Time (UTC): ").AppendLine(DateTimeOffset.UtcNow.ToString("O"))
                .Append("Source: ").AppendLine(source).AppendLine(Describe(exception))
                .AppendLine("Exception details:").AppendLine(exception.ToString()).AppendLine();
            lock (Sync) { File.AppendAllText(FilePath, entry.ToString(), Utf8NoBom); }
            LogConnection(I18n.Tr("СБОЙ [", "FAILURE [") + source + "]: " + exception.Message);
        }
        catch { }
    }
}