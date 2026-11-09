[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [bool]$FastDependencyPackaging = $false
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Join-Path $repoRoot 'Work'
$buildRoot = Join-Path $workspaceRoot 'Build'
$diagnosticsDirectory = Join-Path $workspaceRoot 'Diagnostics\Installer'
$dependenciesDirectory = Join-Path $repoRoot 'Dependencies'
$publishDirectory = Join-Path $buildRoot 'publish\win-x64'
$installerDirectory = Join-Path $buildRoot 'installer'
$installerScript = Join-Path $repoRoot 'installer\MCPBridge.iss'
$versionPropsFile = Join-Path $repoRoot 'Directory.Build.props'
$publishScript = Join-Path $PSScriptRoot 'Publish-Windows.ps1'
$payloadScript = Join-Path $PSScriptRoot 'Prepare-InstallerPayload.ps1'
$compilerDiscoveryScript = Join-Path $PSScriptRoot 'Find-InnoSetupCompiler.ps1'

foreach ($directory in @($diagnosticsDirectory, $installerDirectory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

if (-not (Test-Path -LiteralPath $dependenciesDirectory -PathType Container)) {
    throw "Не найдена локальная папка зависимостей: $dependenciesDirectory"
}

if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw "Не найден сценарий Inno Setup: $installerScript"
}

if (-not (Test-Path -LiteralPath $versionPropsFile -PathType Leaf)) {
    throw "Не найден единый источник версии продукта: $versionPropsFile"
}

try {
    [xml]$versionProperties = Get-Content -LiteralPath $versionPropsFile -Raw
}
catch {
    throw "Не удалось прочитать XML-файл с версией продукта '$versionPropsFile': $($_.Exception.Message)"
}

$versionNodes = @($versionProperties.SelectNodes('/Project/PropertyGroup/Version'))
if ($versionNodes.Count -ne 1) {
    throw "В '$versionPropsFile' должен находиться ровно один элемент <Version> в PropertyGroup. Найдено: $($versionNodes.Count)."
}

$installerVersion = $versionNodes[0].InnerText.Trim()
if ($installerVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw "Некорректная версия продукта '$installerVersion' в '$versionPropsFile'. Ожидается SemVer вида 1.2.3 или 1.2.3-preview.1."
}

Write-Host "Версия MCP Bridge: $installerVersion (Directory.Build.props)"

if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
    throw "Не найден сценарий публикации приложения: $publishScript"
}

if (-not (Test-Path -LiteralPath $payloadScript -PathType Leaf)) {
    throw "Не найден сценарий проверки зависимостей: $payloadScript"
}

$compilerDiscoveryReport = Join-Path $diagnosticsDirectory 'compiler-discovery.txt'
$isccPath = & $compilerDiscoveryScript -ReportPath $compilerDiscoveryReport | Select-Object -Last 1
if (-not $isccPath -or -not (Test-Path -LiteralPath $isccPath -PathType Leaf)) {
    throw "Не найден Inno Setup Compiler (ISCC.exe). Отчёт: $compilerDiscoveryReport"
}

Write-Host "Единый источник зависимостей: $dependenciesDirectory"
& $payloadScript -DependenciesSourceDirectory $dependenciesDirectory

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'Команда dotnet не найдена. Установите .NET SDK.'
}

& $publishScript -Configuration $Configuration

$adapterSourceDirectory = Join-Path $repoRoot 'src\McpBridge\Adapters'
$adapterPublishDirectory = Join-Path $publishDirectory 'Adapters'
if (-not (Test-Path -LiteralPath $adapterSourceDirectory -PathType Container) -or
    -not (Test-Path -LiteralPath $adapterPublishDirectory -PathType Container)) {
    throw 'Не найдены исходная или опубликованная папка MCP-адаптеров. Установщик не будет создан.'
}

$sourceAdapters = @(Get-ChildItem -LiteralPath $adapterSourceDirectory -Filter '*.js' -File | Sort-Object Name)
$publishAdapters = @(Get-ChildItem -LiteralPath $adapterPublishDirectory -Filter '*.js' -File | Sort-Object Name)
if ($sourceAdapters.Count -eq 0 -or $sourceAdapters.Count -ne $publishAdapters.Count) {
    throw "Количество исходных и опубликованных JS-адаптеров не совпадает ($($sourceAdapters.Count) / $($publishAdapters.Count))."
}

