<#
Cut safety (product invariant 1): a cut never deletes a source whose copy failed or whose
destination was not replaced.

  A. Cross-volume cut with a source file held open (FileShare.None): that source stays.
     Needs -SecondVolume, a folder on a different volume than -Root.
  B. conflictDefault=skip, destination has the same size and modified time but different
     content: the source is unchanged (hash). Runs on one volume, and across volumes when
     -SecondVolume is given.

Each scenario also cuts a file that can move, and waits for it to arrive, so a paste that was
refused or never started cannot pass as "the source was kept".

Without -SecondVolume the same-volume part of B runs and the script then exits as SKIP (code 3),
because the cross-volume checks, the ones robocopy /MOV is involved in, did not run.
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    # A writable folder on another volume, for example a small attached disk.
    [string]$SecondVolume
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root
Assert-Installed

Write-Host 'CutSafety.Tests'
$work = Join-Path $script:E2E.Root 'cutsafety'
Remove-TreeIfPresent $work
New-Item -ItemType Directory -Path $work | Out-Null

$second = $null
if ($SecondVolume) {
    New-Item -ItemType Directory -Force -Path $SecondVolume | Out-Null
    $second = Join-Path (Resolve-Path -LiteralPath $SecondVolume).Path 'rrc-e2e-cutsafety'
    $firstDrive = [IO.Path]::GetPathRoot($work).ToUpperInvariant()
    $secondDrive = [IO.Path]::GetPathRoot($second).ToUpperInvariant()
    if ($firstDrive -eq $secondDrive) { throw "-SecondVolume must be on a different volume than -Root (both on $firstDrive)." }
    Remove-TreeIfPresent $second
    New-Item -ItemType Directory -Path $second | Out-Null
}
else {
    Write-Host 'NOTE: -SecondVolume not given. Only the same-volume part of scenario B will run.'
}

$originalConfig = Get-RoboConfigText

function New-SameSizeAndTimeFile([string]$Path, [string]$Content, [DateTime]$Stamp) {
    # Equal length for any two contents of the same length; the stamp is set explicitly.
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding $false))
    [IO.File]::SetLastWriteTimeUtc($Path, $Stamp)
}

# A short pause after robocopy is gone lets the job finish its own bookkeeping (Finalizing)
# before the source is inspected; a deletion the app made late would still be seen.
function Wait-JobQuiet {
    Wait-RobocopyGone -TimeoutSec 180
    Start-Sleep -Seconds 3
}

function Test-SkipConflictCut([string]$DestRoot, [string]$Label) {
    $srcDir = Join-Path $work "b-src-$Label"
    $destParent = Join-Path $DestRoot "b-dest-$Label"
    $destDir = Join-Path $destParent "b-src-$Label"
    New-Item -ItemType Directory -Path $srcDir, $destDir | Out-Null
    $stamp = [DateTime]::new(2021, 5, 6, 7, 8, 9, [DateTimeKind]::Utc)
    $srcFile = Join-Path $srcDir 'same-looking.txt'
    $destFile = Join-Path $destDir 'same-looking.txt'
    New-SameSizeAndTimeFile $srcFile 'AAAAAAAAAAAAAAAA' $stamp
    New-SameSizeAndTimeFile $destFile 'BBBBBBBBBBBBBBBB' $stamp
    $movable = Join-Path $srcDir 'moves.txt'
    Set-Content -LiteralPath $movable -Value "no conflict, so the cut moves this file ($Label)"
    $srcHash = Get-FileSha256 $srcFile
    $destHash = Get-FileSha256 $destFile
    $movableHash = Get-FileSha256 $movable
    Assert-That ($srcHash -ne $destHash) "[$Label] source and destination differ in content but not in size or time"

    Assert-That ((Invoke-Robo -Verb cut -Paths $srcDir) -eq 0) "[$Label] Robo-Cut accepted the folder"
    Assert-That ((Invoke-Robo -Verb paste -Paths $destParent) -eq 0) "[$Label] Robo-Paste accepted the destination"
    $movedTo = Join-Path $destDir 'moves.txt'
    Wait-PathExists $movedTo
    Wait-JobQuiet
    Close-TrayDialogs

    Assert-That ((Get-FileSha256 $movedTo) -eq $movableHash) "[$Label] the job ran: the file without a conflict arrived intact"
    Assert-That (-not (Test-Path -LiteralPath $movable)) "[$Label] the moved file left the source, as a cut does"
    Assert-That (Test-Path -LiteralPath $srcFile) "[$Label] the skipped source file is still there"
    Assert-That ((Get-FileSha256 $srcFile) -eq $srcHash) "[$Label] the skipped source content is unchanged"
    Assert-That ((Get-FileSha256 $destFile) -eq $destHash) "[$Label] the skipped destination is unchanged"
}

try {
    Set-RoboConfig @{ conflictDefault = 'skip' }

    Test-SkipConflictCut -DestRoot $work -Label 'same-volume'
    if ($second) { Test-SkipConflictCut -DestRoot $second -Label 'cross-volume' }

    if ($second) {
        # Scenario A: one file cannot be read by robocopy because this process holds it open.
        $srcDir = Join-Path $work 'a-src'
        New-Item -ItemType Directory -Path $srcDir | Out-Null
        New-DataFile (Join-Path $srcDir 'free-1.bin') (3MB) 11
        New-DataFile (Join-Path $srcDir 'free-2.bin') (3MB) 12
        New-DataFile (Join-Path $srcDir 'locked.bin') (3MB) 13
        $hashes = @{}
        foreach ($f in Get-ChildItem -LiteralPath $srcDir -File) { $hashes[$f.Name] = Get-FileSha256 $f.FullName }

        $destParent = Join-Path $second 'a-dest'
        $destDir = Join-Path $destParent 'a-src'
        New-Item -ItemType Directory -Path $destParent | Out-Null
        $lockedSource = Join-Path $srcDir 'locked.bin'
        $lock = [IO.File]::Open($lockedSource, 'Open', 'Read', 'None')
        try {
            Assert-That ((Invoke-Robo -Verb cut -Paths $srcDir) -eq 0) '[A] Robo-Cut accepted the folder'
            Assert-That ((Invoke-Robo -Verb paste -Paths $destParent) -eq 0) '[A] Robo-Paste accepted the destination'
            foreach ($name in 'free-1.bin', 'free-2.bin') { Wait-PathExists (Join-Path $destDir $name) }
            Wait-JobQuiet
            Assert-That (Test-Path -LiteralPath $lockedSource) '[A] the locked source file is still there while locked'
        }
        finally { $lock.Dispose() }

        # Checked again after the lock is gone, so a deletion the app makes later is caught too.
        Start-Sleep -Seconds 3
        Close-TrayDialogs
        Assert-That (Test-Path -LiteralPath $lockedSource) '[A] the locked source file was not deleted'
        Assert-That ($hashes['locked.bin'] -eq (Get-FileSha256 $lockedSource)) '[A] the locked source is unchanged'
        foreach ($name in 'free-1.bin', 'free-2.bin') {
            $copy = Join-Path $destDir $name
            Assert-That ((Get-FileSha256 $copy) -eq $hashes[$name]) "[A] the job ran: $name arrived intact"
            Assert-That (-not (Test-Path -LiteralPath (Join-Path $srcDir $name))) "[A] $name left the source after its copy completed"
        }
    }
}
finally {
    Restore-RoboConfig $originalConfig
}

if (-not $second) {
    Exit-Skipped 'same-volume skip-conflict cut passed; scenario A and the cross-volume cut did not run. Pass -SecondVolume <folder on another drive> to run them.'
}
Write-Host 'PASS: CutSafety'
