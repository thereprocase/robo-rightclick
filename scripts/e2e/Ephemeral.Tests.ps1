<#
Ephemeral audit (product invariant 2): with logging set to ephemeral, five jobs of different
kinds, and a sixth started with the Robo-Paste hotkey (docs/decisions/0001-paste-hotkey.md),
write nothing about themselves to disk or to the places Windows keeps records.

Phase 1, ephemeral. Snapshots %APPDATA%, %LOCALAPPDATA% and %TEMP% (names, sizes, times and
hashes; this covers Recent items, jump lists, CrashDumps, the per-user WER folder and the
notification database), the machine-wide WER folders, and HKCU; runs five jobs whose file names
carry a unique marker (a copy, a cut, a cancel, a conflict answered with Skip, and a failure
on a locked file); snapshots again. Fails on any new, changed or removed file other than
config.json and the runtime's %TEMP%\.net extraction folder, on any file name or new or
changed file content containing the marker or the test folder's path, on the marker in the
HKCU hive or in the Application and System event logs, and on a missing clipboard opt-out
(the three formats that keep a copy out of clipboard history and the cloud clipboard).

Phase 2, normal mode with logRetentionJobs 3. Six jobs; job.json, robocopy.log and
history.jsonl hold the job as designed, the oldest job folders are pruned, a folder that is
not named like a job survives, and the clipboard carries none of the opt-out formats.

Windows records every toast in its notification database. The jobs raise toasts
(notifyOnComplete), so that folder may change, but its content is still searched for the
marker: an ephemeral toast must not carry a path (product invariant 2).

The test's own folder (-Root) is excluded: the marker is in those names on purpose. A busy
profile can show unrelated changes (browsers, indexers); the failure lists each path so a
person can judge it, and -AllowPath adds substring exclusions for noise that has been judged.
Needs an interactive desktop session and an installed app.
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    [string[]]$AllowPath = @(),
    # Files above this size are compared by size and time only, not by hash.
    [int]$HashLimitMB = 16,
    # Optional: a writable folder on another volume. With it the cut job is a robocopy /MOV run,
    # not a rename.
    [string]$SecondVolume
)
$ErrorActionPreference = 'Stop'
# "powershell -File" hands a list over as one string: "a","b" arrives as "a,b", and Run-All
# joins with '|' ('|' cannot appear in a Windows path). Both separators are split here, so a
# substring itself cannot contain a comma.
$AllowPath = @($AllowPath | ForEach-Object { $_ -split '[|,]' } | Where-Object { $_ })
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root
Assert-Installed

Write-Host 'Ephemeral.Tests'
$work = Join-Path $script:E2E.Root 'ephemeral'
Remove-TreeIfPresent $work
$marker = 'rrcmark' + [Guid]::NewGuid().ToString('N').Substring(0, 12)

# %TEMP% normally sits inside %LOCALAPPDATA%; a root inside another root is scanned once.
$candidates = @($env:APPDATA, $env:LOCALAPPDATA, $env:TEMP, (Join-Path $env:ProgramData 'Microsoft\Windows\WER')) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') } | Select-Object -Unique
$scanRoots = @($candidates | Where-Object {
        $inner = $_
        -not ($candidates | Where-Object { $_ -ne $inner -and $inner.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) })
    })
