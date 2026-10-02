<#
Cut safety (product invariant 1): a cut never deletes a source whose copy failed or whose
destination was not replaced.

  A. Cross-volume cut with a source file held open (FileShare.None): that source stays.
     Needs -SecondVolume, a folder on a different volume than -Root.
  B. conflictDefault=skip, destination has the same size and modified time but different
     content: the source is unchanged (hash). Runs on one volume, and across volumes when
     -SecondVolume is given.
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
    Write-Host 'NOTE: -SecondVolume not given. Scenario A and the cross-volume part of B are not run.'
}

$originalConfig = Get-RoboConfigText

# Both scenarios cut a folder holding the files below, pasted into $destRoot.
function New-SameSizeAndTimeFile([string]$Path, [string]$Content, [DateTime]$Stamp) {
    # Equal length for any two contents of the same length; the stamp is set explicitly.
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding $false))
    [IO.File]::SetLastWriteTimeUtc($Path, $Stamp)
}

function Test-SkipConflictCut([string]$DestRoot, [string]$Label) {
    $srcDir = Join-Path $work "b-src-$Label"
    $destDir = Join-Path $DestRoot "b-dest-$Label\b-src-$Label"
    New-Item -ItemType Directory -Path $srcDir, $destDir | Out-Null
    $stamp = [DateTime]::new(2021, 5, 6, 7, 8, 9, [DateTimeKind]::Utc)
    $srcFile = Join-Path $srcDir 'same-looking.txt'
    $destFile = Join-Path $destDir 'same-looking.txt'
    New-SameSizeAndTimeFile $srcFile 'AAAAAAAAAAAAAAAA' $stamp
    New-SameSizeAndTimeFile $destFile 'BBBBBBBBBBBBBBBB' $stamp
    $srcHash = Get-FileSha256 $srcFile
    $destHash = Get-FileSha256 $destFile
    Assert-That ($srcHash -ne $destHash) "[$Label] source and destination differ in content but not in size or time"

    Assert-That ((Invoke-Robo -Verb cut -Paths $srcDir) -eq 0) "[$Label] Robo-Cut accepted the folder"
    [void](Invoke-Robo -Verb paste -Paths (Split-Path -Parent $destDir))
    Start-Sleep -Seconds 4
    Wait-RobocopyGone
    Close-TrayDialogs

    Assert-That (Test-Path -LiteralPath $srcFile) "[$Label] the source file is still there"
    Assert-That ((Get-FileSha256 $srcFile) -eq $srcHash) "[$Label] the source content is unchanged"
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

        $destDir = Join-Path $second 'a-dest'
        New-Item -ItemType Directory -Path $destDir | Out-Null
        $lock = [IO.File]::Open((Join-Path $srcDir 'locked.bin'), 'Open', 'Read', 'None')
        try {
            Assert-That ((Invoke-Robo -Verb cut -Paths $srcDir) -eq 0) '[A] Robo-Cut accepted the folder'
            [void](Invoke-Robo -Verb paste -Paths $destDir)
            Start-Sleep -Seconds 4
            Wait-RobocopyGone -TimeoutSec 180
            Close-TrayDialogs
        }
        finally { $lock.Dispose() }

        Assert-That (Test-Path -LiteralPath (Join-Path $srcDir 'locked.bin')) '[A] the locked source file was not deleted'
        Assert-That ((Get-FileSha256 (Join-Path $srcDir 'locked.bin')) -eq $hashes['locked.bin']) '[A] the locked source is unchanged'
        # Every file that left the source must have arrived intact; nothing may vanish.
        foreach ($name in 'free-1.bin', 'free-2.bin') {
            $inSource = Test-Path -LiteralPath (Join-Path $srcDir $name)
            $copy = Get-ChildItem -LiteralPath $destDir -Recurse -File -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $inSource) {
                Assert-That ($null -ne $copy -and (Get-FileSha256 $copy.FullName) -eq $hashes[$name]) "[A] $name left the source only because its copy is complete"
            }
        }
    }
}
finally {
    Restore-RoboConfig $originalConfig
}

Write-Host 'PASS: CutSafety'
