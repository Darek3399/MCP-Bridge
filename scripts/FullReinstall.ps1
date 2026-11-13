[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$rebootRequired = $false
$removedNodeProducts = 0

function Get-RegisteredNodeMsiProducts {
    $products = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($uninstallRoot in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $uninstallRoot)) { continue }

        foreach ($entryKey in (Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue)) {
            $entry = Get-ItemProperty -LiteralPath $entryKey.PSPath -ErrorAction SilentlyContinue
            if ([string]$entry.DisplayName -notmatch '^Node\.js(?:\s|$)') { continue }

            $uninstallText = [string]$entry.UninstallString
            $identityText = [string]$entryKey.PSChildName + ' ' + $uninstallText
            $productCodeMatch = [regex]::Match(
                $identityText,
                '\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}')
            if (-not $productCodeMatch.Success) { continue }

            $productCode = $productCodeMatch.Value
            if ($seen.Add($productCode)) {
                $products.Add([pscustomobject]@{
                    ProductCode = $productCode
                    DisplayName = [string]$entry.DisplayName
                })
            }
        }
    }

    return $products.ToArray()
}

function Remove-RegisteredNodeMsiProducts {
    $products = @(Get-RegisteredNodeMsiProducts)
    $msiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'

    $logsDir = Join-Path $AppDirectory 'Logs'
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

    foreach ($product in $products) {
        $logPath = Join-Path $logsDir ('node-msi-uninstall-' + ([Guid]::NewGuid().ToString('N')) + '.log')
        $arguments = '/x "' + $product.ProductCode + '" /qn /norestart /L*v "' + $logPath + '"'
        $process = Start-Process -FilePath $msiExec -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden

        if ($process.ExitCode -in @(3010, 1641)) {
            $script:rebootRequired = $true
        }
        elseif ($process.ExitCode -notin @(0, 1605, 1614)) {
            throw ('Не удалось удалить прежнюю установку ' + $product.DisplayName + '. Код MSI: ' + $process.ExitCode + '. Журнал: ' + $logPath)
        }

        if ($process.ExitCode -notin @(1605, 1614)) {
            $script:removedNodeProducts++
        }
    }
}

function Preserve-LegacyTailscaleState {
    $legacyState = Join-Path $AppDirectory 'Dependencies\tailscale-state.txt'
    $currentState = Join-Path $AppDirectory 'Config\tailscale-state.txt'

    if (-not (Test-Path -LiteralPath $legacyState -PathType Leaf)) { return }

    $configDirectory = Split-Path -Parent $currentState
    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
    if (-not (Test-Path -LiteralPath $currentState -PathType Leaf)) {
        Copy-Item -LiteralPath $legacyState -Destination $currentState -Force
    }
    Remove-Item -LiteralPath $legacyState -Force
}

try {
    if (-not (Test-Path -LiteralPath $AppDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $AppDirectory -Force | Out-Null
    }

    $resolvedAppDirectory = [IO.Path]::GetFullPath($AppDirectory)
    if ([string]::IsNullOrWhiteSpace($resolvedAppDirectory) -or $resolvedAppDirectory -eq [IO.Path]::GetPathRoot($resolvedAppDirectory)) {
        throw 'Путь установки MCP Bridge недопустим для полной очистки.'
    }

    Preserve-LegacyTailscaleState
    Remove-RegisteredNodeMsiProducts

    # Keep user configuration (including the persistent Tunnel ID) and logs.
    # Every application/dependency/adapter file is repopulated from this installer.
    foreach ($item in (Get-ChildItem -LiteralPath $resolvedAppDirectory -Force)) {
        if ($item.Name -in @('Config', 'Logs', 'Updates')) { continue }
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }

    New-Item -ItemType Directory -Path (Join-Path $resolvedAppDirectory 'Config') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $resolvedAppDirectory 'Logs') -Force | Out-Null

    $lines = @(
        'FullReinstallPrepared=1',
        ('RemovedNodeProducts=' + $removedNodeProducts),
        ('RebootRequired=' + [int]$rebootRequired)
    )
    $outputDirectory = Split-Path -Parent $OutputFile
    if ($outputDirectory) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
    [IO.File]::WriteAllLines($OutputFile, $lines, [Text.UTF8Encoding]::new($true))
    exit 0
}
catch {
    $outputDirectory = Split-Path -Parent $OutputFile
    if ($outputDirectory) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
    [IO.File]::WriteAllLines(
        $OutputFile,
        @('FullReinstallPrepared=0', ('Error=' + ($_.Exception.Message -replace '[\r\n]+', ' '))),
        [Text.UTF8Encoding]::new($true))
    exit 1
}
