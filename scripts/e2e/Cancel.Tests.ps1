<#
Cancel: start a large paste, press Cancel in the progress window through UI Automation
(name "Cancel"), then check the destination.

  A. Files that finished are real copies; a partial file must not be left behind; a
     destination that existed beforehand must be untouched.
  B. A late arrival: a file put in the destination after the job's presence check but before
     robocopy looked at the destination. Robocopy skips it without a word (Skip flags), so the
     job never hears of it. The cancel must not delete it: it is not a partial copy. The next
     robocopy is suspended as it starts (Start-RobocopyCatcher) to open that window; if robocopy
     had already started writing when it was caught, the run is reported as inconclusive.

  C. A cross-volume cut canceled midway (needs -SecondVolume): every file is either still in
     the source, unchanged, or complete at the destination; no partial copy is left and no
     source is lost (invariant 1).

Needs an interactive desktop session and an installed app. Copies are throttled with
robocopy's /IORATE (an allowed extraArgs switch) so a fast disk cannot finish before the
progress window opens; raise -FileMB or lower -IoRate if the job still finishes first.
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    [int]$Files = 8,
    [int]$FileMB = 256,
    # robocopy /IORATE value for the test's copies.
    [string]$IoRate = '8M',
    # A writable folder on another volume with room for -CutFiles x -CutFileMB (scenario C).
    [string]$SecondVolume,
    [int]$CutFiles = 6,
    [int]$CutFileMB = 32,
    # Which scenarios to run. C runs only with -SecondVolume.
    [ValidateSet('A', 'B', 'C')][string[]]$Scenarios = @('A', 'B', 'C')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root
Assert-Installed

Write-Host 'Cancel.Tests'
$work = Join-Path $script:E2E.Root 'cancel'
Remove-TreeIfPresent $work

