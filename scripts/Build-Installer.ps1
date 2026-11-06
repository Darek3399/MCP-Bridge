[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$NodeMsiInstaller = $env:MCP_NODE_MSI_INSTALLER,
    [string]$DependenciesSourceDirectory = $env:MCP_DEPENDENCIES_SOURCE,
    [bool]$FastDependencyPackaging = $false
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$parentRoot = Split-Path -Parent $repoRoot
$publishScript = Join-Path $PSScriptRoot 'Publish-Windows.ps1'
$payloadScript = Join-Path $PSScriptRoot 'Prepare-InstallerPayload.ps1'
$installerScript = Join-Path $repoRoot 'installer\MCPBridge.iss'

$requiredSharedFiles = @(
    'supergateway.exe',
    'uv.exe',
    'uvx.exe'
)
if ([string]::IsNullOrWhiteSpace($NodeMsiInstaller)) {
    $requiredSharedFiles += 'node-v26.13.0-x64.msi'
}

if ([string]::IsNullOrWhiteSpace($DependenciesSourceDirectory)) {
    $candidates = @(
        (Join-Path $repoRoot 'Dependencies'),
        (Join-Path $parentRoot 'Dependencies'),
        (Join-Path $repoRoot 'installer\Payload')
    )
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }
        $missing = @($requiredSharedFiles | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $candidate $_) -PathType Leaf)
        })
        $hasTailscaleMsi = [bool](Get-ChildItem -LiteralPath $candidate -File -Filter 'tailscale-setup-*.msi' -ErrorAction SilentlyContinue | Select-Object -First 1) -or
                           (Test-Path -LiteralPath (Join-Path $candidate 'tailscale-setup.msi') -PathType Leaf)
        if ($missing.Count -eq 0 -and $hasTailscaleMsi) {
            $DependenciesSourceDirectory = $candidate
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($DependenciesSourceDirectory)) {
        $DependenciesSourceDirectory = Join-Path $parentRoot 'Dependencies'
    }
}

if (-not (Test-Path -LiteralPath $DependenciesSourceDirectory -PathType Container)) {
    throw "Не найдена папка зависимостей '$DependenciesSourceDirectory'. Укажите -DependenciesSourceDirectory или MCP_DEPENDENCIES_SOURCE."
}
$DependenciesSourceDirectory = (Resolve-Path -LiteralPath $DependenciesSourceDirectory).Path

$missingSharedFiles = @($requiredSharedFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $DependenciesSourceDirectory $_) -PathType Leaf)
})
if ($missingSharedFiles.Count -gt 0) {
    throw ("В папке зависимостей '$DependenciesSourceDirectory' отсутствуют обязательные файлы:`n - " + ($missingSharedFiles -join "`n - "))
}
$hasTailscaleMsi = [bool](Get-ChildItem -LiteralPath $DependenciesSourceDirectory -File -Filter 'tailscale-setup-*.msi' -ErrorAction SilentlyContinue | Select-Object -First 1) -or
                   (Test-Path -LiteralPath (Join-Path $DependenciesSourceDirectory 'tailscale-setup.msi') -PathType Leaf)
if (-not $hasTailscaleMsi) {
    throw "В папке зависимостей '$DependenciesSourceDirectory' отсутствует Tailscale MSI (ожидается tailscale-setup-<version>-amd64.msi)."
}
Write-Host "Источник зависимостей установщика: $DependenciesSourceDirectory"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'Команда dotnet не найдена. Установите .NET SDK.' }

$compilerDiscoveryScript = Join-Path $PSScriptRoot 'Find-InnoSetupCompiler.ps1'
$compilerDiscoveryReport = Join-Path $repoRoot 'artifacts\installer\compiler-discovery.txt'
$isccPath = & $compilerDiscoveryScript -ReportPath $compilerDiscoveryReport | Select-Object -Last 1
if (-not $isccPath -or -not (Test-Path -LiteralPath $isccPath -PathType Leaf)) {
    throw "Не найден Inno Setup Compiler (ISCC.exe). Поиск проверил PATH, реестр и все локальные фиксированные диски. Отчёт: $compilerDiscoveryReport"
}

