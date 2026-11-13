[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UpdatesDirectory
)

$ErrorActionPreference = 'SilentlyContinue'

# During an in-place upgrade, the Setup EXE itself is running from Updates.
# Keep that directory intact until the new installer has finished.
$upgradeInProgress = $false
try {
    $value = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\MCPBridge' -Name 'UpgradeInProgress' -ErrorAction Stop
    $upgradeInProgress = [string]$value -eq '1'
}
catch {
    $upgradeInProgress = $false
}

if ($upgradeInProgress) {
    exit 0
}

# Autostart registry key is always removed on uninstall.
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MCPBridge' -ErrorAction SilentlyContinue

function Get-UninstallFlag([string]$Name, [bool]$Default = $false) {
    try {
        $val = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\MCPBridge' -Name $Name -ErrorAction Stop
        return ([string]$val -eq '1')
    }
    catch {
        return $Default
    }
}

function Remove-DirectoryForce([string]$TargetPath) {
    if ([string]::IsNullOrWhiteSpace($TargetPath) -or -not (Test-Path -LiteralPath $TargetPath)) { return }
    if (Test-Path -LiteralPath $TargetPath -PathType Container) {
        $extendedTarget = '\\?\' + [System.IO.Path]::GetFullPath($TargetPath)
        & cmd.exe /d /c "rmdir /s /q `"$extendedTarget`"" 2>$null | Out-Null
    }
    if (Test-Path -LiteralPath $TargetPath) {
        Remove-Item -LiteralPath $TargetPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$removeSettings = Get-UninstallFlag 'UninstallRemoveSettings' $true
$removePackages = Get-UninstallFlag 'UninstallRemovePackages' $true
$removeLogs = Get-UninstallFlag 'UninstallRemoveLogs' $true
$removeNodeJs = Get-UninstallFlag 'UninstallRemoveNodeJs' $false

Remove-DirectoryForce $UpdatesDirectory

$appDirectory = Split-Path -Parent $UpdatesDirectory
if (-not [string]::IsNullOrWhiteSpace($appDirectory)) {
    $configDirectory = Join-Path $appDirectory 'Config'

    foreach ($rel in @('Temp', 'CustomGateways', 'stdio-tools-cache.json', 'router-upstreams.json', 'filesystem-gateway.json', 'installer-language.txt')) {
        Remove-DirectoryForce (Join-Path $configDirectory $rel)
    }
    Get-ChildItem -LiteralPath $configDirectory -Filter 'tailscale-helper-*.txt' -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }

    if ($removeSettings) {
        foreach ($cfgFile in @('settings.json', 'settings.json.new')) {
            $p = Join-Path $configDirectory $cfgFile
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }
        }
    }

    if ($removePackages) {
        Remove-DirectoryForce (Join-Path $configDirectory 'ServerPackages')
    }

    if ($removeLogs) {
        Remove-DirectoryForce (Join-Path $appDirectory 'Logs')
    }
}

if ($removeNodeJs) {
    $seenCodes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $msiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    foreach ($uninstallRoot in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $uninstallRoot)) { continue }
        foreach ($entryKey in (Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue)) {
            $entry = Get-ItemProperty -LiteralPath $entryKey.PSPath -ErrorAction SilentlyContinue
            if ([string]$entry.DisplayName -notmatch '^Node\.js(?:\s|$)') { continue }
            $identityText = [string]$entryKey.PSChildName + ' ' + [string]$entry.UninstallString
            $m = [regex]::Match($identityText, '\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}')
            if ($m.Success -and $seenCodes.Add($m.Value)) {
                Start-Process -FilePath $msiExec -ArgumentList ('/x "' + $m.Value + '" /qn /norestart') -Wait -WindowStyle Hidden | Out-Null
            }
        }
    }
}

exit 0