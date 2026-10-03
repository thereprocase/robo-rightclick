<#
Footprint: the per-user install touches only the keys and folders the design lists, and
uninstall takes all of them away (product invariant 4). Needs the app NOT installed; installs
and uninstalls once.

Three snapshots, each of the whole HKCU hive (reg export, parsed into key -> value lines) and
of the folders an installer could write to:

  before -> after install:    every added, removed or changed HKCU key and every new file must
                              be one the design lists (Get-ExpectedRegistry, the Run value, the
                              install, config and data folders). Anything else is listed and
                              fails unless -AllowKey / -AllowPath names it, after a person has
                              judged it to be Windows noise.
  after jobs -> after uninstall: three jobs run in between (normal logging, so logs and history
                              exist); uninstall must remove the keys, folders and tray.
  before -> after uninstall:  nothing the app owns is left, in any snapshot.

Beyond HKCU: a search of HKLM\SOFTWARE and HKLM\SYSTEM\CurrentControlSet\Services for the app's
GUIDs and name, and before/after lists of scheduled tasks, services and Run values in HKLM,
prove that nothing outside the user's hive was written.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    # Substrings of HKCU key paths or file paths judged to be Windows noise, per run.
    [string[]]$AllowKey = @(),
    [string[]]$AllowPath = @()
)
$ErrorActionPreference = 'Stop'
$AllowKey = @($AllowKey | ForEach-Object { $_ -split '[|,]' } | Where-Object { $_ })
$AllowPath = @($AllowPath | ForEach-Object { $_ -split '[|,]' } | Where-Object { $_ })
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root -Exe $Exe

Write-Host 'Footprint.Tests'
if (-not (Test-Path -LiteralPath $Exe)) { throw "Exe not found: $Exe" }
if (Test-Path -LiteralPath $script:E2E.InstalledExe) { throw 'Already installed. Uninstall first; this script needs a clean "before".' }
if (Get-RoboProcesses) { throw 'A RoboRightClick process is already running.' }

$work = Join-Path $script:E2E.Root 'footprint'
Remove-TreeIfPresent $work
New-Item -ItemType Directory -Path $work | Out-Null

# ---------------------------------------------------------------------------
# Registry snapshots
# ---------------------------------------------------------------------------

