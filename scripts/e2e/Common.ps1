<#
Shared helpers for the end-to-end scripts. Dot-source it; do not run it.
Windows PowerShell 5.1 and PowerShell 7; inbox modules and .NET only.

Every location comes from parameters. The defaults are under $env:TEMP\rrc-e2e.
These scripts are unverified on Windows until docs/testlog.md has an entry for them.
#>

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Exit code a test script uses to say "nothing was checked". Run-All reports it as SKIP.
$script:SkipExitCode = 3

# ---------------------------------------------------------------------------
# Setup, assertions
# ---------------------------------------------------------------------------

function Initialize-E2E {
    param(
        [Parameter(Mandatory)][string]$Root,
        # The downloaded or published RoboRightClick.exe. Tests other than Install and
        # Security use the installed copy instead.
        [string]$Exe
    )
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $script:E2E = @{
        Root       = (Resolve-Path -LiteralPath $Root).ProviderPath
        DistExe    = $Exe
        InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\RoboRightClick'
        ConfigDir  = Join-Path $env:APPDATA 'RoboRightClick'
        DataDir    = Join-Path $env:LOCALAPPDATA 'RoboRightClick'
    }
    $script:E2E.InstalledExe = Join-Path $script:E2E.InstallDir 'RoboRightClick.exe'
    $script:E2E.ConfigFile = Join-Path $script:E2E.ConfigDir 'config.json'
}

function Write-Step([string]$Message) { Write-Host "  - $Message" }

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERT FAILED: $Message" }
    Write-Step "ok: $Message"
}

function Exit-Skipped([string]$Reason) {
    Write-Host "SKIP: $Reason"
    exit $script:SkipExitCode
}

function Assert-Installed {
    if (-not (Test-Path -LiteralPath $script:E2E.InstalledExe)) {
        throw 'RoboRightClick is not installed. Run Install.Tests.ps1 first.'
    }
}

# ---------------------------------------------------------------------------
# Paths and files
# ---------------------------------------------------------------------------

# The \\?\ prefix lifts the 260-character limit for the .NET file APIs used below.
function ConvertTo-LongPath([string]$Path) {
    if ($Path.StartsWith('\\?\')) { return $Path }
    if ($Path.StartsWith('\\')) { return '\\?\UNC\' + $Path.Substring(2) }
    return '\\?\' + $Path
}

function Get-FileSha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open((ConvertTo-LongPath $Path), 'Open', 'Read', 'ReadWrite')
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)) }
    finally { $stream.Dispose(); $sha.Dispose() }
}

# Deterministic pseudo-random content, so a wrong or truncated copy changes the hash.
function New-DataFile([string]$Path, [long]$Bytes, [int]$Seed = 1) {
    $stream = [IO.File]::Open((ConvertTo-LongPath $Path), 'Create', 'Write', 'None')
    try {
        $random = New-Object Random $Seed
        $buffer = New-Object byte[] (1MB)
        $left = $Bytes
        while ($left -gt 0) {
            $random.NextBytes($buffer)
            $count = [int][Math]::Min($left, $buffer.Length)
            $stream.Write($buffer, 0, $count)
            $left -= $count
        }
    }
    finally { $stream.Dispose() }
}

function Remove-TreeIfPresent([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    # rmdir removes a junction as a link and never follows it into its target.
    cmd /c rmdir /s /q ('"' + $Path + '"') | Out-Null
    if (Test-Path -LiteralPath $Path) {
        # Paths over 260 characters: delete through the extended-length form.
        cmd /c rmdir /s /q ('"' + (ConvertTo-LongPath $Path) + '"') | Out-Null
    }
}

# Names are built from code points so this file stays pure ASCII (Windows PowerShell 5.1
# reads a BOM-less file as the ANSI code page).
function Get-UnicodeNames {
    $emoji = [char]::ConvertFromUtf32(0x1F4C1)
    $cjk = -join ([char]0x6587, [char]0x4EF6, [char]0x30C6, [char]0x30B9, [char]0x30C8)
    $latin = 'caf' + [char]0x00E9 + '-' + [char]0x00FC + 'ber'
    return @{ Emoji = "emoji-$emoji.txt"; Cjk = "$cjk.txt"; Latin = "$latin.txt" }
}

<#
Builds the test tree under $Path:
  names:   Unicode, emoji and CJK file names
  sizes:   0 bytes, small, and $LargeMB megabytes
  flags:   a hidden file and a read-only file
  streams: a file with an alternate data stream
  links:   a junction (robocopy and Explorer treat these differently; see docs/parity.md)
  depth:   a file whose full path is over 260 characters
Timestamps are fixed so the "same size and time" scenarios can be built on top.
#>
function New-Tree {
    param([Parameter(Mandatory)][string]$Path, [int]$LargeMB = 64)

    Remove-TreeIfPresent $Path
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $names = Get-UnicodeNames
    foreach ($d in 'plain', 'names', 'sizes', 'flags', 'streams', 'target') {
        New-Item -ItemType Directory -Path (Join-Path $Path $d) | Out-Null
    }

    Set-Content -LiteralPath (Join-Path $Path 'plain\normal.txt') -Value 'normal'
    foreach ($n in $names.Values) { Set-Content -LiteralPath (Join-Path $Path "names\$n") -Value "content of $n" }

    [IO.File]::WriteAllBytes((Join-Path $Path 'sizes\empty.bin'), [byte[]]@())
    New-DataFile (Join-Path $Path 'sizes\small.bin') 4096 2
    if ($LargeMB -gt 0) { New-DataFile (Join-Path $Path 'sizes\large.bin') ([long]$LargeMB * 1MB) 3 }

    Set-Content -LiteralPath (Join-Path $Path 'flags\hidden.txt') -Value 'hidden'
    Set-Content -LiteralPath (Join-Path $Path 'flags\readonly.txt') -Value 'read-only'
    Set-Content -LiteralPath (Join-Path $Path 'streams\with-ads.txt') -Value 'main stream'
    Set-Content -LiteralPath (Join-Path $Path 'streams\with-ads.txt') -Stream 'custom' -Value 'extra stream'
    Set-Content -LiteralPath (Join-Path $Path 'target\inside-target.txt') -Value 'reached through the junction'

    $deep = $Path
    $segment = 'd' * 60
    for ($i = 0; $i -lt 5; $i++) { $deep = Join-Path $deep $segment }
    [void][IO.Directory]::CreateDirectory((ConvertTo-LongPath $deep))
    $deepFile = Join-Path $deep 'long-path-file.txt'
    [IO.File]::WriteAllText((ConvertTo-LongPath $deepFile), 'deep')
    if ($deepFile.Length -le 260) { throw "Test tree root is too short to make a path over 260 characters ($($deepFile.Length))." }

    # Timestamps, then attributes: a read-only file rejects a timestamp change.
    $stamp = [DateTime]::new(2020, 1, 2, 3, 4, 5, [DateTimeKind]::Utc)
    foreach ($f in Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue) {
        [IO.File]::SetLastWriteTimeUtc($f.FullName, $stamp)
    }
    [IO.File]::SetLastWriteTimeUtc((ConvertTo-LongPath $deepFile), $stamp)
    (Get-Item -LiteralPath (Join-Path $Path 'flags\hidden.txt')).Attributes = 'Hidden, Archive'
    (Get-Item -LiteralPath (Join-Path $Path 'flags\readonly.txt')).Attributes = 'ReadOnly, Archive'

    cmd /c mklink /J ('"' + (Join-Path $Path 'junction-to-target') + '"') ('"' + (Join-Path $Path 'target') + '"') | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $Path 'junction-to-target'))) { throw 'Could not create the junction.' }
}