& $publishScript -Configuration $Configuration
if (-not $?) { throw 'Сборка приложения завершилась с ошибкой.' }
& $payloadScript -NodeMsiInstaller $NodeMsiInstaller -DependenciesSourceDirectory $DependenciesSourceDirectory
if (-not $?) { throw 'Подготовка локальных зависимостей завершилась с ошибкой.' }

# Final guard: never package an installer if any runtime adapter differs from its source.
$adapterSourceDirectory = Join-Path $repoRoot 'src\McpBridge\Adapters'
$adapterPublishDirectory = Join-Path $repoRoot 'artifacts\publish\win-x64\Adapters'
if (-not (Test-Path -LiteralPath $adapterSourceDirectory -PathType Container) -or
    -not (Test-Path -LiteralPath $adapterPublishDirectory -PathType Container)) {
    throw 'Не найдены исходная или опубликованная папка MCP-адаптеров. Установщик не будет создан.'
}

$sourceAdapters = @(Get-ChildItem -LiteralPath $adapterSourceDirectory -Filter '*.js' -File | Sort-Object Name)
$publishAdapters = @(Get-ChildItem -LiteralPath $adapterPublishDirectory -Filter '*.js' -File | Sort-Object Name)
if ($sourceAdapters.Count -eq 0 -or $sourceAdapters.Count -ne $publishAdapters.Count) {
    throw "Количество исходных и опубликованных JS-адаптеров не совпадает ($($sourceAdapters.Count) / $($publishAdapters.Count)). Установщик не будет создан."
}

foreach ($sourceAdapter in $sourceAdapters) {
    $publishedAdapterPath = Join-Path $adapterPublishDirectory $sourceAdapter.Name
    if (-not (Test-Path -LiteralPath $publishedAdapterPath -PathType Leaf)) {
        throw "В publish отсутствует адаптер '$($sourceAdapter.Name)'. Установщик не будет создан."
    }

    $sourceHash = (Get-FileHash -LiteralPath $sourceAdapter.FullName -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $publishedAdapterPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $publishedHash) {
        throw "Адаптер '$($sourceAdapter.Name)' в publish отличается от исходника. Установщик не будет создан."
    }
}
Write-Host "Проверка MCP-адаптеров пройдена: $($sourceAdapters.Count) файлов совпадают с исходниками."

New-Item -ItemType Directory -Path (Join-Path $repoRoot 'artifacts\installer') -Force | Out-Null

$installerVersionMatch = [regex]::Match(
    (Get-Content -LiteralPath $installerScript -Raw),
    '(?m)^\s*#define\s+AppVersion\s+"([^"]+)"')
if (-not $installerVersionMatch.Success) {
    throw "Не удалось определить AppVersion в '$installerScript'."
}
$installerVersion = $installerVersionMatch.Groups[1].Value
$installerOutput = Join-Path $repoRoot "artifacts\installer\MCPBridge-Setup-$installerVersion.exe"
$compilerLog = Join-Path $repoRoot 'artifacts\installer\inno-setup-build.log'

