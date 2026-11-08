[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'PrepareOnline', 'Activate', 'Uninstall')]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,
    [Parameter(Mandatory = $true)]
    [string]$StateFile,
    [Parameter(Mandatory = $true)]
    [string]$OutputFile,
    [Parameter(Mandatory = $false)]
    [string]$CancelFile = '',
    [Parameter(Mandatory = $false)]
    [switch]$ForceRepair
)

$ErrorActionPreference = 'Stop'
$wasInstalled = $null

function Test-CancelRequested {
    if (-not [string]::IsNullOrWhiteSpace($CancelFile) -and
        (Test-Path -LiteralPath $CancelFile -PathType Leaf)) {
        throw [System.OperationCanceledException]::new('Операция Tailscale отменена пользователем.')
    }
}

function Write-Result([string[]]$Lines) {
    $directory = Split-Path -Parent $OutputFile
    if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [IO.File]::WriteAllLines($OutputFile, $Lines, [Text.UTF8Encoding]::new($true))
}
function Test-TailscaleMsiAuthenticodeSignature([string]$Path) {
    # Verify the embedded Authenticode signature through the Windows trust
    # provider directly, without auto-loading Microsoft.PowerShell.Security.
    if (-not ('MCPBridgeWinTrustVerifier' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MCPBridgeWinTrustVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public UInt32 cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public UInt32 cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public UInt32 dwUIChoice;
        public UInt32 fdwRevocationChecks;
        public UInt32 dwUnionChoice;
        public IntPtr pFile;
        public UInt32 dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public UInt32 dwProvFlags;
        public UInt32 dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern UInt32 WinVerifyTrust(
        IntPtr hwnd,
        ref Guid pgActionID,
        ref WinTrustData pWVTData);

    public static UInt32 VerifyFile(string path)
    {
        IntPtr pathPointer = IntPtr.Zero;
        IntPtr fileInfoPointer = IntPtr.Zero;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(path);

            WinTrustFileInfo fileInfo = new WinTrustFileInfo();
            fileInfo.cbStruct = (UInt32)Marshal.SizeOf(typeof(WinTrustFileInfo));
            fileInfo.pcwszFilePath = pathPointer;

            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            WinTrustData data = new WinTrustData();
            data.cbStruct = (UInt32)Marshal.SizeOf(typeof(WinTrustData));
            data.dwUIChoice = 2;          // WTD_UI_NONE
            data.fdwRevocationChecks = 1; // WTD_REVOKE_WHOLECHAIN
            data.dwUnionChoice = 1;       // WTD_CHOICE_FILE
            data.pFile = fileInfoPointer;
            data.dwStateAction = 1;       // WTD_STATEACTION_VERIFY

            Guid action = GenericVerifyV2;
            UInt32 result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = 2;       // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPointer);
            if (pathPointer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pathPointer);
        }
    }
}
'@
    }

    $trustResult = [MCPBridgeWinTrustVerifier]::VerifyFile($Path)
    if ($trustResult -ne 0) {
        throw ('Tailscale MSI не прошёл системную проверку Authenticode (WinVerifyTrust: 0x{0:X8}): {1}' -f $trustResult, $Path)
    }

    $signer = $null
    $certificate = $null
    try {
        $signer = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($Path)
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($signer)
        if ([string]$certificate.Subject -notmatch 'Tailscale') {
            throw ('Подписант MSI не распознан как Tailscale. Сертификат: {0}' -f $certificate.Subject)
        }
    }
    finally {
        if ($certificate) { $certificate.Dispose() }
        if ($signer) { $signer.Dispose() }
    }
}


function Get-TailscalePaths {
    $directories = [System.Collections.Generic.List[string]]::new()
    $serviceDaemonPath = $null

    # The service path is the best source for custom install locations, but a
    # stale ImagePath must not prevent checking the standard and registered paths.
    try {
        $serviceKey = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Tailscale' -ErrorAction Stop
        $imagePath = [Environment]::ExpandEnvironmentVariables([string]$serviceKey.ImagePath)
        if ($imagePath -match '^\s*"(?<path>[^"]*tailscaled\.exe)"' -or
            $imagePath -match '^\s*(?<path>.+?tailscaled\.exe)(?:\s|$)') {
            $serviceDaemonPath = $Matches.path.Trim('"')
            $serviceDirectory = Split-Path -Parent $serviceDaemonPath
            if (-not [string]::IsNullOrWhiteSpace($serviceDirectory)) {
                $directories.Add($serviceDirectory)
            }
        }
    }
    catch { }

    # Read both registry views. Tailscale's InstallLocation can differ from the
    # default Program Files location and is useful when the service path is stale.
    foreach ($uninstallRoot in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $uninstallRoot)) { continue }
        foreach ($entryKey in (Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue)) {
            $entry = Get-ItemProperty -LiteralPath $entryKey.PSPath -ErrorAction SilentlyContinue
            if ([string]$entry.DisplayName -match '^Tailscale(?:\s|$)') {
                $installLocation = [Environment]::ExpandEnvironmentVariables([string]$entry.InstallLocation)
                if (-not [string]::IsNullOrWhiteSpace($installLocation)) {
                    $directories.Add($installLocation.Trim('"'))
                }
            }
        }
    }

    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'Tailscale'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Tailscale'),
        (Join-Path $env:LOCALAPPDATA 'Tailscale')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $directories.Add($candidate)
        }
    }

    $uniqueDirectories = @($directories |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object {
            try { [IO.Path]::GetFullPath($_) } catch { $null }
        } |
        Where-Object { $_ } |
        Select-Object -Unique)

    # Prefer a directory containing the CLI. Do not trust ImagePath alone.
    foreach ($directory in $uniqueDirectories) {
        $cliPath = Join-Path $directory 'tailscale.exe'
        if (Test-Path -LiteralPath $cliPath -PathType Leaf) {
            return @{
                Directory = $directory
                Cli = $cliPath
                Gui = Join-Path $directory 'tailscale-ipn.exe'
                ServiceDaemonPath = $serviceDaemonPath
            }
        }
    }

    # If the CLI is missing, retain the directory containing another Tailscale
    # binary so the installation check can report a damaged/partial install.
    foreach ($directory in $uniqueDirectories) {
        foreach ($binaryName in @('tailscaled.exe', 'tailscale-ipn.exe')) {
            if (Test-Path -LiteralPath (Join-Path $directory $binaryName) -PathType Leaf) {
                return @{
                    Directory = $directory
                    Cli = Join-Path $directory 'tailscale.exe'
                    Gui = Join-Path $directory 'tailscale-ipn.exe'
                    ServiceDaemonPath = $serviceDaemonPath
                }
            }
        }
    }

    $defaultDirectory = Join-Path $env:ProgramFiles 'Tailscale'
    return @{
        Directory = $defaultDirectory
        Cli = Join-Path $defaultDirectory 'tailscale.exe'
        Gui = Join-Path $defaultDirectory 'tailscale-ipn.exe'
        ServiceDaemonPath = $serviceDaemonPath
    }
}

