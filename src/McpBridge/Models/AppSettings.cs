using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace McpBridge.Models;

[DataContract]
public sealed class AppSettings
{
    [DataMember(Order = 1)]
    public int SchemaVersion { get; set; } = 6;

    /// <summary>Directories configured for Filesystem MCP.</summary>
    [DataMember(Order = 2)]
    public List<string> AllowedRoots { get; set; } = new List<string>();

    /// <summary>Whether the manager should start for the current Windows user.</summary>
    [DataMember(Order = 3)]
    public bool RunAtWindowsStartup { get; set; }

    /// <summary>UI font scale selected by the user.</summary>
    [DataMember(Order = 6, EmitDefaultValue = false)]
    public double FontScale { get; set; } = 1.0;

    /// <summary>Subset of AllowedRoots that are currently unchecked (disabled) by the user.</summary>
    [DataMember(Order = 7, EmitDefaultValue = false)]
    public List<string> DisabledRoots { get; set; } = new List<string>();

    /// <summary>Whether built-in Unreal MCP auto-attach is enabled.</summary>
    [DataMember(Order = 8, EmitDefaultValue = false)]
    public bool? UnrealMcpEnabled { get; set; } = true;

    /// <summary>Whether built-in Filesystem MCP is enabled.</summary>
    [DataMember(Order = 9, EmitDefaultValue = false)]
    public bool? FilesystemMcpEnabled { get; set; } = true;

    /// <summary>User-defined custom MCP servers (STDIO or HTTP).</summary>
    [DataMember(Order = 10, EmitDefaultValue = false)]
    public List<CustomMcpServerEntry> CustomServers { get; set; } = new List<CustomMcpServerEntry>();

    /// <summary>Saved server presets for 1-click switching.</summary>
    [DataMember(Order = 11, EmitDefaultValue = false)]
    public List<McpPresetEntry> Presets { get; set; } = new List<McpPresetEntry>();

    /// <summary>Currently selected preset ID, if any.</summary>
    [DataMember(Order = 12, EmitDefaultValue = false)]
    public string? ActivePresetId { get; set; }

    [DataMember(Order = 13, EmitDefaultValue = false)]
    public bool UnrealMcpDeleted { get; set; }

    [DataMember(Order = 14, EmitDefaultValue = false)]
    public bool FilesystemMcpDeleted { get; set; }

    [DataMember(Order = 15, EmitDefaultValue = false)]
    public CustomMcpServerEntry? UnrealMcpCustom { get; set; }

    [DataMember(Order = 16, EmitDefaultValue = false)]
    public CustomMcpServerEntry? FilesystemMcpCustom { get; set; }

    [DataMember(Order = 17, EmitDefaultValue = false)]
    public bool PresetsInitialized { get; set; }

    [DataMember(Order = 18, EmitDefaultValue = false)]
    public string? Language { get; set; }

    /// <summary>Whether Tailscale Funnel should be hosted whenever the local MCP gateway is ready.</summary>
    [DataMember(Order = 19, EmitDefaultValue = false)]
    public bool TailscaleFunnelEnabled { get; set; }

    /// <summary>Whether application log files are written. Default is off; toggled from the Logs page in real time.</summary>
    [DataMember(Order = 20, EmitDefaultValue = false)]
    public bool LoggingEnabled { get; set; }
}

