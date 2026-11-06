[CmdletBinding()]
param(
    [string]$PayloadDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\Payload'),
    [string]$NodeMsiInstaller = $env:MCP_NODE_MSI_INSTALLER,
    [string]$DependenciesSourceDirectory = $env:MCP_DEPENDENCIES_SOURCE,
    [string]$TailscaleMsiInstaller = $env:MCP_TAILSCALE_MSI_INSTALLER
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


$repoRoot = Split-Path -Parent $PSScriptRoot
$parentRoot = Split-Path -Parent $repoRoot
. (Join-Path $repoRoot 'installer\NodeRuntimeDiscovery.ps1')

$nodeMsiVersion = [version]'26.13.0'
$nodeMsiFileName = 'node-v26.13.0-x64.msi'
$nodeMsiSha256 = '0e5ee99c7cbf922887f04af268e3665990748945336a44956f47cc0df695b6df'

if ([string]::IsNullOrWhiteSpace($DependenciesSourceDirectory)) {
    $candidates = @(
        (Join-Path $repoRoot 'Dependencies'),
        (Join-Path $parentRoot 'Dependencies'),
        (Join-Path $repoRoot 'installer\Payload')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'supergateway.exe') -PathType Leaf)) {
            $DependenciesSourceDirectory = $candidate
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($DependenciesSourceDirectory)) {
        $DependenciesSourceDirectory = Join-Path $parentRoot 'Dependencies'
    }
}

function Find-CommandPath([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) { return $command.Source }
    return $null
}

function Get-ExecutableVersion([string]$Path, [string]$Arguments) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $process = $null
    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $Path
        $startInfo.Arguments = $Arguments
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { return $null }
        if (-not $process.WaitForExit(4000)) {
            try { $process.Kill() } catch { }
            return $null
        }
        $output = $process.StandardOutput.ReadToEnd() + ' ' + $process.StandardError.ReadToEnd()
        $match = [regex]::Match($output, '(?<!\d)v?(?<version>\d+\.\d+(?:\.\d+)?)(?!\d)')
        if (-not $match.Success) { return $null }
        return [version]$match.Groups['version'].Value
    }
    catch { return $null }
    finally {
        if ($process) { $process.Dispose() }
    }
}