function Get-TailscaleInstallation {
    $paths = Get-TailscalePaths
    $cliExists = Test-Path -LiteralPath $paths.Cli -PathType Leaf
    $serviceRegistered = Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Tailscale'

    # The service registry key is authoritative even if the service is stopped.
    # Also query SCM in case registry access reports a view/path anomaly.
    try {
        $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='Tailscale'" -ErrorAction Stop
        if ($serviceInfo) { $serviceRegistered = $true }
    }
    catch { }

    $msiRegistered = $false
    foreach ($uninstallRoot in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $uninstallRoot)) { continue }
        foreach ($entryKey in (Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue)) {
            $entry = Get-ItemProperty -LiteralPath $entryKey.PSPath -ErrorAction SilentlyContinue
            if ([string]$entry.DisplayName -match '^Tailscale(?:\s|$)') {
                $msiRegistered = $true
                break
            }
        }
        if ($msiRegistered) { break }
    }

    $otherBinaryExists = $false
    foreach ($binaryName in @('tailscaled.exe', 'tailscale-ipn.exe')) {
        if (Test-Path -LiteralPath (Join-Path $paths.Directory $binaryName) -PathType Leaf) {
            $otherBinaryExists = $true
            break
        }
    }

    $serviceBinaryExists = $false
    $serviceDirectoryMatchesCli = $false
    if (-not [string]::IsNullOrWhiteSpace([string]$paths.ServiceDaemonPath)) {
        $serviceBinaryExists = Test-Path -LiteralPath $paths.ServiceDaemonPath -PathType Leaf
        $serviceDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $paths.ServiceDaemonPath))
        $cliDirectory = [IO.Path]::GetFullPath($paths.Directory)
        $serviceDirectoryMatchesCli = [string]::Equals(
            $serviceDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            $cliDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            [StringComparison]::OrdinalIgnoreCase)
    }

    if ($cliExists -and $serviceRegistered -and
        ([string]::IsNullOrWhiteSpace([string]$paths.ServiceDaemonPath) -or
            ($serviceBinaryExists -and $serviceDirectoryMatchesCli))) {
        return @{
            Installed = $true
            Paths = $paths
        }
    }

    if ($serviceRegistered -or $cliExists -or $msiRegistered -or $otherBinaryExists) {
        $evidence = [System.Collections.Generic.List[string]]::new()
        if ($serviceRegistered) { $evidence.Add('зарегистрирована служба Windows Tailscale') }
        if ($cliExists) { $evidence.Add('найден tailscale.exe') }
        if ($otherBinaryExists) { $evidence.Add('найдены бинарники Tailscale без полного CLI') }
        if ($msiRegistered) { $evidence.Add('найдена регистрация продукта Tailscale в Windows Installer/Programs and Features') }

        throw ('Обнаружена неполная или противоречивая установка Tailscale (' +
            ($evidence -join '; ') +
            '). MSI повторно не запускается, чтобы не повредить существующую установку. Восстановите или удалите Tailscale штатным установщиком и повторите установку MCP Bridge.')
    }

    return @{
        Installed = $false
        Paths = $paths
    }
}

