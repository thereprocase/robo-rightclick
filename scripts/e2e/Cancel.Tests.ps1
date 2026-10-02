<#
Cancel: start a large paste, press Cancel in the progress window through UI Automation
(name "Cancel"), then check the destination. Files that finished are real copies; a partial
file must not be left behind; a destination that existed beforehand must be untouched.
Needs an interactive desktop session and an installed app. Raise -FileMB or -Files if the job
finishes before the progress window can be clicked.
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    [int]$Files = 8,
    [int]$FileMB = 256
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root
Assert-Installed

Write-Host 'Cancel.Tests'
$work = Join-Path $script:E2E.Root 'cancel'
Remove-TreeIfPresent $work
$src = Join-Path $work 'bigsrc'
$destParent = Join-Path $work 'dest'
$destDir = Join-Path $destParent 'bigsrc'
New-Item -ItemType Directory -Path $src, $destDir | Out-Null

for ($i = 0; $i -lt $Files; $i++) { New-DataFile (Join-Path $src "big-$i.bin") ([long]$FileMB * 1MB) (20 + $i) }
Write-Step "created $Files files of $FileMB MB"

# Destinations that exist before the job. With conflictDefault=skip the job leaves them alone,
# so a cancel that deletes or rewrites them is a defect.
$preExisting = @{
    'big-0.bin' = 'a different file that was already here'
    'keep.txt'  = 'unrelated file already in the destination'
}
foreach ($name in $preExisting.Keys) {
    Set-Content -LiteralPath (Join-Path $destDir $name) -Value $preExisting[$name]
}
$preHashes = @{}
foreach ($name in $preExisting.Keys) { $preHashes[$name] = Get-FileSha256 (Join-Path $destDir $name) }
$namesBefore = @((Get-TreeEntries $destDir).Keys)

$originalConfig = Get-RoboConfigText
try {
    Set-RoboConfig @{ conflictDefault = 'skip'; showProgressWindow = $true }

    Assert-That ((Invoke-Robo -Verb copy -Paths $src) -eq 0) 'Robo-Copy accepted the folder'
    Assert-That ((Invoke-Robo -Verb paste -Paths $destParent) -eq 0) 'Robo-Paste accepted the destination'

    # Find the Cancel button in the tray's progress window.
    $button = $null
    $deadline = (Get-Date).AddSeconds(60)
    while ($null -eq $button -and (Get-Date) -lt $deadline) {
        foreach ($p in Get-RoboProcesses) { if ($null -eq $button) { $button = Find-UiaButton $p.Id 'Cancel' } }
        if ($null -eq $button) { Start-Sleep -Milliseconds 200 }
    }
    if ($null -eq $button) { throw 'No progress window with a Cancel button appeared. The job may have finished first: raise -FileMB.' }
    $running = @(Get-Process -Name 'robocopy' -ErrorAction SilentlyContinue).Count -gt 0
    Assert-That $running 'the job was still running when Cancel was pressed'
    Invoke-UiaButton $button
    Write-Step 'pressed Cancel'

    Wait-RobocopyGone -TimeoutSec 120
    Start-Sleep -Seconds 3
    Close-TrayDialogs
}
finally {
    Restore-RoboConfig $originalConfig
}

# Pre-existing destinations are untouched.
foreach ($name in $preExisting.Keys) {
    $path = Join-Path $destDir $name
    Assert-That (Test-Path -LiteralPath $path) "pre-existing $name still exists"
    Assert-That ((Get-FileSha256 $path) -eq $preHashes[$name]) "pre-existing $name is unchanged"
}

# Everything new in the destination is a complete copy of its source file.
$after = Get-TreeEntries $destDir
$partial = @()
foreach ($entry in $after.Values) {
    if ($entry.IsDir -or $namesBefore -contains $entry.Rel) { continue }
    $source = Join-Path $src $entry.Rel
    $complete = (Test-Path -LiteralPath $source) -and
        ((New-Object IO.FileInfo $source).Length -eq $entry.Size) -and
        ((Get-FileSha256 $source) -eq (Get-FileSha256 $entry.Full))
    if (-not $complete) { $partial += $entry.Rel }
}
Assert-That ($partial.Count -eq 0) ("no partial file was left behind" + $(if ($partial.Count) { ': ' + ($partial -join ', ') } else { '' }))
$finished = @($after.Values | Where-Object { -not $_.IsDir -and $namesBefore -notcontains $_.Rel }).Count
Write-Step "$finished of $Files files had finished before the cancel took effect"
Assert-That ($finished -lt ($Files - 1)) 'the cancel stopped the job before it copied everything'

Write-Host 'PASS: Cancel'
