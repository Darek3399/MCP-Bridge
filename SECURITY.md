# Security

MCP Bridge starts and routes local MCP servers. Treat server configurations, executable commands, filesystem roots, and public tunnel URLs as security-sensitive.

## Important security boundaries

- Custom STDIO servers run as processes on your Windows machine, with the permissions of your Windows user. Only configure software and commands you trust.
- The built-in Filesystem MCP is restricted to explicitly allowed directories. Avoid granting access to entire drives or folders containing credentials and private data.
- Local listeners bind to loopback by default.
- User-configured remote HTTP MCP endpoints are accepted only over HTTPS and only after a separate confirmation for the exact origin and server entry. Remote plain HTTP is blocked. A changed host or port requires a new approval.
- The confirmation warns that MCP requests and all configured HTTP headers, including bearer credentials, are sent to that host. Header values are currently stored unencrypted in `Config/settings.json`; protect that file and do not use credentials on machines with untrusted local users.
- The optional Tailscale Funnel exposes the stable Bridge MCP Gateway through a public HTTPS URL. Anyone who obtains that URL may be able to connect; Funnel is not an authentication layer for MCP tools.
- Keep the allowed filesystem roots narrow when using a tunnel, and turn the tunnel off when remote access is no longer required.

## Reporting a vulnerability

Please do not publish exploit details, credentials, private tunnel URLs, or sensitive logs in a public issue.

If GitHub private vulnerability reporting is enabled for this repository, use the **Report a vulnerability** option in the repository's Security tab. Otherwise, open a minimal issue asking for a private contact channel without including exploit details.

Please include the affected version, the impact, and a concise reproduction description when it is safe to do so.