# The setup is expected to contain the complete offline payload. Do not let an
# old small EXE remain at the output path and look like a successful rebuild.
$embeddedPayloadRoots = @(
    (Join-Path $repoRoot 'artifacts\publish\win-x64'),
    (Join-Path $repoRoot 'installer\Payload'),
    (Join-Path $repoRoot 'scripts\TailscaleSetup.ps1'),
    (Join-Path $repoRoot 'scripts\Uninstall-Tailscale.ps1')
)
$embeddedPayloadBytes = [long]0
Write-Host 'Проверка размера встроенного payload (может включать тысячи файлов Filesystem MCP)...'
foreach ($payloadRoot in $embeddedPayloadRoots) {
    if (-not (Test-Path -LiteralPath $payloadRoot)) {
        throw "Не найден файл или каталог payload для установщика: $payloadRoot"
    }

    $scanTimer = [System.Diagnostics.Stopwatch]::StartNew()
    $rootBytes = [long]0
    if (Test-Path -LiteralPath $payloadRoot -PathType Container) {
        # Enumerate lazily through .NET instead of materializing a large PowerShell
        # Get-ChildItem pipeline and then passing every item through Measure-Object.
        $payloadDirectory = [System.IO.DirectoryInfo]::new($payloadRoot)
        foreach ($payloadFile in $payloadDirectory.EnumerateFiles('*', [System.IO.SearchOption]::AllDirectories)) {
            $rootBytes += [long]$payloadFile.Length
        }
    }
    else {
        $rootBytes = [long](Get-Item -LiteralPath $payloadRoot).Length
    }

    $scanTimer.Stop()
    $embeddedPayloadBytes += $rootBytes
    Write-Host ("  Проверено: {0} — {1:N2} MB за {2:N1} сек." -f $payloadRoot, ($rootBytes / 1MB), $scanTimer.Elapsed.TotalSeconds)
}
Write-Host ("Общий размер встроенного payload: {0:N2} MB." -f ($embeddedPayloadBytes / 1MB))
if ($embeddedPayloadBytes -le 0) {
    throw 'Не удалось определить размер встроенного payload установщика.'
}

# Even with maximum LZMA compression, these executable/MSI payloads cannot
# legitimately collapse to a sub-megabyte setup file.
$minimumSetupBytes = [long][Math]::Max(8MB, [Math]::Floor($embeddedPayloadBytes * 0.10))
if (Test-Path -LiteralPath $installerOutput -PathType Leaf) {
    Remove-Item -LiteralPath $installerOutput -Force
}

$compileStartedUtc = [DateTime]::UtcNow
Write-Host 'Запуск Inno Setup. При стандартном режиме выполняется сжатие LZMA2 Ultra64; процесс может занять несколько минут.'

$compilerArguments = @()
$filesystemPayloadIndex = Join-Path $repoRoot 'installer\Payload\FilesystemMcp\dist\index.js'
if (Test-Path -LiteralPath $filesystemPayloadIndex -PathType Leaf) {
    $compilerArguments += '/DFilesystemMcpPayload=1'
    Write-Host 'Filesystem MCP включён в Setup payload (необязательная функция).'
}
else {
    Write-Host 'Filesystem MCP отсутствует в локальном payload. Setup будет собран без него; остальные функции не блокируются.'
}
if ($FastDependencyPackaging) {
    Write-Host 'Быстрый режим: payload-зависимости добавляются в установщик без повторного сжатия.'
    $compilerArguments += '/DFastDependencyPackaging=1'
}
& $isccPath @compilerArguments $installerScript 2>&1 | Tee-Object -FilePath $compilerLog
$compilerExitCode = $LASTEXITCODE

if ($compilerExitCode -ne 0) {
    throw "Inno Setup завершился с кодом $compilerExitCode. Журнал: $compilerLog"
}
if (-not (Test-Path -LiteralPath $installerOutput -PathType Leaf)) {
    throw "Inno Setup завершился без ожидаемого файла установщика: $installerOutput. Журнал: $compilerLog"
}

$installerInfo = Get-Item -LiteralPath $installerOutput
if ($installerInfo.LastWriteTimeUtc -lt $compileStartedUtc.AddSeconds(-2)) {
    throw "Файл установщика не обновился во время компиляции и, вероятно, является старой копией: $installerOutput"
}
if ($installerInfo.Length -lt $minimumSetupBytes) {
    throw ("Размер установщика подозрительно мал: {0:N2} MB. Для payload размером {1:N2} MB ожидалось не менее {2:N2} MB. Старый/неполный установщик не будет считаться успешной сборкой. Журнал: {3}" -f
        ($installerInfo.Length / 1MB),
        ($embeddedPayloadBytes / 1MB),
        ($minimumSetupBytes / 1MB),
        $compilerLog)
}

Write-Host ("Установщик создан: {0} ({1:N2} MB)" -f $installerOutput, ($installerInfo.Length / 1MB))
Write-Host "Журнал Inno Setup: $compilerLog"