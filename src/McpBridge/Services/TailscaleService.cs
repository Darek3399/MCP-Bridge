using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ServiceController = System.ServiceProcess.ServiceController;
using ServiceControllerStatus = System.ServiceProcess.ServiceControllerStatus;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace McpBridge.Services;

internal sealed class TailscaleStatusEventArgs : EventArgs
{
    public TailscaleStatusEventArgs(string status, string? publicUrl, bool isHosting)
    {
        Status = status;
        PublicUrl = publicUrl;
        IsHosting = isHosting;
    }

    public string Status { get; }
    public string? PublicUrl { get; }
    public bool IsHosting { get; }
}

/// <summary>
/// Low-level Windows/Tailscale adapter, used only by TailscaleConnectionManager.
/// Executes service, CLI, and foreground Funnel operations; the manager owns
/// connection policy, sequencing, recovery, and user-visible state.
/// It never uses --bg and never configures Windows DNS.
/// </summary>
internal sealed class TailscaleService : IDisposable
{
    private const string ServiceName = "Tailscale";
    private const int LocalPort = 8004;
    private const string LocalPath = "/mcp";
    private const int ServiceStartupTimeoutMilliseconds = 30000;
    private const int FunnelStartupTimeoutMilliseconds = 240000;

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _sync = new();
    private HostProcessState? _host;
    private string _status = I18n.Tr("Tailscale не подключён", "Tailscale is not connected");
    private string? _publicUrl;
    private bool _disposed;
    private bool _initialSetupComplete = HasCompletedInitialSetup();
    private bool _funnelCapabilityVerified;
    private DateTime _funnelCapabilityVerifiedUntil = DateTime.MinValue;
    private DateTime _nextFunnelCapabilityProbeUtc = DateTime.MinValue;
    private string? _pendingFunnelAuthorizationUrl;

    public event EventHandler<TailscaleStatusEventArgs>? StatusChanged;
    public event Action<string>? FunnelAuthorizationRequired;