function Wait-Service([string]$ExpectedStatus, [int]$TimeoutSeconds = 45) {
    Test-CancelRequested
    $service = Get-Service -Name 'Tailscale' -ErrorAction Stop
    if ($ExpectedStatus -eq 'Running') {
        if ($service.Status -eq 'StopPending') {
            $stopDeadline = [DateTime]::UtcNow.AddSeconds(12)
            do {
                Test-CancelRequested
                Start-Sleep -Milliseconds 200
                $service = Get-Service -Name 'Tailscale'
            } while ($service.Status -eq 'StopPending' -and [DateTime]::UtcNow -lt $stopDeadline)

            if ($service.Status -eq 'StopPending') {
                throw 'Служба Tailscale не завершила остановку за 12 секунд.'
            }
        }

        if ($service.Status -eq 'Paused') {
            Resume-Service -Name 'Tailscale'
        }
        elseif ($service.Status -eq 'Stopped') {
            Start-Service -Name 'Tailscale'
        }
    }
    elseif ($ExpectedStatus -eq 'Stopped' -and
        $service.Status -notin @('Stopped', 'StopPending')) {
        Stop-Service -Name 'Tailscale' -Force
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Test-CancelRequested
        $service = Get-Service -Name 'Tailscale'
        if ([string]$service.Status -eq $ExpectedStatus) { return }
        Start-Sleep -Milliseconds 350
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Служба Tailscale не перешла в состояние $ExpectedStatus."
}

function Stop-TailscaleGui {
    $sessionId = (Get-Process -Id $PID).SessionId
    Get-Process -Name 'tailscale-ipn' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $sessionId } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Stop-TailscaleService {
    $service = Get-Service -Name 'Tailscale' -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name 'Tailscale' -Force
        (Get-Service -Name 'Tailscale').WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

function Invoke-TailscaleCommand(
    [string]$Cli,
    [string]$Arguments,
    [int]$TimeoutSeconds = 8,
    [switch]$IgnoreCancellation
) {
    if (-not $IgnoreCancellation) { Test-CancelRequested }
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Cli
    $startInfo.Arguments = $Arguments
    $startInfo.WorkingDirectory = Split-Path -Parent $Cli
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Не удалось запустить tailscale.exe: $Arguments" }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill(); $process.WaitForExit(2000) } catch { }
        $process.Dispose()
        throw "Команда tailscale $Arguments превысила $TimeoutSeconds секунд."
    }

    $process.WaitForExit()
    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        StandardOutput = $stdoutTask.GetAwaiter().GetResult()
        StandardError = $stderrTask.GetAwaiter().GetResult()
    }
    $process.Dispose()
    if (-not $IgnoreCancellation) { Test-CancelRequested }
    $result | Add-Member -NotePropertyName Output -NotePropertyValue (($result.StandardOutput + [Environment]::NewLine + $result.StandardError).Trim())
    return $result
}

function Get-TailscaleBooleanSetting([string]$Cli, [string]$Name) {
    $result = Invoke-TailscaleCommand $Cli ('get ' + $Name) 8
    $value = ([string]$result.StandardOutput).Trim().ToLowerInvariant()
    if ($result.ExitCode -ne 0 -or $value -notin @('true', 'false')) {
        throw "Не удалось проверить параметр Tailscale $Name. $($result.Output)"
    }
    return $value
}