function Find-FirstFile([string[]]$Candidates) {
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Test-FilesystemBundle([string]$PackageDirectory) {
    $indexPath = Join-Path $PackageDirectory 'dist\index.js'
    $manifestPath = Join-Path $PackageDirectory 'package.json'
    $nodeModulesPath = Join-Path $PackageDirectory 'node_modules'
    if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $nodeModulesPath -PathType Container)) {
        return $false
    }
    # A lockfile by itself is not a usable bundle; at least one installed package directory must exist.
    return [bool](Get-ChildItem -LiteralPath $nodeModulesPath -Directory -Force -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Find-FilesystemPackage([string]$SharedDependencies) {
    $preparedBundleCandidates = @(
        (Join-Path $repoRoot 'Dependencies\FilesystemMcp'),
        (Join-Path $SharedDependencies 'FilesystemMcp')
    )
    foreach ($candidate in $preparedBundleCandidates) {
        if ($candidate -and (Test-FilesystemBundle $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $globalCandidates = @(
        (Join-Path $env:APPDATA 'npm\node_modules\@modelcontextprotocol\server-filesystem'),
        (Join-Path $env:LOCALAPPDATA 'npm\node_modules\@modelcontextprotocol\server-filesystem')
    )
    foreach ($candidate in $globalCandidates) {
        if ($candidate -and
            (Test-Path -LiteralPath (Join-Path $candidate 'dist\index.js') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'package.json') -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    foreach ($candidate in @(
        (Join-Path $repoRoot 'installer\Payload\FilesystemMcp'),
        (Join-Path $env:ProgramFiles 'MCP Bridge\Dependencies\FilesystemMcp'),
        (Join-Path $env:LOCALAPPDATA 'Programs\MCP Bridge\Dependencies\FilesystemMcp')
    )) {
        if ($candidate -and (Test-FilesystemBundle $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Find-Supergateway([string]$SharedDependencies) {
    return (Find-FirstFile @((Join-Path $SharedDependencies 'supergateway.exe')))
}

if (-not (Test-Path -LiteralPath $DependenciesSourceDirectory -PathType Container)) {
    throw "Не найдена общая папка зависимостей '$DependenciesSourceDirectory'. Проверьте путь или передайте -DependenciesSourceDirectory."
}
$DependenciesSourceDirectory = (Resolve-Path -LiteralPath $DependenciesSourceDirectory).Path

$nodeRuntime = Find-InstalledNodeRuntime -MinimumVersion '18.0.0'
$node = if ($nodeRuntime.Found) { $nodeRuntime.Path } else { $null }

$filesystemPackage = Find-FilesystemPackage $DependenciesSourceDirectory
$filesystemManifest = $null
$npmRoot = $null

if ($filesystemPackage) {
    $filesystemManifestPath = Join-Path $filesystemPackage 'package.json'
    if (-not (Test-Path -LiteralPath $filesystemManifestPath -PathType Leaf)) {
        throw 'В найденном Filesystem MCP отсутствует package.json.'
    }
    $filesystemManifest = Get-Content -LiteralPath $filesystemManifestPath -Raw | ConvertFrom-Json
    $nodeEngine = [string]$filesystemManifest.engines.node
    if ($nodeEngine -match '>=\s*(?<minimum>\d+(?:\.\d+){0,2})') {
        $minimumText = $Matches.minimum
        if ($minimumText.Split('.').Count -eq 1) { $minimumText += '.0.0' }
        elseif ($minimumText.Split('.').Count -eq 2) { $minimumText += '.0' }
        if ($nodeMsiVersion -lt [version]$minimumText) {
            throw "Filesystem MCP $($filesystemManifest.version) требует Node.js $nodeEngine, но встроенный Node.js $nodeMsiVersion недостаточно новый."
        }
    }

    $npmCandidates = @()
    if ($node) {
        $npmCandidates += Join-Path (Split-Path -Parent $node) 'npm.cmd'
    }
    $npmCandidates += @(
        (Join-Path $env:APPDATA 'npm\npm.cmd'),
        (Join-Path $env:ProgramFiles 'nodejs\npm.cmd'),
        (Join-Path $env:LOCALAPPDATA 'Programs\nodejs\npm.cmd'),
        (Find-CommandPath 'npm.cmd')
    )
    $npmPath = Find-FirstFile $npmCandidates
    if ($npmPath) {
        $npmRootOutput = (& $npmPath root -g 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $npmRootOutput -PathType Container)) {
            $resolvedPackage = (Resolve-Path -LiteralPath $filesystemPackage).Path
            $resolvedNpmRoot = (Resolve-Path -LiteralPath $npmRootOutput).Path
            if ($resolvedPackage.StartsWith($resolvedNpmRoot, [StringComparison]::OrdinalIgnoreCase)) {
                $npmRoot = $resolvedNpmRoot
            }
        }
    }
    if (-not $npmRoot -and -not (Test-Path -LiteralPath (Join-Path $filesystemPackage 'node_modules') -PathType Container)) {
        throw 'Filesystem MCP найден, но production dependencies не обнаружены. Нужен полный пакет с node_modules или глобальная npm-установка.'
    }
}
else {
    Write-Warning 'Filesystem MCP не найден. Он является необязательной функцией: установщик будет собран без него, а Bridge покажет его как недоступный компонент.'
}

$supergateway = Find-Supergateway $DependenciesSourceDirectory
if (-not $supergateway) { throw "В общей папке зависимостей не найден '$DependenciesSourceDirectory\supergateway.exe'." }
$supergatewayVersion = Get-ExecutableVersion -Path $supergateway -Arguments '--version'
if (-not $supergatewayVersion -or $supergatewayVersion -lt [version]'4.2.1') {
    throw "Для MCP Bridge требуется Supergateway 4.2.1 или новее. Обнаружена версия: $supergatewayVersion ($supergateway)."
}

$nodePayloadPath = Join-Path $PayloadDirectory $nodeMsiFileName
New-Item -ItemType Directory -Path $PayloadDirectory -Force | Out-Null

if (-not $NodeMsiInstaller) {
    $NodeMsiInstaller = Find-FirstFile @(
        (Join-Path $DependenciesSourceDirectory $nodeMsiFileName)
    )
}

if (-not $TailscaleMsiInstaller) {
    $tailscaleCandidates = @()
    $tailscaleCandidates += Get-ChildItem -LiteralPath $DependenciesSourceDirectory -File -Filter 'tailscale-setup-*.msi' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
    $tailscaleCandidates += Get-ChildItem -LiteralPath $DependenciesSourceDirectory -File -Filter 'tailscale-setup.msi' -ErrorAction SilentlyContinue
    $TailscaleMsiInstaller = ($tailscaleCandidates | Select-Object -First 1).FullName
}
if (-not $TailscaleMsiInstaller -or -not (Test-Path -LiteralPath $TailscaleMsiInstaller -PathType Leaf)) {
    throw "Не найден Tailscale Windows x64 MSI в '$DependenciesSourceDirectory'. Ожидается tailscale-setup-<version>-amd64.msi. Скачивание во время сборки отключено."
}
$TailscaleMsiInstaller = (Resolve-Path -LiteralPath $TailscaleMsiInstaller).Path
Test-TailscaleMsiAuthenticodeSignature -Path $TailscaleMsiInstaller

if (-not $NodeMsiInstaller -or -not (Test-Path -LiteralPath $NodeMsiInstaller -PathType Leaf)) {
    throw "Не найден Node.js MSI $nodeMsiFileName. Ожидаемый путь: '$DependenciesSourceDirectory\$nodeMsiFileName'. Скачивание во время сборки отключено."
}

$nodeMsiSource = (Resolve-Path -LiteralPath $NodeMsiInstaller).Path
$nodeMsiHash = (Get-FileHash -LiteralPath $nodeMsiSource -Algorithm SHA256).Hash.ToLowerInvariant()
if ($nodeMsiHash -ne $nodeMsiSha256) {
    throw "SHA-256 файла $nodeMsiSource не совпадает с официальным Node.js v26.13.0 x64 MSI. Ожидался $nodeMsiSha256, получен $nodeMsiHash."
}

if (-not [string]::Equals([IO.Path]::GetFullPath($nodeMsiSource), [IO.Path]::GetFullPath($nodePayloadPath), [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $nodeMsiSource -Destination $nodePayloadPath -Force
}

New-Item -ItemType Directory -Path $PayloadDirectory -Force | Out-Null
foreach ($staleName in @('Node','Supergateway','DevTunnel')) {
    $stalePath = Join-Path $PayloadDirectory $staleName
    if (Test-Path -LiteralPath $stalePath) { Remove-Item -LiteralPath $stalePath -Recurse -Force }
}
$legacyDevTunnelPayload = Join-Path $PayloadDirectory 'devtunnel.exe'
if (Test-Path -LiteralPath $legacyDevTunnelPayload -PathType Leaf) {
    Remove-Item -LiteralPath $legacyDevTunnelPayload -Force
}

$tailscalePayload = Join-Path $PayloadDirectory 'tailscale-setup.msi'
if (-not [string]::Equals(
    [IO.Path]::GetFullPath($TailscaleMsiInstaller),
    [IO.Path]::GetFullPath($tailscalePayload),
    [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $TailscaleMsiInstaller -Destination $tailscalePayload -Force
}
$legacyDotNetInstaller = Join-Path $PayloadDirectory 'ndp481-x86-x64-allos-rus.exe'
if (Test-Path -LiteralPath $legacyDotNetInstaller -PathType Leaf) {
    Remove-Item -LiteralPath $legacyDotNetInstaller -Force
}

if ($filesystemPackage) {
    $fsDestination = Join-Path $PayloadDirectory 'FilesystemMcp'
    $sameFilesystemTree = [string]::Equals(
        [IO.Path]::GetFullPath($filesystemPackage),
        [IO.Path]::GetFullPath($fsDestination),
        [StringComparison]::OrdinalIgnoreCase)

    if (-not $sameFilesystemTree) {
        if (Test-Path -LiteralPath $fsDestination) {
            Remove-Item -LiteralPath $fsDestination -Recurse -Force
        }
        New-Item -ItemType Directory -Path $fsDestination -Force | Out-Null
    }

    $cacheDestination = Join-Path $repoRoot 'Dependencies\FilesystemMcp'
    $sourceIsCache = [string]::Equals(
        [IO.Path]::GetFullPath($filesystemPackage),
        [IO.Path]::GetFullPath($cacheDestination),
        [StringComparison]::OrdinalIgnoreCase)

    if (Test-FilesystemBundle $filesystemPackage) {
        if (-not $sourceIsCache) {
            if (Test-Path -LiteralPath $cacheDestination) {
                Remove-Item -LiteralPath $cacheDestination -Recurse -Force
            }
            New-Item -ItemType Directory -Path $cacheDestination -Force | Out-Null
            Copy-Item -Path (Join-Path $filesystemPackage '*') -Destination $cacheDestination -Recurse -Force
            Write-Host "Готовый Filesystem MCP сохранён в локальный кеш: $cacheDestination"
        }
        else {
            Write-Host "Использован готовый кеш Filesystem MCP: $filesystemPackage"
        }
    }
    else {
        # First build: create the production dependency tree once in the project-local cache.
        if (-not $node) {
            throw "Для первичной сборки Filesystem MCP требуется Node.js >= 18 на build machine: $($nodeRuntime.Detail)"
        }
        $bundleBuilder = Join-Path $PSScriptRoot 'Build-FilesystemMcpBundle.cjs'
        if (-not (Test-Path -LiteralPath $bundleBuilder -PathType Leaf)) {
            throw "Не найден сборщик production dependency bundle: $bundleBuilder"
        }
        $bundleReport = Join-Path $repoRoot 'artifacts\installer\filesystem-mcp-bundle-report.txt'
        New-Item -ItemType Directory -Path (Split-Path -Parent $bundleReport) -Force | Out-Null
        & $node $bundleBuilder $filesystemPackage $cacheDestination $bundleReport
        $bundleExitCode = $LASTEXITCODE
        if ($bundleExitCode -ne 0) {
            throw "Не удалось подготовить кеш Filesystem MCP (код $bundleExitCode). Отчёт: $bundleReport"
        }
        Write-Host "Создан кеш Filesystem MCP: $cacheDestination"
    }

    $filesystemPackage = (Resolve-Path -LiteralPath $cacheDestination).Path
    $filesystemManifestPath = Join-Path $filesystemPackage 'package.json'
    $filesystemManifest = Get-Content -LiteralPath $filesystemManifestPath -Raw | ConvertFrom-Json

    if (-not $sameFilesystemTree) {
        Copy-Item -Path (Join-Path $filesystemPackage '*') -Destination $fsDestination -Recurse -Force
    }

    $engineManifest = Join-Path $PayloadDirectory 'FilesystemMcp-engine.json'
    if (Test-Path -LiteralPath $engineManifest) { Remove-Item -LiteralPath $engineManifest -Force }
    Copy-Item -LiteralPath $filesystemManifestPath -Destination $engineManifest -Force
}
else {
    # Remove an old optional payload so a previous build cannot silently ship it.
    $staleFilesystemPayload = Join-Path $PayloadDirectory 'FilesystemMcp'
    if (Test-Path -LiteralPath $staleFilesystemPayload) {
        Remove-Item -LiteralPath $staleFilesystemPayload -Recurse -Force
    }
    $staleEngineManifest = Join-Path $PayloadDirectory 'FilesystemMcp-engine.json'
    if (Test-Path -LiteralPath $staleEngineManifest) {
        Remove-Item -LiteralPath $staleEngineManifest -Force
    }
}

$supergatewayDest = Join-Path $PayloadDirectory 'supergateway.exe'
if (-not [string]::Equals([IO.Path]::GetFullPath($supergateway), [IO.Path]::GetFullPath($supergatewayDest), [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $supergateway -Destination $supergatewayDest -Force
}

foreach ($uvBin in @('uv.exe', 'uvx.exe')) {
    $uvSource = Find-FirstFile @((Join-Path $DependenciesSourceDirectory $uvBin))
    if (-not $uvSource) {
        throw "В общей папке зависимостей не найден '$DependenciesSourceDirectory\$uvBin'."
    }
    $uvVersion = Get-ExecutableVersion -Path $uvSource -Arguments '--version'
    if (-not $uvVersion -or $uvVersion -lt [version]'0.1.0') {
        throw "$uvBin должен иметь версию 0.1.0 или новее. Обнаружена версия: $uvVersion ($uvSource)."
    }
    $uvDest = Join-Path $PayloadDirectory $uvBin
    if (-not [string]::Equals([IO.Path]::GetFullPath($uvSource), [IO.Path]::GetFullPath($uvDest), [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $uvSource -Destination $uvDest -Force
    }
}
Write-Host "Источник зависимостей: $DependenciesSourceDirectory"
$filesystemSummary = if ($filesystemManifest) { "Filesystem MCP $($filesystemManifest.version)" } else { 'Filesystem MCP: optional, skipped' }
Write-Host "Payload готов: $filesystemSummary, supergateway.exe, uv.exe, uvx.exe, node-v26.13.0-x64.msi и tailscale-setup.msi ($([IO.Path]::GetFileName($TailscaleMsiInstaller))). Node MSI SHA-256: $nodeMsiHash."