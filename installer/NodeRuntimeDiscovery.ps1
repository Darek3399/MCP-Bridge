function Add-NodeExecutableCandidate {
    param(
        [System.Collections.Generic.List[string]]$Candidates,
        [AllowNull()][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) { return }
    $expanded = [Environment]::ExpandEnvironmentVariables($Value.Trim().Trim('"'))
    $expanded = $expanded.Trim()
    if ($expanded -match '^(?<path>.+?\.exe)(?:,\d+)?$') {
        $expanded = $Matches.path.Trim('"')
    }

    if ([IO.Path]::GetFileName($expanded) -ieq 'node.exe') {
        if (-not $Candidates.Contains($expanded)) { $Candidates.Add($expanded) }
        return
    }

    if (Test-Path -LiteralPath $expanded -PathType Container) {
        $nodePath = Join-Path $expanded 'node.exe'
        if (-not $Candidates.Contains($nodePath)) { $Candidates.Add($nodePath) }
    }
}

function Get-InstalledNodeCandidates {
    $candidates = [System.Collections.Generic.List[string]]::new()

    # Standard MSI locations, including custom Program Files roots.
    foreach ($directory in @(
        (Join-Path $env:ProgramFiles 'nodejs'),
        (Join-Path ${env:ProgramFiles(x86)} 'nodejs'),
        (Join-Path $env:LOCALAPPDATA 'Programs\nodejs'),
        (Join-Path $env:APPDATA 'Programs\nodejs'),
        (Join-Path $env:ProgramData 'chocolatey\bin'),
        (Join-Path $env:LOCALAPPDATA 'Volta\bin'),
        $env:NODE_HOME,
        $env:NODEJS_HOME,
        (Join-Path $env:USERPROFILE 'scoop\apps\nodejs\current')
    )) {
        Add-NodeExecutableCandidate -Candidates $candidates -Value $directory
    }

    # Manager/version-manager environment roots.
    foreach ($name in @('NVM_SYMLINK', 'VOLTA_HOME', 'FNM_DIR', 'NVS_HOME', 'SCOOP', 'ChocolateyInstall', 'NODE_HOME', 'NODEJS_HOME')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            if ($name -eq 'NVM_SYMLINK' -or $name -eq 'VOLTA_HOME') {
                Add-NodeExecutableCandidate -Candidates $candidates -Value (Join-Path $value 'node.exe')
                Add-NodeExecutableCandidate -Candidates $candidates -Value (Join-Path $value 'bin')
            }
            elseif ($name -eq 'FNM_DIR') {
                $versionsRoot = Join-Path $value 'node-versions'
                if (Test-Path -LiteralPath $versionsRoot -PathType Container) {
                    foreach ($versionDirectory in Get-ChildItem -LiteralPath $versionsRoot -Directory -ErrorAction SilentlyContinue) {
                        Add-NodeExecutableCandidate -Candidates $candidates -Value (Join-Path $versionDirectory.FullName 'installation')
                    }
                }
            }
            elseif ($name -eq 'SCOOP') {
                Add-NodeExecutableCandidate -Candidates $candidates -Value (Join-Path $value 'apps\nodejs\current')
            }
            elseif ($name -eq 'ChocolateyInstall') {
                Add-NodeExecutableCandidate -Candidates $candidates -Value (Join-Path $value 'bin')
            }
            elseif ($name -eq 'NODE_HOME' -or $name -eq 'NODEJS_HOME') {
                Add-NodeExecutableCandidate -Candidates $candidates -Value $value
            }
            elseif ($name -eq 'NVS_HOME') {
                $nodeRoot = Join-Path $value 'node'
                if (Test-Path -LiteralPath $nodeRoot -PathType Container) {
                    foreach ($versionDirectory in Get-ChildItem -LiteralPath $nodeRoot -Directory -ErrorAction SilentlyContinue) {
                        Add-NodeExecutableCandidate -Candidates $candidates -Value $versionDirectory.FullName
                        foreach ($architectureDirectory in Get-ChildItem -LiteralPath $versionDirectory.FullName -Directory -ErrorAction SilentlyContinue) {
                            Add-NodeExecutableCandidate -Candidates $candidates -Value (Join-Path $architectureDirectory.FullName 'bin')
                        }
                    }
                }
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:NVM_HOME) -and (Test-Path -LiteralPath $env:NVM_HOME -PathType Container)) {
        foreach ($versionDirectory in Get-ChildItem -LiteralPath $env:NVM_HOME -Directory -Filter 'v*' -ErrorAction SilentlyContinue) {
            Add-NodeExecutableCandidate -Candidates $candidates -Value $versionDirectory.FullName
        }
    }

    # Search PATH from the current process and both persistent Windows scopes.
    foreach ($scope in @(
        [EnvironmentVariableTarget]::Process,
        [EnvironmentVariableTarget]::User,
        [EnvironmentVariableTarget]::Machine
    )) {
        try {
            $pathValue = [Environment]::GetEnvironmentVariable('Path', $scope)
            foreach ($directory in ([string]$pathValue -split ';')) {
                if (-not [string]::IsNullOrWhiteSpace($directory)) {
                    Add-NodeExecutableCandidate -Candidates $candidates -Value $directory.Trim().Trim('"')
                }
            }
        }
        catch { }
    }

    # Installed-program registry records, MSI Node.js registration and App Paths.
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $base = $null
            try {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
                foreach ($subPath in @(
                    'SOFTWARE\Node.js',
                    'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\node.exe'
                )) {
                    $key = $null
                    try {
                        $key = $base.OpenSubKey($subPath)
                        if ($key) {
                            foreach ($valueName in @('', 'InstallPath', 'InstallLocation', 'Path')) {
                                $value = $key.GetValue($valueName, $null)
                                if ($value -is [string]) {
                                    Add-NodeExecutableCandidate -Candidates $candidates -Value $value
                                }
                            }
                        }
                    }
                    catch { }
                    finally { if ($key) { $key.Dispose() } }
                }

                $uninstall = $null
                try {
                    $uninstall = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
                    if ($uninstall) {
                        foreach ($entryName in $uninstall.GetSubKeyNames()) {
                            $entry = $null
                            try {
                                $entry = $uninstall.OpenSubKey($entryName)
                                if (-not $entry) { continue }
                                $displayName = [string]$entry.GetValue('DisplayName', '')
                                if ($displayName -match '(?i)\bNode\.js\b') {
                                    foreach ($valueName in @('InstallLocation', 'DisplayIcon')) {
                                        $value = $entry.GetValue($valueName, $null)
                                        if ($value -is [string]) {
                                            Add-NodeExecutableCandidate -Candidates $candidates -Value $value
                                        }
                                    }
                                }
                            }
                            catch { }
                            finally { if ($entry) { $entry.Dispose() } }
                        }
                    }
                }
                catch { }
                finally { if ($uninstall) { $uninstall.Dispose() } }
            }
            catch { }
            finally { if ($base) { $base.Dispose() } }
        }
    }

    # PowerShell command resolution and Windows where.exe provide a final PATH lookup.
    foreach ($command in @(Get-Command 'node.exe' -All -ErrorAction SilentlyContinue)) {
        if ($command.Source) { Add-NodeExecutableCandidate -Candidates $candidates -Value $command.Source }
    }
    try {
        foreach ($line in @(& where.exe node.exe 2>$null)) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                Add-NodeExecutableCandidate -Candidates $candidates -Value $line.Trim()
            }
        }
    }
    catch { }

    return $candidates.ToArray()
}

