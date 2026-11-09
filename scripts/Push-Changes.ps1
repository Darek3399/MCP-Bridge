# Commit all current repository changes and push the current branch to origin.
# Usage:
#   .\scripts\Push-Changes.ps1
#   .\scripts\Push-Changes.ps1 -Message "Fix MCP startup handling"
#   .\scripts\Push-Changes.ps1 -Message "Update adapters" -Yes

[CmdletBinding()]
param(
    [string]$Message,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $gitCommand) {
    $gitCommand = Get-Command git -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $gitCommand -or -not $gitCommand.Source) {
    throw 'Git was not found. Install Git for Windows and make sure git.exe is available in PATH.'
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & $gitCommand.Source -C $repoRoot @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        $details = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        $commandText = 'git ' + ($Arguments -join ' ')
        if ([string]::IsNullOrWhiteSpace($details)) {
            throw "$commandText failed with exit code $exitCode."
        }

        throw ($commandText + ' failed with exit code ' + $exitCode + '.' + [Environment]::NewLine + $details)
    }

    return $output
}

# Verify that this script is running from the repository it belongs to.
$actualRoot = (Invoke-Git -Arguments @('rev-parse', '--show-toplevel') | Select-Object -First 1).ToString().Trim()
$expectedRoot = [System.IO.Path]::GetFullPath($repoRoot)
$actualRoot = [System.IO.Path]::GetFullPath($actualRoot)

if (-not $actualRoot.Equals($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Repository root mismatch. Expected '$expectedRoot', got '$actualRoot'."
}

# A detached HEAD cannot be safely pushed as the current branch.
$branch = (Invoke-Git -Arguments @('symbolic-ref', '--quiet', '--short', 'HEAD') | Select-Object -First 1).ToString().Trim()
if ([string]::IsNullOrWhiteSpace($branch)) {
    throw 'HEAD is detached. Check out a branch before running this script.'
}

# Use the repository's configured origin; do not hardcode a personal remote URL.
$originUrl = (Invoke-Git -Arguments @('remote', 'get-url', 'origin') | Select-Object -First 1).ToString().Trim()
if ([string]::IsNullOrWhiteSpace($originUrl)) {
    throw "Remote 'origin' has no URL configured."
}

Write-Host "Repository: $actualRoot"
Write-Host "Branch:     $branch"
Write-Host "Remote:     $originUrl"
Write-Host ''

# Stage tracked changes, deletions, and new non-ignored files.
$null = Invoke-Git -Arguments @('add', '--all')

$pendingChanges = @(Invoke-Git -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
if ($pendingChanges.Count -gt 0) {
    Write-Host 'Changes prepared for commit:'
    foreach ($change in $pendingChanges) {
        Write-Host "  $change"
    }

    # Catch whitespace errors before creating a commit.
    $null = Invoke-Git -Arguments @('diff', '--cached', '--check')

    if ([string]::IsNullOrWhiteSpace($Message) -and -not $Yes) {
        $Message = Read-Host 'Commit message (press Enter for an automatic message)'
    }

    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = 'Update latest changes ' + (Get-Date -Format 'yyyy-MM-dd HH:mm')
    }

    $Message = $Message.Trim()
    Write-Host ''
    Write-Host "Commit message: $Message"
}
else {
    Write-Host 'Working tree is clean. No new commit will be created.'
    Write-Host 'The script will still push the current branch in case local commits are waiting to be sent.'
    Write-Host ''
}

if (-not $Yes) {
    $confirmation = Read-Host 'Commit (when needed) and push to origin? [y/N]'
    if ($confirmation -notmatch '^(?i:y|yes)$') {
        Write-Host 'Cancelled. No commit or push was performed.'
        exit 0
    }
}

if ($pendingChanges.Count -gt 0) {
    $commitOutput = Invoke-Git -Arguments @('commit', '-m', $Message)
    foreach ($line in $commitOutput) {
        Write-Host $line
    }
}
else {
    Write-Host 'Skipping commit.'
}

Write-Host ''
Write-Host "Pushing '$branch' to origin..."

# -u is safe to use repeatedly and also configures upstream on the first push.
$pushOutput = Invoke-Git -Arguments @('push', '--set-upstream', 'origin', $branch)
foreach ($line in $pushOutput) {
    Write-Host $line
}

$commitHash = (Invoke-Git -Arguments @('rev-parse', '--short', 'HEAD') | Select-Object -First 1).ToString().Trim()

Write-Host ''
Write-Host "Push completed successfully. Current commit: $commitHash" -ForegroundColor Green