# ---------------------------------------------------------------------------
# Tree comparison
# ---------------------------------------------------------------------------

$script:ComparedAttributes = [IO.FileAttributes]'ReadOnly, Hidden, System, Archive'

# Relative path -> entry for everything under $Root. Reparse points (junctions, symlinks) are
# left out and never followed, so a source junction is not compared and Explorer's empty
# stand-in folder is not mistaken for a link.
function Get-TreeEntries([string]$Root) {
    $entries = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    $rootLong = (ConvertTo-LongPath $Root).TrimEnd('\')
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($rootLong)
    while ($pending.Count -gt 0) {
        $dir = $pending.Pop()
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($dir)) {
            $attr = [IO.File]::GetAttributes($path)
            if ($attr -band [IO.FileAttributes]::ReparsePoint) { continue }
            $rel = $path.Substring($rootLong.Length).TrimStart('\')
            if ($attr -band [IO.FileAttributes]::Directory) {
                $entries[$rel] = [pscustomobject]@{ Rel = $rel; Full = $path; IsDir = $true; Size = 0L; Attr = $attr }
                $pending.Push($path)
            }
            else {
                $info = New-Object IO.FileInfo $path
                $entries[$rel] = [pscustomobject]@{ Rel = $rel; Full = $path; IsDir = $false; Size = $info.Length; Attr = $attr }
            }
        }
    }
    return $entries
}

# Returns $null when the streams cannot be read, so the caller reports that instead of
# treating two failed reads as two equal results.
function Get-StreamNames([string]$Path) {
    # The cmdlet is given the plain form: Windows PowerShell 5.1 does not reliably accept
    # the \\?\ prefix together with -Stream.
    if ($Path.StartsWith('\\?\')) { $Path = $Path.Substring(4) }
    try {
        # -Force: without it Get-Item does not see hidden files and reports them as missing.
        $names = Get-Item -LiteralPath $Path -Stream * -Force -ErrorAction Stop |
            Where-Object { $_.Stream -ne ':$DATA' } | ForEach-Object { $_.Stream + '=' + $_.Length }
        return (@($names) | Sort-Object) -join ','
    }
    catch { return $null }
}

<#
Compares two trees: names, sizes, SHA-256, attributes and alternate streams.
Returns difference objects (Kind, Rel, Detail); empty means equal.
Not compared, because docs/parity.md lists them as measured deviations: created time and
folder modified time. File modified time is kept by both engines, so it is compared.
-IgnoreArchive is for comparing a copy with its source: every copy sets the archive bit.
#>
function Compare-Tree {
    param(
        [Parameter(Mandatory)][string]$Reference,
        [Parameter(Mandatory)][string]$Difference,
        [switch]$IgnoreArchive
    )
    $mask = $script:ComparedAttributes
    if ($IgnoreArchive) { $mask = $mask -bxor [IO.FileAttributes]::Archive }

    $a = Get-TreeEntries $Reference
    $b = Get-TreeEntries $Difference
    $diffs = New-Object System.Collections.Generic.List[object]
    function Add-Diff($kind, $rel, $detail) { $diffs.Add([pscustomobject]@{ Kind = $kind; Rel = $rel; Detail = $detail }) }

    foreach ($rel in $a.Keys) {
        if (-not $b.ContainsKey($rel)) { Add-Diff 'MissingInDifference' $rel ''; continue }
        $x = $a[$rel]; $y = $b[$rel]
        if ($x.IsDir -ne $y.IsDir) { Add-Diff 'TypeDiffers' $rel ''; continue }
        if ($x.IsDir) { continue }
        if ($x.Size -ne $y.Size) { Add-Diff 'SizeDiffers' $rel "$($x.Size) vs $($y.Size)"; continue }
        if ((Get-FileSha256 $x.Full) -ne (Get-FileSha256 $y.Full)) { Add-Diff 'HashDiffers' $rel '' }
        if (($x.Attr -band $mask) -ne ($y.Attr -band $mask)) { Add-Diff 'AttributesDiffer' $rel "$($x.Attr -band $mask) vs $($y.Attr -band $mask)" }
        $lastA = [IO.File]::GetLastWriteTimeUtc($x.Full); $lastB = [IO.File]::GetLastWriteTimeUtc($y.Full)
        if ($lastA -ne $lastB) { Add-Diff 'ModifiedTimeDiffers' $rel "$($lastA.ToString('o')) vs $($lastB.ToString('o'))" }
        # Stream names are read only where both plain paths are short enough for the cmdlet.
        if ($x.Full.Length -lt 250 -and $y.Full.Length -lt 250) {
            $sa = Get-StreamNames $x.Full; $sb = Get-StreamNames $y.Full
            if ($null -eq $sa -or $null -eq $sb) { Add-Diff 'StreamsUnreadable' $rel 'could not list alternate data streams' }
            elseif ($sa -ne $sb) { Add-Diff 'StreamsDiffer' $rel "$sa vs $sb" }
        }
    }
    foreach ($rel in $b.Keys) {
        if (-not $a.ContainsKey($rel)) { Add-Diff 'ExtraInDifference' $rel '' }
    }
    return , $diffs.ToArray()
}

function Format-Diffs($Diffs) {
    ($Diffs | ForEach-Object { "$($_.Kind): $($_.Rel) $($_.Detail)" }) -join [Environment]::NewLine
}

# ---------------------------------------------------------------------------
# Running the app
# ---------------------------------------------------------------------------

function Get-RoboProcesses { @(Get-Process -Name 'RoboRightClick' -ErrorAction SilentlyContinue) }

# A trailing backslash would escape the closing quote; a drive root needs two to survive.
function Quote-Arg([string]$Value) {
    if ($Value -match '^[A-Za-z]:\\$') { return '"' + $Value + '\"' }
    return '"' + $Value.TrimEnd('\') + '"'
}

# Looks for a button called $Name in the windows owned by one process.
function Find-UiaButton([int]$ProcessId, [string]$Name) {
    $owner = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $ProcessId
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $owner)
    $buttonName = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $Name
    $buttonType = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Button)
    $both = New-Object Windows.Automation.AndCondition $buttonName, $buttonType
    foreach ($window in $windows) {
        $found = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $both)
        if ($found) { return $found }
    }
    return $null
}

# Any element called $Name (a button, a check box, ...) in the windows owned by one process.
function Find-UiaElement([int]$ProcessId, [string]$Name) {
    $owner = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty), $ProcessId
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $owner)
    $byName = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $Name
    foreach ($window in $windows) {
        $found = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $byName)
        if ($found) { return $found }
    }
    return $null
}