function Get-NodeMsiInstallDirectory {
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $base = $null
            $key = $null
            try {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
                $key = $base.OpenSubKey('SOFTWARE\Node.js')
                if (-not $key) { continue }
                $installDirectory = [string]$key.GetValue('InstallPath', '')
                if ([string]::IsNullOrWhiteSpace($installDirectory)) { continue }
                $installDirectory = [Environment]::ExpandEnvironmentVariables($installDirectory.Trim().Trim('"'))
                $nodeExecutable = Join-Path $installDirectory 'node.exe'
                if (Test-Path -LiteralPath $nodeExecutable -PathType Leaf) {
                    return (Resolve-Path -LiteralPath $installDirectory).Path
                }
            }
            catch { }
            finally {
                if ($key) { $key.Dispose() }
                if ($base) { $base.Dispose() }
            }
        }
    }
    return ''
}

function Find-InstalledNodeRuntime {
    param([string]$MinimumVersion = '18.0.0')

    $valid = [System.Collections.Generic.List[object]]::new()
    $invalidFiles = [System.Collections.Generic.List[string]]::new()
    $msiInstallDirectory = Get-NodeMsiInstallDirectory
    foreach ($candidate in (Get-InstalledNodeCandidates)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        try {
            $versionText = (& $candidate --version 2>&1 | Out-String).Trim()
            $exitCode = $LASTEXITCODE
            $match = [regex]::Match($versionText, '(?m)^v(?<version>\d+\.\d+\.\d+)\s*$')
            if ($exitCode -eq 0 -and $match.Success) {
                $valid.Add([pscustomobject]@{
                    Path = (Resolve-Path -LiteralPath $candidate).Path
                    Version = [version]$match.Groups['version'].Value
                    VersionText = $versionText
                })
            }
            else {
                $invalidFiles.Add($candidate)
            }
        }
        catch { $invalidFiles.Add($candidate) }
    }

    if ($valid.Count -eq 0) {
        $detail = 'Node.js не найден среди установленных программ, MSI/App Paths, PATH и известных менеджеров версий'
        if ($invalidFiles.Count -gt 0) { $detail += '; обнаруженные node.exe не запустились: ' + ($invalidFiles -join ', ') }
        return [pscustomobject]@{ Found = $false; Path = ''; Version = $null; VersionText = ''; MsiInstallDirectory = $msiInstallDirectory; Detail = $detail }
    }

    $best = $valid | Sort-Object -Property Version -Descending | Select-Object -First 1
    $minimum = [version]$MinimumVersion
    if ($best.Version -lt $minimum) {
        return [pscustomobject]@{
            Found = $false; Path = $best.Path; Version = $best.Version; VersionText = $best.VersionText
            MsiInstallDirectory = $msiInstallDirectory
            Detail = "Найден установленный Node.js $($best.Version) ($($best.Path)), но требуется $minimum или новее"
        }
    }

    return [pscustomobject]@{
        Found = $true; Path = $best.Path; Version = $best.Version; VersionText = $best.VersionText
        MsiInstallDirectory = $msiInstallDirectory
        Detail = "$($best.Path) ($($best.VersionText))"
    }
}