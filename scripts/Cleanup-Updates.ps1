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

if (Test-Path -LiteralPath $UpdatesDirectory -PathType Container) {
    Remove-Item -LiteralPath $UpdatesDirectory -Recurse -Force
}

exit 0
