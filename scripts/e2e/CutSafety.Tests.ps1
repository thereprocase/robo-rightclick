<#
Cut safety (product invariant 1): a cut never deletes a source whose copy failed or whose
destination was not replaced.

  A. Cross-volume cut with a source file held open (FileShare.None): that source stays.
     Needs -SecondVolume, a folder on a different volume than -Root.
  B. conflictDefault=skip, destination has the same size and modified time but different
     content: the source is unchanged (hash). Runs on one volume, and across volumes when
     -SecondVolume is given.
  C. Cross-volume cut into a folder the user may not create files in (a deny ACE): the
     refused file stays in the source. Needs -SecondVolume.
  D. Cross-volume cut of a file larger than the destination's free space: it stays in the
     source. Needs -SmallVolume, a folder on a nearly full volume (for example a 40 MB VHD).
  E. Same-volume cut of a folder is a rename: the file keeps its file ID.
  F. conflictDefault=ask, the conflict dialog answered through UI Automation: Replace, Skip,
     and "Let me decide" with both sides ticked (keep both on one volume; across volumes keep
     both is not offered and the source stays). Across volumes only with -SecondVolume.

Each scenario also cuts a file that can move, and waits for it to arrive, so a paste that was
refused or never started cannot pass as "the source was kept".

Without -SecondVolume the same-volume parts run and the script then exits as SKIP (code 3),
because the cross-volume checks, the ones robocopy /MOV is involved in, did not run.
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    # A writable folder on another volume, for example a small attached disk.
    [string]$SecondVolume,
    # A folder on a volume with less free space than -FullFileMB (scenario D).
    [string]$SmallVolume,
    [int]$FullFileMB = 64
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
    $second = Join-Path (Resolve-Path -LiteralPath $SecondVolume).ProviderPath 'rrc-e2e-cutsafety'
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

# The job ends with errors, so its summary stays open; a fresh tray starts the next scenario clean.
function Reset-TrayAfterErrors {
    Wait-JobQuiet
    Restart-Tray
}

# Scenario C: robocopy may not create files in the destination folder.
function Test-DeniedDestinationCut {
    $srcDir = Join-Path $work 'c-src'
    New-Item -ItemType Directory -Path (Join-Path $srcDir 'ok') | Out-Null
    $denied = Join-Path $srcDir 'denied.txt'
    Set-Content -LiteralPath $denied -Value 'robocopy may not create this file at the destination'
    $movable = Join-Path $srcDir 'ok\moves.txt'
    Set-Content -LiteralPath $movable -Value 'a subfolder robocopy may create'
    $deniedHash = Get-FileSha256 $denied
    $destParent = Join-Path $second 'c-dest'
    $destDir = Join-Path $destParent 'c-src'
    New-Item -ItemType Directory -Path $destDir | Out-Null

    # Deny "create files" (WD) on this folder only; creating subfolders stays allowed. icacls
    # changes only the DACL (Set-Acl would also try to write the owner, which needs a privilege).
    $sid = '*' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    icacls $destDir /deny "${sid}:(WD)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '[C] icacls could not add the deny entry.' }
    try {
        Assert-That ((Invoke-Robo -Verb cut -Paths $srcDir) -eq 0) '[C] Robo-Cut accepted the folder'
        Assert-That ((Invoke-Robo -Verb paste -Paths $destParent) -eq 0) '[C] Robo-Paste accepted the destination'
        Wait-PathExists (Join-Path $destDir 'ok\moves.txt')
        Reset-TrayAfterErrors
        Assert-That (-not (Test-Path -LiteralPath (Join-Path $destDir 'denied.txt'))) '[C] the destination refused the file'
        Assert-That (Test-Path -LiteralPath $denied) '[C] the refused source file is still there'
        Assert-That ((Get-FileSha256 $denied) -eq $deniedHash) '[C] the refused source file is unchanged'
        Assert-That (-not (Test-Path -LiteralPath $movable)) '[C] the job ran: the movable file left the source'
    }
    finally {
        icacls $destDir /remove:d $sid | Out-Null
    }
}