# PowerShell itself rewrites StartupProfileData-NonInteractive whenever the test starts a
# PowerShell process; it is the harness, not the app.
$excluded = @($script:E2E.Root) + @(Join-Path $env:TEMP '.net') +
    @(Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\PowerShell\StartupProfileData-NonInteractive')
# Explorer writes the tray icon's image to ActionCenterCache (and briefly to %TEMP%) for each
# toast. Like the notification database it may change, and it is searched for the marker.
$searchedOnly = @(
    (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Notifications'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\ActionCenterCache')
)

# For a balloon with an Info, Warning or Error icon, Windows writes that stock icon to
# %TEMP%\{GUID}.png while the toast shows (seen on build 26200: the blue "i", 306 x 306).
$tempRoot = $env:TEMP.TrimEnd('\')
function Test-SearchedOnly([string]$Path) {
    foreach ($e in $searchedOnly) { if ($Path.StartsWith($e, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    if ((Split-Path -Parent $Path) -ieq $tempRoot -and (Split-Path -Leaf $Path) -match '^\{[0-9a-fA-F-]{36}\}\.png$') { return $true }
    return $false
}

# Windows' own background writers, judged unrelated to the app in the 2026-10-02 testlog entries:
# the web cache database, the per-user class registration hive logs, the WebView cache, and
# another app's settings. Added 2026-10-03, once job 6 opened File Explorer windows: Explorer's
# icon cache (iconcache_*.db, written when a window shows icons) and OneDrive's sync engine
# logs; both were searched and held neither the marker nor the test path. A change here is not
# a trace, but the content is still searched for the marker and the test path (and said so
# when it cannot be read).
$judgedNoise = @(
    '\Microsoft\Windows\WebCache\', '\Microsoft\Windows\UsrClass.dat', '\EBWebView\',
    '\Packages\Microsoft.MicrosoftOfficeHub_', '\Microsoft\Windows\Explorer\iconcache_',
    '\Microsoft\OneDrive\logs\'
)
function Test-JudgedNoise([string]$Path) {
    foreach ($n in $judgedNoise) { if ($Path.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }
    return $false
}

function Test-Excluded([string]$Path) {
    foreach ($e in $excluded) { if ($Path.StartsWith($e, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    foreach ($a in $AllowPath) { if ($Path.IndexOf($a, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }
    return $false
}

# Path -> "size|modified|hash" for every readable file under the scan roots.
function Get-Snapshot {
    $snapshot = @{}
    foreach ($scanRoot in $scanRoots) {
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($scanRoot)
        while ($pending.Count -gt 0) {
            $dir = $pending.Pop()
            if (Test-Excluded $dir) { continue }
            try { $entries = [IO.Directory]::GetFileSystemEntries($dir) } catch { continue }
            foreach ($path in $entries) {
                try {
                    $attr = [IO.File]::GetAttributes($path)
                    if ($attr -band [IO.FileAttributes]::ReparsePoint) { continue }
                    if ($attr -band [IO.FileAttributes]::Directory) { $pending.Push($path); continue }
                    if (Test-Excluded $path) { continue }
                    $info = New-Object IO.FileInfo $path
                    $hash = 'not-hashed'
                    if ($info.Length -le ([long]$HashLimitMB * 1MB)) {
                        try { $hash = Get-FileSha256 $path } catch { $hash = 'unreadable' }
                    }
                    $snapshot[$path] = "$($info.Length)|$($info.LastWriteTimeUtc.Ticks)|$hash"
                }
                catch { continue }
            }
        }
    }
    return $snapshot
}

# $true or $false; $null when the file cannot be read, which the caller reports, so an
# unreadable file is never mistaken for a clean one. The file is opened with full sharing because
# databases and logs stay open for writing. Bytes are mapped one-to-one to characters (code page
# 28591), so a native IndexOf finds the marker in UTF-8 and in UTF-16LE text quickly.
function Test-FileContainsText([string]$Path, [string]$Text, [int]$LimitMB = 256) {
    try {
        $stream = [IO.File]::Open((ConvertTo-LongPath $Path), 'Open', 'Read', 'ReadWrite, Delete')
        try {
            if ($stream.Length -gt ([long]$LimitMB * 1MB)) { return $null }
            $bytes = New-Object byte[] ([int]$stream.Length)
            $read = 0
            while ($read -lt $bytes.Length) {
                $n = $stream.Read($bytes, $read, $bytes.Length - $read)
                if ($n -le 0) { break }
                $read += $n
            }
        }
        finally { $stream.Dispose() }
    }
    catch { return $null }
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $haystack = $latin1.GetString($bytes, 0, $read)
    foreach ($encoding in [Text.Encoding]::UTF8, [Text.Encoding]::Unicode) {
        $needle = $latin1.GetString($encoding.GetBytes($Text))
        if ($haystack.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}

# True when the file name or the (new or changed) content carries the marker or the test path.
$testPathText = $script:E2E.Root

function Close-ErrorSummary {
    # A job summary's button is named SkipErrors (it reads "Skip" beside "Try again", or "OK" alone); message boxes have "OK".
    for ($i = 0; $i -lt 10; $i++) {
        $pressed = $false
        foreach ($p in Get-RoboProcesses) {
            foreach ($name in 'OK', 'SkipErrors') {
                $button = Find-UiaButton $p.Id $name
                if ($button) { Invoke-UiaButton $button; $pressed = $true; Write-Step "pressed '$name' on a tray dialog" }
            }
        }
        if (-not $pressed) { return }
        Start-Sleep -Milliseconds 500
    }
}

# Formats on the clipboard right now, by name.
function Get-ClipboardFormatNames {
    for ($i = 0; $i -lt 20; $i++) {
        try { return @([Windows.Forms.Clipboard]::GetDataObject().GetFormats($false)) } catch { Start-Sleep -Milliseconds 100 }
    }
    throw 'Could not read the clipboard formats.'
}

$optOutFormats = @('ExcludeClipboardContentFromMonitorProcessing', 'CanIncludeInClipboardHistory', 'CanUploadToCloudClipboard')

function Wait-ProgressCancelAndPress {
    $button = $null
    $deadline = (Get-Date).AddSeconds(60)
    while ($null -eq $button -and (Get-Date) -lt $deadline) {
        foreach ($p in Get-RoboProcesses) { if ($null -eq $button) { $button = Find-UiaButton $p.Id 'Cancel' } }
        if ($null -eq $button) { Start-Sleep -Milliseconds 200 }
    }
    if ($null -eq $button) { throw 'No progress window with a Cancel button appeared; the job may have finished first.' }
    Assert-That (@(Get-Process -Name 'robocopy' -ErrorAction SilentlyContinue).Count -gt 0) 'the cancel job was still running when Cancel was pressed'
    Invoke-UiaButton $button
    Wait-RobocopyGone -TimeoutSec 120
    Start-Sleep -Seconds 3
}

# Writes a text copy of HKCU and searches it for the marker (the Recent, MRU and UserAssist keys
# keep file names). The file is under -Root, which the file snapshots exclude.
function Test-HkcuContains([string]$Text) {
    $file = Join-Path $work 'hkcu.reg'
    $out = & reg.exe export HKCU $file /y 2>&1
    if ($LASTEXITCODE -ne 0) { throw "reg export failed: $out" }
    try { return Test-FileContainsText $file $Text 1024 } finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
}

function Test-EventLogsContain([string]$Text, [datetime]$Since) {
    $hits = 0
    foreach ($log in 'Application', 'System') {
        try {
            foreach ($event in Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $Since } -ErrorAction Stop) {
                if ($event.ToXml().IndexOf($Text, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hits++ }
            }
        }
        catch [System.Exception] { if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { Write-Step "could not read the $log event log: $($_.Exception.Message)" } }
    }
    return $hits
}

$originalConfig = Get-RoboConfigText
$startedAt = Get-Date
$stray = $null
try {
    # =======================================================================
    # Phase 1: ephemeral
    # =======================================================================
    Write-Host '[1] ephemeral: copy, cut, cancel, conflict, failure, hotkey paste'
    Set-RoboConfig @{
        logging = 'ephemeral'; showProgressWindow = $true; conflictDefault = 'ask'; notifyOnComplete = $true
        pasteHotkey = 'Ctrl+Shift+V'
        extraArgs = @{ copy = '/IORATE:8M'; move = '/IORATE:8M' }
    }

    $srcDir = Join-Path $work 'src'
    $destCopy = Join-Path $work 'dest-copy'
    $destCut = if ($SecondVolume) { Join-Path $SecondVolume "$marker-cut-dest" } else { Join-Path $work 'dest-cut' }
    $destBig = Join-Path $work 'dest-big'
    $destConflict = Join-Path $work 'dest-conflict'
    $destFail = Join-Path $work 'dest-fail'
    $destHotkey = Join-Path $work 'dest-hotkey'
    New-Item -ItemType Directory -Path $srcDir, $destCopy, $destCut, $destBig, $destConflict, $destFail, $destHotkey | Out-Null

    $copyFile = Join-Path $srcDir "$marker-copy.txt"
    $cutFile = Join-Path $srcDir "$marker-cut.txt"
    $bigDir = Join-Path $srcDir "$marker-big"
    $conflictFile = Join-Path $srcDir "$marker-conflict.txt"
    $lockedFile = Join-Path $srcDir "$marker-locked.txt"
    Set-Content -LiteralPath $copyFile -Value 'copy'
    Set-Content -LiteralPath $cutFile -Value 'cut'
    New-Item -ItemType Directory -Path $bigDir | Out-Null
    for ($i = 0; $i -lt 4; $i++) { New-DataFile (Join-Path $bigDir "$marker-big-$i.bin") (48MB) (30 + $i) }
    Set-Content -LiteralPath $conflictFile -Value 'the version being pasted'
    Set-Content -LiteralPath (Join-Path $destConflict "$marker-conflict.txt") -Value 'the version already there'
    Set-Content -LiteralPath $lockedFile -Value 'locked'
    $hotkeyFile = Join-Path $srcDir "$marker-hotkey.txt"
    Set-Content -LiteralPath $hotkeyFile -Value 'hotkey'

    # Explorer writes its own records (shell bags, view state) when a window opens on a folder.
    # Open the hotkey's destination before the first snapshot so only the paste is measured.
    Close-ExplorerWindows
    $hotkeyWindow = Open-ExplorerLocation $destHotkey
    Start-Sleep -Seconds 3
    $startedAt = Get-Date

    $before = Get-Snapshot
    Write-Step "snapshot before: $($before.Count) files"

    # 1. copy
    Assert-That ((Invoke-Robo -Verb copy -Paths $copyFile) -eq 0) 'job 1 (copy): Robo-Copy accepted'
    $formats = Get-ClipboardFormatNames
    $missing = @($optOutFormats | Where-Object { $formats -notcontains $_ })
    Assert-That ($missing.Count -eq 0) "job 1 (copy): the clipboard carries every opt-out format ($($optOutFormats -join ', '))"
    Assert-That ((Invoke-Robo -Verb paste -Paths $destCopy) -eq 0) 'job 1 (copy): Robo-Paste accepted'
    Wait-PathExists (Join-Path $destCopy "$marker-copy.txt") 60
    [void](Wait-Settled -Path $destCopy -MinFiles 1)

    # 2. cut
    Assert-That ((Invoke-Robo -Verb cut -Paths $cutFile) -eq 0) 'job 2 (cut): Robo-Cut accepted'
    Assert-That ((Invoke-Robo -Verb paste -Paths $destCut) -eq 0) 'job 2 (cut): Robo-Paste accepted'
    Wait-PathExists (Join-Path $destCut "$marker-cut.txt") 60
    [void](Wait-Settled -Path $destCut -MinFiles 1)
    Assert-That (-not (Test-Path -LiteralPath $cutFile)) 'job 2 (cut): the source is gone'

    # 3. cancel
    Assert-That ((Invoke-Robo -Verb copy -Paths $bigDir) -eq 0) 'job 3 (cancel): Robo-Copy accepted'
    Assert-That ((Invoke-Robo -Verb paste -Paths $destBig) -eq 0) 'job 3 (cancel): Robo-Paste accepted'
    Wait-ProgressCancelAndPress
    Close-ErrorSummary

    # 4. conflict, answered with Skip: the whole paste is skipped, which is a Done job, not a
    # failure. Pressing whatever summary appears would let a Failed job pass too.
    Assert-That ((Invoke-Robo -Verb copy -Paths $conflictFile) -eq 0) 'job 4 (conflict): Robo-Copy accepted'
    Assert-That ((Invoke-Robo -Verb paste -Paths $destConflict) -eq 0) 'job 4 (conflict): Robo-Paste accepted'
    Invoke-UiaButton (Wait-TrayElement 'Skip')
    Write-Step 'job 4 (conflict): answered Skip'
    Assert-NoJobSummary 'job 4 (conflict, all skipped)'
    Assert-That ((Get-Content -LiteralPath (Join-Path $destConflict "$marker-conflict.txt") -Raw).Trim() -eq 'the version already there') 'job 4 (conflict): the file already there is unchanged'
    Close-ErrorSummary

    # 5. failure: the source is held open without sharing, so robocopy cannot read it
    $lock = [IO.File]::Open($lockedFile, 'Open', 'Read', 'None')
    try {
        Assert-That ((Invoke-Robo -Verb copy -Paths $lockedFile) -eq 0) 'job 5 (failure): Robo-Copy accepted'
        Assert-That ((Invoke-Robo -Verb paste -Paths $destFail) -eq 0) 'job 5 (failure): Robo-Paste accepted'
        [void](Wait-TrayElement 'SkipErrors' 60)
        Start-Sleep -Seconds 1
        Close-ErrorSummary
    }
    finally { $lock.Dispose() }
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $destFail "$marker-locked.txt"))) 'job 5 (failure): the locked file did not arrive'

    # 6. hotkey: Robo-Copy, then the Robo-Paste hotkey in the destination's file list
    Assert-That ((Invoke-Robo -Verb copy -Paths $hotkeyFile) -eq 0) 'job 6 (hotkey): Robo-Copy accepted'
    Set-ForegroundWindowFirmly $hotkeyWindow
    Set-ExplorerFileListFocus $hotkeyWindow
    Send-PasteHotkey
    Wait-PathExists (Join-Path $destHotkey "$marker-hotkey.txt") 30
    [void](Wait-Settled -Path $destHotkey -MinFiles 1)
    Write-Step 'job 6 (hotkey): pasted into the open folder'

    Start-Sleep -Seconds 8
    Close-ErrorSummary
    Close-TrayDialogs

    $after = Get-Snapshot
    Write-Step "snapshot after: $($after.Count) files"
    Close-ExplorerWindows

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($path in $after.Keys) {
        $isNew = -not $before.ContainsKey($path)
        $isChanged = -not $isNew -and $before[$path] -ne $after[$path]
        $isConfig = $path -ieq $script:E2E.ConfigFile
        $isNoise = Test-JudgedNoise $path
        $isSearchedOnly = (Test-SearchedOnly $path) -or $isNoise
        if (($isNew -or $isChanged) -and -not $isConfig -and -not $isSearchedOnly) {
            $problems.Add($(if ($isNew) { 'new:     ' } else { 'changed: ' }) + $path)
        }
        if ([IO.Path]::GetFileName($path).IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("marker in name: $path") }
        elseif (($isNew -or $isChanged) -and -not $isConfig) {
            foreach ($needle in $marker, $testPathText) {
                $found = Test-FileContainsText $path $needle
                if ($found -eq $true) { $problems.Add("'$needle' in content: $path"); break }
                # Explorer deletes its toast images within seconds; one that is gone left nothing behind.
                elseif ($null -eq $found -and $isNoise) { Write-Step "unverified: could not read judged-noise file to search it: $path"; break }
                elseif ($null -eq $found -and $isSearchedOnly -and (Test-Path -LiteralPath $path)) { $problems.Add("could not search for '$needle': $path"); break }
            }
        }
    }
    # A deleted file is a change too (a temp file created and removed leaves the other list empty).
    foreach ($path in $before.Keys) {
        if (-not $after.ContainsKey($path) -and -not (Test-SearchedOnly $path) -and -not (Test-JudgedNoise $path)) { $problems.Add("removed:  $path") }
    }
    if ($problems.Count -gt 0) {
        throw ('Ephemeral mode left traces (judge each; add -AllowPath for noise unrelated to the app):' + [Environment]::NewLine + ($problems -join [Environment]::NewLine))
    }
    Assert-That $true 'no new, changed or removed file outside config.json, %TEMP%\.net and the notification files; no file name or content with the marker or the test path'

    Assert-That ((Test-HkcuContains $marker) -eq $false) 'the marker is not in the HKCU hive (Recent, MRU, UserAssist and the rest)'
    $eventHits = Test-EventLogsContain $marker $startedAt.AddMinutes(-1)
    Assert-That ($eventHits -eq 0) 'the marker is not in the Application or System event logs'
    $crashLog = Join-Path $script:E2E.DataDir 'crash.log'
    Assert-That (-not (Test-Path -LiteralPath $crashLog) -or (Get-Item -LiteralPath $crashLog).LastWriteTime -lt $startedAt) 'no crash log was written during the ephemeral jobs'

    # =======================================================================
    # Phase 2: normal mode, retention 3
    # =======================================================================
    Write-Host '[2] normal mode: logs and retention'
    Set-RoboConfig @{ logging = 'normal'; showProgressWindow = $false; conflictDefault = 'ask'; logRetentionJobs = 3; extraArgs = @{ copy = ''; move = '' } }
    $jobsDir = Join-Path $script:E2E.DataDir 'jobs'
    New-Item -ItemType Directory -Force -Path $jobsDir | Out-Null
    $stray = Join-Path $jobsDir 'keep-me-not-a-job'
    New-Item -ItemType Directory -Force -Path $stray | Out-Null
    Set-Content -LiteralPath (Join-Path $stray 'note.txt') -Value 'a folder that is not named like a job'

    $normalMarker = 'rrcnorm' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
    $normalSrc = Join-Path $work 'normal-src'
    $normalDest = Join-Path $work 'normal-dest'
    New-Item -ItemType Directory -Path $normalSrc, $normalDest | Out-Null
    $normalFormats = $null
    for ($i = 1; $i -le 6; $i++) {
        $f = Join-Path $normalSrc "$normalMarker-$i.txt"
        Set-Content -LiteralPath $f -Value "normal job $i"
        Assert-That ((Invoke-Robo -Verb copy -Paths $f) -eq 0) "normal job ${i}: Robo-Copy accepted"
        if ($i -eq 1) { $normalFormats = Get-ClipboardFormatNames }
        Assert-That ((Invoke-Robo -Verb paste -Paths $normalDest) -eq 0) "normal job ${i}: Robo-Paste accepted"
        [void](Wait-Settled -Path $normalDest -MinFiles $i)
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 4
    Close-TrayDialogs
    Assert-That (@($optOutFormats | Where-Object { $normalFormats -contains $_ }).Count -eq 0) 'normal mode: the clipboard carries none of the ephemeral opt-out formats'

    $historyFile = Join-Path $script:E2E.DataDir 'history.jsonl'
    Assert-That (Test-Path -LiteralPath $historyFile) 'normal mode: history.jsonl exists'
    $history = @(Get-Content -LiteralPath $historyFile | Where-Object { $_ -like "*$normalMarker*" } | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-That ($history.Count -eq 6) "normal mode: history.jsonl holds all six jobs ($($history.Count))"
    Assert-That (@($history | Where-Object { $_.finalState -ne 'done' -or $_.errorCount -ne 0 }).Count -eq 0) 'normal mode: every history line says done, no errors'

    $folders = @(Get-ChildItem -LiteralPath $jobsDir -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}-[0-9a-f]{8}$' } | ForEach-Object Name | Sort-Object)
    Assert-That ($folders.Count -eq 3) "normal mode: retention keeps 3 job folders ($($folders.Count): $($folders -join ', '))"
    $ordered = @($history | Sort-Object createdAt)
    $newestThree = @($ordered | Select-Object -Last 3 | ForEach-Object { $_.id.Substring(0, 8) })
    $keptSuffixes = @($folders | ForEach-Object { $_.Substring($_.Length - 8) })
    Assert-That ((($keptSuffixes | Sort-Object) -join '|') -eq (($newestThree | Sort-Object) -join '|')) 'normal mode: the three kept folders are the three newest jobs'
    Assert-That (Test-Path -LiteralPath (Join-Path $stray 'note.txt')) 'normal mode: a folder that is not named like a job was not pruned'

    $newest = Join-Path $jobsDir $folders[-1]
    $jobJson = Get-Content -LiteralPath (Join-Path $newest 'job.json') -Raw | ConvertFrom-Json
    Assert-That ($jobJson.job.verb -eq 'copy' -and ($jobJson.job.sources -join ' ') -like "*$normalMarker-6.txt*" -and $jobJson.summary.finalState -eq 'done') 'normal mode: job.json names the verb and the source and ends done'
    $robocopyLog = Get-Content -LiteralPath (Join-Path $newest 'robocopy.log') -Raw
    Assert-That ($robocopyLog -like "*$normalMarker-6.txt*") 'normal mode: robocopy.log holds robocopy''s output for the job'
}
finally {
    if ($stray -and (Test-Path -LiteralPath $stray)) { Remove-Item -LiteralPath $stray -Recurse -Force }
    Restore-RoboConfig $originalConfig
    if ($SecondVolume) { Remove-TreeIfPresent (Join-Path $SecondVolume "$marker-cut-dest") }
}

Write-Host 'PASS: Ephemeral'
