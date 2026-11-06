using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using McpBridge.Models;

namespace McpBridge.Services;

public enum TailscaleConnectionPhase
{
    Offline,
    Disconnected,
    Connecting,
    Disconnecting,
    WaitingForLogin,
    WaitingForDeviceApproval,
    WaitingForHttpsSetup,
    WaitingForFunnelApproval,
    WaitingForGateway,
    Connected,
    Error
}

/// <summary>
/// Single in-process owner of the Tailscale/Funnel connection lifecycle.
/// The UI talks only to this manager; it does not query Tailscale directly.
/// No Tailscale process or status polling is started until online access is enabled.
/// </summary>
public sealed class TailscaleConnectionManager : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(15);

    private readonly TailscaleService _service = new();
    private readonly AppSettings _settings;
    private readonly Func<bool> _saveSettings;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private Timer? _monitorTimer;
    private CancellationTokenSource? _activeConnection;
    private TailscaleConnectionPhase _phase;
    private string _statusText;
    private bool _gatewayReady;
    private bool _started;
    private bool _busy;
    private volatile bool _shutdownStarted;
    private volatile bool _disposed;
    private int _monitorRunning;
    private string _lastOpenedFunnelApprovalUrl = string.Empty;
    private string _lastOpenedHttpsSetupUrl = string.Empty;

    public TailscaleConnectionManager(AppSettings settings, Func<bool> saveSettings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _service.StatusChanged += Service_StatusChanged;
        _service.FunnelAuthorizationRequired += Service_FunnelAuthorizationRequired;

        if (_settings.TailscaleFunnelEnabled)
        {
            _phase = TailscaleConnectionPhase.Offline;
            _statusText = I18n.Tr(
                "ОФЛАЙН · Онлайн-доступ настроен, но туннель сейчас недоступен.",
                "OFFLINE · Online access is configured, but the tunnel is currently unavailable.");
        }
        else if (_service.IsInitialSetupComplete)
        {
            _phase = TailscaleConnectionPhase.Disconnected;
            _statusText = I18n.Tr(
                "ОТКЛЮЧЕНО · Онлайн-доступ ранее настраивался, но сейчас выключен.",
                "DISCONNECTED · Online access was configured before, but is currently turned off.");
        }
        else
        {
            _phase = TailscaleConnectionPhase.Offline;
            _statusText = I18n.Tr(
                "ОФЛАЙН · Онлайн-доступ ещё не настраивался. Локальная работа доступна.",
                "OFFLINE · Online access has not been configured. Local operation is available.");
        }
    }

    public event EventHandler? StatusChanged;

    public TailscaleConnectionPhase Phase
    {
        get { lock (_sync) return _phase; }
    }

    public string StatusText
    {
        get { lock (_sync) return _statusText; }
    }

    public string? PublicUrl => _service.PublicUrl;
    public bool IsConnected => _service.IsPublished;
    public bool IsEnabled => _settings.TailscaleFunnelEnabled;
    public bool IsInitialSetupComplete => _service.IsInitialSetupComplete;

    public bool IsBusy
    {
        get { lock (_sync) return _busy; }
    }

    /// <summary>
    /// Called once by Bridge after its local MCP gateway has initialized.
    /// A disabled/offline profile returns without invoking Tailscale or CLI checks.
    /// </summary>
    public void Start(bool gatewayReady)
    {
        ThrowIfDisposed();
        if (_shutdownStarted) return;
        _gatewayReady = gatewayReady;
        _started = true;

        if (!_settings.TailscaleFunnelEnabled)
            return;

        if (!IsInitialSetupComplete)
        {
            SetStatus(TailscaleConnectionPhase.Offline, I18n.Tr(
                "ОФЛАЙН · Требуется первичная настройка онлайн-доступа.",
                "OFFLINE · Initial online access setup is required."));
            return;
        }

        if (!gatewayReady)
        {
            SetStatus(TailscaleConnectionPhase.WaitingForGateway, I18n.Tr(
                "ОЖИДАНИЕ · Локальный MCP-шлюз ещё не готов.",
                "WAITING · The local MCP gateway is not ready yet."));
            return;
        }

        StartMonitor();
        _ = ReconcileAsync(_lifetimeCancellation.Token);
    }

    /// <summary>
    /// Bridge reports only local gateway availability. This method never probes Tailscale
    /// when the user has not enabled online access.
    /// </summary>
    public void NotifyGatewayAvailability(bool ready)
    {
        if (_disposed || _shutdownStarted || _gatewayReady == ready)
            return;

        _gatewayReady = ready;
        if (!_started || !_settings.TailscaleFunnelEnabled)
            return;

        if (!ready)
        {
            StopMonitor();
            _ = StopForGatewayUnavailableAsync();
            return;
        }

        if (IsInitialSetupComplete)
        {
            StartMonitor();
            _ = ReconcileAsync(_lifetimeCancellation.Token);
        }
    }

    public async Task ToggleConnectionAsync()
    {
        ThrowIfDisposed();
        if (_shutdownStarted) return;

        bool busy;
        lock (_sync) busy = _busy;
        if (busy)
            return;

        // Once the first setup is complete, an enabled online profile means the user
        // intends to keep Funnel enabled—even while temporarily offline or recovering.
        // Allow the single toggle to disable that intent after a connection failure.
        if (IsConnected || (_settings.TailscaleFunnelEnabled && IsInitialSetupComplete))
            await DisconnectAsync();
        else
            await ConnectAsync();
    }

    public async Task ShutdownAsync()
    {
        if (_disposed || _shutdownStarted)
            return;

        _shutdownStarted = true;
        _lifetimeCancellation.Cancel();
        StopMonitor();
        CancellationTokenSource? activeConnection;
        lock (_sync) activeConnection = _activeConnection;
        activeConnection?.Cancel();

        var acquired = await _operationGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!acquired)
        {
            // If the active operation did not release its gate after cancellation,
            // remove only the Bridge-owned public route before killing its process.
            try { await _service.DisableManagedFunnelBestEffortAsync().ConfigureAwait(false); }
            catch { }

            _service.StatusChanged -= Service_StatusChanged;
            _service.FunnelAuthorizationRequired -= Service_FunnelAuthorizationRequired;
            _service.Dispose();
            _disposed = true;
            return;
        }

        try
        {
            try
            {
                await _service.StopHostingAsync().ConfigureAwait(false);
            }
            finally
            {
                _service.StatusChanged -= Service_StatusChanged;
                _service.FunnelAuthorizationRequired -= Service_FunnelAuthorizationRequired;
                _service.Dispose();
                _disposed = true;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ConnectAsync()
    {
        if (!await _operationGate.WaitAsync(0))
            return;

        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _activeConnection = cancellation;
            _busy = true;
        }
        RaiseStatusChanged();

        try
        {
            await RunConnectionFlowAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var previousEnabled = _settings.TailscaleFunnelEnabled;
            _settings.TailscaleFunnelEnabled = true;
            if (!_saveSettings())
            {
                _settings.TailscaleFunnelEnabled = previousEnabled;
                throw new InvalidOperationException(I18n.Tr(
                    "Не удалось сохранить настройку онлайн-доступа. Проверьте права на папку Config.",
                    "Could not save the online access setting. Check write permissions for the Config folder."));
            }

            StartMonitor();
            if (!_gatewayReady)
            {
                SetStatus(TailscaleConnectionPhase.WaitingForGateway, I18n.Tr(
                    "ПОДКЛЮЧЕНО · Ожидаю готовности локального MCP-шлюза для запуска Funnel.",
                    "CONNECTED · Waiting for the local MCP gateway before starting Funnel."));
                return;
            }

            SetStatus(TailscaleConnectionPhase.Connecting, I18n.Tr(
                "Запускаю публичный HTTPS-туннель…",
                "Starting the public HTTPS tunnel…"));
            cancellation.Token.ThrowIfCancellationRequested();
            await _service.StartHostingAsync(cancellation.Token);
            SetStatus(TailscaleConnectionPhase.Connected, I18n.Tr(
                "ПОДКЛЮЧЕНО · Публичный HTTPS-туннель активен.",
                "CONNECTED · Public HTTPS tunnel is active."));
            ErrorLog.LogConnection(I18n.Tr(
                "Tailscale Funnel активен: " + (_service.PublicUrl ?? string.Empty),
                "Tailscale Funnel is active: " + (_service.PublicUrl ?? string.Empty)));
            StartMonitor();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            try { await _service.StopHostingAsync(); } catch { }
            var phase = IsInitialSetupComplete
                ? TailscaleConnectionPhase.Disconnected
                : TailscaleConnectionPhase.Offline;
            SetStatus(phase, I18n.Tr("ПОДКЛЮЧЕНИЕ ОТМЕНЕНО.", "CONNECTION CANCELED."));
        }
        catch (Exception exception)
        {
            SetStatus(TailscaleConnectionPhase.Error,
                I18n.Tr("ОШИБКА ПОДКЛЮЧЕНИЯ · ", "CONNECTION ERROR · ") + exception.Message);
            ErrorLog.Write("TailscaleConnectionManager.Connect", exception);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeConnection, cancellation))
                    _activeConnection = null;
                _busy = false;
            }
            cancellation.Dispose();
            _operationGate.Release();
            RaiseStatusChanged();
        }
    }

    private async Task RunConnectionFlowAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _lastOpenedFunnelApprovalUrl = string.Empty;
            _lastOpenedHttpsSetupUrl = string.Empty;
        }
        var deadline = DateTime.UtcNow.Add(ConnectionTimeout);

        // Windows Tailscale consists of a machine service and a per-user tray
        // client. Ensure the current user session has the client before polling
        // the daemon; otherwise the service can remain in NoState indefinitely.
        SetStatus(TailscaleConnectionPhase.Connecting, I18n.Tr(
            "ЗАПУСК · Проверяю клиент Tailscale в системном трее…",
            "STARTING · Checking the Tailscale system-tray client…"));
        var clientAlreadyRunning = await _service.LaunchClientAsync(cancellationToken);
        SetStatus(TailscaleConnectionPhase.Connecting, I18n.Tr(
            clientAlreadyRunning
                ? "ПРОВЕРКА · Клиент Tailscale уже запущен. Проверяю состояние устройства…"
                : "ЗАПУСК · Клиент Tailscale запущен. Проверяю состояние устройства…",
            clientAlreadyRunning
                ? "CHECKING · The Tailscale client is already running. Checking device state…"
                : "STARTING · The Tailscale client has been started. Checking device state…"));

        var loginUrlOpened = string.Empty;
        var loginRequestAttempted = false;
        var pendingSince = DateTime.MinValue;
        var deviceApprovalPageOpened = false;
        var firstCheck = true;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (firstCheck)
            {
                SetStatus(TailscaleConnectionPhase.Connecting, I18n.Tr(
                    "ПРОВЕРКА · Проверяю состояние Tailscale…",
                    "CHECKING · Checking Tailscale state…"));
                firstCheck = false;
            }

            var result = await _service.CheckConnectionAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.IsConnected)
                return;

            var details = result.Details ?? string.Empty;
            if (details.StartsWith("DEVICE_LOGIN_REQUIRED|", StringComparison.Ordinal))
            {
                pendingSince = DateTime.MinValue;
                var payload = details.Substring("DEVICE_LOGIN_REQUIRED|".Length);
                var separator = payload.IndexOfAny(new[] { '\r', '\n' });
                var authUrl = separator >= 0 ? payload.Substring(0, separator).Trim() : payload.Trim();

                if (string.IsNullOrWhiteSpace(authUrl) &&
                    string.IsNullOrWhiteSpace(loginUrlOpened) &&
                    !loginRequestAttempted)
                {
                    loginRequestAttempted = true;
                    SetStatus(TailscaleConnectionPhase.WaitingForLogin, I18n.Tr(
                        "АВТОРИЗАЦИЯ · Получаю персональную ссылку для входа в аккаунт Tailscale…",
                        "AUTHENTICATION · Requesting a device-specific Tailscale sign-in link…"));
                    try
                    {
                        authUrl = await _service.BeginLoginAsync(cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        // If the CLI cannot produce an AuthURL, fall back to the
                        // tray UI below instead of failing the whole connection flow.
                        authUrl = string.Empty;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(authUrl))
                {
                    loginRequestAttempted = true;
                }

                if (!string.IsNullOrWhiteSpace(authUrl) &&
                    !string.Equals(authUrl, loginUrlOpened, StringComparison.Ordinal))
                {
                    if (!Uri.TryCreate(authUrl, UriKind.Absolute, out var loginUri) ||
                        !string.Equals(loginUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(loginUri.Host, "login.tailscale.com", StringComparison.OrdinalIgnoreCase) ||
                        !loginUri.AbsolutePath.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Tailscale вернул некорректную ссылку авторизации: " + authUrl);
                    }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = loginUri.AbsoluteUri,
                        UseShellExecute = true
                    });
                    loginUrlOpened = loginUri.AbsoluteUri;
                }
                SetStatus(TailscaleConnectionPhase.WaitingForLogin, string.IsNullOrWhiteSpace(loginUrlOpened)
                    ? I18n.Tr(
                        clientAlreadyRunning
                            ? "ОЖИДАНИЕ ВХОДА · Клиент Tailscale уже запущен. Проверь стрелку скрытых значков трея. Если значка нет, открой Tailscale через меню Пуск и нажми Log in."
                            : "ОЖИДАНИЕ ВХОДА · Открыл клиент Tailscale. Нажми Log in; если значок не виден, проверь скрытые значки трея.",
                        clientAlreadyRunning
                            ? "WAITING FOR SIGN-IN · The Tailscale client is already running. Check the system-tray overflow arrow. If the icon is not there, open Tailscale from the Start menu and select Log in."
                            : "WAITING FOR SIGN-IN · Opened the Tailscale client. Select Log in; if the icon is not visible, check the hidden system-tray icons.")
                    : I18n.Tr(
                        "АВТОРИЗАЦИЯ · Открыл персональную ссылку Tailscale для входа и добавления этого устройства. После входа продолжу автоматически.",
                        "AUTHENTICATION · Opened the device-specific Tailscale link to sign in and add this device. I will continue automatically."));
            }
            else if (details.StartsWith("DEVICE_STATE_PENDING|", StringComparison.Ordinal))
            {
                if (pendingSince == DateTime.MinValue)
                    pendingSince = DateTime.UtcNow;

                var pendingFor = DateTime.UtcNow - pendingSince;
                SetStatus(TailscaleConnectionPhase.Connecting, I18n.Tr(
                    clientAlreadyRunning
                        ? "ОЖИДАНИЕ · Клиент Tailscale уже запущен, но daemon остаётся в NoState. Проверь стрелку скрытых значков; если значка нет, открой Tailscale через меню Пуск."
                        : "ОЖИДАНИЕ · Клиент Tailscale запущен, но daemon ещё не вышел из NoState.",
                    clientAlreadyRunning
                        ? "WAITING · The Tailscale client is already running, but the daemon remains in NoState. Check the tray overflow arrow; if the icon is absent, open Tailscale from the Start menu."
                        : "WAITING · The Tailscale client has been started, but the daemon has not left NoState yet."));

                if (pendingFor >= TimeSpan.FromSeconds(35))
                {
                    var diagnostic = details.Substring("DEVICE_STATE_PENDING|".Length).Trim();
                    throw new TimeoutException(I18n.Tr(
                        "Tailscale остаётся в состоянии NoState более 35 секунд. Проверь стрелку скрытых значков трея и наличие tailscale-ipn.exe. Если значка нет и процесса тоже нет, запусти Tailscale через меню Пуск.",
                        "Tailscale remained in NoState for more than 35 seconds. Check the system-tray overflow arrow and whether tailscale-ipn.exe is running. If both are absent, launch Tailscale from the Start menu.") +
                        (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : Environment.NewLine + diagnostic));
                }
            }
            else if (details.StartsWith("DEVICE_NOT_CONNECTED|", StringComparison.Ordinal))
            {
                if (pendingSince == DateTime.MinValue)
                    pendingSince = DateTime.UtcNow;

                var unconnectedFor = DateTime.UtcNow - pendingSince;

                SetStatus(TailscaleConnectionPhase.WaitingForLogin, I18n.Tr(
                    clientAlreadyRunning
                        ? "ОЖИДАНИЕ ПОДКЛЮЧЕНИЯ · Клиент Tailscale уже работает. Открой его из стрелки скрытых значков трея и нажми Connect. Если значка нет, запусти Tailscale через меню Пуск."
                        : "ОЖИДАНИЕ ПОДКЛЮЧЕНИЯ · Открыл клиент Tailscale. Нажми Connect, если он этого требует.",
                    clientAlreadyRunning
                        ? "WAITING FOR CONNECTION · The Tailscale client is already running. Open it from the tray overflow arrow and select Connect. If the icon is absent, launch Tailscale from the Start menu."
                        : "WAITING FOR CONNECTION · Opened the Tailscale client. Select Connect if requested."));

                if (unconnectedFor >= TimeSpan.FromSeconds(45))
                {
                    var diagnostic = details.Substring("DEVICE_NOT_CONNECTED|".Length).Trim();
                    throw new TimeoutException(I18n.Tr(
                        "Tailscale не выдал устройству адрес за 45 секунд. Проверь состояние Connect в клиенте Tailscale.",
                        "Tailscale did not assign an IP address within 45 seconds. Check the Connect state in the Tailscale client.") +
                        (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : Environment.NewLine + diagnostic));
                }
            }
            else if (details.StartsWith("DEVICE_APPROVAL_REQUIRED|", StringComparison.Ordinal))
            {
                pendingSince = DateTime.MinValue;
                if (!deviceApprovalPageOpened)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://console.tailscale.com/admin/machines",
                        UseShellExecute = true
                    });
                    deviceApprovalPageOpened = true;
                }

                SetStatus(TailscaleConnectionPhase.WaitingForDeviceApproval, I18n.Tr(
                    "ОДОБРЕНИЕ УСТРОЙСТВА · Подтвердите устройство в Tailscale Admin Console → Machines. Продолжу автоматически.",
                    "DEVICE APPROVAL · Approve this device in Tailscale Admin Console → Machines. I will continue automatically."));
            }
            else if (details.StartsWith("HTTPS_SETUP_REQUIRED|", StringComparison.Ordinal))
            {
                pendingSince = DateTime.MinValue;
                var payload = details.Substring("HTTPS_SETUP_REQUIRED|".Length);
                var separator = payload.IndexOfAny(new[] { '\r', '\n' });
                var httpsSetupUrl = separator >= 0 ? payload.Substring(0, separator).Trim() : payload.Trim();

                if (!OpenHttpsSetupPage(httpsSetupUrl))
                    throw new InvalidOperationException("Не удалось открыть или проверить ссылку включения HTTPS Tailscale.");

                SetStatus(TailscaleConnectionPhase.WaitingForHttpsSetup, I18n.Tr(
                    "НАСТРОЙКА HTTPS · Открыл настройки HTTPS в Tailscale. Включите HTTPS-сертификаты; Bridge продолжит проверку автоматически.",
                    "HTTPS SETUP · Opened Tailscale HTTPS settings. Enable HTTPS certificates; Bridge will continue checking automatically."));
            }
            else if (details.StartsWith("FUNNEL_AUTHORIZATION_REQUIRED|", StringComparison.Ordinal))
            {
                pendingSince = DateTime.MinValue;
                var payload = details.Substring("FUNNEL_AUTHORIZATION_REQUIRED|".Length);
                var separator = payload.IndexOfAny(new[] { '\r', '\n' });
                var authorizationUrl = separator >= 0 ? payload.Substring(0, separator).Trim() : payload.Trim();

                if (!OpenFunnelApprovalPage(authorizationUrl))
                    throw new InvalidOperationException("Не удалось открыть или проверить ссылку отдельного разрешения Tailscale Funnel.");

                SetStatus(TailscaleConnectionPhase.WaitingForFunnelApproval, I18n.Tr(
                    "РАЗРЕШЕНИЕ FUNNEL · Открыл отдельную страницу подтверждения Funnel. Нажмите Enable; Bridge продолжит автоматически.",
                    "FUNNEL APPROVAL · Opened the separate Funnel approval page. Select Enable; Bridge will continue automatically."));
            }
            else if (details.StartsWith("SETUP_FAILED|", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(details.Substring("SETUP_FAILED|".Length));
            }
            else
            {
                throw new InvalidOperationException(details);
            }

            await Task.Delay(RetryInterval, cancellationToken);
        }

        throw new TimeoutException(I18n.Tr(
            "Время ожидания подключения истекло. Нажмите «Подключить», чтобы повторить попытку.",
            "Connection timed out. Select Connect to try again."));
    }

    private async Task DisconnectAsync()
    {
        if (!await _operationGate.WaitAsync(0))
            return;

        lock (_sync) _busy = true;
        RaiseStatusChanged();
        try
        {
            SetStatus(TailscaleConnectionPhase.Disconnecting, I18n.Tr(
                "ОТКЛЮЧЕНИЕ · Останавливаю только Funnel. Служба Tailscale останется подключённой…",
                "DISCONNECTING · Stopping Funnel only. The Tailscale service will remain connected…"));

            var previousEnabled = _settings.TailscaleFunnelEnabled;
            _settings.TailscaleFunnelEnabled = false;
            if (!_saveSettings())
            {
                _settings.TailscaleFunnelEnabled = previousEnabled;
                throw new InvalidOperationException(I18n.Tr(
                    "Не удалось сохранить отключение онлайн-доступа. Проверьте права на папку Config.",
                    "Could not save the online access setting. Check write permissions for the Config folder."));
            }

            StopMonitor();
            await _service.StopHostingAsync().ConfigureAwait(false);
            SetStatus(TailscaleConnectionPhase.Disconnected, I18n.Tr(
                "ОТКЛЮЧЕНО · Локальный MCP Bridge продолжает работать.",
                "DISCONNECTED · Local MCP Bridge remains available."));
            ErrorLog.LogConnection(I18n.Tr(
                "Tailscale Funnel отключён пользователем.",
                "Tailscale Funnel disabled by user."));
        }
        catch (Exception exception)
        {
            SetStatus(TailscaleConnectionPhase.Error, I18n.Tr(
                "ОШИБКА ОТКЛЮЧЕНИЯ · ", "DISCONNECT ERROR · ") + exception.Message);
            ErrorLog.Write("TailscaleConnectionManager.Disconnect", exception);
        }
        finally
        {
            lock (_sync) _busy = false;
            _operationGate.Release();
            RaiseStatusChanged();
        }
    }

    private void StartMonitor()
    {
        if (_disposed || _shutdownStarted || !_settings.TailscaleFunnelEnabled || !_gatewayReady)
            return;

        lock (_sync)
        {
            _monitorTimer ??= new Timer(
                MonitorTimer_Tick,
                null,
                MonitorInterval,
                MonitorInterval);
        }
    }

    private void StopMonitor()
    {
        lock (_sync)
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }
    }

    private void MonitorTimer_Tick(object? state)
    {
        if (_shutdownStarted || _disposed) return;
        _ = ReconcileAsync(_lifetimeCancellation.Token);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _shutdownStarted || cancellationToken.IsCancellationRequested ||
            !_settings.TailscaleFunnelEnabled || !_gatewayReady ||
            !IsInitialSetupComplete || Interlocked.Exchange(ref _monitorRunning, 1) != 0)
            return;

        if (!await _operationGate.WaitAsync(0).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _monitorRunning, 0);
            return;
        }

        if (cancellationToken.IsCancellationRequested || _disposed || _shutdownStarted)
        {
            _operationGate.Release();
            Interlocked.Exchange(ref _monitorRunning, 0);
            return;
        }

        // A background health check is not a user operation. Keep the
        // settings toggle stable while the monitor reconciles Funnel.
        RaiseStatusChanged();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_service.IsHosting && await _service.CheckTailnetConnectivityAsync(cancellationToken).ConfigureAwait(false))
            {
                SetStatus(TailscaleConnectionPhase.Connected, I18n.Tr(
                    "ПОДКЛЮЧЕНО · Публичный HTTPS-туннель активен.",
                    "CONNECTED · Public HTTPS tunnel is active."));
                return;
            }

            if (_service.IsHosting)
                await _service.StopHostingAsync().ConfigureAwait(false);

            SetStatus(TailscaleConnectionPhase.Connecting, I18n.Tr(
                "ВОССТАНОВЛЕНИЕ · Проверяю службу, tray-клиент и авторизацию Tailscale…",
                "RECOVERY · Checking the Tailscale service, tray client and authentication…"));

            // Auto-recovery must use the same state machine as a manual Connect.
            // In particular, a running Windows service with no per-user tray client
            // can report NoState until tailscale-ipn.exe is launched.
            await RunConnectionFlowAsync(cancellationToken).ConfigureAwait(false);

            SetStatus(TailscaleConnectionPhase.Connecting, I18n.Tr(
                "ВОССТАНОВЛЕНИЕ · Запускаю публичный HTTPS-туннель…",
                "RECOVERY · Starting the public HTTPS tunnel…"));
            await _service.StartHostingAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(TailscaleConnectionPhase.Connected, I18n.Tr(
                "ПОДКЛЮЧЕНО · Публичный HTTPS-туннель активен.",
                "CONNECTED · Public HTTPS tunnel is active."));
            ErrorLog.LogConnection(I18n.Tr(
                "Tailscale Funnel восстановлен и активен: " + (_service.PublicUrl ?? string.Empty),
                "Tailscale Funnel recovered and active: " + (_service.PublicUrl ?? string.Empty)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown: no new UI state or retry is scheduled.
        }
        catch (Exception exception)
        {
            if (IsElevationCanceled(exception))
                StopMonitor();

            if (!_shutdownStarted && !_disposed)
                SetStatus(TailscaleConnectionPhase.Offline,
                    I18n.Tr("ОФЛАЙН · ", "OFFLINE · ") + exception.Message);
        }
        finally
        {
            _operationGate.Release();
            Interlocked.Exchange(ref _monitorRunning, 0);
            if (!_shutdownStarted && !_disposed)
                RaiseStatusChanged();
        }
    }

    private async Task StopForGatewayUnavailableAsync()
    {
        if (_disposed || _shutdownStarted || !_settings.TailscaleFunnelEnabled)
            return;

        if (!await _operationGate.WaitAsync(0).ConfigureAwait(false))
            return;

        // Gateway availability is also maintained in the background.
        // Do not reuse the manual-operation busy flag that drives the UI button.
        RaiseStatusChanged();
        try
        {
            if (_service.IsHosting)
                await _service.StopHostingAsync().ConfigureAwait(false);

            SetStatus(TailscaleConnectionPhase.WaitingForGateway, I18n.Tr(
                "ОЖИДАНИЕ · Локальный MCP-шлюз остановлен. Funnel будет запущен после его восстановления.",
                "WAITING · The local MCP gateway stopped. Funnel will start when it becomes available again."));
        }
        catch (Exception exception)
        {
            SetStatus(TailscaleConnectionPhase.Error, I18n.Tr(
                "ОШИБКА ОСТАНОВКИ ТУННЕЛЯ · ", "TUNNEL STOP ERROR · ") + exception.Message);
            ErrorLog.Write("TailscaleConnectionManager.GatewayUnavailable", exception);
        }
        finally
        {
            _operationGate.Release();
            RaiseStatusChanged();
        }
    }

    private static bool IsElevationCanceled(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is Win32Exception win32Exception && win32Exception.NativeErrorCode == 1223)
                return true;
            if (current.HResult == unchecked((int)0x800704C7))
                return true;
        }

        return false;
    }

    private void Service_FunnelAuthorizationRequired(string authorizationUrl)
    {
        if (_disposed || _shutdownStarted)
            return;

        if (OpenFunnelApprovalPage(authorizationUrl))
        {
            SetStatus(TailscaleConnectionPhase.WaitingForFunnelApproval, I18n.Tr(
                "РАЗРЕШЕНИЕ FUNNEL · Открыл отдельную страницу подтверждения Funnel. Нажмите Enable; Bridge продолжит автоматически.",
                "FUNNEL APPROVAL · Opened the separate Funnel approval page. Select Enable; Bridge will continue automatically."));
        }
    }

    private bool OpenHttpsSetupPage(string value)
    {
        if (!TryValidateHttpsSetupUrl(value, out var setupUrl))
            return false;

        lock (_sync)
        {
            if (string.Equals(_lastOpenedHttpsSetupUrl, setupUrl, StringComparison.Ordinal))
                return true;
            _lastOpenedHttpsSetupUrl = setupUrl;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = setupUrl,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception)
        {
            lock (_sync) _lastOpenedHttpsSetupUrl = string.Empty;
            ErrorLog.Write("TailscaleConnectionManager.OpenHttpsSetupPage", exception);
            return false;
        }
    }

    private static bool TryValidateHttpsSetupUrl(string value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "tailscale.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, "/s/https", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private bool OpenFunnelApprovalPage(string value)
    {
        if (!TryValidateFunnelApprovalUrl(value, out var authorizationUrl))
            return false;

        lock (_sync)
        {
            if (string.Equals(_lastOpenedFunnelApprovalUrl, authorizationUrl, StringComparison.Ordinal))
                return true;
            _lastOpenedFunnelApprovalUrl = authorizationUrl;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception)
        {
            lock (_sync) _lastOpenedFunnelApprovalUrl = string.Empty;
            ErrorLog.Write("TailscaleConnectionManager.OpenFunnelApprovalPage", exception);
            return false;
        }
    }

    private static bool TryValidateFunnelApprovalUrl(string value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "login.tailscale.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, "/f/funnel", StringComparison.OrdinalIgnoreCase))
            return false;

        var node = uri.Query.TrimStart('?').Split('&')
            .FirstOrDefault(part => part.StartsWith("node=", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(node) || node.Length <= 5)
            return false;

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private void Service_StatusChanged(object? sender, TailscaleStatusEventArgs e)
    {
        if (_disposed)
            return;

        if (_service.IsPublished)
        {
            SetStatus(TailscaleConnectionPhase.Connected, I18n.Tr(
                "ПОДКЛЮЧЕНО · Публичный HTTPS-туннель активен.",
                "CONNECTED · Public HTTPS tunnel is active."));
        }
        else if (_busy && _phase == TailscaleConnectionPhase.Connecting)
        {
            SetStatus(TailscaleConnectionPhase.Connecting, e.Status);
        }
        else if (_settings.TailscaleFunnelEnabled && _phase == TailscaleConnectionPhase.Connected)
        {
            SetStatus(TailscaleConnectionPhase.Offline, e.Status);
        }
        else
        {
            RaiseStatusChanged();
        }
    }

    private void SetStatus(TailscaleConnectionPhase phase, string message)
    {
        if (_disposed || _shutdownStarted) return;

        var changed = false;
        lock (_sync)
        {
            if (_disposed || _shutdownStarted) return;
            if (_phase != phase || !string.Equals(_statusText, message, StringComparison.Ordinal))
            {
                _phase = phase;
                _statusText = message;
                changed = true;
            }
        }

        if (changed)
            RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        if (_disposed || _shutdownStarted) return;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TailscaleConnectionManager));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _shutdownStarted = true;
        _lifetimeCancellation.Cancel();
        StopMonitor();
        lock (_sync) _activeConnection?.Cancel();
        _service.StatusChanged -= Service_StatusChanged;
        _service.FunnelAuthorizationRequired -= Service_FunnelAuthorizationRequired;
        _service.Dispose();
        _disposed = true;
    }
}