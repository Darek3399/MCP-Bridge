[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,
    [Parameter(Mandatory = $true)]
    [string]$StateFile
)

$ErrorActionPreference = 'Stop'

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



if (-not (Test-Path -LiteralPath $StateFile -PathType Leaf)) {
    exit 0
}

$stateText = (Get-Content -LiteralPath $StateFile) -join [Environment]::NewLine
$installedByBridge = $stateText -match '(?m)^InstalledByMcpBridge=1$'
$bridgeAppliedSettings = $stateText -match '(?m)^TailscaleSettingsApplied=1$' -or
    $stateText -match '(?m)^BridgeSetupComplete=1$'

# During an in-place upgrade, preserve an unconfigured pre-existing Tailscale
# session. Stop the service only if Bridge had already taken ownership of it.
$upgradeInProgress = $false
try {
    $upgradeValue = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\MCPBridge' -Name 'UpgradeInProgress' -ErrorAction Stop
    $upgradeInProgress = [string]$upgradeValue -eq '1'
}
catch { }

# Keep the installed Tailscale client and its machine identity during an
# MCP Bridge upgrade. The next installer can repair/update the MSI in place.
if ($upgradeInProgress) {
    exit 0
}

if ($installedByBridge -or $bridgeAppliedSettings) {
    $sessionId = (Get-Process -Id $PID).SessionId
    Get-Process -Name 'tailscale-ipn' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $sessionId } |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $service = Get-Service -Name 'Tailscale' -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name 'Tailscale' -Force
        (Get-Service -Name 'Tailscale').WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

if ($upgradeInProgress) {
    exit 0
}

if ($installedByBridge) {
    $managedMsiPath = $MsiPath
    if (-not (Test-Path -LiteralPath $managedMsiPath -PathType Leaf)) {
        throw "Для удаления установленного Bridge клиента не найден сохранённый исходный MSI: $managedMsiPath"
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
    $previousAcceptDns = [regex]::Match($stateText, '(?m)^PreviousAcceptDns=(true|false)$').Groups[1].Value
    $previousUnattended = [regex]::Match($stateText, '(?m)^PreviousUnattended=(true|false)$').Groups[1].Value

    if ($previousAcceptDns -or $previousUnattended) {
        $daemonPath = $null
        try {
            $serviceKey = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Tailscale' -ErrorAction Stop
            $imagePath = [string]$serviceKey.ImagePath
            if ($imagePath -match '^\s*"(?<path>[^"]*tailscaled\.exe)"' -or
                $imagePath -match '^\s*(?<path>.+?tailscaled\.exe)(?:\s|$)') {
                $daemonPath = $Matches.path
            }
        }
        catch { }

        if (-not $daemonPath) {
            throw 'Не удалось определить путь к tailscaled.exe для восстановления настроек.'
        }

        $cliPath = Join-Path (Split-Path -Parent $daemonPath) 'tailscale.exe'
        if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) {
            throw "Не найден tailscale.exe: $cliPath"
        }

        Start-Service -Name 'Tailscale'
        (Get-Service -Name 'Tailscale').WaitForStatus('Running', [TimeSpan]::FromSeconds(45))

        if ($previousAcceptDns) {
            & $cliPath set ('--accept-dns=' + $previousAcceptDns)
            if ($LASTEXITCODE -ne 0) {
                throw 'Не удалось восстановить исходное значение accept-dns.'
            }
        }
        if ($previousUnattended) {
            & $cliPath set ('--unattended=' + $previousUnattended)
            if ($LASTEXITCODE -ne 0) {
                throw 'Не удалось восстановить исходное значение unattended mode.'
            }
        }
    }

    $previousMode = [regex]::Match($stateText, '(?m)^PreviousStartMode=(.*)$').Groups[1].Value.Trim()
    switch ($previousMode.ToLowerInvariant()) {
        'auto' { Set-Service -Name 'Tailscale' -StartupType Automatic }
        'automatic' { Set-Service -Name 'Tailscale' -StartupType Automatic }
        'manual' { Set-Service -Name 'Tailscale' -StartupType Manual }
        'disabled' { Set-Service -Name 'Tailscale' -StartupType Disabled }
        default { Set-Service -Name 'Tailscale' -StartupType Manual }
    }

    $previousStatus = [regex]::Match($stateText, '(?m)^PreviousServiceStatus=(.*)$').Groups[1].Value.Trim()
    if ($previousStatus -ne 'Running') {
        $service = Get-Service -Name 'Tailscale'
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name 'Tailscale' -Force
            (Get-Service -Name 'Tailscale').WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
    }
    elseif ((Get-Service -Name 'Tailscale').Status -ne 'Running') {
        Start-Service -Name 'Tailscale'
        (Get-Service -Name 'Tailscale').WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    }

    $encoded = [regex]::Match($stateText, '(?m)^PreviousSddlBase64=(.*)$').Groups[1].Value.Trim()
    if ($encoded) {
        $previousSddl = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded))
        & sc.exe sdset Tailscale $previousSddl
        if ($LASTEXITCODE -ne 0) {
            throw 'Не удалось восстановить исходный ACL службы Tailscale.'
        }
    }
}

Remove-Item -LiteralPath $StateFile -Force -ErrorAction SilentlyContinue
