using System.Collections.Generic;
using System.Runtime.Serialization;

namespace McpBridge.Services;

[DataContract]
internal sealed class McpGatewayConfig
{
    [DataMember(Name = "host", Order = 1, EmitDefaultValue = false)]
    public string? Host { get; set; }

    [DataMember(Name = "port", Order = 2)]
    public int Port { get; set; }

    [DataMember(Name = "outputTransport", Order = 3, EmitDefaultValue = false)]
    public string? OutputTransport { get; set; }

    [DataMember(Name = "logLevel", Order = 4, EmitDefaultValue = false)]
    public string? LogLevel { get; set; }

    [DataMember(Name = "healthEndpoint", Order = 5, EmitDefaultValue = false)]
    public string[]? HealthEndpoint { get; set; }

    [DataMember(Name = "healthCheck", Order = 6, EmitDefaultValue = false)]
    public string? HealthCheck { get; set; }

    [DataMember(Name = "protocolVersion", Order = 7, EmitDefaultValue = false)]
    public string? ProtocolVersion { get; set; }

    [DataMember(Name = "mcpServers", Order = 8)]
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
}

[DataContract]
internal sealed class McpServerConfig
{
    [DataMember(Name = "url", Order = 1, EmitDefaultValue = false)]
    public string? Url { get; set; }

    [DataMember(Name = "type", Order = 2, EmitDefaultValue = false)]
    public string? Type { get; set; }

    [DataMember(Name = "command", Order = 3, EmitDefaultValue = false)]
    public string? Command { get; set; }

    [DataMember(Name = "args", Order = 4, EmitDefaultValue = false)]
    public List<string>? Args { get; set; }

    [DataMember(Name = "env", Order = 5, EmitDefaultValue = false)]
    public Dictionary<string, string>? Env { get; set; }

    [DataMember(Name = "outputTransport", Order = 6, EmitDefaultValue = false)]
    public string? OutputTransport { get; set; }

    [DataMember(Name = "mcpServers", Order = 7, EmitDefaultValue = false)]
    public Dictionary<string, McpServerConfig>? McpServers { get; set; }
}

[DataContract]
internal sealed class RouterUpstreamsPayload
{
    [DataMember(Name = "upstreams", Order = 1)]
    public List<RouterUpstreamItem> Upstreams { get; set; } = new();

    [DataMember(Name = "proxyEnv", Order = 2, EmitDefaultValue = false)]
    public Dictionary<string, string>? ProxyEnv { get; set; }
}

[DataContract]
internal sealed class RouterUpstreamItem
{
    [DataMember(Name = "id", Order = 1)]
    public string Id { get; set; } = string.Empty;

    [DataMember(Name = "name", Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "url", Order = 3)]
    public string Url { get; set; } = string.Empty;

    [DataMember(Name = "headers", Order = 4, EmitDefaultValue = false)]
    public Dictionary<string, string>? Headers { get; set; }

    [DataMember(Name = "enabled", Order = 5)]
    public bool Enabled { get; set; } = true;

    [DataMember(Name = "pending", Order = 6, EmitDefaultValue = false)]
    public bool Pending { get; set; }

    [DataMember(Name = "scope", Order = 7)]
    public string Scope { get; set; } = "local";

    [DataMember(Name = "transport", Order = 8)]
    public string Transport { get; set; } = "http";

    [DataMember(Name = "configurationFingerprint", Order = 9, EmitDefaultValue = false)]
    public string? ConfigurationFingerprint { get; set; }
}
