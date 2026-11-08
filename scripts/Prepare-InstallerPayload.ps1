[CmdletBinding()]
param(
    [string]$DependenciesSourceDirectory = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DependenciesSourceDirectory)) {
    $DependenciesSourceDirectory = Join-Path $repoRoot 'Dependencies'
}

if (-not (Test-Path -LiteralPath $DependenciesSourceDirectory -PathType Container)) {
    throw "Не найдена локальная папка зависимостей '$DependenciesSourceDirectory'."
}

$DependenciesSourceDirectory = (Resolve-Path -LiteralPath $DependenciesSourceDirectory).Path
$filesystemPackage = Join-Path $DependenciesSourceDirectory 'FilesystemMcp'
$filesystemManifestPath = Join-Path $filesystemPackage 'package.json'
$filesystemIndexPath = Join-Path $filesystemPackage 'dist\index.js'
$filesystemModulesPath = Join-Path $filesystemPackage 'node_modules'
$nodeMsiPath = Join-Path $DependenciesSourceDirectory 'node-v26.13.0-x64.msi'
$tailscaleMsiPath = Join-Path $DependenciesSourceDirectory 'tailscale-setup.msi'
$filesystemEnginePath = Join-Path $DependenciesSourceDirectory 'FilesystemMcp-engine.json'
$nodeMsiSha256 = '0e5ee99c7cbf922887f04af268e3665990748945336a44956f47cc0df695b6df'

$requiredFiles = @(
    'supergateway.exe',
    'uv.exe',
    'uvx.exe',
    'node-v26.13.0-x64.msi',
    'tailscale-setup.msi'
)
$missingFiles = @($requiredFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $DependenciesSourceDirectory $_) -PathType Leaf)
})
if ($missingFiles.Count -gt 0) {
    throw ("В Dependencies отсутствуют обязательные файлы:" + [Environment]::NewLine + ' - ' + ($missingFiles -join ([Environment]::NewLine + ' - ')))
}

if (-not (Test-Path -LiteralPath $filesystemManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $filesystemIndexPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $filesystemModulesPath -PathType Container)) {
    throw "Filesystem MCP должен быть полностью подготовлен в '$filesystemPackage' (package.json, dist\index.js и production node_modules)."
}

$filesystemManifest = Get-Content -LiteralPath $filesystemManifestPath -Raw | ConvertFrom-Json
if ([version]$filesystemManifest.version -lt [version]'1.0.0') {
    throw "Требуется Filesystem MCP версии 1.0.0 или новее. Найдена версия $($filesystemManifest.version)."
}

function ConvertTo-Version([string]$Value) {
    $parts = $Value.Split('.')
    while ($parts.Count -lt 3) {
        $parts += '0'
    }
    return [version]($parts -join '.')
}

function Get-ExecutableVersion([string]$Path, [string[]]$Arguments) {
    $output = & $Path @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Не удалось проверить '$Path'. Код: $exitCode. Вывод: $output"
    }

    $match = [regex]::Match($output, '(?<!\d)v?(?<version>\d+(?:\.\d+){0,2})(?!\d)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        throw "Не удалось определить версию '$Path'. Вывод: $output"
    }
    return ConvertTo-Version $match.Groups['version'].Value
}

$supergatewayPath = Join-Path $DependenciesSourceDirectory 'supergateway.exe'
$supergatewayVersion = Get-ExecutableVersion -Path $supergatewayPath -Arguments @('--version')
if ($supergatewayVersion -lt [version]'4.2.1') {
    throw "Требуется Supergateway 4.2.1 или новее. Обнаружена версия $supergatewayVersion."
}

foreach ($executableName in @('uv.exe', 'uvx.exe')) {
    $executablePath = Join-Path $DependenciesSourceDirectory $executableName
    $executableVersion = Get-ExecutableVersion -Path $executablePath -Arguments @('--version')
    if ($executableVersion -lt [version]'0.1.0') {
        throw "Требуется $executableName версии 0.1.0 или новее. Обнаружена версия $executableVersion."
    }
}

$nodeMsiHash = (Get-FileHash -LiteralPath $nodeMsiPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($nodeMsiHash -ne $nodeMsiSha256) {
    throw "SHA-256 Node.js MSI не совпадает с ожидаемым значением. Ожидался $nodeMsiSha256, получен $nodeMsiHash."
}

$tailscaleSignature = Get-AuthenticodeSignature -LiteralPath $tailscaleMsiPath
if ($tailscaleSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    -not $tailscaleSignature.SignerCertificate -or
    $tailscaleSignature.SignerCertificate.Subject -notmatch 'Tailscale') {
    throw "Tailscale MSI не прошёл проверку Authenticode или сертификат подписанта не принадлежит Tailscale: $tailscaleMsiPath"
}

$nodeEngine = [string]$filesystemManifest.engines.node
$minimumNodeMatch = [regex]::Match($nodeEngine, '>=\s*(?<version>\d+(?:\.\d+){0,2})')
if ($minimumNodeMatch.Success) {
    $minimumNodeVersion = ConvertTo-Version $minimumNodeMatch.Groups['version'].Value
    if ([version]'26.13.0' -lt $minimumNodeVersion) {
        throw "Filesystem MCP требует Node.js $nodeEngine, но MSI содержит Node.js 26.13.0."
    }
}

# Keep a small metadata file next to the local dependency cache. Inno Setup
# embeds it separately for the install-time dependency probe.
Copy-Item -LiteralPath $filesystemManifestPath -Destination $filesystemEnginePath -Force

Write-Host "Источник зависимостей проверен: $DependenciesSourceDirectory"
Write-Host "Filesystem MCP: $($filesystemManifest.version)"
Write-Host "Supergateway: $supergatewayVersion"
Write-Host "uv: $(Get-ExecutableVersion -Path (Join-Path $DependenciesSourceDirectory 'uv.exe') -Arguments @('--version'))"
Write-Host "uvx: $(Get-ExecutableVersion -Path (Join-Path $DependenciesSourceDirectory 'uvx.exe') -Arguments @('--version'))"
Write-Host 'Node.js MSI: SHA-256 проверен.'
Write-Host "Tailscale MSI: подпись проверена ($($tailscaleSignature.SignerCertificate.Subject))."
