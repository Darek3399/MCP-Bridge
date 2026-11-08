using System;
using McpBridge.Models;

namespace McpBridge.Services;

/// <summary>
/// Keeps remote MCP access explicit and scoped to the exact HTTPS origin
/// that the user approved for a particular server entry.
/// </summary>
public static class McpRemoteEndpointTrust
{
    public static bool TryGetEndpoint(
        string? rawUrl,
        out Uri? endpoint,
        out string origin,
        out string error)
    {
        endpoint = null;
        origin = string.Empty;
        error = string.Empty;

        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var parsed) ||
            (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            error = I18n.Tr(
                "Требуется корректный URL с протоколом HTTP или HTTPS.",
                "A valid HTTP or HTTPS URL is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = I18n.Tr(
                "URL MCP-сервера должен содержать имя хоста или IP-адрес.",
                "The MCP URL must include a host name or IP address.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(parsed.UserInfo))
        {
            error = I18n.Tr(
                "Учетные данные нельзя указывать непосредственно в URL. Используйте HTTP-заголовки.",
                "Credentials must not be embedded in the URL. Use HTTP headers instead.");
            return false;
        }

        endpoint = parsed;
        origin = parsed.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    public static bool IsTrusted(CustomMcpServerEntry server, out string error)
    {
        error = string.Empty;
        if (server == null)
        {
            error = I18n.Tr("Конфигурация MCP-сервера отсутствует.", "MCP server configuration is missing.");
            return false;
        }

        if (!TryGetEndpoint(server.Url, out var endpoint, out var origin, out error))
            return false;

        if (endpoint!.IsLoopback)
            return true;

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = I18n.Tr(
                "Удалённые MCP-серверы должны использовать HTTPS. Обычный HTTP разрешён только для localhost / loopback.",
                "Remote MCP servers must use HTTPS. Plain HTTP is allowed only for localhost / loopback.");
            return false;
        }

        if (!string.Equals(server.TrustedExternalOrigin?.Trim().TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))
        {
            error = I18n.Tr(
                "Удалённый адрес не подтверждён. Откройте редактор сервера и отдельно подтвердите доверие к: ",
                "Remote endpoint has not been approved. Open the server editor and explicitly trust: ") + origin;
            return false;
        }

        return true;
    }
}