# Scenario D: the destination volume has less free space than one of the files.
function Test-FullDestinationCut {
    New-Item -ItemType Directory -Force -Path $SmallVolume | Out-Null
    $small = Join-Path (Resolve-Path -LiteralPath $SmallVolume).ProviderPath 'rrc-e2e-full'
    Remove-TreeIfPresent $small
    New-Item -ItemType Directory -Path $small | Out-Null
    $free = (New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($small))).AvailableFreeSpace
    if ($free -ge [long]$FullFileMB * 1MB) { throw "[D] -SmallVolume has $free bytes free; it needs less than -FullFileMB ($FullFileMB MB)." }

    $srcDir = Join-Path $work 'd-src'
    New-Item -ItemType Directory -Path $srcDir | Out-Null
    $big = Join-Path $srcDir 'too-big.bin'
    New-DataFile $big ([long]$FullFileMB * 1MB) 31
    $movable = Join-Path $srcDir 'fits.txt'
    Set-Content -LiteralPath $movable -Value 'small enough for the destination'
    $bigHash = Get-FileSha256 $big
    $destDir = Join-Path $small 'd-src'

    Assert-That ((Invoke-Robo -Verb cut -Paths $srcDir) -eq 0) '[D] Robo-Cut accepted the folder'
    Assert-That ((Invoke-Robo -Verb paste -Paths $small) -eq 0) '[D] Robo-Paste accepted the destination'
    Wait-PathExists (Join-Path $destDir 'fits.txt')
    Reset-TrayAfterErrors
    Assert-That (Test-Path -LiteralPath $big) '[D] the source that did not fit is still there'
    Assert-That ((Get-FileSha256 $big) -eq $bigHash) '[D] the source that did not fit is unchanged'
    Assert-That (-not (Test-Path -LiteralPath $movable)) '[D] the job ran: the file that fit left the source'
    $leftover = Join-Path $destDir 'too-big.bin'
    Write-Step ("[D] destination copy of the large file: " + $(if (Test-Path -LiteralPath $leftover) { "present, $((Get-Item -LiteralPath $leftover).Length) bytes" } else { 'absent' }))
    Remove-TreeIfPresent $small
}

# fsutil prints "File ID is 0x...": the same ID after the cut means a rename, not a copy.
function Get-FileId([string]$Path) {
    $text = (fsutil file queryFileID $Path) -join ' '
    if ($text -notmatch '0x[0-9a-fA-F]+') { throw "fsutil gave no file ID for $Path : $text" }
    return $Matches[0]
}

# Scenario E: a same-volume cut moves by renaming.
function Test-SameVolumeCutIsRename {
    $srcDir = Join-Path $work 'e-src'
    New-Item -ItemType Directory -Path $srcDir | Out-Null
    $file = Join-Path $srcDir 'renamed.bin'
    New-DataFile $file (4MB) 41
    $id = Get-FileId $file
    $hash = Get-FileSha256 $file
    $destParent = Join-Path $work 'e-dest'
    New-Item -ItemType Directory -Path $destParent | Out-Null
    $moved = Join-Path $destParent 'e-src\renamed.bin'

    Assert-That ((Invoke-Robo -Verb cut -Paths $srcDir) -eq 0) '[E] Robo-Cut accepted the folder'
    Assert-That ((Invoke-Robo -Verb paste -Paths $destParent) -eq 0) '[E] Robo-Paste accepted the destination'
    Wait-PathExists $moved
    Wait-JobQuiet
    Assert-That (-not (Test-Path -LiteralPath $srcDir)) '[E] the source folder is gone'
    Assert-That ((Get-FileSha256 $moved) -eq $hash) '[E] the content is intact'
    Assert-That ((Get-FileId $moved) -eq $id) "[E] the file kept its file ID ($id): a rename, not a copy"
}

