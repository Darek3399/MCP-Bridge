[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\McpBridge\McpBridge.csproj'
$output = Join-Path $repoRoot 'artifacts\publish\win-x64'
$diagnosticsDirectory = Join-Path $repoRoot 'artifacts\publish'
$diagnosticsFile = Join-Path $diagnosticsDirectory 'build-diagnostics.txt'

# Use the active dotnet SDK explicitly. Some IDE terminals leave MSBuildSDKsPath
# pointing at an older SDK (for example 8.0.300) while dotnet selects a newer one
# (for example 10.0.303). That mismatch breaks SDK-style solution/project imports.
$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $dotnet -or -not $dotnet.Source) {
    throw 'Команда dotnet не найдена. Установите .NET SDK или откройте терминал Rider с настроенным SDK.'
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Не найден проект приложения: $project"
}

# The publish directory is generated output, never a source of truth.
# Remove it completely so files from an earlier build cannot survive into a new installer.
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path $diagnosticsDirectory -Force | Out-Null

$adapterSourceDirectory = Join-Path $repoRoot 'src\McpBridge\Adapters'
$adapterOutputDirectory = Join-Path $output 'Adapters'

$sdkVersionOutput = & $dotnet.Source --version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось определить активную версию .NET SDK: $sdkVersionOutput"
}
$sdkVersion = ($sdkVersionOutput | Out-String).Trim()
if ($sdkVersion -notmatch '^\d+\.\d+\.\d+') {
    throw "dotnet --version вернул неожиданный результат: $sdkVersion"
}

$sdkListOutput = & $dotnet.Source --list-sdks 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось получить список установленных .NET SDK: $sdkListOutput"
}

$sdkBaseDirectory = $null
$sdkVersionPattern = '^' + [regex]::Escape($sdkVersion) + '\s+\[(?<base>.+)\]$'
foreach ($sdkLine in $sdkListOutput) {
    if ([string]$sdkLine -match $sdkVersionPattern) {
        $sdkBaseDirectory = $Matches.base
        break
    }
}
if (-not $sdkBaseDirectory) {
    # Fallback for standard dotnet installations if --list-sdks output is localized or unusual.
    $sdkBaseDirectory = Join-Path (Split-Path -Parent $dotnet.Source) 'sdk'
}
$sdkDirectory = Join-Path $sdkBaseDirectory $sdkVersion
$sdkSdksDirectory = Join-Path $sdkDirectory 'Sdks'
$sdkProps = Join-Path $sdkSdksDirectory 'Microsoft.NET.Sdk\Sdk\Sdk.props'
if (-not (Test-Path -LiteralPath $sdkProps -PathType Leaf)) {
    throw "Для активного .NET SDK $sdkVersion не найден каталог SDK targets: $sdkSdksDirectory"
}

$previousMsBuildSDKsPath = [Environment]::GetEnvironmentVariable('MSBuildSDKsPath', 'Process')
$previousResolverSdk = [Environment]::GetEnvironmentVariable('DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR', 'Process')

$diagnostics = [System.Collections.Generic.List[string]]::new()
$diagnostics.Add("Timestamp: $([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz'))")
$diagnostics.Add("dotnet: $($dotnet.Source)")
$diagnostics.Add("SDK version: $sdkVersion")
$diagnostics.Add("SDK targets: $sdkSdksDirectory")
$diagnostics.Add("Project: $project")
$diagnostics.Add("Configuration: $Configuration")
$diagnostics.Add("Previous MSBuildSDKsPath: $(if ($previousMsBuildSDKsPath) { $previousMsBuildSDKsPath } else { '<not set>' })")
$diagnostics.Add("Previous DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR: $(if ($previousResolverSdk) { $previousResolverSdk } else { '<not set>' })")

try {
    # Force MSBuild to use the targets belonging to the SDK selected by dotnet.
    $env:MSBuildSDKsPath = $sdkSdksDirectory
    Remove-Item Env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR -ErrorAction SilentlyContinue

    Write-Host ".NET SDK: $sdkVersion"
    Write-Host "MSBuild SDK targets: $sdkSdksDirectory"
    if ($previousMsBuildSDKsPath -and ($previousMsBuildSDKsPath -ne $sdkSdksDirectory)) {
        Write-Warning "Обнаружен конфликт MSBuildSDKsPath: '$previousMsBuildSDKsPath'. Для этой сборки используется SDK $sdkVersion. Системная переменная после завершения будет восстановлена."
    }

    $outDirArgument = "-p:OutDir=$output/"
    $buildOutput = & $dotnet.Source build $project --configuration $Configuration --nologo --verbosity minimal $outDirArgument '-p:PlatformTarget=x64' 2>&1
    $buildExitCode = $LASTEXITCODE
    $diagnostics.Add("Build exit code: $buildExitCode")
    if ($buildExitCode -ne 0) {
        $diagnostics.Add("---- BEGIN dotnet build output ----")
        foreach ($line in $buildOutput) {
            $diagnostics.Add([string]$line)
        }
        $diagnostics.Add("---- END dotnet build output ----")
        throw "Сборка приложения завершилась с кодом $buildExitCode. Подробности: $diagnosticsFile"
    }

    # JavaScript adapters are runtime payload, not compiled into MCPBridge.exe.
    # Copy them explicitly from the one canonical source directory after the build.
    if (-not (Test-Path -LiteralPath $adapterSourceDirectory -PathType Container)) {
        throw "Не найдена папка исходных MCP-адаптеров: $adapterSourceDirectory"
    }

    $adapterSources = @(Get-ChildItem -LiteralPath $adapterSourceDirectory -Filter '*.js' -File | Sort-Object Name)
    if ($adapterSources.Count -eq 0) {
        throw "В папке исходных MCP-адаптеров нет JS-файлов: $adapterSourceDirectory"
    }

    if (Test-Path -LiteralPath $adapterOutputDirectory) {
        Remove-Item -LiteralPath $adapterOutputDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $adapterOutputDirectory -Force | Out-Null

    foreach ($adapter in $adapterSources) {
        $destination = Join-Path $adapterOutputDirectory $adapter.Name
        Copy-Item -LiteralPath $adapter.FullName -Destination $destination -Force

        $sourceHash = (Get-FileHash -LiteralPath $adapter.FullName -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "JS-адаптер '$($adapter.Name)' не совпадает с исходником после копирования. Установщик не будет создан."
        }

        $diagnostics.Add("Adapter verified: $($adapter.Name) SHA256=$sourceHash")
    }

    $publishedAdapters = @(Get-ChildItem -LiteralPath $adapterOutputDirectory -Filter '*.js' -File)
    if ($publishedAdapters.Count -ne $adapterSources.Count) {
        throw "Количество JS-адаптеров в publish ($($publishedAdapters.Count)) не совпадает с исходным каталогом ($($adapterSources.Count)). Установщик не будет создан."
    }

    $diagnostics.Add("Adapter synchronization: OK ($($adapterSources.Count) files)")
}
finally {
    if ($null -eq $previousMsBuildSDKsPath) {
        Remove-Item Env:MSBuildSDKsPath -ErrorAction SilentlyContinue
    }
    else {
        $env:MSBuildSDKsPath = $previousMsBuildSDKsPath
    }

    if ($null -eq $previousResolverSdk) {
        Remove-Item Env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR -ErrorAction SilentlyContinue
    }
    else {
        $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR = $previousResolverSdk
    }

    $diagnostics | Set-Content -LiteralPath $diagnosticsFile -Encoding UTF8
}

Write-Host "Готово: $output"
Write-Host "Диагностика сборки: $diagnosticsFile"