[DataContract]
public sealed class CustomMcpServerEntry
{
    [DataMember(Order = 1)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    [DataMember(Order = 2)]
    public string Name { get; set; } = string.Empty;

    /// <summary>"stdio" or "http"; HTTP is negotiated as Streamable HTTP or legacy SSE at runtime.</summary>
    [DataMember(Order = 3)]
    public string Transport { get; set; } = "stdio";

    [DataMember(Order = 4, EmitDefaultValue = false)]
    public string? Command { get; set; }

    [DataMember(Order = 5, EmitDefaultValue = false)]
    public List<string> Args { get; set; } = new List<string>();

    [DataMember(Order = 6, EmitDefaultValue = false)]
    public Dictionary<string, string> Env { get; set; } = new Dictionary<string, string>();

    [DataMember(Order = 7, EmitDefaultValue = false)]
    public string? WorkingDirectory { get; set; }

    [DataMember(Order = 8, EmitDefaultValue = false)]
    public string? Url { get; set; }

    [DataMember(Order = 9)]
    public bool Enabled { get; set; } = true;

    /// <summary>Custom HTTP headers used by external MCP endpoints.</summary>
    [DataMember(Order = 10, EmitDefaultValue = false)]
    public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unrecognized transport-specific JSON properties preserved from the imported MCP definition.</summary>
    [DataMember(Order = 11, EmitDefaultValue = false)]
    public string? AdditionalJson { get; set; }

    /// <summary>Exact HTTPS origin explicitly approved by the user for this remote MCP server.</summary>
    [DataMember(Order = 12, EmitDefaultValue = false)]
    public string? TrustedExternalOrigin { get; set; }

    public CustomMcpServerEntry Clone()
    {
        return new CustomMcpServerEntry
        {
            Id = Id,
            Name = Name,
            Transport = Transport,
            Command = Command,
            Args = Args != null ? new List<string>(Args) : new List<string>(),
            Env = Env != null ? new Dictionary<string, string>(Env, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            WorkingDirectory = WorkingDirectory,
            Url = Url,
            Enabled = Enabled,
            Headers = Headers != null ? new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            AdditionalJson = AdditionalJson,
            TrustedExternalOrigin = TrustedExternalOrigin
        };
    }
}

[DataContract]
public sealed class McpPresetEntry
{
    [DataMember(Order = 1)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    [DataMember(Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public List<string> EnabledServerIds { get; set; } = new List<string>();

    [DataMember(Order = 4)]
    public bool IsBuiltIn { get; set; }

    public string DisplayName
    {
        get
        {
            if (string.Equals(Id, "preset_all", StringComparison.OrdinalIgnoreCase))
                return McpBridge.Services.I18n.Tr("Все серверы", "All Servers");
            if (string.Equals(Id, "preset_ue_fs", StringComparison.OrdinalIgnoreCase))
                return McpBridge.Services.I18n.Tr("Unreal + Файлы", "Unreal + Files");
            if (string.Equals(Id, "preset_fs_only", StringComparison.OrdinalIgnoreCase))
                return McpBridge.Services.I18n.Tr("Только Файлы", "Files Only");
            return Name;
        }
    }
}

public sealed class RootPathItemViewModel : INotifyPropertyChanged
{
    private string _path = string.Empty;
    private bool _isEnabled = true;

    public string Path
    {
        get => _path;
        set
        {
            if (_path == value) return;
            _path = value;
            OnPropertyChanged(nameof(Path));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusForeground));
            OnPropertyChanged(nameof(PathForeground));
        }
    }

    public string StatusLabel => _isEnabled ? (McpBridge.Services.I18n.IsEnglish ? "Active" : "Активна") : (McpBridge.Services.I18n.IsEnglish ? "Disabled" : "Отключена");
    public string StatusForeground => _isEnabled ? "#2CE5D0" : "#7F8790";
    public string PathForeground => _isEnabled ? "#EAECEF" : "#68707A";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum McpServerHealthState
{
    Disabled = 0,
    Waiting = 1,
    Online = 2,
    Quarantine = 3,
    Offline = 4,
    Downloading = 5
}

public sealed class McpServerStatusSnapshot : INotifyPropertyChanged
{
    private bool _isEnabled;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string EndpointDisplay { get; set; } = string.Empty;
    public string PathDisplay { get; set; } = string.Empty;
    public string TransportBadge { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }
    public McpServerHealthState HealthState { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string DisplayStatusText => McpBridge.Services.I18n.TranslateServerStatus(StatusText);
    public int ToolsCount { get; set; }
    public string ToolsPreview { get; set; } = string.Empty;
    public string LastDiagnosticLog { get; set; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateFrom(McpServerStatusSnapshot source)
    {
        if (source == null) return;
        Name = source.Name;
        Subtitle = source.Subtitle;
        EndpointDisplay = source.EndpointDisplay;
        PathDisplay = source.PathDisplay;
        TransportBadge = source.TransportBadge;
        IsBuiltIn = source.IsBuiltIn;
        _isEnabled = source.IsEnabled;
        HealthState = source.HealthState;
        StatusText = source.StatusText;
        ToolsCount = source.ToolsCount;
        ToolsPreview = source.ToolsPreview;
        LastDiagnosticLog = source.LastDiagnosticLog;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string BadgeText => HealthState switch
    {
        McpServerHealthState.Online => McpBridge.Services.I18n.IsEnglish ? "ACTIVE" : "АКТИВЕН",
        McpServerHealthState.Downloading => McpBridge.Services.I18n.IsEnglish ? "DOWNLOADING" : "ЗАГРУЗКА",
        McpServerHealthState.Waiting => McpBridge.Services.I18n.IsEnglish ? "WAITING" : "ОЖИДАНИЕ",
        McpServerHealthState.Offline => McpBridge.Services.I18n.IsEnglish ? "OFFLINE" : "ОФЛАЙН",
        McpServerHealthState.Quarantine => McpBridge.Services.I18n.IsEnglish ? "QUARANTINE" : "КАРАНТИН",
        _ => McpBridge.Services.I18n.IsEnglish ? "DISABLED" : "ВЫКЛЮЧЕН"
    };

    public string BadgeBackground => HealthState switch
    {
        McpServerHealthState.Online => "#15322F",
        McpServerHealthState.Downloading => "#162938",
        McpServerHealthState.Waiting => "#332915",
        McpServerHealthState.Offline => "#1E242B",
        McpServerHealthState.Quarantine => "#381B20",
        _ => "#1D2025"
    };

    public string BadgeBorder => HealthState switch
    {
        McpServerHealthState.Online => "#2CE5D0",
        McpServerHealthState.Downloading => "#38AEFF",
        McpServerHealthState.Waiting => "#F5A623",
        McpServerHealthState.Offline => "#435060",
        McpServerHealthState.Quarantine => "#FF5A67",
        _ => "#3A4049"
    };

    public string BadgeForeground => HealthState switch
    {
        McpServerHealthState.Online => "#2CE5D0",
        McpServerHealthState.Downloading => "#52B8FF",
        McpServerHealthState.Waiting => "#F5A623",
        McpServerHealthState.Offline => "#94A3B8",
        McpServerHealthState.Quarantine => "#FF6B78",
        _ => "#7F8790"
    };

    public System.Windows.Visibility CustomActionsVisibility => System.Windows.Visibility.Visible;

    public System.Windows.Visibility DeleteButtonVisibility =>
        string.Equals(Id, "filesystem", StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Visibility.Hidden
            : System.Windows.Visibility.Visible;

    public System.Windows.Visibility ToolsBadgeVisibility =>
        ToolsCount > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string ToolsBadgeText => ToolsCount.ToString();
}