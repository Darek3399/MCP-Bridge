[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputFile,
    [string]$PayloadFilesystemPackageJson = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NodeRuntimeDiscovery.ps1')

function Get-FirstExistingFile([string[]]$Candidates) {
    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Find-OnPath([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }
    return $null
}

function Test-NodeRuntime {
    $runtime = Find-InstalledNodeRuntime -MinimumVersion '18.0.0'
    return @{
        Found = [bool]$runtime.Found
        Detail = [string]$runtime.Detail
        Path = [string]$runtime.Path
        MsiInstallDirectory = [string]$runtime.MsiInstallDirectory
    }
}

function Test-FilesystemMcp {
    $candidates = @(
        (Join-Path $AppDirectory 'Dependencies\FilesystemMcp\dist\index.js'),
        (Join-Path $env:APPDATA 'npm\node_modules\@modelcontextprotocol\server-filesystem\dist\index.js'),
        (Join-Path $env:LOCALAPPDATA 'npm\node_modules\@modelcontextprotocol\server-filesystem\dist\index.js')
    )
    $path = Get-FirstExistingFile $candidates
    if (-not $path) {
        return @{ Found = $false; Path = ''; Detail = 'Filesystem MCP не установлен (необязательная функция)' }
    }

    try {
        $packagePath = Join-Path (Split-Path -Parent (Split-Path -Parent $path)) 'package.json'
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            return @{ Found = $false; Path = $path; Detail = 'Filesystem MCP найден, но package.json отсутствует' }
        }
        $manifest = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
        $version = [version]$manifest.version
        $minimum = [version]'1.0.0'
        if ($version -lt $minimum) {
            return @{ Found = $false; Path = $path; Detail = "Filesystem MCP $version найден, требуется $minimum или новее" }
        }
        return @{ Found = $true; Path = $path; Detail = "$path (version $version)" }
    }
    catch {
        return @{ Found = $false; Path = $path; Detail = "Не удалось проверить версию Filesystem MCP: $($_.Exception.Message)" }
    }
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

function Test-Supergateway {
    $candidates = @(
        (Join-Path $AppDirectory 'Dependencies\supergateway.exe'),
        (Join-Path $env:LOCALAPPDATA 'Supergateway\supergateway.exe'),
        (Join-Path $env:ProgramFiles 'Supergateway\supergateway.exe'),
        (Find-OnPath 'supergateway.exe')
    )
    $path = Get-FirstExistingFile $candidates
    if (-not $path) { return @{ Found = $false; Detail = 'standalone supergateway.exe не найден' } }

    $version = Get-ExecutableVersion -Path $path -Arguments '--version'
    $minimum = [version]'4.2.1'
    if (-not $version) {
        return @{ Found = $false; Detail = "$path найден, но версию определить не удалось; требуется $minimum или новее" }
    }
    if ($version -lt $minimum) {
        return @{ Found = $false; Detail = "Supergateway $version найден, требуется $minimum или новее: $path" }
    }
    return @{ Found = $true; Detail = "$path (version $version)" }
}

function Test-DotNet48 {
    $release = 0
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = $null
        $key = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
            $key = $base.OpenSubKey('SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full')
            if ($key) {
                $value = $key.GetValue('Release', 0)
                if ($value -is [int] -or $value -is [long]) {
                    if ([int]$value -gt $release) { $release = [int]$value }
                }
            }
        }
        catch { }
        finally {
            if ($key) { $key.Dispose() }
            if ($base) { $base.Dispose() }
        }
    }

    # .NET Framework 4.8 starts at Release 528040. Newer in-place 4.x updates
    # have a larger Release value and also satisfy this minimum.
    $requiredRelease = 528040
    if ($release -ge $requiredRelease) {
        return @{
            Found = $true
            Detail = ".NET Framework 4.8+ (Release=$release, минимум=$requiredRelease)"
            Release = $release
            RequiredRelease = $requiredRelease
        }
    }

    return @{
        Found = $false
        Detail = ".NET Framework 4.8 не обнаружен (Release=$release, требуется >= $requiredRelease)"
        Release = $release
        RequiredRelease = $requiredRelease
    }
}

$checks = [ordered]@{
    Node = Test-NodeRuntime
    FilesystemMcp = Test-FilesystemMcp
    Supergateway = Test-Supergateway
    DotNet48 = Test-DotNet48
}

# Validate the selected Node runtime against the actual Filesystem MCP package engine range.
if ($checks.Node.Found) {
    try {
        $packagePath = $null
        if ($checks.FilesystemMcp.Found -and $checks.FilesystemMcp.Path) {
            $scriptPath = [string]$checks.FilesystemMcp.Path
            $packagePath = Join-Path (Split-Path -Parent (Split-Path -Parent $scriptPath)) 'package.json'
        }
        elseif ($PayloadFilesystemPackageJson -and (Test-Path -LiteralPath $PayloadFilesystemPackageJson -PathType Leaf)) {
            $packagePath = $PayloadFilesystemPackageJson
        }
        elseif ($checks.FilesystemMcp.Path) {
            $scriptPath = [string]$checks.FilesystemMcp.Path
            $packagePath = Join-Path (Split-Path -Parent (Split-Path -Parent $scriptPath)) 'package.json'
        }

        if ($packagePath -and (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            $manifest = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
            $range = [string]$manifest.engines.node
            if ($range -match '>=\s*(?<minimum>\d+(?:\.\d+){0,2})') {
                $minimumText = $Matches.minimum
                if ($minimumText.Split('.').Count -eq 1) { $minimumText += '.0.0' }
                elseif ($minimumText.Split('.').Count -eq 2) { $minimumText += '.0' }
                $nodeVersionText = [regex]::Match($checks.Node.Detail, '\((v?\d+\.\d+\.\d+)\)').Groups[1].Value.TrimStart('v')
                if ($nodeVersionText) {
                    $installedNodeVersion = [version]$nodeVersionText
                    $requiredNodeVersion = [version]$minimumText
                    if ($installedNodeVersion -lt $requiredNodeVersion) {
                        $checks.Node = @{
                            Found = $false
                            Detail = "Filesystem MCP требует Node.js $range, найден $installedNodeVersion"
                            Path = [string]$checks.Node.Path
                            MsiInstallDirectory = [string]$checks.Node.MsiInstallDirectory
                        }
                    }
                }
            }
        }
    }
    catch {
        $checks.Node = @{
            Found = $false
            Detail = "не удалось проверить совместимость Node.js с Filesystem MCP: $($_.Exception.Message)"
            Path = [string]$checks.Node.Path
            MsiInstallDirectory = [string]$checks.Node.MsiInstallDirectory
        }
    }
}

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $checks.GetEnumerator()) {
    $lines.Add("$($entry.Key)=$([int][bool]$entry.Value.Found)")
    $lines.Add("$($entry.Key)Detail=$($entry.Value.Detail -replace '[\r\n]+', ' ')")
    if ($entry.Key -eq 'Node') {
        $lines.Add("NodePath=$($entry.Value.Path)")
        $lines.Add("NodeMsiInstallDirectory=$($entry.Value.MsiInstallDirectory)")
    }
    if ($entry.Key -eq 'DotNet48') {
        $lines.Add("DotNet48Release=$($entry.Value.Release)")
        $lines.Add("DotNet48RequiredRelease=$($entry.Value.RequiredRelease)")
    }
}
$lines.Insert(0, 'Encoding=UTF-8')
[System.IO.File]::WriteAllLines($OutputFile, $lines, [System.Text.UTF8Encoding]::new($true))
exit 0