# Scenario F: the conflict dialog. Choice: Replace, Skip or KeepBoth ("Let me decide", both ticked).
function Test-AskCut([string]$DestRoot, [string]$Label, [string]$Choice, [bool]$CrossVolume) {
    $srcDir = Join-Path $work "f-src-$Label"
    $destParent = Join-Path $DestRoot "f-dest-$Label"
    $destDir = Join-Path $destParent "f-src-$Label"
    New-Item -ItemType Directory -Path $srcDir, $destDir | Out-Null
    $srcFile = Join-Path $srcDir 'conflict.txt'
    $destFile = Join-Path $destDir 'conflict.txt'
    Set-Content -LiteralPath $srcFile -Value "the version being pasted ($Label)"
    Set-Content -LiteralPath $destFile -Value "the version already there ($Label)"
    $movable = Join-Path $srcDir 'moves.txt'
    Set-Content -LiteralPath $movable -Value "no conflict ($Label)"
    $srcHash = Get-FileSha256 $srcFile
    $destHash = Get-FileSha256 $destFile

    Assert-That ((Invoke-Robo -Verb cut -Paths $srcDir) -eq 0) "[$Label] Robo-Cut accepted the folder"
    Assert-That ((Invoke-Robo -Verb paste -Paths $destParent) -eq 0) "[$Label] Robo-Paste accepted the destination"
    switch ($Choice) {
        'Replace' { Invoke-UiaButton (Wait-TrayElement 'Replace') }
        'Skip' { Invoke-UiaButton (Wait-TrayElement 'Skip') }
        'KeepBoth' {
            Invoke-UiaButton (Wait-TrayElement 'Decide')
            Set-UiaToggle (Wait-TrayElement 'SelectAllSource') $true
            Set-UiaToggle (Wait-TrayElement 'SelectAllDestination') $true
            Invoke-UiaButton (Wait-TrayElement 'Continue')
        }
    }
    Write-Step "[$Label] answered the conflict dialog: $Choice"
    Wait-PathExists (Join-Path $destDir 'moves.txt')
    Wait-JobQuiet
    Close-TrayDialogs

    Assert-That (-not (Test-Path -LiteralPath $movable)) "[$Label] the job ran: the file without a conflict left the source"
    $keptBoth = Join-Path $destDir 'conflict (2).txt'
    if ($Choice -eq 'Replace') {
        Assert-That ((Get-FileSha256 $destFile) -eq $srcHash) "[$Label] the destination now holds the pasted version"
        Assert-That (-not (Test-Path -LiteralPath $srcFile)) "[$Label] the replaced file left the source, as a cut does"
    }
    elseif ($Choice -eq 'KeepBoth' -and -not $CrossVolume) {
        Assert-That ((Get-FileSha256 $destFile) -eq $destHash) "[$Label] the file already there is unchanged"
        Assert-That ((Test-Path -LiteralPath $keptBoth) -and (Get-FileSha256 $keptBoth) -eq $srcHash) "[$Label] the pasted version arrived as 'conflict (2).txt'"
        Assert-That (-not (Test-Path -LiteralPath $srcFile)) "[$Label] the kept-both file left the source, as a cut does"
    }
    else {
        # Skip, and "keep both" across volumes: not offered there, so the dialog keeps the two
        # boxes exclusive and ticking the destination last leaves "1 file skipped". Nothing
        # about the conflicting file changes on either side.
        Assert-That ((Get-FileSha256 $destFile) -eq $destHash) "[$Label] the file already there is unchanged"
        Assert-That (-not (Test-Path -LiteralPath $keptBoth)) "[$Label] no second copy was made"
        Assert-That ((Test-Path -LiteralPath $srcFile) -and (Get-FileSha256 $srcFile) -eq $srcHash) "[$Label] the source file is still there, unchanged"
    }
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

        Test-DeniedDestinationCut
    }
    if ($SmallVolume) { Test-FullDestinationCut }
    Test-SameVolumeCutIsRename

    Set-RoboConfig @{ conflictDefault = 'ask' }
    foreach ($choice in 'Replace', 'Skip', 'KeepBoth') {
        Test-AskCut -DestRoot $work -Label "same-volume-$choice" -Choice $choice -CrossVolume $false
        if ($second) { Test-AskCut -DestRoot $second -Label "cross-volume-$choice" -Choice $choice -CrossVolume $true }
    }
}
finally {
    Restore-RoboConfig $originalConfig
}

if (-not $second) {
    Exit-Skipped 'the same-volume scenarios passed; A, C and the cross-volume cuts did not run. Pass -SecondVolume <folder on another drive> to run them.'
}
if (-not $SmallVolume) { Write-Host 'NOTE: -SmallVolume not given; scenario D (full destination) did not run.' }
Write-Host 'PASS: CutSafety'