function Test-FunnelCapability([string]$Cli) {
    # Prefer the already configured Bridge endpoint. Reading status is safe:
    # it does not create, replace, or remove any Funnel/Serve listener.
    $existingListenerFound = $false
    try {
        $existingStatus = Invoke-TailscaleCommand $Cli 'funnel status --json' 5
        if ($existingStatus.ExitCode -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($existingStatus.StandardOutput)) {
            $existingJson = $existingStatus.StandardOutput | ConvertFrom-Json -ErrorAction Stop
            foreach ($nodeProperty in $existingJson.Foreground.PSObject.Properties) {
                $nodeConfig = $nodeProperty.Value
                $tcp443Property = $nodeConfig.TCP.PSObject.Properties |
                    Where-Object { $_.Name -eq '443' } | Select-Object -First 1
                if ($tcp443Property -and $tcp443Property.Value.HTTPS -eq $true) {
                    $existingListenerFound = $true
                }

                foreach ($webProperty in $nodeConfig.Web.PSObject.Properties) {
                    if ($webProperty.Name -notmatch '^(?<host>[a-z0-9.-]+\.ts\.net):443$') { continue }
                    $hostName = $Matches.host
                    $allowProperty = $nodeConfig.AllowFunnel.PSObject.Properties |
                        Where-Object { $_.Name -eq $webProperty.Name } | Select-Object -First 1
                    $handlerProperty = $webProperty.Value.Handlers.PSObject.Properties |
                        Where-Object { $_.Name -eq '/mcp' } | Select-Object -First 1
                    if (-not $tcp443Property -or $tcp443Property.Value.HTTPS -ne $true -or
                        -not $allowProperty -or $allowProperty.Value -ne $true -or
                        -not $handlerProperty) { continue }

                    $proxyUri = $null
                    $proxyText = [string]$handlerProperty.Value.Proxy
                    if (-not [Uri]::TryCreate($proxyText, [UriKind]::Absolute, [ref]$proxyUri) -or
                        $proxyUri.Scheme -ne 'http' -or $proxyUri.Host -ne '127.0.0.1' -or
                        $proxyUri.Port -notin @(8001, 8004)) { continue }

                    return [pscustomobject]@{
                        Ready = $true
                        AuthorizationUrl = ''
                        HttpsSetupUrl = ''
                        PublicUrl = 'https://' + $hostName
                        Details = 'Reused existing Funnel endpoint /mcp -> ' + $proxyText + '; no listener was changed.'
                    }
                }
            }
        }
    }
    catch {
        # If status cannot be parsed, retain the original bootstrap path below.
    }

    # Never start a second HTTPS listener on port 443. If another Funnel/Serve
    # listener already owns it, report the mismatch without changing its config.
    if ($existingListenerFound) {
        return [pscustomobject]@{
            Ready = $false
            AuthorizationUrl = ''
            HttpsSetupUrl = ''
            PublicUrl = ''
            Details = 'Порт HTTPS 443 уже настроен, но действующий /mcp с разрешённым Funnel и локальным proxy 127.0.0.1:8001 или :8004 не найден. Существующая конфигурация не изменялась.'
        }
    }

    $id = [Guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path $env:TEMP ('MCPBridge-FunnelProbe-' + $id + '-funnel.out')
    $stderrPath = Join-Path $env:TEMP ('MCPBridge-FunnelProbe-' + $id + '-funnel.err')
    $probePath = '/mcpbridge-test-' + $id.Substring(0, 8)

    $funnelProcess = $null
    $publicUrl = ''
    $authorizationUrl = ''
    $httpsSetupUrl = ''
    $output = ''
    $routeConfigured = $false
    $processExitCode = $null

    try {
        # Keep this URL-discovery phase identical to the proven standalone
        # PowerShell procedure: launch once, capture stdout/stderr, and poll
        # only those files. Do not call "funnel status" while waiting for URL.
        $funnelArguments = "funnel --yes --https=443 --set-path=$probePath http://127.0.0.1:8004"
        $funnelProcess = Start-Process -FilePath $Cli -ArgumentList $funnelArguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            Test-CancelRequested
            Start-Sleep -Milliseconds 300

            $output = (Get-Content $stdoutPath, $stderrPath -Raw -ErrorAction SilentlyContinue) -join "`n"

            $authMatch = [regex]::Match(
                $output,
                'https://login\.tailscale\.com/f/funnel\?node=[^\s"''<>]+',
                [Text.RegularExpressions.RegexOptions]::IgnoreCase
            )

            if ($authMatch.Success) {
                $authorizationUrl = $authMatch.Value.TrimEnd('.', ',', ')', ']')
                break
            }

            # Current Tailscale CLI confirms a successful publication with:
            # "Available on the internet: https://<host>/<path> |-- proxy http://127.0.0.1:8004"
            # This live CLI response is authoritative; status --json is not
            # required to repeat the same information in a particular schema.
            $publicMatch = [regex]::Match(
                $output,
                '(?im)Available on the internet:\s*(?<url>https://[a-z0-9.-]+\.ts\.net/[^\s|]+)\s*\|--\s*proxy\s*http://127\.0\.0\.1:8004',
                [Text.RegularExpressions.RegexOptions]::IgnoreCase
            )

            if ($publicMatch.Success) {
                $candidatePublicUri = $null
                if ([Uri]::TryCreate($publicMatch.Groups['url'].Value.TrimEnd('.', ',', ')', ']'), [UriKind]::Absolute, [ref]$candidatePublicUri) -and
                    $candidatePublicUri.AbsolutePath.TrimEnd('/') -eq $probePath) {
                    $publicUrl = $candidatePublicUri.GetLeftPart([UriPartial]::Authority)
                    $routeConfigured = $true
                    break
                }
            }
        } while (-not $funnelProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)

        if ($funnelProcess.HasExited) {
            $processExitCode = $funnelProcess.ExitCode
        }
        else {
            Stop-Process -Id $funnelProcess.Id -Force -ErrorAction SilentlyContinue
            try { $funnelProcess.WaitForExit(3000) } catch { }
        }

        # Final read after stopping/waiting, exactly as in the successful
        # standalone script, so buffered output is not lost.
        $output = (Get-Content $stdoutPath, $stderrPath -Raw -ErrorAction SilentlyContinue) -join "`n"

        if ([string]::IsNullOrWhiteSpace($authorizationUrl)) {
            $authMatch = [regex]::Match(
                $output,
                'https://login\.tailscale\.com/f/funnel\?node=[^\s"''<>]+',
                [Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
            if ($authMatch.Success) {
                $authorizationUrl = $authMatch.Value.TrimEnd('.', ',', ')', ']')
            }
        }

        if ($authorizationUrl) {
            return [pscustomobject]@{
                Ready = $false
                AuthorizationUrl = $authorizationUrl
                HttpsSetupUrl = ''
                PublicUrl = ''
                Details = $output.Trim()
            }
        }

        # The CLI's successful publication message already proves that the
        # unique probe path is publicly reachable through the expected local
        # proxy. Do not reject it because funnel status --json uses a different
        # structure or omits fields used by older Tailscale versions.
        if ($publicUrl) {
            return [pscustomobject]@{
                Ready = $true
                AuthorizationUrl = ''
                HttpsSetupUrl = ''
                PublicUrl = $publicUrl
                Details = 'Tailscale CLI confirmed the public HTTPS URL and the exact temporary path/proxy.'
            }
        }

        # Tailscale reports the prerequisite as a documentation URL in the
        # Funnel CLI error. Return that exact URL to the connection manager
        # instead of treating it as an empty Funnel authorization response.
        $httpsMatch = [regex]::Match(
            $output,
            'https://tailscale\.com/s/https[^\s"''<>]*',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase
        )
        if ($httpsMatch.Success -and
            $output -match '(?i)Funnel not available; HTTPS must be enabled') {
            $httpsSetupUrl = $httpsMatch.Value.TrimEnd('.', ',', ')', ']')
            return [pscustomobject]@{
                Ready = $false
                AuthorizationUrl = ''
                HttpsSetupUrl = $httpsSetupUrl
                PublicUrl = ''
                Details = $output.Trim()
            }
        }

        # Only after the exact URL-discovery attempt failed, check whether
        # Funnel was already authorized and the requested path became active.
        try {
            $funnelStatus = Invoke-TailscaleCommand $Cli 'funnel status --json' 5
            if ($funnelStatus.ExitCode -eq 0 -and
                -not [string]::IsNullOrWhiteSpace($funnelStatus.StandardOutput) -and
                $funnelStatus.StandardOutput.Contains($probePath)) {

                $publicHost = ''
                $deviceStatus = Invoke-TailscaleCommand $Cli 'status --json' 5
                if ($deviceStatus.ExitCode -eq 0 -and
                    -not [string]::IsNullOrWhiteSpace($deviceStatus.StandardOutput)) {
                    $deviceJson = $deviceStatus.StandardOutput | ConvertFrom-Json -ErrorAction Stop
                    $publicHost = ([string]$deviceJson.Self.DNSName).Trim().TrimEnd('.')
                }

                if (-not $publicHost) {
                    $hostMatch = [regex]::Match(
                        $funnelStatus.StandardOutput,
                        '"(?<host>[a-z0-9.-]+\.ts\.net):443"\s*:',
                        [Text.RegularExpressions.RegexOptions]::IgnoreCase
                    )
                    if ($hostMatch.Success) {
                        $publicHost = $hostMatch.Groups['host'].Value
                    }
                }

                if ($publicHost) {
                    $allowPattern = '"' + [regex]::Escape($publicHost) + ':443"\s*:\s*true'
                    $httpsPattern = '"443"\s*:\s*\{\s*"HTTPS"\s*:\s*true'
                    $allowConfirmed = [regex]::IsMatch(
                        $funnelStatus.StandardOutput,
                        $allowPattern,
                        [Text.RegularExpressions.RegexOptions]::IgnoreCase
                    )
                    $httpsConfirmed = [regex]::IsMatch(
                        $funnelStatus.StandardOutput,
                        $httpsPattern,
                        [Text.RegularExpressions.RegexOptions]::IgnoreCase
                    )

                    if ($allowConfirmed -and $httpsConfirmed) {
                        $publicUrl = 'https://' + $publicHost
                        $routeConfigured = $true
                    }
                }
            }
        }
        catch {
            # Preserve the original CLI response as the primary diagnostic.
        }

        if (-not $publicUrl) {
            $exitText = if ($null -ne $processExitCode) {
                "Код выхода: $processExitCode."
            } else {
                'Funnel status не подтвердил публичный URL, нужный путь и AllowFunnel=true.'
            }

            return [pscustomobject]@{
                Ready = $false
                AuthorizationUrl = ''
                HttpsSetupUrl = ''
                PublicUrl = ''
                Details = ($exitText + [Environment]::NewLine + $output).Trim()
            }
        }

        return [pscustomobject]@{
            Ready = $true
            AuthorizationUrl = ''
            HttpsSetupUrl = ''
            PublicUrl = $publicUrl
            Details = 'Funnel status confirms the public HTTPS hostname, active probe path and AllowFunnel permission.'
        }
    }
    finally {
        if ($funnelProcess) {
            try {
                if (-not $funnelProcess.HasExited) {
                    Stop-Process -Id $funnelProcess.Id -Force -ErrorAction SilentlyContinue
                    $funnelProcess.WaitForExit(3000)
                }
            }
            catch { }

            $funnelProcess.Dispose()
        }

        # Remove only the unique temporary route, and only if it was verified active.
        if ($routeConfigured) {
            try {
                Invoke-TailscaleCommand $Cli ('funnel --https=443 --set-path=' + $probePath + ' off') 5 -IgnoreCancellation | Out-Null
            }
            catch { }
        }

        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Restore-PreexistingTailscale([string]$StateText) {
    $paths = Get-TailscalePaths
    if (-not (Test-Path -LiteralPath $paths.Cli -PathType Leaf)) {
        throw 'Не найден tailscale.exe для восстановления исходных настроек.'
    }

    $previousAcceptDns = [regex]::Match($StateText, '(?m)^PreviousAcceptDns=(true|false)$').Groups[1].Value
    $previousUnattended = [regex]::Match($StateText, '(?m)^PreviousUnattended=(true|false)$').Groups[1].Value
    if ($previousAcceptDns -or $previousUnattended) {
        Wait-Service 'Running'
        if ($previousAcceptDns) {
            & $paths.Cli set ('--accept-dns=' + $previousAcceptDns)
            if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить исходное значение accept-dns.' }
        }
        if ($previousUnattended) {
            & $paths.Cli set ('--unattended=' + $previousUnattended)
            if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить исходное значение unattended mode.' }
        }
    }

    $previousMode = [regex]::Match($StateText, '(?m)^PreviousStartMode=(.*)$').Groups[1].Value.Trim()
    switch ($previousMode.ToLowerInvariant()) {
        'auto' { Set-Service -Name 'Tailscale' -StartupType Automatic }
        'automatic' { Set-Service -Name 'Tailscale' -StartupType Automatic }
        'manual' { Set-Service -Name 'Tailscale' -StartupType Manual }
        'disabled' { Set-Service -Name 'Tailscale' -StartupType Disabled }
        default { Set-Service -Name 'Tailscale' -StartupType Manual }
    }

    $previousStatus = [regex]::Match($StateText, '(?m)^PreviousServiceStatus=(.*)$').Groups[1].Value.Trim()
    if ($previousStatus -ne 'Running') {
        Stop-TailscaleService
    }
    elseif ((Get-Service -Name 'Tailscale').Status -ne 'Running') {
        Start-Service -Name 'Tailscale'
        (Get-Service -Name 'Tailscale').WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    }

    $encoded = [regex]::Match($StateText, '(?m)^PreviousSddlBase64=(.*)$').Groups[1].Value.Trim()
    if ($encoded) {
        $previousSddl = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded))
        & sc.exe sdset Tailscale $previousSddl
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить исходный ACL службы Tailscale.' }
    }
}

try {
    if ($Mode -eq 'Install') {
        # Decide from the installed system state before validating or invoking MSI.
        # A complete existing installation is reused; partial registrations are
        # reported rather than being overwritten by a second MSI installation.
        $installation = Get-TailscaleInstallation
        $before = $installation.Paths
        $wasInstalled = [bool]$installation.Installed

        if (-not $wasInstalled) {
            if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
                throw "Не найден встроенный Tailscale MSI: $MsiPath"
            }
            Test-TailscaleMsiAuthenticodeSignature -Path $MsiPath
        }

        $managedMsiPath = Join-Path (Split-Path -Parent $MsiPath) 'tailscale-installed.msi'
        $installDirectory = $before.Directory

        if (-not (Test-Path -LiteralPath $StateFile -PathType Leaf)) {
            $stateDirectory = Split-Path -Parent $StateFile
            if ($stateDirectory) { New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null }

            $previousMode = 'Unknown'
            $previousStatus = 'Unknown'
            $previousSddl = ''
            if ($wasInstalled) {
                $existingService = Get-Service -Name 'Tailscale' -ErrorAction Stop
                $previousStatus = [string]$existingService.Status
                $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='Tailscale'" -ErrorAction Stop
                if (-not $serviceInfo) {
                    throw 'Tailscale CLI найден, но служба Tailscale не зарегистрирована.'
                }
                $previousMode = [string]$serviceInfo.StartMode

                $sddlOutput = & sc.exe sdshow Tailscale 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Не удалось сохранить исходный ACL службы: $($sddlOutput -join ' ')" }
                $previousSddl = [string]($sddlOutput | Where-Object { $_ -match 'D:' } | Select-Object -Last 1)
                if ([string]::IsNullOrWhiteSpace($previousSddl)) {
                    throw 'Не удалось получить исходный ACL службы Tailscale.'
                }
            }

            $installedByBridge = if ($wasInstalled) { '0' } else { '1' }
            $encodedSddl = if ($previousSddl) {
                [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($previousSddl))
            } else { '' }

            @(
                ('InstalledByMcpBridge=' + $installedByBridge),
                ('PreviousStartMode=' + $previousMode),
                ('PreviousServiceStatus=' + $previousStatus),
                ('PreviousSddlBase64=' + $encodedSddl)
            ) | Set-Content -LiteralPath $StateFile -Encoding ASCII
        }

        if (-not $wasInstalled) {
            Copy-Item -LiteralPath $MsiPath -Destination $managedMsiPath -Force
            $logPath = Join-Path $env:TEMP 'MCPBridge-tailscale-msi.log'
            $arguments = '/i "' + $MsiPath + '" /qn /norestart TS_NOLAUNCH=1 INSTALLDIR="' + $installDirectory + '" /L*v "' + $logPath + '"'
            $msi = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
            if ($msi.ExitCode -notin @(0, 3010, 1641)) {
                Remove-Item -LiteralPath $managedMsiPath -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $StateFile -Force -ErrorAction SilentlyContinue
                throw "Установка Tailscale MSI завершилась с кодом $($msi.ExitCode). Журнал: $logPath"
            }
        }
        else {
            # Reuse the existing user's Tailscale installation and account state.
            $installDirectory = $before.Directory
            $existingState = if (Test-Path -LiteralPath $StateFile -PathType Leaf) {
                Get-Content -LiteralPath $StateFile -Raw
            } else { '' }
            $bridgeAlreadyConfigured = $existingState -match '(?m)^TailscaleSettingsApplied=1$' -or
                $existingState -match '(?m)^BridgeSetupComplete=1$'

            # A previous Bridge version may have stopped a pre-existing service
            # during an upgrade. If online setup was never completed, restore the
            # startup mode and running state captured before Bridge took ownership.
            if (-not $bridgeAlreadyConfigured) {
                $previousMode = [regex]::Match($existingState, '(?m)^PreviousStartMode=(.*)$').Groups[1].Value.Trim()
                switch ($previousMode.ToLowerInvariant()) {
                    'auto' { Set-Service -Name 'Tailscale' -StartupType Automatic }
                    'automatic' { Set-Service -Name 'Tailscale' -StartupType Automatic }
                    'manual' { Set-Service -Name 'Tailscale' -StartupType Manual }
                    'disabled' { Set-Service -Name 'Tailscale' -StartupType Disabled }
                }

                $previousStatus = [regex]::Match($existingState, '(?m)^PreviousServiceStatus=(.*)$').Groups[1].Value.Trim()
                $currentService = Get-Service -Name 'Tailscale' -ErrorAction Stop
                if ($previousStatus -eq 'Running' -and $currentService.Status -ne 'Running') {
                    Start-Service -Name 'Tailscale'
                    Wait-Service 'Running'
                }
                # Do not stop a service that is currently running: it may already
                # hold the user's authenticated tailnet session.
            }
        }

        if ($wasInstalled -and $ForceRepair) {
            Stop-TailscaleGui
            Test-TailscaleMsiAuthenticodeSignature -Path $MsiPath

            $stateText = if (Test-Path -LiteralPath $StateFile -PathType Leaf) {
                Get-Content -LiteralPath $StateFile -Raw
            } else { '' }
            if ($stateText -match '(?m)^InstalledByMcpBridge=1$') {
                Copy-Item -LiteralPath $MsiPath -Destination $managedMsiPath -Force
            }

            $repairLogPath = Join-Path $env:TEMP 'MCPBridge-tailscale-msi-repair.log'
            $repairArguments = '/i "' + $MsiPath + '" REINSTALL=ALL REINSTALLMODE=vomus TS_NOLAUNCH=1 /qn /norestart /L*v "' + $repairLogPath + '"'
            $repair = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $repairArguments -PassThru -Wait -WindowStyle Hidden
            if ($repair.ExitCode -notin @(0, 3010, 1641)) {
                throw "Восстановление/переустановка Tailscale MSI завершилось с кодом $($repair.ExitCode). Журнал: $repairLogPath"
            }
        }

        $paths = Get-TailscalePaths
        if (-not (Test-Path -LiteralPath $paths.Cli -PathType Leaf)) {
            throw "После установки не найден tailscale.exe: $($paths.Cli)"
        }

        # MCP Bridge does not take ownership of the Windows service.
        # Preserve the Tailscale installer’s startup mode, ACL and running state.
        $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='Tailscale'" -ErrorAction Stop
        if (-not $serviceInfo) {
            throw 'Не удалось прочитать состояние службы Tailscale.'
        }

        Write-Result @(
            'Installed=1',
            ('Cli=' + $paths.Cli),
            ('Gui=' + $paths.Gui),
            ('Service=' + [string](Get-Service -Name 'Tailscale').Status),
            ('ServiceStartup=' + $serviceInfo.StartMode),
            'ClientLaunched=0',
            'Connected=0',
            'OnlineSetup=Pending',
            ('PreExisting=' + [string]$wasInstalled)
        )
        exit 0
    }

    if ($Mode -eq 'PrepareOnline') {
        $paths = Get-TailscalePaths
        if (-not (Test-Path -LiteralPath $paths.Cli -PathType Leaf)) {
            throw 'Tailscale не установлен или его CLI не найден.'
        }

        # Start the existing service only when it is stopped. Preserve its
        # startup mode, service ACL and authenticated Tailscale state.
        Wait-Service 'Running' 25
        $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='Tailscale'" -ErrorAction Stop
        if (-not $serviceInfo) {
            throw 'Не удалось прочитать состояние службы Tailscale.'
        }

        Write-Result @(
            'Prepared=1',
            ('ServiceStartup=' + $serviceInfo.StartMode),
            'Service=Running',
            'ClientLaunched=0',
            'Connected=0',
            'AccountSettingsChanged=0'
        )
        exit 0
    }

    if ($Mode -eq 'Activate') {
        $paths = Get-TailscalePaths
        if (-not (Test-Path -LiteralPath $paths.Cli -PathType Leaf)) {
            throw 'Tailscale не установлен или его CLI не найден.'
        }

        $service = Get-Service -Name 'Tailscale' -ErrorAction Stop
        if ($service.Status -ne 'Running') {
            throw 'Служба Tailscale остановлена. Повторите подключение, чтобы Bridge запустил её с нужными правами.'
        }

        $ipOutput = & $paths.Cli ip -4 2>&1
        $ipExit = $LASTEXITCODE
        $ipAddress = [string]($ipOutput | Select-Object -First 1)
        if ($ipExit -ne 0 -or $ipAddress -notmatch '^100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}$') {
            Write-Result @(
                'Connected=0',
                'Status=Устройство ещё не подключено. Войдите в аккаунт Tailscale, нажмите Connect и повторите проверку.'
            )
            exit 10
        }

        $stateText = if (Test-Path -LiteralPath $StateFile -PathType Leaf) { Get-Content -LiteralPath $StateFile -Raw } else { '' }
        $settingsApplied = $stateText -match '(?m)^TailscaleSettingsApplied=1$'

        if (-not $settingsApplied) {
            if ($stateText -notmatch '(?m)^PreviousAcceptDns=') {
                $previousAcceptDns = Get-TailscaleBooleanSetting $paths.Cli 'accept-dns'
                Add-Content -LiteralPath $StateFile -Value ('PreviousAcceptDns=' + $previousAcceptDns) -Encoding ASCII
            }
            if ($stateText -notmatch '(?m)^PreviousUnattended=') {
                $previousUnattended = Get-TailscaleBooleanSetting $paths.Cli 'unattended'
                Add-Content -LiteralPath $StateFile -Value ('PreviousUnattended=' + $previousUnattended) -Encoding ASCII
            }

            $dnsSet = Invoke-TailscaleCommand $paths.Cli 'set --accept-dns=false' 8
            if ($dnsSet.ExitCode -ne 0) {
                throw 'Не удалось зафиксировать запрет на изменение системного DNS (accept-dns=false). ' + $dnsSet.Output
            }
            $unattendedSet = Invoke-TailscaleCommand $paths.Cli 'set --unattended=true' 8
            if ($unattendedSet.ExitCode -ne 0) {
                throw 'Tailscale не смог включить unattended mode. ' + $unattendedSet.Output
            }

            $dnsValue = Get-TailscaleBooleanSetting $paths.Cli 'accept-dns'
            $unattendedValue = Get-TailscaleBooleanSetting $paths.Cli 'unattended'
            if ($dnsValue -ne 'false') { throw 'Проверка accept-dns завершилась неудачно: ожидается false.' }
            if ($unattendedValue -ne 'true') { throw 'Проверка unattended mode завершилась неудачно: ожидается true.' }

            # Tailscale set applies these preferences without restarting the daemon.
            # Keep the existing service and authenticated tailnet session running.

            $reconnectedOutput = & $paths.Cli ip -4 2>&1
            $reconnectedExit = $LASTEXITCODE
            $reconnectedIp = [string]($reconnectedOutput | Select-Object -First 1)
            if ($reconnectedExit -ne 0 -or $reconnectedIp -notmatch '^100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}$') {
                throw 'После применения параметров Tailscale соединение временно недоступно. ' + ($reconnectedOutput -join ' ')
            }

            Add-Content -LiteralPath $StateFile -Value 'TailscaleSettingsApplied=1' -Encoding ASCII
            $settingsApplied = $true
        }
        else {
            if ($stateText -notmatch '(?m)^PreviousAcceptDns=(true|false)$' -or
                $stateText -notmatch '(?m)^PreviousUnattended=(true|false)$') {
                throw 'Состояние исходных параметров Tailscale неполное. Нельзя безопасно продолжить автоматическую проверку.'
            }

            $dnsValue = Get-TailscaleBooleanSetting $paths.Cli 'accept-dns'
            $unattendedValue = Get-TailscaleBooleanSetting $paths.Cli 'unattended'
            if ($dnsValue -ne 'false' -or $unattendedValue -ne 'true') {
                throw 'Параметры Tailscale изменились после первичной настройки. Требуется восстановить accept-dns=false и unattended=true.'
            }

            $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='Tailscale'" -ErrorAction Stop
            if (-not $serviceInfo) {
                throw 'Не удалось прочитать состояние службы Tailscale.'
            }

            $reconnectedOutput = & $paths.Cli ip -4 2>&1
            $reconnectedExit = $LASTEXITCODE
            $reconnectedIp = [string]($reconnectedOutput | Select-Object -First 1)
            if ($reconnectedExit -ne 0 -or $reconnectedIp -notmatch '^100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}$') {
                throw 'После применения параметров Tailscale соединение не подтверждено. ' + ($reconnectedOutput -join ' ')
            }
        }

        $funnelCheck = Test-FunnelCapability $paths.Cli
        if (-not $funnelCheck.Ready) {
            $resultLines = @(
                'Connected=0',
                ('Address=' + $reconnectedIp),
                ('AuthorizationUrl=' + $funnelCheck.AuthorizationUrl),
                ('HttpsSetupUrl=' + $funnelCheck.HttpsSetupUrl),
                ('PublicUrl=' + $funnelCheck.PublicUrl),
                ('TailscaleSettingsApplied=' + [string]$settingsApplied),
                ('Status=' + $(if ($funnelCheck.AuthorizationUrl) { 'Требуется разрешить Tailscale Funnel в браузере, затем повторить проверку.' } elseif ($funnelCheck.HttpsSetupUrl) { 'Требуется включить HTTPS в настройках Tailscale, затем повторить проверку Funnel.' } else { 'Проверка публичного Tailscale Funnel не пройдена.' })),
                ('Details=' + (($funnelCheck.Details -replace '[\r\n]+', ' ').Trim()))
            )
            Write-Result $resultLines
            exit 11
        }

        $finalDns = Get-TailscaleBooleanSetting $paths.Cli 'accept-dns'
        $finalUnattended = Get-TailscaleBooleanSetting $paths.Cli 'unattended'
        if ($finalDns -ne 'false' -or $finalUnattended -ne 'true') {
            throw 'Финальная проверка параметров Tailscale не пройдена.'
        }

        Wait-Service 'Running'
        $finalService = Get-CimInstance Win32_Service -Filter "Name='Tailscale'" -ErrorAction Stop
        if (-not $finalService) {
            throw 'Не удалось подтвердить состояние службы Tailscale после проверки Funnel.'
        }

        $stateText = Get-Content -LiteralPath $StateFile -Raw
        if ($stateText -notmatch '(?m)^BridgeSetupComplete=1$') {
            Add-Content -LiteralPath $StateFile -Value 'BridgeSetupComplete=1' -Encoding ASCII
        }
        Write-Result @(
            'Connected=1',
            ('Address=' + $reconnectedIp),
            ('PublicUrl=' + $funnelCheck.PublicUrl),
            'Unattended=1',
            'Dns=disabled',
            ('ServiceStartup=' + $finalService.StartMode),
            'ServiceRunning=1',
            'TailscaleSettingsApplied=1',
            'FunnelAuthorized=1',
            'FunnelStatusVerified=1'
        )
        exit 0
    }

    if ($Mode -eq 'Uninstall') {
        Stop-TailscaleGui
        Stop-TailscaleService

        if (Test-Path -LiteralPath $StateFile -PathType Leaf) {
            $stateText = (Get-Content -LiteralPath $StateFile) -join [Environment]::NewLine
            $installedByBridge = $stateText -match '(?m)^InstalledByMcpBridge=1$'

            if ($installedByBridge) {
                $managedMsiPath = Join-Path (Split-Path -Parent $MsiPath) 'tailscale-installed.msi'
                if (-not (Test-Path -LiteralPath $managedMsiPath -PathType Leaf)) {
                    throw "Не найден сохранённый исходный MSI: $managedMsiPath"
                }
                Test-TailscaleMsiAuthenticodeSignature -Path $managedMsiPath

                $arguments = '/x "' + $managedMsiPath + '" /qn /norestart'
                $msi = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
                if ($msi.ExitCode -notin @(0, 1605, 1614, 3010)) {
                    throw "Не удалось удалить Tailscale MSI. Код: $($msi.ExitCode)"
                }
                Remove-Item -LiteralPath $managedMsiPath -Force -ErrorAction SilentlyContinue
            }
            else {
                Restore-PreexistingTailscale $stateText
            }

            Remove-Item -LiteralPath $StateFile -Force -ErrorAction SilentlyContinue
        }

        Write-Result @('Uninstalled=1')
        exit 0
    }
}
catch {
    if ($Mode -eq 'Uninstall' -or
        ($Mode -eq 'Install' -and $wasInstalled -eq $false)) {
        try { Stop-TailscaleGui } catch { }
        try { Stop-TailscaleService } catch { }
    }
    Write-Result @(
        'Connected=0',
        ('Error=' + ($_.Exception.Message -replace '[\r\n]+', ' '))
    )
    exit 1
}
