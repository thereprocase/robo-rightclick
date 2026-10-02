<#
Ephemeral audit (product invariant 2): with logging set to ephemeral, five jobs write nothing
about themselves to disk.

Snapshots %APPDATA%, %LOCALAPPDATA% and %TEMP% (names, sizes, times and hashes), runs five
jobs whose file names carry a unique marker, snapshots again, and fails on any new or changed
file other than config.json and the runtime's %TEMP%\.net extraction folder, and on any file
name or new or changed file content containing the marker.

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
    [int]$HashLimitMB = 16
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root
Assert-Installed

Write-Host 'Ephemeral.Tests'
$work = Join-Path $script:E2E.Root 'ephemeral'
Remove-TreeIfPresent $work
$marker = 'rrcmark' + [Guid]::NewGuid().ToString('N').Substring(0, 12)

# %TEMP% normally sits inside %LOCALAPPDATA%; a root inside another root is scanned once.
$candidates = @($env:APPDATA, $env:LOCALAPPDATA, $env:TEMP) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') } | Select-Object -Unique
$scanRoots = @($candidates | Where-Object {
        $inner = $_
        -not ($candidates | Where-Object { $_ -ne $inner -and $inner.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) })
    })
$excluded = @($script:E2E.Root) + @(Join-Path $env:TEMP '.net')
$searchedOnly = @(Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Notifications')

function Test-SearchedOnly([string]$Path) {
    foreach ($e in $searchedOnly) { if ($Path.StartsWith($e, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
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

$originalConfig = Get-RoboConfigText
try {
    Set-RoboConfig @{ logging = 'ephemeral' }

    $srcDir = Join-Path $work 'src'
    $destDir = Join-Path $work 'dest'
    New-Item -ItemType Directory -Path $srcDir, $destDir | Out-Null
    for ($i = 1; $i -le 5; $i++) { Set-Content -LiteralPath (Join-Path $srcDir "$marker-$i.txt") -Value "job $i" }

    $before = Get-Snapshot
    Write-Step "snapshot before: $($before.Count) files"

    for ($i = 1; $i -le 5; $i++) {
        $file = Join-Path $srcDir "$marker-$i.txt"
        Assert-That ((Invoke-Robo -Verb copy -Paths $file) -eq 0) "job ${i}: Robo-Copy accepted"
        Assert-That ((Invoke-Robo -Verb paste -Paths $destDir) -eq 0) "job ${i}: Robo-Paste accepted"
        [void](Wait-Settled -Path $destDir -MinFiles $i)
    }
    Start-Sleep -Seconds 5
    Close-TrayDialogs

    $after = Get-Snapshot
    Write-Step "snapshot after: $($after.Count) files"

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($path in $after.Keys) {
        $isNew = -not $before.ContainsKey($path)
        $isChanged = -not $isNew -and $before[$path] -ne $after[$path]
        $isConfig = $path -ieq $script:E2E.ConfigFile
        $isSearchedOnly = Test-SearchedOnly $path
        if (($isNew -or $isChanged) -and -not $isConfig -and -not $isSearchedOnly) {
            $problems.Add($(if ($isNew) { 'new:     ' } else { 'changed: ' }) + $path)
        }
        if ([IO.Path]::GetFileName($path).IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("marker in name: $path") }
        elseif (($isNew -or $isChanged) -and -not $isConfig) {
            $found = Test-FileContainsText $path $marker
            if ($found -eq $true) { $problems.Add("marker in content: $path") }
            elseif ($null -eq $found -and $isSearchedOnly) { $problems.Add("could not search for the marker: $path") }
        }
    }
    # A deleted file is a change too (a temp file created and removed leaves the other list empty).
    foreach ($path in $before.Keys) {
        if (-not $after.ContainsKey($path) -and -not (Test-SearchedOnly $path)) { $problems.Add("removed:  $path") }
    }

    if ($problems.Count -gt 0) {
        throw ('Ephemeral mode left traces (judge each; add -AllowPath for noise unrelated to the app):' + [Environment]::NewLine + ($problems -join [Environment]::NewLine))
    }
    Assert-That $true 'no new or changed file outside config.json, %TEMP%\.net and the notification database, and no file name or content with the marker'
}
finally {
    Restore-RoboConfig $originalConfig
}

Write-Host 'PASS: Ephemeral'