# Waits for an element called $Name in any tray window, or throws.
function Wait-TrayElement([string]$Name, [int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($p in Get-RoboProcesses) {
            $found = Find-UiaElement $p.Id $Name
            if ($found) { return $found }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "No tray window showed '$Name' within $TimeoutSec s."
}

# Fails when a job summary or failure shows up within $Seconds: the error summary's buttons
# (SkipErrors, TryAgain) or its failure strip (FailureReason). A Done job closes its progress
# window and shows none of them; a job that ended Failed or with errors shows them. Pressing
# whatever is open instead (Close-ErrorSummary) would let both outcomes pass.
function Assert-NoJobSummary([string]$Label, [int]$Seconds = 10) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($p in Get-RoboProcesses) {
            foreach ($name in 'FailureReason', 'TryAgain', 'SkipErrors') {
                if (Find-UiaElement $p.Id $name) {
                    Assert-That $false "$Label : the job ended with a summary ('$name' is shown); a Done job shows none"
                }
            }
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Step "$Label : no summary within $Seconds s (the job ended Done)"
}

# The item names of a list (the error summary's lists name each row by its path and note).
function Get-UiaListItemNames($Element) {
    $itemType = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::ListItem)
    return @($Element.FindAll([Windows.Automation.TreeScope]::Descendants, $itemType) | ForEach-Object { $_.Current.Name })
}

function Set-UiaToggle($Element, [bool]$On) {
    $toggle = $Element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $want = if ($On) { [Windows.Automation.ToggleState]::On } else { [Windows.Automation.ToggleState]::Off }
    for ($i = 0; $i -lt 3 -and $toggle.Current.ToggleState -ne $want; $i++) { $toggle.Toggle() }
    if ($toggle.Current.ToggleState -ne $want) { throw "Could not set the check box to $want." }
}

function Invoke-UiaButton($Element) {
    $pattern = $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

# Install and uninstall are run with --quiet, which shows no message box; the exit code is the
# result. Pressing OK on any box that still appears is kept as a fallback, so a build without
# --quiet cannot leave the script waiting. Returns the exit code.
function Invoke-RoboCommand {
    param([Parameter(Mandatory)][string]$ExePath, [string[]]$Arguments = @(), [int]$TimeoutSec = 90)
    $argText = ($Arguments | ForEach-Object { if ($_ -match '^--?[A-Za-z]') { $_ } else { Quote-Arg $_ } }) -join ' '
    if ($argText) { $p = Start-Process -FilePath $ExePath -ArgumentList $argText -PassThru }
    else { $p = Start-Process -FilePath $ExePath -PassThru }
    # Reading Handle now keeps it open; without it ExitCode can come back empty once the
    # process has exited (a known Start-Process -PassThru behavior).
    $null = $p.Handle
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not $p.WaitForExit(300)) {
        if ((Get-Date) -gt $deadline) { $p.Kill(); throw "Timed out after $TimeoutSec s: $ExePath $argText" }
        $ok = Find-UiaButton $p.Id 'OK'
        if ($ok) { Invoke-UiaButton $ok }
    }
    return $p.ExitCode
}

# The clipboard can be held open briefly by its owner (the tray, while it writes), so every
# access retries instead of failing the test on the first ExternalException.
function Clear-ClipboardWithRetry {
    for ($i = 0; $i -lt 20; $i++) {
        try { [Windows.Forms.Clipboard]::Clear(); return }
        catch { Start-Sleep -Milliseconds 100 }
    }
    throw 'Could not clear the clipboard: another program keeps it open.'
}

# Leaf names of the files on the clipboard, sorted; empty when there are none or it is busy.
function Get-ClipboardFileNames {
    try {
        if (-not [Windows.Forms.Clipboard]::ContainsFileDropList()) { return @() }
        return @([Windows.Forms.Clipboard]::GetFileDropList() | ForEach-Object { [IO.Path]::GetFileName($_.TrimEnd('\')) } | Sort-Object)
    }
    catch { return @() }
}

function Set-ClipboardFiles([string[]]$Paths) {
    $list = New-Object System.Collections.Specialized.StringCollection
    foreach ($path in $Paths) { [void]$list.Add($path) }
    for ($i = 0; $i -lt 20; $i++) {
        try { [Windows.Forms.Clipboard]::SetFileDropList($list); return }
        catch { Start-Sleep -Milliseconds 100 }
    }
    throw 'Could not write the clipboard: another program keeps it open.'
}

<#
Waits until the clipboard holds exactly the items just copied or cut. Leaf names are compared
rather than full paths, because the tray may write the long form of a path given in 8.3 form
(a %TEMP% under a long user name). Invoke-Robo clears the clipboard first, so an earlier
selection still on the clipboard cannot satisfy this wait.
#>
function Wait-ClipboardFiles([string[]]$Expected, [int]$TimeoutSec = 10) {
    $want = (@($Expected | ForEach-Object { [IO.Path]::GetFileName($_.TrimEnd('\')) } | Sort-Object)) -join '|'
    $have = ''
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $have = (Get-ClipboardFileNames) -join '|'
        if ($have -ieq $want) { return }
        Start-Sleep -Milliseconds 200
    }
    throw "After $TimeoutSec s the clipboard does not hold the items just copied. Expected: '$want'. Found: '$have'."
}

<#
Runs a verb through the exe, the same COM path as a right-click, and returns the exit code.
The exe is a GUI program, so Start-Process -Wait -PassThru is what yields the code.
Exit 0 means the tray accepted the verb, not that a paste has finished (see Wait-Settled).
Without -ExePath the installed copy is used.
#>
function Invoke-Robo {
    param(
        [Parameter(Mandatory)][ValidateSet('copy', 'cut', 'paste')][string]$Verb,
        [Parameter(Mandatory)][string[]]$Paths,
        [string]$ExePath
    )
    if (-not $ExePath) { $ExePath = $script:E2E.InstalledExe }
    # Without this, a paste that follows could take the previous selection, which is still
    # on the clipboard until the tray has written the new one.
    if ($Verb -ne 'paste') { Clear-ClipboardWithRetry }
    $argText = $Verb + ' ' + (($Paths | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $p = Start-Process -FilePath $ExePath -ArgumentList $argText -Wait -PassThru
    if ($p.ExitCode -eq 0 -and $Verb -ne 'paste') { Wait-ClipboardFiles -Expected $Paths }
    return $p.ExitCode
}

function Get-TreeStats([string]$Path) {
    $count = 0; $bytes = 0L
    if (Test-Path -LiteralPath $Path) {
        foreach ($e in (Get-TreeEntries $Path).Values) { if (-not $e.IsDir) { $count++; $bytes += $e.Size } }
    }
    return [pscustomobject]@{ Files = $count; Bytes = $bytes }
}

<#
There is no --wait: a paste is a job in the tray, so completion is observed on disk. Done means
the destination's file count and size were unchanged for $StableSec seconds and no robocopy is
running. Size alone is not enough: robocopy allocates a destination file at full length before
it writes the data (testlog 2026-10-02), so the size can be final while the copy is not.
-MinFiles guards against calling an empty destination "settled" before the job has started.
#>
function Wait-Settled {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$MinFiles = 1,
        [int]$StableSec = 3,
        [int]$TimeoutSec = 180
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = $null
    $stableSince = Get-Date
    while ((Get-Date) -lt $deadline) {
        $now = Get-TreeStats $Path
        $busy = @(Get-Process -Name 'robocopy' -ErrorAction SilentlyContinue).Count -gt 0
        if ($busy -or $null -eq $last -or $now.Files -ne $last.Files -or $now.Bytes -ne $last.Bytes -or $now.Files -lt $MinFiles) {
            $stableSince = Get-Date
        }
        elseif (((Get-Date) - $stableSince).TotalSeconds -ge $StableSec) { return $now }
        $last = $now
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out after $TimeoutSec s waiting for $Path to settle."
}

# Evidence that a job ran at all: a safety check that only looks at what stayed in place
# would also pass if the paste had been refused or had never started.
function Wait-PathExists([string]$Path, [int]$TimeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { return }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out after $TimeoutSec s waiting for $Path to appear. The paste may not have run; check the tray's Jobs window."
}

function Wait-RobocopyGone([int]$TimeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-Process -Name 'robocopy' -ErrorAction SilentlyContinue).Count -eq 0) { return }
        Start-Sleep -Milliseconds 300
    }
    throw 'robocopy is still running.'
}

<#
Suspends the next robocopy.exe that starts, as early as a polling thread can catch it, and
resumes it on request. Cancel.Tests uses it to put a file in the destination after the job's
presence check (done before robocopy starts) but before robocopy looks at the destination:
the late arrival that cancel cleanup must never mistake for a partial copy. Robocopy may still
have started before the catch; the caller checks for that and reports the run as inconclusive.
#>
function Start-RobocopyCatcher {
    if (-not ('RrcE2E.RobocopyCatcher' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
namespace RrcE2E {
    public sealed class RobocopyCatcher : IDisposable {
        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private const uint ProcessSuspendResume = 0x0800;
        private readonly HashSet<int> before = new HashSet<int>();
        private readonly ManualResetEvent caught = new ManualResetEvent(false);
        private readonly Thread thread;
        private volatile bool stop;
        private IntPtr handle = IntPtr.Zero;
        private bool suspended;
        public int ProcessId;
        public RobocopyCatcher() {
            foreach (var p in Process.GetProcessesByName("robocopy")) { before.Add(p.Id); }
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
        }
        private void Loop() {
            while (!stop) {
                foreach (var p in Process.GetProcessesByName("robocopy")) {
                    if (before.Contains(p.Id)) { continue; }
                    var h = OpenProcess(ProcessSuspendResume, false, p.Id);
                    if (h == IntPtr.Zero) { continue; }
                    if (NtSuspendProcess(h) >= 0) { handle = h; suspended = true; ProcessId = p.Id; caught.Set(); return; }
                    CloseHandle(h);
                }
                Thread.Sleep(0);
            }
        }
        public bool WaitCaught(int milliseconds) { return caught.WaitOne(milliseconds); }
        public bool Resume() {
            if (!suspended) { return false; }
            suspended = false;
            return NtResumeProcess(handle) >= 0;
        }
        public void Dispose() {
            stop = true;
            if (suspended) { Resume(); }
            if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
        }
    }
}
'@
    }
    return New-Object RrcE2E.RobocopyCatcher
}

# Presses OK on any message box the tray left open (a refusal or an error summary).
function Close-TrayDialogs {
    foreach ($p in Get-RoboProcesses) {
        $ok = Find-UiaButton $p.Id 'OK'
        if ($ok) { Invoke-UiaButton $ok; Write-Step 'closed a tray message box' }
    }
}

function Wait-TrayRunning([int]$TimeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $running = Get-RoboProcesses | Where-Object { $_.Path -eq $script:E2E.InstalledExe }
        if ($running) { return }
        Start-Sleep -Milliseconds 300
    }
    throw 'The tray process is not running from the install folder.'
}

# A forced stop is acceptable here: these scripts only call it with no job running.
function Restart-Tray {
    foreach ($p in Get-RoboProcesses) { Stop-Process -Id $p.Id -Force; [void]$p.WaitForExit(10000) }
    Start-Process -FilePath $script:E2E.InstalledExe | Out-Null
    Wait-TrayRunning
}

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

function Get-RoboConfigText {
    if (Test-Path -LiteralPath $script:E2E.ConfigFile) { return [IO.File]::ReadAllText($script:E2E.ConfigFile) }
    return $null
}

# Changes top-level settings in config.json, then restarts the tray so they apply.
function Set-RoboConfig([hashtable]$Changes) {
    $text = Get-RoboConfigText
    if ($text) { $config = $text | ConvertFrom-Json } else { $config = [pscustomobject]@{} }
    foreach ($key in $Changes.Keys) { Add-Member -InputObject $config -NotePropertyName $key -NotePropertyValue $Changes[$key] -Force }
    New-Item -ItemType Directory -Force -Path $script:E2E.ConfigDir | Out-Null
    [IO.File]::WriteAllText($script:E2E.ConfigFile, ($config | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    Restart-Tray
}

function Restore-RoboConfig([string]$OriginalText) {
    if ($null -eq $OriginalText) { return }
    [IO.File]::WriteAllText($script:E2E.ConfigFile, $OriginalText, (New-Object Text.UTF8Encoding $false))
    Restart-Tray
}

# ---------------------------------------------------------------------------
# File Explorer windows and the Robo-Paste hotkey (Hotkey.Tests.ps1, Ephemeral.Tests.ps1)
# ---------------------------------------------------------------------------

function Initialize-WindowNative {
    if ('RrcWindowNative' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RrcWindowNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
'@
}

# SendKeys reads + ^ % ~ ( ) { } [ ] as commands. A path must be escaped: a short %TEMP%
# path such as C:\Users\ABCDEF~1\... contains a ~, which SendKeys sends as Enter.
function ConvertTo-SendKeysText([string]$Text) {
    return [regex]::Replace($Text, '[+^%~(){}\[\]]', { param($m) '{' + $m.Value + '}' })
}

function Get-ExplorerWindows {
    $shell = New-Object -ComObject Shell.Application
    return @($shell.Windows())
}

# Closes every File Explorer window, so the next one opened is the only one. Quit on a tab's
# entry can leave another tab of the window open, so it is repeated until none is left.
function Close-ExplorerWindows {
    $deadline = (Get-Date).AddSeconds(10)
    do {
        foreach ($w in Get-ExplorerWindows) { try { $w.Quit() } catch { } }
        Start-Sleep -Milliseconds 300
    } while (@(Get-ExplorerWindows).Count -gt 0 -and (Get-Date) -lt $deadline)
}

# The folder path a ShellWindows entry shows, or $null (virtual locations have none).
function Get-ExplorerWindowPath($Window) {
    try { return $Window.Document.Folder.Self.Path } catch { return $null }
}

function Wait-ExplorerShows([string]$Path, [int]$TimeoutSec = 15) {
    $want = $Path.TrimEnd('\')
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in Get-ExplorerWindows) {
            $p = Get-ExplorerWindowPath $w
            if ($p -and $p.TrimEnd('\') -ieq $want) { return $w }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "File Explorer did not show $Path within $TimeoutSec s."
}

function Set-ForegroundWindowFirmly([IntPtr]$Hwnd) {
    Initialize-WindowNative
    for ($i = 0; $i -lt 5; $i++) {
        [void][RrcWindowNative]::ShowWindow($Hwnd, 9) # SW_RESTORE
        [void][RrcWindowNative]::SetForegroundWindow($Hwnd)
        Start-Sleep -Milliseconds 300
        if ([RrcWindowNative]::GetForegroundWindow() -eq $Hwnd) { return }
        # Windows lets a process take the foreground right after it sent input; a tap of Alt
        # is that input.
        [RrcWindowNative]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [RrcWindowNative]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    }
    throw 'Could not bring the File Explorer window to the front.'
}

# Keyboard focus into the file list: the UI Automation element of File Explorer's item view
# (class UIItemsView, the DirectUIHWND under SHELLDLL_DefView that the hotkey requires).
# On build 26200 the UIItemsView element itself is not keyboard-focusable and SetFocus throws
# "Target element cannot receive focus"; its DUIListView parent is, and focusing it puts the
# keyboard focus in the same DirectUIHWND. So the nearest focusable element from the item view
# upwards, never above the window, takes the focus.
function Set-ExplorerFileListFocus([IntPtr]$Hwnd) {
    $window = [Windows.Automation.AutomationElement]::FromHandle($Hwnd)
    $byClass = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ClassNameProperty), 'UIItemsView'
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $list = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $byClass)
        if ($list) {
            $target = $list
            while ($target -and -not $target.Current.IsKeyboardFocusable -and -not [Windows.Automation.Automation]::Compare($target, $window)) {
                $target = $walker.GetParent($target)
            }
            if (-not $target -or -not $target.Current.IsKeyboardFocusable) {
                throw 'Neither the file list (UIItemsView) nor an element around it inside the window accepts the keyboard focus.'
            }
            $target.SetFocus()
            Start-Sleep -Milliseconds 300
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'No file list (UIItemsView) found in the File Explorer window.'
}

<#
Opens $Location (a folder, a zip file or a shell: location) in File Explorer, which must have
no window open (Close-ExplorerWindows), brings the window to the front and puts the focus in
its file list. Returns the window handle.
#>
function Open-ExplorerLocation([string]$Location, [int]$TimeoutSec = 15) {
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList (Quote-Arg $Location) | Out-Null
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $windows = @()
    while ($windows.Count -eq 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $windows = @(Get-ExplorerWindows)
    }
    if ($windows.Count -eq 0) { throw "File Explorer did not open $Location within $TimeoutSec s." }
    # A window left open by an earlier step must not be mistaken for this one: a folder is
    # found by its path, anything else (a library, a zip) only when it is the only window.
    if (Test-Path -LiteralPath $Location -PathType Container) { $window = Wait-ExplorerShows $Location $TimeoutSec }
    elseif ($windows.Count -eq 1) { $window = $windows[0] }
    else { throw "File Explorer has $($windows.Count) windows open; cannot tell which one shows $Location." }
    $hwnd = [IntPtr][long]$window.HWND
    Set-ForegroundWindowFirmly $hwnd
    Set-ExplorerFileListFocus $hwnd
    return $hwnd
}

# The default hotkey, as one press and release. SendKeys input is injected at this script's
# integrity level, which the hook accepts (it refuses only lower-integrity injection).
function Send-PasteHotkey { [System.Windows.Forms.SendKeys]::SendWait('^+v') }

# The default hotkey held down: Ctrl and Shift, then V repeating every 33 ms as a held key
# does, then the releases.
function Send-PasteHotkeyHeld([int]$Milliseconds = 2000) {
    Initialize-WindowNative
    $ctrl = 0x11; $shift = 0x10; $v = 0x56; $keyUp = 2
    [RrcWindowNative]::keybd_event($ctrl, 0, 0, [UIntPtr]::Zero)
    [RrcWindowNative]::keybd_event($shift, 0, 0, [UIntPtr]::Zero)
    $deadline = (Get-Date).AddMilliseconds($Milliseconds)
    do {
        [RrcWindowNative]::keybd_event($v, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 33
    } while ((Get-Date) -lt $deadline)
    [RrcWindowNative]::keybd_event($v, 0, $keyUp, [UIntPtr]::Zero)
    [RrcWindowNative]::keybd_event($shift, 0, $keyUp, [UIntPtr]::Zero)
    [RrcWindowNative]::keybd_event($ctrl, 0, $keyUp, [UIntPtr]::Zero)
}

# A second paste of the same file into the same folder asks Replace or Skip (conflictDefault
# "ask"); no such question means there was no second paste.
function Test-ConflictQuestionShown {
    foreach ($p in Get-RoboProcesses) {
        if (Find-UiaElement $p.Id 'Replace') { return $true }
    }
    return $false
}

# ---------------------------------------------------------------------------
# Registry
# ---------------------------------------------------------------------------

# KEEP IN SYNC with Registration.InstallValues, Registration.UninstallRemovals and the
# ShellVerbs table in src/RoboRightClick.Core (also docs/host-architecture.md section 4).
# If one of them changes, change this table in the same commit.
$script:AppId = '{B708F29C-8ED8-40BD-832E-F05180F1B285}'
$script:Verbs = @(
    @{ Key = 'RoboCopy'; Label = 'Robo-Copy'; MenuLabel = 'Robo-Cop&y'; Icon = 'robo-copy.ico'; Clsid = '{BD15DC6A-FBC1-4949-B61D-3B8FC390062F}'; Multi = 'Player'
        Associations = @('AllFilesystemObjects') },
    @{ Key = 'RoboCut'; Label = 'Robo-Cut'; MenuLabel = 'Robo-C&ut'; Icon = 'robo-cut.ico'; Clsid = '{1A061376-A3F7-41BF-A516-E635ED91ACDF}'; Multi = 'Player'
        Associations = @('AllFilesystemObjects') },
    @{ Key = 'RoboPaste'; Label = 'Robo-Paste'; MenuLabel = 'Ro&bo-Paste'; Icon = 'robo-paste.ico'; Clsid = '{9D1BAE79-13C3-427F-A7E6-34150D5C49AB}'; Multi = 'Single'
        Associations = @('Directory\Background', 'Directory', 'Drive') }
)
$script:RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$script:UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\RoboRightClick'

# Kind: String, DWord or Binary. A Data of $null means "present, any value of that kind".
function Get-ExpectedRegistry([string]$ExePath, [string]$Version) {
    $quoted = '"' + $ExePath + '"'
    $rows = New-Object System.Collections.Generic.List[object]
    function Add-Row($key, $name, $kind, $data) { $rows.Add([pscustomobject]@{ Key = $key; Name = $name; Kind = $kind; Data = $data }) }

    $appIdKey = "Software\Classes\AppID\$script:AppId"
    Add-Row $appIdKey '' 'String' 'RoboRightClick'
    Add-Row $appIdKey 'AccessPermission' 'Binary' $null
    Add-Row $appIdKey 'LaunchPermission' 'Binary' $null
    foreach ($v in $script:Verbs) {
        $clsidKey = "Software\Classes\CLSID\$($v.Clsid)"
        Add-Row $clsidKey '' 'String' "RoboRightClick $($v.Label)"
        Add-Row $clsidKey 'AppID' 'String' $script:AppId
        Add-Row "$clsidKey\LocalServer32" '' 'String' $quoted
        foreach ($assoc in $v.Associations) {
            $verbKey = "Software\Classes\$assoc\shell\$($v.Key)"
            # The menu text carries the access key ("&"); the class name above does not.
            Add-Row $verbKey 'MUIVerb' 'String' $v.MenuLabel
            Add-Row $verbKey 'Icon' 'String' (Join-Path (Split-Path -Parent $ExePath) $v.Icon)
            # A background click selects nothing; Explorer hides a background verb marked Single.
            if ($assoc -ne 'Directory\Background') {
                Add-Row $verbKey 'MultiSelectModel' 'String' $v.Multi
            }
            Add-Row "$verbKey\command" 'DelegateExecute' 'String' $v.Clsid
        }
    }
    Add-Row $script:UninstallKey 'DisplayName' 'String' 'RoboRightClick'
    Add-Row $script:UninstallKey 'DisplayVersion' 'String' $Version
    Add-Row $script:UninstallKey 'DisplayIcon' 'String' $ExePath
    Add-Row $script:UninstallKey 'InstallLocation' 'String' (Split-Path -Parent $ExePath)
    Add-Row $script:UninstallKey 'UninstallString' 'String' "$quoted --uninstall"
    Add-Row $script:UninstallKey 'NoModify' 'DWord' 1
    Add-Row $script:UninstallKey 'NoRepair' 'DWord' 1
    return $rows.ToArray()
}

# The key trees uninstall deletes (the Run value is handled separately).
function Get-RemovedKeyTrees {
    $keys = @("Software\Classes\AppID\$script:AppId", $script:UninstallKey)
    foreach ($v in $script:Verbs) {
        $keys += "Software\Classes\CLSID\$($v.Clsid)"
        foreach ($assoc in $v.Associations) { $keys += "Software\Classes\$assoc\shell\$($v.Key)" }
    }
    return $keys
}

function Read-HkcuValue([string]$Key, [string]$Name) {
    $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Key)
    if ($null -eq $k) { return $null }
    try {
        $value = $k.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($null -eq $value) { return $null }
        return [pscustomobject]@{ Value = $value; Kind = $k.GetValueKind($Name).ToString() }
    }
    finally { $k.Dispose() }
}

function Assert-RegistryInstalled([string]$ExePath, [string]$Version, $ExpectRun) {
    $problems = New-Object System.Collections.Generic.List[string]
    $rows = Get-ExpectedRegistry $ExePath $Version
    foreach ($row in $rows) {
        $actual = Read-HkcuValue $row.Key $row.Name
        if ($null -eq $actual) { $problems.Add("missing: HKCU\$($row.Key) [$($row.Name)]"); continue }
        if ($actual.Kind -ne $row.Kind) { $problems.Add("kind $($actual.Kind), expected $($row.Kind): HKCU\$($row.Key) [$($row.Name)]"); continue }
        if ($null -ne $row.Data -and "$($actual.Value)" -ne "$($row.Data)") {
            $problems.Add("value '$($actual.Value)', expected '$($row.Data)': HKCU\$($row.Key) [$($row.Name)]")
        }
        if ($row.Kind -eq 'Binary' -and $actual.Value.Length -eq 0) { $problems.Add("empty binary: HKCU\$($row.Key) [$($row.Name)]") }
    }
    $run = Read-HkcuValue $script:RunKey 'RoboRightClick'
    if ($ExpectRun -eq $true -and ($null -eq $run -or $run.Value -ne ('"' + $ExePath + '"'))) { $problems.Add('Run value missing or wrong') }
    if ($ExpectRun -eq $false -and $null -ne $run) { $problems.Add('Run value present but autostart is off') }
    if ($problems.Count -gt 0) { throw ('Registry check failed:' + [Environment]::NewLine + ($problems -join [Environment]::NewLine)) }
    Write-Step "ok: all $($rows.Count) expected registry values present"
}

function Assert-RegistryRemoved {
    $left = @(Get-RemovedKeyTrees | Where-Object {
            $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($_)
            if ($null -ne $k) { $k.Dispose() }
            $null -ne $k
        })
    if ($null -ne (Read-HkcuValue $script:RunKey 'RoboRightClick')) { $left += "$($script:RunKey) [RoboRightClick]" }
    if ($left.Count -gt 0) { throw ('Registry entries survived uninstall:' + [Environment]::NewLine + ($left -join [Environment]::NewLine)) }
    Write-Step 'ok: every registry key and the Run value are gone'
}

# Keys other software shares with the app. Install creates them when absent and uninstall must
# never delete them (product invariant 4, docs/host-architecture.md section 4), so each one
# still exists after uninstall.
function Get-SharedParentKeys {
    $keys = @('Software\Classes\CLSID', 'Software\Classes\AppID', $script:RunKey,
        (Split-Path -Parent $script:UninstallKey))
    foreach ($v in $script:Verbs) {
        foreach ($assoc in $v.Associations) { $keys += "Software\Classes\$assoc\shell" }
    }
    return @($keys | Select-Object -Unique)
}

function Assert-SharedParentsKept {
    $gone = @(Get-SharedParentKeys | Where-Object {
            $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($_)
            if ($null -ne $k) { $k.Dispose() }
            $null -eq $k
        })
    if ($gone.Count -gt 0) { throw ('Uninstall deleted shared keys it does not own:' + [Environment]::NewLine + ($gone -join [Environment]::NewLine)) }
    Write-Step 'ok: shared parent keys (CLSID, AppID, Run, Uninstall, each shell key) are still there'
}

# ---------------------------------------------------------------------------
# Hostile input helpers (Security.Tests)
# ---------------------------------------------------------------------------

function Initialize-RawClipboardType {
    if ('RrcE2E.RawClipboard' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace RrcE2E {
    public static class RawClipboard {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string name, StringBuilder target, uint max);

        public static uint FormatId(string name) { return RegisterClipboardFormat(name); }

        // Replaces the clipboard with exactly these formats and bytes, whatever they hold.
        public static bool Set(uint[] formats, byte[][] data) {
            if (!OpenClipboard(IntPtr.Zero)) { return false; }
            try {
                if (!EmptyClipboard()) { return false; }
                for (int i = 0; i < formats.Length; i++) {
                    IntPtr mem = GlobalAlloc(0x0002, (UIntPtr)(uint)Math.Max(1, data[i].Length));
                    if (mem == IntPtr.Zero) { return false; }
                    IntPtr p = GlobalLock(mem);
                    Marshal.Copy(data[i], 0, p, data[i].Length);
                    GlobalUnlock(mem);
                    if (SetClipboardData(formats[i], mem) == IntPtr.Zero) { return false; }
                }
                return true;
            }
            finally { CloseClipboard(); }
        }

        // "\Device\HarddiskVolume3" for "C:".
        public static string DeviceOf(string drive) {
            var sb = new StringBuilder(1024);
            return QueryDosDevice(drive, sb, 1024) == 0 ? null : sb.ToString().Split('\0')[0];
        }
    }
}
'@
}

# DROPFILES (shlobj_core.h): pFiles, POINT, fNC, fWide, then names each ended by NUL and a
# final NUL. The parameters let a test forge any header it likes.
function New-DropFilesBlock {
    param(
        [string[]]$Paths = @(),
        [bool]$Wide = $true,
        [Nullable[uint32]]$PFiles = $null,
        [switch]$NoTerminator
    )
    $text = ($Paths -join [char]0)
    if (-not $NoTerminator) { $text += ([string][char]0) + ([string][char]0) }
    $body = if ($Wide) { [Text.Encoding]::Unicode.GetBytes($text) } else { [Text.Encoding]::ASCII.GetBytes($text) }
    $header = New-Object byte[] 20
    $start = if ($null -ne $PFiles) { [uint32]$PFiles } else { [uint32]20 }
    [BitConverter]::GetBytes($start).CopyTo($header, 0)
    if ($Wide) { [BitConverter]::GetBytes([int]1).CopyTo($header, 16) }
    $block = New-Object byte[] ($header.Length + $body.Length)
    $header.CopyTo($block, 0)
    $body.CopyTo($block, $header.Length)
    return , $block
}

# Puts a forged CF_HDROP and a Preferred DropEffect (1 copy, 2 move) on the clipboard.
function Set-RawClipboardFiles([byte[]]$DropFiles, [uint32]$DropEffect = 1) {
    Initialize-RawClipboardType
    $effectFormat = [RrcE2E.RawClipboard]::FormatId('Preferred DropEffect')
    $formats = [uint32[]]@(15, $effectFormat)
    $data = [byte[][]]@($DropFiles, [BitConverter]::GetBytes($DropEffect))
    for ($i = 0; $i -lt 20; $i++) {
        if ([RrcE2E.RawClipboard]::Set($formats, $data)) { return }
        Start-Sleep -Milliseconds 100
    }
    throw 'Could not write the forged clipboard: another program keeps it open.'
}

# "\Device\HarddiskVolume3" for a path's drive, for \\?\GLOBALROOT\... test paths.
function Get-VolumeDevicePath([string]$Path) {
    Initialize-RawClipboardType
    $device = [RrcE2E.RawClipboard]::DeviceOf($Path.Substring(0, 2))
    if (-not $device) { throw "No device name for the drive of $Path." }
    return $device
}

# The tray's process id (one tray runs at a time); $null when none.
function Get-TrayProcessId {
    $running = @(Get-RoboProcesses | Where-Object { $_.Path -eq $script:E2E.InstalledExe })
    if ($running.Count -eq 0) { return $null }
    return $running[0].Id
}

function Get-ProcessCommandLine([int]$ProcessId) {
    return (Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId").CommandLine
}

# Size of the app's crash log, or -1 when there is none; a changed value means a crash was logged.
function Get-CrashLogSize {
    $file = Join-Path $script:E2E.DataDir 'crash.log'
    if (Test-Path -LiteralPath $file) { return (Get-Item -LiteralPath $file).Length }
    return -1
}
