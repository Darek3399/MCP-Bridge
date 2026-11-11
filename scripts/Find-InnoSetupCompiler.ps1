[CmdletBinding()]
param(
    [string]$ReportPath = ""
)

$ErrorActionPreference = "SilentlyContinue"
$messages = [System.Collections.Generic.List[string]]::new()
$candidates = [System.Collections.Generic.List[string]]::new()

function Add-CompilerCandidate([string]$Path, [string]$Source) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if (Test-Path -LiteralPath $expanded -PathType Container) {
        $expanded = Join-Path $expanded "ISCC.exe"
    }

    if (Test-Path -LiteralPath $expanded -PathType Leaf) {
        $resolved = (Resolve-Path -LiteralPath $expanded).Path
        $alreadyAdded = $false
        foreach ($candidate in $candidates) {
            if ([string]::Equals($candidate, $resolved, [StringComparison]::OrdinalIgnoreCase)) {
                $alreadyAdded = $true
                break
            }
        }

        if (-not $alreadyAdded) {
            $candidates.Add($resolved)
            $messages.Add("FOUND [$Source] $resolved")
        }
    }
}

$messages.Add("Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")

# 1. PATH lookup.
$pathCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pathCommand -and $pathCommand.Source) {
    Add-CompilerCandidate $pathCommand.Source "PATH"
}

# 2. Conventional per-machine and per-user installation locations.
$programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
$programRoots = @($env:ProgramFiles, $programFilesX86, $env:LOCALAPPDATA, $env:APPDATA) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique

foreach ($root in $programRoots) {
    foreach ($folder in @("Inno Setup 7", "Inno Setup 6", "Programs\Inno Setup 7", "Programs\Inno Setup 6")) {
        Add-CompilerCandidate (Join-Path $root "$folder\ISCC.exe") "common path"
    }
}

# 3. Registry uninstall records.
$uninstallKeys = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
)

foreach ($keyPath in $uninstallKeys) {
    $entries = Get-ItemProperty -Path $keyPath -ErrorAction SilentlyContinue
    foreach ($entry in $entries) {
        if ([string]$entry.DisplayName -notmatch "Inno\s+Setup") { continue }

        $messages.Add("REGISTRY [$($entry.DisplayName)]")

        if ($entry.InstallLocation) {
            Add-CompilerCandidate ([string]$entry.InstallLocation) "registry InstallLocation"
            Add-CompilerCandidate (Join-Path ([string]$entry.InstallLocation) "ISCC.exe") "registry InstallLocation"
        }

        $uninstallValue = [string]$entry.UninstallString
        if ($uninstallValue -match '^\s*"(?<exe>[^"]+\.exe)"') {
            $uninstallerPath = $Matches.exe
            if (Test-Path -LiteralPath $uninstallerPath -PathType Leaf) {
                Add-CompilerCandidate (Join-Path (Split-Path -Parent $uninstallerPath) "ISCC.exe") "registry UninstallString"
            }
        }
    }
}

# 4. App Paths registry entries.
$appPathKeys = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ISCC.exe",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\ISCC.exe",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ISCC.exe"
)

foreach ($appPathKey in $appPathKeys) {
    $appPath = Get-ItemProperty -LiteralPath $appPathKey -ErrorAction SilentlyContinue
    if ($appPath -and $appPath.'(default)') {
        Add-CompilerCandidate ([string]$appPath.'(default)') "App Paths registry"
    }
}

# 5. Last resort: recursively search each ready local fixed drive.
if ($candidates.Count -eq 0) {
    $whereExe = Join-Path $env:SystemRoot "System32\where.exe"
    if (-not (Test-Path -LiteralPath $whereExe -PathType Leaf)) {
        $whereExe = "where.exe"
    }

    $fixedDrives = [System.IO.DriveInfo]::GetDrives() |
        Where-Object { $_.DriveType -eq [System.IO.DriveType]::Fixed -and $_.IsReady }

    foreach ($drive in $fixedDrives) {
        $root = $drive.RootDirectory.FullName
        $messages.Add("FULL SCAN $root")
        $hits = & $whereExe /r $root "ISCC.exe" 2>$null

        foreach ($hit in $hits) {
            $hitPath = [string]$hit
            if (Test-Path -LiteralPath $hitPath -PathType Leaf) {
                Add-CompilerCandidate $hitPath "full drive scan $root"
            }
        }
    }
}

$selected = $null
if ($candidates.Count -gt 0) {
    $selected = $candidates |
        Sort-Object -Unique |
        Sort-Object @{
            Expression = {
                if ($_ -match "(?i)Inno Setup 7") { 0 }
                elseif ($_ -match "(?i)Inno Setup 6") { 1 }
                else { 2 }
            }
        } |
        Select-Object -First 1

    $messages.Add("SELECTED $selected")
}
else {
    $messages.Add("РЕЗУЛЬТАТ: ISCC.exe не найден в PATH, реестре, стандартных каталогах или на локальных дисках.")
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $ReportPath = Join-Path $repoRoot "Work\Diagnostics\Installer\compiler-discovery.txt"
}

$reportDirectory = Split-Path -Parent $ReportPath
if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
}

[System.IO.File]::WriteAllLines($ReportPath, $messages, [System.Text.UTF8Encoding]::new($false))

if ($selected) {
    Write-Host "Найден Inno Setup Compiler: $selected"
    Write-Host "Отчёт поиска: $ReportPath"
    Write-Output $selected
}
else {
    Write-Host "ISCC.exe не найден. Проверены PATH, реестр, стандартные каталоги и локальные диски."
    Write-Host "Search report: $ReportPath"
}