# Presses the progress window's Cancel while robocopy is still running.
function Invoke-CancelWhileRunning {
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

# Everything new under $Dest is a complete copy of its file under $Src.
function Assert-NoPartialFiles([string]$Src, [string]$Dest, [string[]]$NamesBefore, [string]$Label) {
    $after = Get-TreeEntries $Dest
    $partial = @()
    foreach ($entry in $after.Values) {
        if ($entry.IsDir -or $NamesBefore -contains $entry.Rel) { continue }
        $source = Join-Path $Src $entry.Rel
        $complete = (Test-Path -LiteralPath $source) -and
            ((New-Object IO.FileInfo $source).Length -eq $entry.Size) -and
            ((Get-FileSha256 $source) -eq (Get-FileSha256 $entry.Full))
        if (-not $complete) { $partial += $entry.Rel }
    }
    Assert-That ($partial.Count -eq 0) ("[$Label] no partial file was left behind" + $(if ($partial.Count) { ': ' + ($partial -join ', ') } else { '' }))
    return @($after.Values | Where-Object { -not $_.IsDir -and $NamesBefore -notcontains $_.Rel }).Count
}

$originalConfig = Get-RoboConfigText
try {
    Set-RoboConfig @{ conflictDefault = 'skip'; showProgressWindow = $true; extraArgs = @{ copy = "/IORATE:$IoRate"; move = "/IORATE:$IoRate" } }

    # ---- A. partial files go, pre-existing destinations stay ----
    if ($Scenarios -contains 'A') {
    $src = Join-Path $work 'bigsrc'
    $destParent = Join-Path $work 'dest'
    $destDir = Join-Path $destParent 'bigsrc'
    New-Item -ItemType Directory -Path $src, $destDir | Out-Null
    for ($i = 0; $i -lt $Files; $i++) { New-DataFile (Join-Path $src "big-$i.bin") ([long]$FileMB * 1MB) (20 + $i) }
    Write-Step "[A] created $Files files of $FileMB MB"

    # Destinations that exist before the job. With conflictDefault=skip the job leaves them
    # alone, so a cancel that deletes or rewrites them is a defect.
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

    Assert-That ((Invoke-Robo -Verb copy -Paths $src) -eq 0) '[A] Robo-Copy accepted the folder'
    Assert-That ((Invoke-Robo -Verb paste -Paths $destParent) -eq 0) '[A] Robo-Paste accepted the destination'
    Invoke-CancelWhileRunning

    foreach ($name in $preExisting.Keys) {
        $path = Join-Path $destDir $name
        Assert-That (Test-Path -LiteralPath $path) "[A] pre-existing $name still exists"
        Assert-That ((Get-FileSha256 $path) -eq $preHashes[$name]) "[A] pre-existing $name is unchanged"
    }
    $finished = Assert-NoPartialFiles $src $destDir $namesBefore 'A'
    Write-Step "[A] $finished of $Files files had finished before the cancel took effect"
    Assert-That ($finished -lt ($Files - 1)) '[A] the cancel stopped the job before it copied everything'
    }

    # ---- B. a late arrival robocopy skipped is not deleted ----
    if ($Scenarios -contains 'B') {
    $lateSrc = Join-Path $work 'latesrc'
    $lateDestParent = Join-Path $work 'latedest'
    $lateDestDir = Join-Path $lateDestParent 'latesrc'
    # Robocopy works through the tree in name order: "a" (large files) first, then "m".
    New-Item -ItemType Directory -Path (Join-Path $lateSrc 'a'), (Join-Path $lateSrc 'm'), $lateDestParent | Out-Null
    for ($i = 0; $i -lt $Files; $i++) { New-DataFile (Join-Path $lateSrc "a\big-$i.bin") ([long]$FileMB * 1MB) (40 + $i) }
    Set-Content -LiteralPath (Join-Path $lateSrc 'm\late.txt') -Value 'the version being pasted'
    $late = Join-Path $lateDestDir 'm\late.txt'

    Assert-That ((Invoke-Robo -Verb copy -Paths $lateSrc) -eq 0) '[B] Robo-Copy accepted the folder'
    $catcher = Start-RobocopyCatcher
    try {
        Assert-That ((Invoke-Robo -Verb paste -Paths $lateDestParent) -eq 0) '[B] Robo-Paste accepted the destination'
        if (-not $catcher.WaitCaught(60000)) { throw '[B] robocopy did not start within 60 s.' }
        Write-Step "[B] robocopy $($catcher.ProcessId) suspended as it started"
        if (Test-Path -LiteralPath $lateDestDir) {
            throw '[B] INCONCLUSIVE: robocopy had already created the destination when it was caught. Run again.'
        }
        # After the job's presence check (it ran before robocopy started), before robocopy's look.
        New-Item -ItemType Directory -Force -Path (Split-Path $late) | Out-Null
        Set-Content -LiteralPath $late -Value 'a file another program put here a moment ago'
        $lateHash = Get-FileSha256 $late
        Assert-That ($catcher.Resume()) '[B] robocopy resumed'
    }
    finally { $catcher.Dispose() }

    Wait-PathExists (Join-Path $lateDestDir 'a\big-0.bin')
    # Robocopy has listed every folder by the time its first large file is under way.
    Start-Sleep -Milliseconds 1500
    Invoke-CancelWhileRunning

    Assert-That (Test-Path -LiteralPath $late) '[B] the late arrival still exists after the cancel'
    Assert-That ((Get-FileSha256 $late) -eq $lateHash) '[B] the late arrival is unchanged'
    $null = Assert-NoPartialFiles $lateSrc $lateDestDir @('m\late.txt') 'B'
    }

    # ---- C. a canceled cross-volume cut loses nothing ----
    if ($Scenarios -contains 'C' -and $SecondVolume) {
        New-Item -ItemType Directory -Force -Path $SecondVolume | Out-Null
        $cutDestParent = Join-Path (Resolve-Path -LiteralPath $SecondVolume).ProviderPath 'rrc-e2e-cancel-cut'
        if ([IO.Path]::GetPathRoot($cutDestParent) -ieq [IO.Path]::GetPathRoot($work)) { throw '-SecondVolume must be on another volume.' }
        Remove-TreeIfPresent $cutDestParent
        New-Item -ItemType Directory -Path $cutDestParent | Out-Null
        $cutSrc = Join-Path $work 'cutsrc'
        New-Item -ItemType Directory -Path $cutSrc | Out-Null
        $hashes = @{}
        for ($i = 0; $i -lt $CutFiles; $i++) {
            $f = Join-Path $cutSrc "part-$i.bin"
            # The first file is small, so it is usually moved before the cancel lands.
            New-DataFile $f $(if ($i -eq 0) { 1MB } else { [long]$CutFileMB * 1MB }) (60 + $i)
            $hashes["part-$i.bin"] = Get-FileSha256 $f
        }
        $cutDestDir = Join-Path $cutDestParent 'cutsrc'

        Assert-That ((Invoke-Robo -Verb cut -Paths $cutSrc) -eq 0) '[C] Robo-Cut accepted the folder'
        Assert-That ((Invoke-Robo -Verb paste -Paths $cutDestParent) -eq 0) '[C] Robo-Paste accepted the destination'
        Invoke-CancelWhileRunning

        $inSource = 0; $moved = 0
        foreach ($name in $hashes.Keys) {
            $s = Join-Path $cutSrc $name
            $d = Join-Path $cutDestDir $name
            $sourceOk = (Test-Path -LiteralPath $s) -and (Get-FileSha256 $s) -eq $hashes[$name]
            $destOk = (Test-Path -LiteralPath $d) -and (Get-FileSha256 $d) -eq $hashes[$name]
            Assert-That ($sourceOk -or $destOk) "[C] $name survives: unchanged in the source or complete at the destination"
            if ($sourceOk) { $inSource++ }
            if ($destOk -and -not $sourceOk) { $moved++ }
            if ((Test-Path -LiteralPath $d) -and -not $destOk) { throw "ASSERT FAILED: [C] a partial copy of $name was left at the destination" }
        }
        Write-Step "[C] $moved moved, $inSource still in the source"
        Assert-That ($inSource -gt 0) '[C] the cancel stopped the cut before it moved everything'
        Remove-TreeIfPresent $cutDestParent
    }
}
finally {
    Restore-RoboConfig $originalConfig
}

Write-Host 'PASS: Cancel'