foreach ($sourceAdapter in $sourceAdapters) {
    $publishedAdapterPath = Join-Path $adapterPublishDirectory $sourceAdapter.Name
    if (-not (Test-Path -LiteralPath $publishedAdapterPath -PathType Leaf)) {
        throw "В publish отсутствует адаптер '$($sourceAdapter.Name)'."
    }

    $sourceHash = (Get-FileHash -LiteralPath $sourceAdapter.FullName -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $publishedAdapterPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $publishedHash) {
        throw "Адаптер '$($sourceAdapter.Name)' в publish отличается от исходника."
    }
}

Write-Host "Проверка MCP-адаптеров пройдена: $($sourceAdapters.Count) файлов совпадают с исходниками."

$installerOutput = Join-Path $installerDirectory "MCPBridge-Setup-$installerVersion.exe"
$compilerLog = Join-Path $diagnosticsDirectory 'inno-setup-build.log'

$embeddedPayloadRoots = @(
    $publishDirectory,
    $dependenciesDirectory,
    (Join-Path $PSScriptRoot 'TailscaleSetup.ps1'),
    (Join-Path $PSScriptRoot 'Uninstall-Tailscale.ps1')
)

$embeddedPayloadBytes = [long]0
Write-Host 'Проверка размера полного payload, который будет встроен в установщик...'
foreach ($payloadRoot in $embeddedPayloadRoots) {
    if (-not (Test-Path -LiteralPath $payloadRoot)) {
        throw "Не найден обязательный ресурс установщика: $payloadRoot"
    }

    $rootBytes = [long]0
    if (Test-Path -LiteralPath $payloadRoot -PathType Container) {
        $payloadDirectory = [System.IO.DirectoryInfo]::new($payloadRoot)
        foreach ($payloadFile in $payloadDirectory.EnumerateFiles('*', [System.IO.SearchOption]::AllDirectories)) {
            $rootBytes += [long]$payloadFile.Length
        }
    }
    else {
        $rootBytes = [long](Get-Item -LiteralPath $payloadRoot).Length
    }

    $embeddedPayloadBytes += $rootBytes
    Write-Host ("  {0}: {1:N2} MB" -f $payloadRoot, ($rootBytes / 1MB))
}

if ($embeddedPayloadBytes -le 0) {
    throw 'Не удалось определить размер встроенного payload установщика.'
}

Write-Host ("Общий размер встроенного payload: {0:N2} MB." -f ($embeddedPayloadBytes / 1MB))
$minimumSetupBytes = [long][Math]::Max(8MB, [Math]::Floor($embeddedPayloadBytes * 0.10))

if (Test-Path -LiteralPath $installerOutput -PathType Leaf) {
    Remove-Item -LiteralPath $installerOutput -Force
}

$compilerArguments = @(
    '/DFilesystemMcpPayload=1'
    ('/DAppVersion="' + $installerVersion + '"')
)
if ($FastDependencyPackaging) {
    Write-Host 'Быстрый режим: зависимости добавляются в установщик без повторного сжатия.'
    $compilerArguments += '/DFastDependencyPackaging=1'
}

$compileStartedUtc = [DateTime]::UtcNow
Write-Host 'Запуск Inno Setup для создания полного автономного установщика.'
& $isccPath @compilerArguments $installerScript 2>&1 | Tee-Object -FilePath $compilerLog
$compilerExitCode = $LASTEXITCODE

if ($compilerExitCode -ne 0) {
    throw "Inno Setup завершился с кодом $compilerExitCode. Диагностика: $compilerLog"
}
if (-not (Test-Path -LiteralPath $installerOutput -PathType Leaf)) {
    throw "Inno Setup не создал ожидаемый установщик: $installerOutput. Диагностика: $compilerLog"
}

$installerInfo = Get-Item -LiteralPath $installerOutput
if ($installerInfo.LastWriteTimeUtc -lt $compileStartedUtc.AddSeconds(-2)) {
    throw "Файл установщика не обновился во время компиляции: $installerOutput"
}
if ($installerInfo.Length -lt $minimumSetupBytes) {
    throw ("Размер установщика подозрительно мал: {0:N2} MB. Для payload {1:N2} MB ожидалось не менее {2:N2} MB. Диагностика: {3}" -f
        ($installerInfo.Length / 1MB),
        ($embeddedPayloadBytes / 1MB),
        ($minimumSetupBytes / 1MB),
        $compilerLog)
}

Write-Host ("Установщик создан: {0} ({1:N2} MB)" -f $installerOutput, ($installerInfo.Length / 1MB))
Write-Host "Диагностика Inno Setup: $compilerLog"