    public async Task<bool> LaunchClientAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);

            var cli = RequireExecutable();
            var directory = Path.GetDirectoryName(cli);
            var gui = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "tailscale-ipn.exe");
            if (string.IsNullOrWhiteSpace(gui) || !File.Exists(gui))
                throw new FileNotFoundException("Не найден графический клиент Tailscale (tailscale-ipn.exe).", gui);

            var sessionId = Process.GetCurrentProcess().SessionId;
            Process? existing = null;
            foreach (var candidate in Process.GetProcessesByName("tailscale-ipn"))
            {
                try
                {
                    if (existing == null && candidate.SessionId == sessionId)
                        existing = candidate;
                    else
                        candidate.Dispose();
                }
                catch
                {
                    candidate.Dispose();
                }
            }

            if (existing != null)
            {
                existing.Dispose();
                return true;
            }

            using var guiProcess = Process.Start(new ProcessStartInfo
            {
                FileName = gui,
                WorkingDirectory = directory!,
                UseShellExecute = true
            });

            if (guiProcess == null)
                throw new InvalidOperationException("Не удалось запустить графический клиент Tailscale.");

            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Starts the Tailscale login flow and returns the device-specific AuthURL.
    /// Tailscale emits this URL in the first JSON response while the CLI command
    /// can remain alive waiting for the user to finish browser authentication.
    /// </summary>
    public async Task<string> BeginLoginAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);

            var executable = RequireExecutable();
            var directory = Path.GetDirectoryName(executable) ?? AppPaths.InstallDirectory;
            var output = new StringBuilder();
            var error = new StringBuilder();
            var outputSync = new object();
            var authUrlSource = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "up --json --accept-dns=false --unattended=true",
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                lock (outputSync) output.AppendLine(args.Data);
                var url = ReadAuthUrl(args.Data);
                if (!string.IsNullOrWhiteSpace(url))
                    authUrlSource.TrySetResult(url ?? string.Empty);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null) return;
                lock (outputSync) error.AppendLine(args.Data);
                var url = ReadAuthUrl(args.Data);
                if (!string.IsNullOrWhiteSpace(url))
                    authUrlSource.TrySetResult(url!);
            };

            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить команду tailscale up --json.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exitTask = Task.Run(() => process.WaitForExit());
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            var completed = await Task.WhenAny(authUrlSource.Task, exitTask, timeoutTask).ConfigureAwait(false);

            if (completed == authUrlSource.Task)
            {
                var authUrl = await authUrlSource.Task.ConfigureAwait(false);
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }

                await Task.WhenAny(exitTask, Task.Delay(2000)).ConfigureAwait(false);
                return authUrl;
            }

            if (completed == exitTask)
            {
                await exitTask.ConfigureAwait(false);
                if (authUrlSource.Task.IsCompleted)
                    return await authUrlSource.Task.ConfigureAwait(false);

                if (process.ExitCode == 0)
                    return string.Empty;
            }
            else
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }
                await Task.WhenAny(exitTask, Task.Delay(2000)).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            string diagnostic;
            lock (outputSync)
                diagnostic = (output.ToString() + Environment.NewLine + error).Trim();

            if (completed == timeoutTask)
                throw new System.TimeoutException(
                    "Tailscale не вернул ссылку авторизации за 20 секунд."+
                    (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : Environment.NewLine + diagnostic));

            throw new InvalidOperationException(
                "Tailscale не смог создать ссылку авторизации. Код выхода: " + process.ExitCode +
                (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : Environment.NewLine + diagnostic));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public string Status { get { lock (_sync) return _status; } }
    public string? PublicUrl { get { lock (_sync) return _publicUrl; } }
    public bool IsInitialSetupComplete
    {
        get { lock (_sync) return _initialSetupComplete; }
    }
    public bool IsPublished
    {
        get
        {
            lock (_sync)
            {
                var host = _host;
                if (host == null || host.UrlReady.Task.Status != TaskStatus.RanToCompletion) return false;
                try { return !host.Process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }
    public bool IsHosting
    {
        get
        {
            lock (_sync)
            {
                if (_host == null) return false;
                try { return !_host.Process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    public async Task<bool> CheckTailnetConnectivityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = RequireExecutable();
            var result = await RunCommandAsync(executable, "ip -4", 4000, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 &&
                   Regex.IsMatch(result.StandardOutput,
                       @"\b100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}\b");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool IsConnected, string Details)> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);
            var executable = RequireExecutable();
            Publish(
                I18n.Tr("ПРОВЕРКА · Служба Tailscale запущена. Проверяю состояние устройства…", "CHECKING · Tailscale service is running. Checking device state…"),
                null,
                false);

            CommandResult statusResult;
            try
            {
                // Ask the daemon for its actual state first. When the per-user
                // tray client is absent, this reports NoState/Starting much faster
                // than waiting for "tailscale ip -4" to time out.
                statusResult = await RunCommandAsync(
                    executable,
                    "status --json",
                    5000,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (System.TimeoutException exception)
            {
                return (false, "DEVICE_STATE_PENDING|Tailscale status --json timed out: " + exception.Message);
            }

            var backendState = ReadBackendState(statusResult.StandardOutput);
            var stateDetails = string.IsNullOrWhiteSpace(backendState)
                ? "Tailscale status --json exit code: " + statusResult.ExitCode +
                  (string.IsNullOrWhiteSpace(statusResult.CombinedOutput)
                      ? string.Empty
                      : Environment.NewLine + statusResult.CombinedOutput)
                : "Tailscale BackendState: " + backendState;

            if (statusResult.CombinedOutput.IndexOf(
                    "failed to connect to local tailscaled process",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (false, "DEVICE_STATE_PENDING|" + stateDetails);
            }

            if (string.Equals(backendState, "NeedsLogin", StringComparison.Ordinal))
            {
                var authUrl = ReadAuthUrl(statusResult.StandardOutput);
                return (false, "DEVICE_LOGIN_REQUIRED|" + (authUrl ?? string.Empty) +
                    Environment.NewLine + stateDetails);
            }

            if (string.Equals(backendState, "NoState", StringComparison.Ordinal) ||
                string.Equals(backendState, "Starting", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(backendState))
            {
                return (false, "DEVICE_STATE_PENDING|" + stateDetails);
            }

            if (string.Equals(backendState, "NeedsMachineAuth", StringComparison.Ordinal))
            {
                return (false, "DEVICE_APPROVAL_REQUIRED|" + I18n.Tr(
                    "Вход в аккаунт выполнен. Устройство ожидает одобрения администратора tailnet. Откройте страницу Machines, найдите новое устройство и нажмите Approve.",
                    "Account sign-in is complete. This device is waiting for tailnet administrator approval. Open the Machines page, find the new device, and select Approve.") +
                    Environment.NewLine + stateDetails);
            }

            if (statusResult.ExitCode != 0)
            {
                return (false, "DEVICE_STATE_PENDING|" + stateDetails);
            }

            Publish(
                I18n.Tr("ПРОВЕРКА · Tailscale daemon запущен. Проверяю IPv4-адрес устройства…", "CHECKING · Tailscale daemon is running. Checking the device IPv4 address…"),
                null,
                false);

            CommandResult ipResult;
            try
            {
                ipResult = await RunCommandAsync(
                    executable,
                    "ip -4",
                    5000,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (System.TimeoutException exception)
            {
                return (false, "DEVICE_STATE_PENDING|Tailscale ip -4 timed out: " + exception.Message);
            }

            var ip = Regex.Match(
                ipResult.StandardOutput,
                @"\b100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}\b");
            if (ipResult.ExitCode != 0 || !ip.Success)
            {
                Publish(
                    I18n.Tr("Только локально · подключите устройство в Tailscale", "Local only · connect this device in Tailscale"),
                    null,
                    false);

                var diagnostic = string.Join(Environment.NewLine,
                    new[] { ipResult.CombinedOutput, stateDetails }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                return (false, "DEVICE_NOT_CONNECTED|" + I18n.Tr(
                    "Tailscale запущен, но ещё не выдал адрес устройства. Ожидаю завершения подключения.",
                    "Tailscale is running but has not assigned a device address yet. Waiting for the connection to complete.") +
                    (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : Environment.NewLine + diagnostic));
            }
            if (!HasCompletedInitialSetup())
            {
                if (!string.IsNullOrWhiteSpace(_pendingFunnelAuthorizationUrl) &&
                    DateTime.UtcNow < _nextFunnelCapabilityProbeUtc)
                {
                    return (false, "FUNNEL_AUTHORIZATION_REQUIRED|" + _pendingFunnelAuthorizationUrl);
                }

                Publish(
                    I18n.Tr("Первичная настройка онлайн-доступа · проверяю разрешение Funnel", "Initial online setup · checking Funnel authorization"),
                    null,
                    false);

                var setup = await RunElevatedSetupHelperAsync(
                    "Activate",
                    requestElevation: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var httpsSetupUrl = GetOutputValue(setup.Values, "HttpsSetupUrl");
                if (!string.IsNullOrWhiteSpace(httpsSetupUrl))
                {
                    if (!TryNormalizeHttpsSetupUrl(httpsSetupUrl, out var normalizedHttpsSetupUrl))
                        throw new InvalidOperationException("PowerShell helper вернул некорректную ссылку включения HTTPS Tailscale.");

                    return (false, "HTTPS_SETUP_REQUIRED|" + normalizedHttpsSetupUrl + Environment.NewLine +
                        GetOutputValue(setup.Values, "Details"));
                }

                var authorizationUrl = GetOutputValue(setup.Values, "AuthorizationUrl");
                if (string.IsNullOrWhiteSpace(authorizationUrl))
                {
                    // Fallback: recover the approval URL from the helper's raw
                    // diagnostic payload if a PowerShell output-file field was lost.
                    authorizationUrl = ReadFunnelApprovalUrl(string.Join(Environment.NewLine, new[]
                    {
                        setup.Output,
                        GetOutputValue(setup.Values, "Details"),
                        GetOutputValue(setup.Values, "Error"),
                        GetOutputValue(setup.Values, "Status")
                    })) ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(authorizationUrl))
                {
                    if (!TryNormalizeFunnelApprovalUrl(authorizationUrl, out var normalizedAuthorizationUrl))
                        throw new InvalidOperationException("PowerShell helper вернул некорректную ссылку подтверждения Funnel.");

                    _pendingFunnelAuthorizationUrl = normalizedAuthorizationUrl;
                    _nextFunnelCapabilityProbeUtc = DateTime.UtcNow.AddSeconds(6);
                    _funnelCapabilityVerified = false;
                    return (false, "FUNNEL_AUTHORIZATION_REQUIRED|" + normalizedAuthorizationUrl + Environment.NewLine +
                        I18n.Tr(
                            "Подтвердите включение Funnel на отдельной странице Tailscale. Bridge продолжит проверку автоматически.",
                            "Approve Funnel on the separate Tailscale page. Bridge will continue checking automatically."));
                }

                if (setup.ExitCode == 10)
                {
                    var connectStatus = GetOutputValue(setup.Values, "Status");
                    return (false, "DEVICE_NOT_CONNECTED|" + (string.IsNullOrWhiteSpace(connectStatus)
                        ? I18n.Tr("Подключите устройство в клиенте Tailscale и повторите проверку.", "Connect the device in the Tailscale client and retry the check.")
                        : connectStatus));
                }

                var setupError = GetOutputValue(setup.Values, "Error");
                var setupStatus = GetOutputValue(setup.Values, "Status");
                var funnelDetails = GetOutputValue(setup.Values, "Details");

                // The helper writes its success record only after verifying the
                // Tailscale preferences, service, Funnel route and HTTPS hostname.
                // Use this complete record as the success contract, not ExitCode alone.
                var setupConnected = GetOutputValue(setup.Values, "Connected") == "1";
                var funnelAuthorized = GetOutputValue(setup.Values, "FunnelAuthorized") == "1";
                var funnelVerified = GetOutputValue(setup.Values, "FunnelStatusVerified") == "1";
                var settingsApplied = GetOutputValue(setup.Values, "TailscaleSettingsApplied") == "1";
                var serviceRunning = GetOutputValue(setup.Values, "ServiceRunning") == "1";
                var unattended = GetOutputValue(setup.Values, "Unattended") == "1";
                var dnsDisabled = string.Equals(
                    GetOutputValue(setup.Values, "Dns"), "disabled", StringComparison.OrdinalIgnoreCase);
                var setupAddress = GetOutputValue(setup.Values, "Address");
                var setupPublicUrl = GetOutputValue(setup.Values, "PublicUrl");
                var validAddress = Regex.IsMatch(
                    setupAddress,
                    @"^100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}$");
                var validPublicUrl = Uri.TryCreate(setupPublicUrl, UriKind.Absolute, out var publicUri) &&
                    string.Equals(publicUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    publicUri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase);

                var setupSucceeded = setupConnected && funnelAuthorized && funnelVerified &&
                    settingsApplied && serviceRunning && unattended && dnsDisabled &&
                    validAddress && validPublicUrl;

                if (!setupSucceeded)
                {
                    var detailParts = new[]
                        {
                            setupStatus,
                            setupError,
                            funnelDetails,
                            setup.ExitCode == 0 ? string.Empty : "PowerShell exit code: " + setup.ExitCode,
                            setup.Output
                        }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var details = detailParts.Length > 0
                        ? string.Join(Environment.NewLine, detailParts)
                        : I18n.Tr("Первичная настройка онлайн-доступа не завершена.", "Initial online setup did not complete.");
                    ErrorLog.WriteMessage("Tailscale.InitialSetup", details);
                    return (false, "SETUP_FAILED|" + details);
                }

                // The helper normally writes this marker itself. Repair it from
                // the verified success record if it was not persisted.
                EnsureInitialSetupMarker();
                _funnelCapabilityVerified = true;
                _funnelCapabilityVerifiedUntil = DateTime.UtcNow.AddMinutes(5);
                _pendingFunnelAuthorizationUrl = null;
                _nextFunnelCapabilityProbeUtc = DateTime.MinValue;
            }
            else
            {
                await VerifyManagedPreferencesAsync(executable, cancellationToken).ConfigureAwait(false);
                var funnelAuthorizationUrl = await EnsureFunnelCapabilityAsync(cancellationToken).ConfigureAwait(false);
                if (funnelAuthorizationUrl != null && !string.IsNullOrWhiteSpace(funnelAuthorizationUrl))
                {
                    if (funnelAuthorizationUrl.StartsWith("HTTPS_SETUP_REQUIRED|", StringComparison.Ordinal))
                        return (false, funnelAuthorizationUrl);

                    return (false, "FUNNEL_AUTHORIZATION_REQUIRED|" + funnelAuthorizationUrl + Environment.NewLine +
                        I18n.Tr(
                            "Подтвердите включение Funnel на открывшейся странице Tailscale. После подтверждения Bridge продолжит автоматически.",
                            "Approve Funnel on the opened Tailscale page. Bridge will continue automatically after approval."));
                }
            }

            lock (_sync) _initialSetupComplete = HasCompletedInitialSetup();
            Publish(
                I18n.Tr("Онлайн-подключение Tailscale настроено · ", "Tailscale online connection is configured · ") + ip.Value,
                null,
                false);
            return (true, ip.Value);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StartHostingAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (IsHosting) return;

            Publish(I18n.Tr("Проверка службы Tailscale…", "Checking Tailscale service…"), null, false);
            await EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);
            var executable = RequireExecutable();
            await VerifyManagedPreferencesAsync(executable, cancellationToken).ConfigureAwait(false);

            var connected = await RunCommandAsync(executable, "ip -4", 5000, cancellationToken).ConfigureAwait(false);
            var ip = Regex.Match(connected.StandardOutput, @"\b100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}\b");
            if (connected.ExitCode != 0 || !ip.Success)
                throw new InvalidOperationException(I18n.Tr(
                    "Устройство не подключено к Tailscale. Откройте клиент, нажмите Connect и проверьте подключение.",
                    "This device is not connected to Tailscale. Open the client, select Connect, and verify the connection.") +
                    Environment.NewLine + connected.CombinedOutput);

            Publish(I18n.Tr("Публикация Funnel для Bridge MCP Gateway :8004…", "Publishing Funnel for Bridge MCP Gateway :8004…"), null, false);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "funnel --yes --https=443 --set-path=" + LocalPath + " http://127.0.0.1:" + LocalPort + LocalPath,
                    WorkingDirectory = AppPaths.InstallDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            var job = new WindowsJobObject("Tailscale Funnel");
            var state = new HostProcessState(process, job);
            process.OutputDataReceived += (_, args) => HandleHostOutput(state, args.Data);
            process.ErrorDataReceived += (_, args) => HandleHostOutput(state, args.Data);
            process.Exited += (_, _) => HandleHostExit(state);

            try
            {
                lock (_sync) _host = state;
                if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Tailscale Funnel.");
                job.Assign(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                lock (_sync) { if (ReferenceEquals(_host, state)) _host = null; }
                try { if (process.Id > 0 && !process.HasExited) process.Kill(); } catch { }
                job.Dispose();
                process.Dispose();
                throw;
            }

            var funnelDeadline = DateTime.UtcNow.AddMilliseconds(FunnelStartupTimeoutMilliseconds);
            var approvalStatusPublished = false;
            while (!state.UrlReady.Task.IsCompleted &&
                   !state.Exited.Task.IsCompleted &&
                   DateTime.UtcNow < funnelDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (state.FunnelApprovalUrl.Task.IsCompleted && !approvalStatusPublished)
                {
                    approvalStatusPublished = true;
                    Publish(
                        I18n.Tr("ОЖИДАНИЕ · Требуется отдельно разрешить Tailscale Funnel в браузере.",
                            "WAITING · Tailscale Funnel requires a separate browser approval."),
                        null,
                        false);
                }

                // CLI output can be buffered when redirected. Query the daemon's
                // active Funnel configuration instead of depending on stdout.
                var discoveredUrl = await TryDiscoverFunnelUrlAsync(executable, LocalPath, cancellationToken).ConfigureAwait(false);
                if (discoveredUrl != null && !string.IsNullOrWhiteSpace(discoveredUrl))
                    state.UrlReady.TrySetResult(discoveredUrl);

                if (state.UrlReady.Task.IsCompleted || state.Exited.Task.IsCompleted)
                    break;

                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (state.Exited.Task.IsCompleted && !state.UrlReady.Task.IsCompleted)
                throw new InvalidOperationException(
                    "Tailscale Funnel завершился до публикации URL. Код выхода: " + state.Exited.Task.Result +
                    "." + Environment.NewLine + state.GetOutput());

            if (!state.UrlReady.Task.IsCompleted)
                throw new System.TimeoutException(
                    "Tailscale Funnel не опубликовал URL за " + FunnelStartupTimeoutMilliseconds / 1000 +
                    " секунд." + Environment.NewLine + state.GetOutput());

            if (IsExited(process))
                throw new InvalidOperationException("Tailscale Funnel завершился сразу после публикации URL." + Environment.NewLine + state.GetOutput());

            var publicUrl = state.UrlReady.Task.Result.TrimEnd('/') + LocalPath;
            ErrorLog.LogConnection(I18n.Tr(
                "Tailscale Funnel активен: " + publicUrl,
                "Tailscale Funnel is active: " + publicUrl));
            Publish(I18n.Tr("Tailscale Funnel активен", "Tailscale Funnel is active"), publicUrl, true);
        }
        catch (Exception exception)
        {
            HostProcessState? failedHost;
            lock (_sync) failedHost = _host;
            try { await StopHostProcessAsync(failedHost).ConfigureAwait(false); } catch { }
            Publish(
                I18n.Tr("Ошибка Tailscale Funnel: ", "Tailscale Funnel error: ") + exception.Message,
                null,
                false);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopHostingAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            HostProcessState? state;
            lock (_sync) state = _host;

            var wasHosting = state != null;
            await StopHostProcessAsync(state).ConfigureAwait(false);
            if (wasHosting)
            {
                ErrorLog.LogConnection(I18n.Tr(
                    "Tailscale Funnel отключён.",
                    "Tailscale Funnel disabled."));
            }
            Publish(I18n.Tr("Tailscale Funnel отключён · служба Tailscale продолжает работать",
                "Tailscale Funnel is disabled · Tailscale service remains running"), null, false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task StopHostProcessAsync(HostProcessState? state)
    {
        if (state == null) return;

        state.ExpectedStop = true;
        // Remove only the Bridge-managed path. The command is time-bounded and
        // is safe even when Funnel failed before publishing a URL.
        try
        {
            var executable = FindExecutable();
            if (executable != null)
            {
                await RunCommandAsync(executable,
                    "funnel --https=443 --set-path=" + LocalPath + " off", 3000).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("Tailscale.FunnelOff", exception);
        }

        try
        {
            if (!IsExited(state.Process))
            {
                var exited = await Task.Run(() => state.Process.WaitForExit(1000)).ConfigureAwait(false);
                if (!exited && !IsExited(state.Process))
                {
                    state.Job.TryTerminateAll(out _);
                    state.Process.Kill();
                    await Task.Run(() => state.Process.WaitForExit(2000)).ConfigureAwait(false);
                }
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        HandleHostExit(state);
    }

    private async Task<bool> EnsureServiceRunningAsync(CancellationToken cancellationToken = default)
    {
        using var service = new ServiceController(ServiceName);
        try { service.Refresh(); }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("Служба Tailscale не установлена. Повторите установку MCP Bridge.");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1060)
        {
            throw new InvalidOperationException("Служба Tailscale не установлена. Повторите установку MCP Bridge.", exception);
        }

        if (service.Status == ServiceControllerStatus.Running) return false;

        Publish(
            service.Status == ServiceControllerStatus.StartPending
                ? I18n.Tr("ОЖИДАНИЕ · Служба Tailscale уже запускается…", "WAITING · Tailscale service is already starting…")
                : I18n.Tr("ЗАПУСК · Запускаю службу Tailscale…", "STARTING · Starting the Tailscale service…"),
            null,
            false);

        async Task StartElevatedAsync()
        {
            // Do not rewrite the service ACL. Request elevation only for this
            // specific service-control operation when Windows denies it.
            Publish(
                I18n.Tr("ПРАВА WINDOWS · Запрашиваю разрешение на запуск службы Tailscale…", "WINDOWS PERMISSION · Requesting permission to start the Tailscale service…"),
                null,
                false);

            var elevatedStart = await RunElevatedSetupHelperAsync(
                "PrepareOnline",
                requestElevation: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (elevatedStart.ExitCode != 0 ||
                GetOutputValue(elevatedStart.Values, "Prepared") != "1")
            {
                var details = GetOutputValue(elevatedStart.Values, "Error");
                if (string.IsNullOrWhiteSpace(details))
                    details = elevatedStart.Output;
                throw new InvalidOperationException(
                    "Не удалось запустить службу Tailscale с повышенными правами. " + details);
            }

            service.Refresh();
        }

        // A previous stop/restart can leave the service in a transition state.
        // Wait for StopPending to finish before issuing Start; StartPending and
        // ContinuePending are already being handled by Windows.
        if (service.Status == ServiceControllerStatus.StopPending)
        {
            var stopDeadline = DateTime.UtcNow.AddSeconds(12);
            while (service.Status == ServiceControllerStatus.StopPending &&
                   DateTime.UtcNow < stopDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                service.Refresh();
            }

            if (service.Status == ServiceControllerStatus.StopPending)
                throw new System.TimeoutException("Служба Tailscale не завершила остановку за 12 секунд.");
        }

        if (service.Status == ServiceControllerStatus.Paused)
        {
            try
            {
                service.Continue();
            }
            catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 5)
            {
                await StartElevatedAsync().ConfigureAwait(false);
            }
        }
        else if (service.Status != ServiceControllerStatus.StartPending &&
                 service.Status != ServiceControllerStatus.ContinuePending &&
                 service.Status != ServiceControllerStatus.Running)
        {
            try
            {
                service.Start();
            }
            catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 5)
            {
                await StartElevatedAsync().ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "Bridge не удалось запустить службу Tailscale. " + exception.Message,
                    exception);
            }
        }
        var deadline = DateTime.UtcNow.AddMilliseconds(ServiceStartupTimeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Running) return true;
            if (service.Status == ServiceControllerStatus.Stopped)
                throw new InvalidOperationException("Служба Tailscale остановилась при запуске.");
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        throw new System.TimeoutException("Служба Tailscale не перешла в состояние Running за 30 секунд.");
    }

    private string RequireExecutable()
    {
        var path = FindExecutable();
        if (path == null)
            throw new FileNotFoundException("Не найден tailscale.exe. Переустановите Tailscale из встроенного пакета MCP Bridge.");
        return path;
    }

    private static string? FindExecutable()
    {
        var candidates = new[]
        {
            GetServiceExecutableSibling(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tailscale", "tailscale.exe")
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static string? GetServiceExecutableSibling()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tailscale");
            var imagePath = key?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(imagePath)) return null;

            var match = Regex.Match(imagePath, "^\\s*\\\"(?<path>[^\\\"]+tailscaled\\.exe)\\\"|^\\s*(?<path>.+?tailscaled\\.exe)", RegexOptions.IgnoreCase);
            if (!match.Success) return null;

            var daemon = match.Groups["path"].Value.Trim();
            var directory = Path.GetDirectoryName(daemon);
            return string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "tailscale.exe");
        }
        catch { return null; }
    }

    private static async Task<string?> TryDiscoverFunnelUrlAsync(string executable, string expectedPath, CancellationToken cancellationToken)
    {
        try
        {
            var funnel = await RunCommandAsync(executable, "funnel status --json", 2500, cancellationToken).ConfigureAwait(false);
            if (funnel.ExitCode != 0 ||
                string.IsNullOrWhiteSpace(funnel.StandardOutput) ||
                !funnel.StandardOutput.Contains(expectedPath))
                return null;

            var status = await RunCommandAsync(executable, "status --json", 2500, cancellationToken).ConfigureAwait(false);
            if (status.ExitCode != 0 || string.IsNullOrWhiteSpace(status.StandardOutput))
                return null;

            var hostMatch = Regex.Match(
                status.StandardOutput,
                "\"DNSName\"\\s*:\\s*\"(?<host>[a-z0-9.-]+\\.ts\\.net\\.?)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!hostMatch.Success)
            {
                hostMatch = Regex.Match(
                    funnel.StandardOutput,
                    "\"(?<host>[a-z0-9.-]+\\.ts\\.net):443\"\\s*:",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            if (!hostMatch.Success)
                return null;

            var host = hostMatch.Groups["host"].Value.TrimEnd('.');
            var allowPattern = "\"" + Regex.Escape(host) + ":443\"\\s*:\\s*true";
            var httpsPattern = "\"443\"\\s*:\\s*\\{\\s*\"HTTPS\"\\s*:\\s*true";
            if (!Regex.IsMatch(funnel.StandardOutput, allowPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(funnel.StandardOutput, httpsPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return null;

            return "https://" + host;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void HandleHostOutput(HostProcessState state, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        state.AddOutput(line!);

        var approvalMatch = Regex.Match(
            line!,
            @"https://login\.tailscale\.com/f/funnel\?node=[^\s""'<>]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (approvalMatch.Success)
        {
            var candidate = approvalMatch.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
            if (TryNormalizeFunnelApprovalUrl(candidate, out var approvalUrl) &&
                state.FunnelApprovalUrl.TrySetResult(approvalUrl))
            {
                try { FunnelAuthorizationRequired?.Invoke(approvalUrl); } catch { }
            }
        }

        var match = Regex.Match(line!, @"https://[a-z0-9.-]+\.ts\.net", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            state.UrlReady.TrySetResult(match.Value);
    }

    private void HandleHostExit(HostProcessState state)
    {
        if (Interlocked.Exchange(ref state.CleanupStarted, 1) != 0) return;
        int exitCode = -1;
        try { exitCode = state.Process.ExitCode; } catch (InvalidOperationException) { }
        state.Exited.TrySetResult(exitCode);
        var isCurrent = false;
        lock (_sync)
        {
            if (ReferenceEquals(_host, state))
            {
                _host = null;
                _publicUrl = null;
                isCurrent = true;
            }
        }

        state.Job.Dispose();
        state.Process.Dispose();

        if (isCurrent && !state.ExpectedStop)
        {
            var details = "Tailscale Funnel неожиданно завершился. Код выхода: " + exitCode + "." +
                (string.IsNullOrWhiteSpace(state.GetOutput()) ? string.Empty : Environment.NewLine + state.GetOutput());
            ErrorLog.WriteMessage("Tailscale.FunnelExited", details);
            Publish("Tailscale Funnel неожиданно завершился (код " + exitCode + ").", null, false);
            _ = RemoveManagedFunnelPathAsync();
        }
    }

    internal Task DisableManagedFunnelBestEffortAsync() => RemoveManagedFunnelPathAsync();

    private async Task RemoveManagedFunnelPathAsync()
    {
        try
        {
            var executable = FindExecutable();
            if (executable == null) return;

            var status = await RunCommandAsync(executable, "funnel status --json", 2000).ConfigureAwait(false);
            if (status.ExitCode != 0 ||
                string.IsNullOrWhiteSpace(status.StandardOutput) ||
                !status.StandardOutput.Contains(LocalPath))
                return;

            var result = await RunCommandAsync(
                executable,
                "funnel --https=443 --set-path=" + LocalPath + " off",
                3000).ConfigureAwait(false);

            if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.CombinedOutput))
                ErrorLog.WriteMessage("Tailscale.FunnelExitCleanup", result.CombinedOutput);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("Tailscale.FunnelExitCleanup", exception);
        }
    }

    private void Publish(string status, string? publicUrl, bool isHosting)
    {
        lock (_sync)
        {
            _status = status;
            _publicUrl = publicUrl;
        }
        StatusChanged?.Invoke(this, new TailscaleStatusEventArgs(status, publicUrl, isHosting));
    }

    private static async Task VerifyManagedPreferencesAsync(string executable, CancellationToken cancellationToken = default)
    {
        var dnsCheck = await RunCommandAsync(executable, "get accept-dns", 5000, cancellationToken).ConfigureAwait(false);
        EnsureSuccess("Проверка Tailscale accept-dns", dnsCheck);
        if (!string.Equals(dnsCheck.StandardOutput.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            var dnsSet = await RunCommandAsync(executable, "set --accept-dns=false", 8000, cancellationToken).ConfigureAwait(false);
            EnsureSuccess("Восстановление Tailscale accept-dns=false", dnsSet);
        }

        var unattendedCheck = await RunCommandAsync(executable, "get unattended", 5000, cancellationToken).ConfigureAwait(false);
        EnsureSuccess("Проверка Tailscale unattended mode", unattendedCheck);
        if (!string.Equals(unattendedCheck.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            var unattendedSet = await RunCommandAsync(executable, "set --unattended=true", 8000, cancellationToken).ConfigureAwait(false);
            EnsureSuccess("Восстановление Tailscale unattended=true", unattendedSet);
        }
    }

    private static bool HasCompletedInitialSetup()
    {
        var stateFile = Path.Combine(AppPaths.ConfigDirectory, "tailscale-state.txt");
        if (!File.Exists(stateFile)) return false;
        try
        {
            return File.ReadAllLines(stateFile).Any(line =>
                string.Equals(line.Trim(), "BridgeSetupComplete=1", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureInitialSetupMarker()
    {
        if (HasCompletedInitialSetup())
            return;

        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        var stateFile = Path.Combine(AppPaths.ConfigDirectory, "tailscale-state.txt");
        var existingState = File.Exists(stateFile) ? File.ReadAllText(stateFile) : string.Empty;
        if (existingState.Length > 0 &&
            !existingState.EndsWith("\n", StringComparison.Ordinal) &&
            !existingState.EndsWith("\r", StringComparison.Ordinal))
        {
            File.AppendAllText(stateFile, Environment.NewLine, Encoding.ASCII);
        }

        File.AppendAllText(stateFile, "BridgeSetupComplete=1" + Environment.NewLine, Encoding.ASCII);
        if (!HasCompletedInitialSetup())
            throw new InvalidOperationException("Не удалось сохранить признак завершённой настройки Tailscale.");
    }

    private static bool HasAppliedTailscaleSettings()
    {
        var stateFile = Path.Combine(AppPaths.ConfigDirectory, "tailscale-state.txt");
        if (!File.Exists(stateFile)) return false;
        try
        {
            return File.ReadAllLines(stateFile).Any(line =>
                string.Equals(line.Trim(), "TailscaleSettingsApplied=1", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> EnsureFunnelCapabilityAsync(CancellationToken cancellationToken)
    {
        if (_funnelCapabilityVerified && DateTime.UtcNow < _funnelCapabilityVerifiedUntil)
            return null;

        if (!string.IsNullOrWhiteSpace(_pendingFunnelAuthorizationUrl) &&
            DateTime.UtcNow < _nextFunnelCapabilityProbeUtc)
            return _pendingFunnelAuthorizationUrl;

        var setup = await RunElevatedSetupHelperAsync(
            "Activate",
            requestElevation: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var httpsSetupUrl = GetOutputValue(setup.Values, "HttpsSetupUrl");
        if (!string.IsNullOrWhiteSpace(httpsSetupUrl))
        {
            if (!TryNormalizeHttpsSetupUrl(httpsSetupUrl, out var normalizedHttpsSetupUrl))
                throw new InvalidOperationException("PowerShell helper вернул некорректную ссылку включения HTTPS Tailscale.");
            _funnelCapabilityVerified = false;
            return "HTTPS_SETUP_REQUIRED|" + normalizedHttpsSetupUrl;
        }

        var rawAuthorizationUrl = GetOutputValue(setup.Values, "AuthorizationUrl");
        if (string.IsNullOrWhiteSpace(rawAuthorizationUrl))
        {
            rawAuthorizationUrl = ReadFunnelApprovalUrl(string.Join(Environment.NewLine, new[]
            {
                setup.Output,
                GetOutputValue(setup.Values, "Details"),
                GetOutputValue(setup.Values, "Error"),
                GetOutputValue(setup.Values, "Status")
            })) ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(rawAuthorizationUrl))
        {
            if (!TryNormalizeFunnelApprovalUrl(rawAuthorizationUrl, out var normalizedUrl))
                throw new InvalidOperationException("PowerShell helper вернул некорректную ссылку подтверждения Funnel.");

            _pendingFunnelAuthorizationUrl = normalizedUrl;
            _nextFunnelCapabilityProbeUtc = DateTime.UtcNow.AddSeconds(6);
            _funnelCapabilityVerified = false;
            return normalizedUrl;
        }

        var setupConnected = GetOutputValue(setup.Values, "Connected") == "1";
        var funnelAuthorized = GetOutputValue(setup.Values, "FunnelAuthorized") == "1";
        var funnelVerified = GetOutputValue(setup.Values, "FunnelStatusVerified") == "1";
        var settingsApplied = GetOutputValue(setup.Values, "TailscaleSettingsApplied") == "1";
        var serviceRunning = GetOutputValue(setup.Values, "ServiceRunning") == "1";
        var unattended = GetOutputValue(setup.Values, "Unattended") == "1";
        var dnsDisabled = string.Equals(
            GetOutputValue(setup.Values, "Dns"), "disabled", StringComparison.OrdinalIgnoreCase);
        var address = GetOutputValue(setup.Values, "Address");
        var publicUrl = GetOutputValue(setup.Values, "PublicUrl");
        var validAddress = Regex.IsMatch(
            address,
            @"^100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}$");
        var validPublicUrl = Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri) &&
            string.Equals(publicUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            publicUri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase);

        if (setup.ExitCode != 0 || !setupConnected || !funnelAuthorized || !funnelVerified ||
            !settingsApplied || !serviceRunning || !unattended || !dnsDisabled ||
            !validAddress || !validPublicUrl)
        {
            _funnelCapabilityVerified = false;
            _pendingFunnelAuthorizationUrl = null;
            _nextFunnelCapabilityProbeUtc = DateTime.UtcNow.AddSeconds(10);
            var details = string.Join(Environment.NewLine, new[]
            {
                GetOutputValue(setup.Values, "Status"),
                GetOutputValue(setup.Values, "Error"),
                GetOutputValue(setup.Values, "Details"),
                setup.Output,
                setup.ExitCode == 0 ? string.Empty : "PowerShell exit code: " + setup.ExitCode
            }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));

            throw new InvalidOperationException(
                "Не удалось подтвердить разрешение Tailscale Funnel." +
                (string.IsNullOrWhiteSpace(details) ? string.Empty : Environment.NewLine + details));
        }

        EnsureInitialSetupMarker();
        _funnelCapabilityVerified = true;
        _funnelCapabilityVerifiedUntil = DateTime.UtcNow.AddMinutes(5);
        _pendingFunnelAuthorizationUrl = null;
        _nextFunnelCapabilityProbeUtc = DateTime.MinValue;
        return null;
    }

    private static bool TryNormalizeFunnelApprovalUrl(string value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "login.tailscale.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, "/f/funnel", StringComparison.OrdinalIgnoreCase))
            return false;

        var hasNode = false;
        foreach (var parameter in uri.Query.TrimStart('?').Split('&'))
        {
            if (parameter.StartsWith("node=", StringComparison.OrdinalIgnoreCase) &&
                parameter.Length > 5 &&
                !string.IsNullOrWhiteSpace(parameter.Substring(5)))
            {
                hasNode = true;
                break;
            }
        }

        if (!hasNode) return false;
        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private static bool TryNormalizeHttpsSetupUrl(string value, out string normalizedUrl)
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

    private static string? ReadFunnelApprovalUrl(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = Regex.Match(
            output,
            @"https://login\.tailscale\.com/f/funnel\?node=[^\s""'<>]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        var candidate = match.Value.TrimEnd('.', ',', ')', ']');
        return TryNormalizeFunnelApprovalUrl(candidate, out var normalizedUrl)
            ? normalizedUrl
            : null;
    }

    private static string GetOutputValue(System.Collections.Generic.IDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string ReadBackendState(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson)) return string.Empty;
        var match = Regex.Match(
            statusJson,
            "\"BackendState\"\\s*:\\s*\"(?<state>[^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["state"].Value : string.Empty;
    }

    private static string? ReadAuthUrl(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var match = Regex.Match(
            json,
            @"""AuthURL""\s*:\s*""(?<url>(?:\\.|[^""\\])*)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var value = match.Groups["url"].Value
            .Replace("\\/", "/")
            .Replace("\\u0026", "&");

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "login.tailscale.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/a/", StringComparison.OrdinalIgnoreCase))
            return null;

        return uri.AbsoluteUri;
    }

    private static async Task<SetupHelperResult> RunElevatedSetupHelperAsync(
        string mode,
        bool requestElevation = true,
        CancellationToken cancellationToken = default)
    {
        var scriptDirectory = Path.Combine(AppPaths.ConfigDirectory, "Temp");
        Directory.CreateDirectory(scriptDirectory);
        var helperId = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(scriptDirectory, "TailscaleSetup-" + helperId + ".ps1");
        var cancelFile = Path.Combine(scriptDirectory, "TailscaleSetup-" + helperId + ".cancel");
        var msiPath = Path.Combine(AppPaths.DependenciesDirectory, "tailscale-setup.msi");
        if (!File.Exists(msiPath) && Directory.Exists(AppPaths.DependenciesDirectory))
        {
            msiPath = Directory.GetFiles(
                    AppPaths.DependenciesDirectory,
                    "tailscale-setup-*.msi",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault() ?? msiPath;
        }

        var stateFile = Path.Combine(AppPaths.ConfigDirectory, "tailscale-state.txt");
        var outputFile = Path.Combine(AppPaths.ConfigDirectory, "tailscale-helper-" + Guid.NewGuid().ToString("N") + ".txt");
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        using (var embeddedScript = typeof(TailscaleService).Assembly.GetManifestResourceStream("TailscaleSetup.ps1"))
        {
            if (embeddedScript == null)
                throw new FileNotFoundException("В сборке MCPBridge отсутствует EmbeddedResource TailscaleSetup.ps1.");
            using var reader = new StreamReader(embeddedScript, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var scriptContent = reader.ReadToEnd().TrimStart('\uFEFF');
            File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(true));
        }

        if (!File.Exists(msiPath))
        {
            try { File.Delete(scriptPath); } catch { }
            throw new FileNotFoundException("Не найден встроенный Tailscale MSI.", msiPath);
        }

        var arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath +
            "\" -Mode \"" + mode +
            "\" -MsiPath \"" + msiPath +
            "\" -StateFile \"" + stateFile +
            "\" -OutputFile \"" + outputFile + "\" -CancelFile \"" + cancelFile + "\"";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo
            {
                FileName = powerShellPath,
                Arguments = arguments,
                WorkingDirectory = AppPaths.InstallDirectory,
                UseShellExecute = requestElevation,
                Verb = requestElevation ? "runas" : string.Empty,
                // The non-elevated helper must be a fully detached hidden process.
                // CreateNoWindow is effective only with UseShellExecute=false.
                CreateNoWindow = !requestElevation,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = !requestElevation,
                RedirectStandardError = !requestElevation
            };

            // ShellExecute("runas") can wait for the UAC dialog. Never call it
            // on the WPF dispatcher; use a dedicated STA thread because the shell
            // elevation verb is a desktop-shell operation.
            var startSource = new TaskCompletionSource<Process?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var launchThread = new Thread(() =>
            {
                try { startSource.TrySetResult(Process.Start(startInfo)); }
                catch (Exception exception) { startSource.TrySetException(exception); }
            })
            {
                IsBackground = true,
                Name = "MCPBridge Tailscale setup launcher"
            };
            launchThread.SetApartmentState(ApartmentState.STA);
            launchThread.Start();

            var startTask = startSource.Task;
            var launchCanceled = Task.Delay(Timeout.Infinite, cancellationToken);
            if (await Task.WhenAny(startTask, launchCanceled).ConfigureAwait(false) != startTask)
            {
                try { File.WriteAllText(cancelFile, "cancel"); } catch { }
                _ = startTask.ContinueWith(
                    task =>
                    {
                        if (task.IsFaulted)
                        {
                            _ = task.Exception;
                            return;
                        }
                        if (task.Status != TaskStatus.RanToCompletion || task.Result == null)
                            return;
                        try
                        {
                            if (!task.Result.HasExited)
                                task.Result.Kill();
                        }
                        catch { }
                        finally { task.Result.Dispose(); }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
            }

            using var process = await startTask.ConfigureAwait(false);
            if (process == null)
                throw new InvalidOperationException("Не удалось запустить сценарий настройки Tailscale с правами администратора.");

            var stdoutTask = !requestElevation ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
            var stderrTask = !requestElevation ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);

            var helperDeadline = DateTime.UtcNow.AddSeconds(75);
            try
            {
                while (!process.HasExited && DateTime.UtcNow < helperDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                try { File.WriteAllText(cancelFile, "cancel"); } catch { }
                var cleanupDeadline = DateTime.UtcNow.AddSeconds(9);
                while (!process.HasExited && DateTime.UtcNow < cleanupDeadline)
                    await Task.Delay(100).ConfigureAwait(false);
                try { if (!process.HasExited) process.Kill(); process.WaitForExit(2000); } catch { }
                throw;
            }

            if (!process.HasExited)
            {
                try { File.WriteAllText(cancelFile, "timeout"); } catch { }
                var cleanupDeadline = DateTime.UtcNow.AddSeconds(5);
                while (!process.HasExited && DateTime.UtcNow < cleanupDeadline)
                    await Task.Delay(100).ConfigureAwait(false);
                try { if (!process.HasExited) process.Kill(); process.WaitForExit(3000); } catch { }
                throw new System.TimeoutException("Сценарий настройки Tailscale превысил 75 секунд.");
            }

            var consoleOutput = string.Empty;
            if (!requestElevation)
            {
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                    consoleOutput = (stdoutTask.Result + Environment.NewLine + stderrTask.Result).Trim();
                }
                catch { }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var values = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var output = string.Empty;
            if (File.Exists(outputFile))
            {
                var lines = File.ReadAllLines(outputFile);
                output = string.Join(Environment.NewLine, lines);
                foreach (var line in lines)
                {
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(output) && !string.IsNullOrWhiteSpace(consoleOutput))
                output = consoleOutput;

            return new SetupHelperResult(process.ExitCode, values, output);
        }
        finally
        {
            try { if (File.Exists(outputFile)) File.Delete(outputFile); } catch { }
            try { if (File.Exists(cancelFile)) File.Delete(cancelFile); } catch { }
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
        }
    }

    private static async Task<CommandResult> RunCommandAsync(
        string executable,
        string arguments,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = AppPaths.InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Tailscale CLI: " + arguments);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

        try
        {
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); process.WaitForExit(2000); } catch { }
            if (process.HasExited)
            {
                try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }
            }
            throw;
        }

        if (!process.HasExited)
        {
            try { process.Kill(); process.WaitForExit(2000); } catch { }
            if (process.HasExited)
            {
                try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }
            }
            throw new System.TimeoutException("Команда tailscale " + arguments + " превысила " + timeoutMilliseconds / 1000 + " секунд.");
        }
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return new CommandResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static void EnsureSuccess(string operation, CommandResult result)
    {
        if (result.ExitCode == 0) return;
        throw new InvalidOperationException(operation + " завершилось с кодом " + result.ExitCode + "." +
            (string.IsNullOrWhiteSpace(result.CombinedOutput) ? string.Empty : Environment.NewLine + result.CombinedOutput));
    }

    private static bool IsExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TailscaleService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        HostProcessState? state;
        lock (_sync) state = _host;
        if (state == null) return;

        state.ExpectedStop = true;
        try { state.Job.TryTerminateAll(out _); } catch { }
        try
        {
            if (!state.Process.HasExited)
                state.Process.Kill();
        }
        catch { }
    }

    private sealed class HostProcessState
    {
        private readonly object _sync = new();
        private readonly System.Collections.Generic.Queue<string> _output = new();

        public HostProcessState(Process process, WindowsJobObject job)
        {
            Process = process;
            Job = job;
        }

        public Process Process { get; }
        public WindowsJobObject Job { get; }
        public TaskCompletionSource<string> UrlReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> FunnelApprovalUrl { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ExpectedStop;
        public int CleanupStarted;

        public void AddOutput(string line)
        {
            lock (_sync)
            {
                _output.Enqueue(line);
                while (_output.Count > 60) _output.Dequeue();
            }
        }

        public string GetOutput()
        {
            lock (_sync) return string.Join(Environment.NewLine, _output);
        }
    }

    private sealed class SetupHelperResult
    {
        public SetupHelperResult(int exitCode, System.Collections.Generic.Dictionary<string, string> values, string output)
        {
            ExitCode = exitCode;
            Values = values;
            Output = output;
        }

        public int ExitCode { get; }
        public System.Collections.Generic.Dictionary<string, string> Values { get; }
        public string Output { get; }
    }

    private sealed class CommandResult
    {
        public CommandResult(int exitCode, string standardOutput, string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        public int ExitCode { get; }
        public string StandardOutput { get; }
        public string StandardError { get; }
        public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
    }
}