# "[HKEY_CURRENT_USER\key]" -> sorted value lines. reg export writes UTF-16; a long binary value
# continues on the next line after a trailing backslash, which is joined back here.
function Get-HkcuSnapshot([string]$Name) {
    $file = Join-Path $work "$Name.reg"
    $out = & reg.exe export HKCU $file /y 2>&1
    if ($LASTEXITCODE -ne 0) { throw "reg export failed: $out" }
    $snapshot = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[string]]' ([StringComparer]::OrdinalIgnoreCase)
    $reader = New-Object IO.StreamReader($file, [Text.Encoding]::Unicode)
    try {
        $key = $null
        $pending = $null
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($null -ne $pending) {
                $pending += $line.TrimStart()
                if (-not $pending.EndsWith('\')) { $snapshot[$key].Add($pending); $pending = $null } else { $pending = $pending.Substring(0, $pending.Length - 1) }
                continue
            }
            if ($line.StartsWith('[') -and $line.EndsWith(']')) {
                $key = $line.Substring(1, $line.Length - 2)
                if (-not $snapshot.ContainsKey($key)) { $snapshot[$key] = New-Object 'System.Collections.Generic.List[string]' }
            }
            elseif ($line.Length -gt 0 -and $null -ne $key -and -not $line.StartsWith('Windows Registry Editor')) {
                if ($line.EndsWith('\') -and $line -match '=hex') { $pending = $line.Substring(0, $line.Length - 1) }
                else { $snapshot[$key].Add($line) }
            }
        }
    }
    finally { $reader.Dispose() }
    Remove-Item -LiteralPath $file -Force
    return $snapshot
}

# One line per difference: "+ key", "- key", "~ key: value-line-added / removed".
function Compare-Registry($Before, $After) {
    $diffs = New-Object System.Collections.Generic.List[string]
    foreach ($key in $After.Keys) {
        if (-not $Before.ContainsKey($key)) { $diffs.Add("+ $key"); continue }
        $old = New-Object 'System.Collections.Generic.HashSet[string]' (, [string[]]$Before[$key].ToArray())
        $new = New-Object 'System.Collections.Generic.HashSet[string]' (, [string[]]$After[$key].ToArray())
        foreach ($line in $new) { if (-not $old.Contains($line)) { $diffs.Add("~ $key :: + " + $(if ($line.Length -gt 120) { $line.Substring(0, 120) + '...' } else { $line })) } }
        foreach ($line in $old) { if (-not $new.Contains($line)) { $diffs.Add("~ $key :: - " + $(if ($line.Length -gt 120) { $line.Substring(0, 120) + '...' } else { $line })) } }
    }
    foreach ($key in $Before.Keys) { if (-not $After.ContainsKey($key)) { $diffs.Add("- $key") } }
    return , $diffs.ToArray()
}

# The keys the design owns. Trees: every key in the expected table, where anything below is the
# app's too. Parents: the shared keys above them, which install creates when absent; only the key
# itself counts, never what other software keeps below it.
function Get-OwnedKeys {
    $trees = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $parents = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in (Get-ExpectedRegistry 'C:\x\RoboRightClick.exe' '0')) {
        $key = 'HKEY_CURRENT_USER\' + $row.Key
        [void]$trees.Add($key)
        $key = $key.Substring(0, $key.LastIndexOf('\'))
        while ($key.Contains('\')) { [void]$parents.Add($key); $key = $key.Substring(0, $key.LastIndexOf('\')) }
    }
    [void]$parents.Add('HKEY_CURRENT_USER\' + $script:RunKey)
    return [pscustomobject]@{ Trees = $trees; Parents = $parents }
}

# Runtime activity of Windows itself that shows up in any two HKCU exports taken minutes apart.
# Judged once and recorded in docs/testlog.md; anything else is reported.
$script:RegistryNoise = @(
    '\Explorer\UserAssist', '\Explorer\RecentDocs', '\Explorer\ComDlg32', '\Explorer\Streams', '\Explorer\TypedPaths',
    '\Explorer\FeatureUsage', '\Explorer\StartPage', '\CloudStore\', '\Search\', '\CurrentVersion\Notifications\',
    '\CurrentVersion\PushNotifications', '\ContentDeliveryManager', '\Software\Microsoft\Windows\Shell\Bags',
    '\Software\Microsoft\Windows\Shell\BagMRU', '\Software\Microsoft\Windows NT\CurrentVersion\Windows',
    '\Explorer\SessionInfo', '\Explorer\Accent', '\Explorer\HideDesktopIcons', '\SystemSettings\', '\Software\Microsoft\Input\',
    '\Software\Microsoft\Windows\CurrentVersion\Explorer\Modules', '\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager',
    '\Software\Microsoft\Windows\CurrentVersion\Group Policy', '\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer',
    '\Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable', '\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved',
    '\Software\Microsoft\Windows\CurrentVersion\Explorer\CIDOpen', '\Software\Microsoft\Windows\CurrentVersion\Explorer\CIDSave',
    '\Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts', '\Software\Microsoft\Windows\CurrentVersion\Internet Settings',
    '\Software\Microsoft\Windows\DWM', '\Software\Microsoft\Windows\Shell\Associations', '\Software\Microsoft\Windows\CurrentVersion\UFH',
    '\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU', '\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband',
    '\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons', '\Software\Microsoft\Windows\CurrentVersion\AppModel',
    '\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers', '\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\People'
)

function Test-OwnedKey([string]$Path, $Owned) {
    foreach ($p in $Owned.Trees) { if ($Path.Equals($p, [StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($p + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    return $false
}

# Splits a difference list into those the design owns, known runtime noise, and the rest.
function Get-RegistryClassification($Diffs) {
    $owned = Get-OwnedKeys
    $app = New-Object System.Collections.Generic.List[string]
    $noise = New-Object System.Collections.Generic.List[string]
    $kept = New-Object System.Collections.Generic.List[string]
    $other = New-Object System.Collections.Generic.List[string]
    foreach ($d in $Diffs) {
        $path = ($d -replace '^[+~-] ', '') -replace ' :: .*$', ''
        # Windows records that a program added a startup entry (the "new startup app" notice) in
        # RunNotification, by itself; the app does not write it and cannot own it.
        if ($path -match '\\CurrentVersion\\RunNotification$' -and $d -match 'RoboRightClick') { $kept.Add($d) }
        elseif ($d -match 'RoboRightClick' -or (Test-OwnedKey $path $owned) -or ($d.StartsWith('+ ') -and $owned.Parents.Contains($path))) { $app.Add($d) }
        elseif ($script:RegistryNoise | Where-Object { $path.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }) { $noise.Add($d) }
        elseif ($AllowKey | Where-Object { $path.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }) { $noise.Add($d) }
        else { $other.Add($d) }
    }
    return [pscustomobject]@{ App = $app.ToArray(); Noise = $noise.ToArray(); Other = $other.ToArray(); WindowsKept = $kept.ToArray() }
}

# ---------------------------------------------------------------------------
# File snapshots
# ---------------------------------------------------------------------------

# Where an installer could leave something. Depth-limited for the broad roots (Windows writes
# deep inside them all the time); the app's own folders are listed in full.
function Get-FileSnapshot {
    $snapshot = @{}
    $profile = $env:USERPROFILE
    $roots = @(
        @{ Path = $env:APPDATA; Depth = 2 },
        @{ Path = $env:LOCALAPPDATA; Depth = 2 },
        @{ Path = $env:ProgramData; Depth = 2 },
        @{ Path = $env:ProgramFiles; Depth = 1 },
        @{ Path = ${env:ProgramFiles(x86)}; Depth = 1 },
        @{ Path = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu'); Depth = 4 },
        @{ Path = (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu'); Depth = 4 },
        @{ Path = [Environment]::GetFolderPath('Desktop'); Depth = 2 },
        @{ Path = [Environment]::GetFolderPath('MyDocuments'); Depth = 2 },
        @{ Path = (Join-Path $profile 'Downloads'); Depth = 1 },
        @{ Path = (Join-Path $env:SystemRoot 'System32\Tasks'); Depth = 3 },
        @{ Path = $env:TEMP; Depth = 1 },
        @{ Path = $profile; Depth = 1 }
    )
    foreach ($r in $roots) {
        if (-not $r.Path -or -not (Test-Path -LiteralPath $r.Path)) { continue }
        $stack = New-Object 'System.Collections.Generic.Stack[object]'
        $stack.Push(@($r.Path, 0))
        while ($stack.Count -gt 0) {
            $item = $stack.Pop()
            try { $entries = [IO.Directory]::GetFileSystemEntries($item[0]) } catch { continue }
            foreach ($path in $entries) {
                if ($path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($script:E2E.Root, [StringComparison]::OrdinalIgnoreCase)) { continue }
                try { $attr = [IO.File]::GetAttributes($path) } catch { continue }
                $isDir = ($attr -band [IO.FileAttributes]::Directory) -ne 0
                if ($isDir) { $snapshot[$path + '\'] = 'dir' } else { try { $snapshot[$path] = 'file ' + (New-Object IO.FileInfo $path).Length } catch { $snapshot[$path] = 'file ?' } }
                $isApp = $path -match 'RoboRightClick'
                if ($isDir -and ($item[1] + 1 -lt $r.Depth -or $isApp) -and -not ($attr -band [IO.FileAttributes]::ReparsePoint)) { $stack.Push(@($path, $item[1] + 1)) }
            }
        }
    }
    return $snapshot
}

function Compare-Files($Before, $After) {
    $added = @($After.Keys | Where-Object { -not $Before.ContainsKey($_) } | Sort-Object)
    $removed = @($Before.Keys | Where-Object { -not $After.ContainsKey($_) } | Sort-Object)
    return [pscustomobject]@{ Added = $added; Removed = $removed }
}

# Toast images Windows writes while a toast shows (judged in the 2026-10-02 testlog entries and
# searched for the app's strings by Ephemeral.Tests): the notification icon cache, and the stock
# balloon icon in %TEMP% named by a GUID.
function Test-ToastImage([string]$Path) {
    if ($Path.IndexOf('\Microsoft\Windows\ActionCenterCache\', [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    return ((Split-Path -Parent $Path) -ieq $env:TEMP.TrimEnd('\')) -and ((Split-Path -Leaf $Path) -match '^\{[0-9a-fA-F-]{36}\}\.png$')
}

# Windows' own background writers under the profile (the web cache database, the token broker's
# cache, the per-user class hive logs, the WebView cache, Windows Update's logs), judged unrelated to the app in the
# 2026-10-02 testlog entries; Ephemeral.Tests searches their content for the app's strings.
$script:JudgedNoisePaths = @('\USOShared\Logs\', '\Microsoft\Windows\WebCache\', '\Microsoft\TokenBroker\', '\Microsoft\Windows\UsrClass.dat', '\EBWebView\')

function Test-AllowedPath([string]$Path) {
    if (Test-ToastImage $Path) { return $true }
    foreach ($n in $script:JudgedNoisePaths) { if ($Path.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }
    foreach ($a in $AllowPath) { if ($Path.IndexOf($a, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }
    return $false
}

function Get-OutsideState {
    $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object { $_.TaskPath + $_.TaskName } | Sort-Object)
    $services = @(Get-CimInstance Win32_Service | ForEach-Object Name | Sort-Object)
    $hklmRun = New-Object System.Collections.Generic.List[string]
    foreach ($k in 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') {
        if (Test-Path $k) { foreach ($name in (Get-Item $k).Property) { $hklmRun.Add("$k\$name") } }
    }
    $hklmRun = @($hklmRun | Sort-Object)
    return [pscustomobject]@{ Tasks = $tasks; Services = $services; HklmRun = $hklmRun }
}

# Searches a machine-wide registry tree for the app's name or any of its GUIDs.
function Find-MachineWideTraces {
    $needles = @('RoboRightClick', $script:AppId) + @($script:Verbs | ForEach-Object { $_.Clsid })
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($tree in 'HKLM\SOFTWARE', 'HKLM\SYSTEM\CurrentControlSet\Services') {
        foreach ($needle in $needles) {
            $result = & reg.exe query $tree /s /f $needle 2>$null
            foreach ($line in @($result)) {
                if ($line -and $line -notmatch '^End of search' -and $line -match '^HKEY_') { $hits.Add("$needle -> $line") }
            }
        }
    }
    return $hits.ToArray()
}

# Windows' own Apps-and-features cache (UFH\ARP) copies the per-user Uninstall entry into HKLM
# by itself. The app never writes there; the entry is reported, and not counted as the app's.
function Split-MachineWideTraces($Hits) {
    $windows = @($Hits | Where-Object { $_ -match '\\CurrentVersion\\UFH\\ARP' })
    $others = @($Hits | Where-Object { $_ -notmatch '\\CurrentVersion\\UFH\\ARP' })
    return [pscustomobject]@{ Windows = $windows; Others = $others }
}

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------

$beforeReg = Get-HkcuSnapshot 'before'
$beforeFiles = Get-FileSnapshot
$beforeOutside = Get-OutsideState
Write-Step "before: $($beforeReg.Count) HKCU keys, $($beforeFiles.Count) listed paths, $($beforeOutside.Tasks.Count) tasks, $($beforeOutside.Services.Count) services"
$machineBefore = Split-MachineWideTraces @(Find-MachineWideTraces)
Assert-That ($machineBefore.Others.Count -eq 0) "before: no trace of the app under HKLM\SOFTWARE or the services key other than Windows' own cache ($($machineBefore.Windows.Count) match(es) of that)"

$dist = Join-Path $work 'dist'
New-Item -ItemType Directory -Path $dist | Out-Null
$downloaded = Join-Path $dist 'RoboRightClick.exe'
Copy-Item -LiteralPath $Exe -Destination $downloaded
$code = Invoke-RoboCommand -ExePath $downloaded -Arguments @('--install', '--quiet')
Assert-That ($code -eq 0) "--install exits 0 (got $code)"
Wait-TrayRunning
Start-Sleep -Seconds 3

$afterInstallReg = Get-HkcuSnapshot 'install'
$afterInstallFiles = Get-FileSnapshot
$installReg = Get-RegistryClassification (Compare-Registry $beforeReg $afterInstallReg)
Write-Step "install: $($installReg.App.Count) app-owned registry differences, $($installReg.Noise.Count) judged noise, $($installReg.Other.Count) other"
$installFiles = Compare-Files $beforeFiles $afterInstallFiles
$installOtherFiles = @($installFiles.Added | Where-Object { $_ -notmatch 'RoboRightClick' -and -not (Test-AllowedPath $_) })
Write-Step "install: $($installFiles.Added.Count) new paths, $($installFiles.Removed.Count) removed; $($installOtherFiles.Count) not under a RoboRightClick folder"
if ($installReg.Other.Count -gt 0) { throw ('Install changed HKCU outside the design (judge each; -AllowKey for Windows noise):' + [Environment]::NewLine + ($installReg.Other -join [Environment]::NewLine)) }
Assert-That $true 'install: every HKCU difference is a key the design owns (or listed noise)'
Assert-That (@($installReg.App | Where-Object { $_ -like '- *' }).Count -eq 0) 'install: no app-owned key was removed or replaced'
# Explorer settings: nothing under Explorer\Advanced, Shell Extensions or the shell verbs of other
# programs changed.
$explorerChanges = @($installReg.App + $installReg.Noise + $installReg.Other | Where-Object { $_ -match 'CurrentVersion\\Explorer\\(Advanced|Shell Extensions)|\\Classes\\\*\\shellex|ContextMenuHandlers' })
Assert-That ($explorerChanges.Count -eq 0) 'install: no Explorer setting, shell extension or context-menu handler key changed'
if ($installOtherFiles.Count -gt 0) { throw ('Install created paths outside the RoboRightClick folders (judge each; -AllowPath for noise):' + [Environment]::NewLine + ($installOtherFiles -join [Environment]::NewLine)) }
Assert-That $true 'install: every new file or folder is inside a RoboRightClick folder (or listed noise)'

$outsideAfterInstall = Get-OutsideState
Assert-That (($outsideAfterInstall.Tasks -join '|') -eq ($beforeOutside.Tasks -join '|')) 'install: no scheduled task was added or removed'
Assert-That (($outsideAfterInstall.Services -join '|') -eq ($beforeOutside.Services -join '|')) 'install: no service was added or removed'
Assert-That (($outsideAfterInstall.HklmRun -join '|') -eq ($beforeOutside.HklmRun -join '|')) 'install: no machine-wide Run value changed'
$machineAfter = Split-MachineWideTraces @(Find-MachineWideTraces)
Assert-That ($machineAfter.Others.Count -eq 0) "install: no trace of the app under HKLM\SOFTWARE or the services key other than Windows' own copy of the Uninstall entry ($($machineAfter.Others -join '; '))"
Write-Step "Windows itself recorded the Uninstall entry under HKLM UFH\ARP: $($machineAfter.Windows.Count) match(es)"
$installed = Test-Path -LiteralPath $script:E2E.InstalledExe
Assert-That $installed 'the exe is in the install folder'

# Use: three jobs in normal mode so logs, history and the notification registration exist.
$src = Join-Path $work 'src'
$dst = Join-Path $work 'dst'
New-Item -ItemType Directory -Path $src, $dst | Out-Null
for ($i = 1; $i -le 3; $i++) { Set-Content -LiteralPath (Join-Path $src "f$i.txt") -Value "file $i" }
for ($i = 1; $i -le 3; $i++) {
    Assert-That ((Invoke-Robo -Verb copy -Paths (Join-Path $src "f$i.txt")) -eq 0) "job ${i}: Robo-Copy accepted"
    Assert-That ((Invoke-Robo -Verb paste -Paths $dst) -eq 0) "job ${i}: Robo-Paste accepted"
    [void](Wait-Settled -Path $dst -MinFiles $i)
}
Start-Sleep -Seconds 4
Close-TrayDialogs
Assert-That (Test-Path -LiteralPath $script:E2E.DataDir) 'normal mode created its data folder'

$afterUseReg = Get-HkcuSnapshot 'use'
$useReg = Get-RegistryClassification (Compare-Registry $afterInstallReg $afterUseReg)
$useRegApp = @($useReg.App)
Write-Step "use: $($useRegApp.Count) app-owned registry differences, $($useReg.Noise.Count) noise, $($useReg.Other.Count) other"
if ($useReg.Other.Count -gt 0) { throw ('Running jobs changed HKCU outside the design (judge each):' + [Environment]::NewLine + ($useReg.Other -join [Environment]::NewLine)) }
Assert-That $true 'use: running jobs wrote no HKCU key outside the design (or listed noise)'

# Uninstall, run from the installed copy as a user would.
$code = Invoke-RoboCommand -ExePath $script:E2E.InstalledExe -Arguments @('--uninstall', '--quiet')
Assert-That ($code -eq 0) "--uninstall exits 0 (got $code)"
$folders = @($script:E2E.InstallDir, $script:E2E.ConfigDir, $script:E2E.DataDir)
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and ($folders | Where-Object { Test-Path -LiteralPath $_ })) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 3

$afterUninstallReg = Get-HkcuSnapshot 'uninstall'
$afterUninstallFiles = Get-FileSnapshot
$remaining = Get-RegistryClassification (Compare-Registry $beforeReg $afterUninstallReg)
# What the design keeps on purpose: the shared parent keys install created when they were absent.
$sharedParents = Get-SharedParentKeys | ForEach-Object { 'HKEY_CURRENT_USER\' + $_ }
$leftApp = @($remaining.App | Where-Object {
        $line = $_
        -not ($sharedParents | Where-Object { $line -eq "+ $_" })
    })
Write-Step "uninstall: $($remaining.App.Count) app-owned differences from 'before' remain ($($remaining.App.Count - $leftApp.Count) are shared parent keys kept by design)"
if ($leftApp.Count -gt 0) { throw ('Registry entries survived uninstall:' + [Environment]::NewLine + ($leftApp -join [Environment]::NewLine)) }
Assert-That $true 'uninstall: every key and value the app owned is gone; only shared parent keys remain'
foreach ($line in $remaining.WindowsKept) { Write-Step "left by Windows, not written by the app: $line" }
if ($remaining.Other.Count -gt 0) { throw ('HKCU differs from "before" outside the design after uninstall (judge each):' + [Environment]::NewLine + ($remaining.Other -join [Environment]::NewLine)) }
Assert-That $true "uninstall: HKCU matches 'before' apart from shared parent keys and listed noise"
Assert-That ($null -eq (Read-HkcuValue $script:RunKey 'RoboRightClick')) 'uninstall: the Run value is gone'

$gone = Compare-Files $beforeFiles $afterUninstallFiles
$leftFiles = @($gone.Added | Where-Object { -not (Test-AllowedPath $_) })
$leftApp = @($leftFiles | Where-Object { $_ -match 'RoboRightClick' })
Assert-That ($leftApp.Count -eq 0) "uninstall: no file or folder named RoboRightClick is left ($($leftApp -join ', '))"
foreach ($folder in $folders) { Assert-That (-not (Test-Path -LiteralPath $folder)) "uninstall: folder is gone: $folder" }
if ($leftFiles.Count -gt 0) { Write-Step ("listed paths new since 'before' that are not the app's (judge; Windows writes in these folders): " + ($leftFiles -join '; ')) }
Assert-That ($gone.Removed.Count -eq 0 -or @($gone.Removed | Where-Object { $_ -match 'RoboRightClick' }).Count -eq 0) 'uninstall: nothing the app did not own was removed under a RoboRightClick name'
Assert-That (@(Get-RoboProcesses).Count -eq 0) 'uninstall: no RoboRightClick process is running'
$outsideAfterUninstall = Get-OutsideState
Assert-That (($outsideAfterUninstall.Tasks -join '|') -eq ($beforeOutside.Tasks -join '|')) 'uninstall: scheduled tasks match "before"'
Assert-That (($outsideAfterUninstall.Services -join '|') -eq ($beforeOutside.Services -join '|')) 'uninstall: services match "before"'
$machineEnd = Split-MachineWideTraces @(Find-MachineWideTraces)
Assert-That ($machineEnd.Others.Count -eq 0) 'uninstall: no trace of the app under HKLM other than Windows'' own cache'
Write-Step "after uninstall Windows' own UFH\ARP cache holds $($machineEnd.Windows.Count) match(es) (written by Windows, not by the app)"

Write-Host 'PASS: Footprint'
