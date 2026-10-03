<#
Update: the version-aware install over an existing one, with the tray running. Needs the app
absent at the start (run it after Uninstall.Tests.ps1, as Run-All does) and two builds: -OlderExe
and -Exe, whose product versions differ, the older one first. Without -OlderExe it checks nothing
and exits with the skip code.

  A. Install the older build, then run the newer one with --install while the tray runs:
     exit 0, the installed exe is the newer one (hash), DisplayVersion follows, config.json is
     byte for byte unchanged, no .old or .tmp file is left, and the tray runs again. Before the
     update, config.json is rewritten as a format-1 file from before the per-drive thread
     choice ("version": 1, "threads": 32, with a UTF-8 BOM): after the update a paste's
     robocopy.log must say "# threads /MT:n (auto: ...", not "(fixed in settings)".
  B. Same version again (repair): exit 0, nothing else changes.
  C. The older build without --force: exit 1, nothing changes. With DisplayVersion made stale
     first, the refusal also puts the installed exe's version back into it.
  D. The older build with --force: exit 0, the older exe and its version are installed.
  E. A failure after the new exe is in place rolls back: config.json is made read-only and the
     newer build is run with --autostart, so writing config.json fails (LIKELY: MoveFileEx will
     not replace a read-only file; the script says INCONCLUSIVE when the install succeeds
     anyway). Expect exit 1, the older exe and version back, the tray running, no .old left.
  G. An update whose new tray cannot start still succeeds: files created in the install folder
     get an inherited deny-execute entry for this user (LIKELY to block the start the way a
     software restriction rule does; the script says INCONCLUSIVE when a tray runs anyway).
     Expect exit 0, the newer exe and version installed, no rollback, no tray. The entry is then
     removed and the tray started by hand. The result message's extra sentence
     (InstallText.TrayNotStarted) is a manual check: --quiet shows no message.
  F. Uninstall removes a planted .old copy and the fixed temp names install writes
     (RoboRightClick.exe.tmp, each icon's .tmp), and the install folder goes with them.

Unverified on Windows until docs/testlog.md has an entry for it.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$OlderExe,
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    [int]$FolderTimeoutSec = 60
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root -Exe $Exe

Write-Host 'Update.Tests'
if (-not $OlderExe) { Exit-Skipped 'no -OlderExe given; the update path needs two builds' }
foreach ($path in @($Exe, $OlderExe)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Exe not found: $path" }
}
if (Test-Path -LiteralPath $script:E2E.InstalledExe) { throw 'Already installed. Run Uninstall.Tests.ps1 (or --uninstall) first.' }
if (Get-RoboProcesses) { throw 'A RoboRightClick process is already running.' }

# Both builds run from copies outside the install folder, as downloads would.
$dist = Join-Path $script:E2E.Root 'dist-update'
New-Item -ItemType Directory -Force -Path (Join-Path $dist 'older'), (Join-Path $dist 'newer') | Out-Null
$older = Join-Path $dist 'older\RoboRightClick.exe'
$newer = Join-Path $dist 'newer\RoboRightClick.exe'
Copy-Item -LiteralPath $OlderExe -Destination $older -Force
Copy-Item -LiteralPath $Exe -Destination $newer -Force
$olderVersion = (Get-Item -LiteralPath $older).VersionInfo.ProductVersion
$newerVersion = (Get-Item -LiteralPath $newer).VersionInfo.ProductVersion
$olderHash = Get-FileSha256 $older
$newerHash = Get-FileSha256 $newer
Assert-That ($olderVersion -ne $newerVersion) "the two builds carry different versions ($olderVersion, $newerVersion)"
Assert-That ($olderHash -ne $newerHash) 'the two builds are different files'

$backupFile = $script:E2E.InstalledExe + '.old'
$tempFiles = @($script:E2E.InstalledExe + '.tmp') + @(
    'robo-copy.ico', 'robo-cut.ico', 'robo-paste.ico' | ForEach-Object { Join-Path $script:E2E.InstallDir ($_ + '.tmp') })

function Get-DisplayVersion {
    $value = Read-HkcuValue $script:UninstallKey 'DisplayVersion'
    if ($null -eq $value) { return $null }
    return "$($value.Value)"
}

function Assert-InstalledBuild([string]$Hash, [string]$Version, [string]$What) {
    Assert-That ((Get-FileSha256 $script:E2E.InstalledExe) -eq $Hash) "$What : the installed exe is the expected build"
    Assert-That ((Get-DisplayVersion) -eq $Version) "$What : DisplayVersion is $Version (got $(Get-DisplayVersion))"
}

function Assert-NoLeftovers([string]$What) {
    Assert-That (-not (Test-Path -LiteralPath $backupFile)) "$What : no .old copy is left"
    foreach ($temp in $tempFiles) {
        Assert-That (-not (Test-Path -LiteralPath $temp)) "$What : no temp file is left ($(Split-Path -Leaf $temp))"
    }
}

function Get-TrayIds { @(Get-RoboProcesses | Where-Object { $_.Path -eq $script:E2E.InstalledExe } | ForEach-Object { $_.Id }) }

# config.json as a build from before the per-drive thread choice wrote it: no format 2, and the
# old default "threads": 32. A BOM, as some editors save it.
function Set-FormatOneConfig {
    $config = (Get-RoboConfigText) | ConvertFrom-Json
    Add-Member -InputObject $config -NotePropertyName 'version' -NotePropertyValue 1 -Force
    Add-Member -InputObject $config -NotePropertyName 'threads' -NotePropertyValue 32 -Force
    [IO.File]::WriteAllText($script:E2E.ConfigFile, ($config | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $true))
}

# Runs a one-file copy in normal mode and returns its robocopy.log text.
function Get-PasteRobocopyLog([string]$Label) {
    $src = Join-Path $script:E2E.Root "update-$Label-src"
    $dst = Join-Path $script:E2E.Root "update-$Label-dst"
    Remove-TreeIfPresent $src
    Remove-TreeIfPresent $dst
    New-Item -ItemType Directory -Path $src, $dst | Out-Null
    $file = Join-Path $src "threads-$Label.txt"
    Set-Content -LiteralPath $file -Value 'which /MT did this run get?'
    $jobs = Join-Path $script:E2E.DataDir 'jobs'
    $before = @(if (Test-Path -LiteralPath $jobs) { Get-ChildItem -LiteralPath $jobs -Directory | ForEach-Object Name })
    Assert-That ((Invoke-Robo -Verb copy -Paths $file) -eq 0) "$Label : Robo-Copy accepted"
    Assert-That ((Invoke-Robo -Verb paste -Paths $dst) -eq 0) "$Label : Robo-Paste accepted"
    Wait-PathExists (Join-Path $dst "threads-$Label.txt") 60
    Wait-RobocopyGone -TimeoutSec 60
    Start-Sleep -Seconds 3
    $new = @(Get-ChildItem -LiteralPath $jobs -Directory | Where-Object { $before -notcontains $_.Name } | Sort-Object Name)
    Assert-That ($new.Count -ge 1) "$Label : the paste wrote a job folder (normal mode)"
    return [IO.File]::ReadAllText((Join-Path $new[-1].FullName 'robocopy.log'))
}

# ---------------------------------------------------------------------------
Write-Host 'A. update over a running older install'
$code = Invoke-RoboCommand -ExePath $older -Arguments @('--install', '--quiet')
Assert-That ($code -eq 0) "older --install exits 0 (got $code)"
Wait-TrayRunning
Assert-InstalledBuild $olderHash $olderVersion 'A, before'
Assert-That ($null -ne (Get-RoboConfigText)) 'config.json exists after the first install'
Set-FormatOneConfig
$configBefore = Get-RoboConfigText
$trayBefore = Get-TrayIds

$code = Invoke-RoboCommand -ExePath $newer -Arguments @('--install', '--quiet')
Assert-That ($code -eq 0) "newer --install exits 0 (got $code)"
Assert-InstalledBuild $newerHash $newerVersion 'A'
Assert-That ((Get-RoboConfigText) -ceq $configBefore) 'A: config.json is unchanged'
Assert-NoLeftovers 'A'
Wait-TrayRunning
$trayAfter = Get-TrayIds
Assert-That (@($trayAfter | Where-Object { $trayBefore -contains $_ }).Count -eq 0) 'A: the old tray exited and a new one runs'
$threadsLine = @((Get-PasteRobocopyLog 'A') -split "`r?`n" | Where-Object { $_ -like '# threads*' })
Assert-That ($threadsLine.Count -ge 1 -and $threadsLine[0] -match '^# threads /MT:\d+ \(auto') "A: the format-1 'threads': 32 reads as auto after the update ($($threadsLine -join ' | '))"
Assert-That ((Get-RoboConfigText) -ceq $configBefore) 'A: the paste did not rewrite config.json'

# ---------------------------------------------------------------------------
Write-Host 'B. repair with the same version'
$code = Invoke-RoboCommand -ExePath $newer -Arguments @('--install', '--quiet')
Assert-That ($code -eq 0) "repair exits 0 (got $code)"
Assert-InstalledBuild $newerHash $newerVersion 'B'
Assert-That ((Get-RoboConfigText) -ceq $configBefore) 'B: config.json is unchanged'
Assert-NoLeftovers 'B'
Wait-TrayRunning

# ---------------------------------------------------------------------------
Write-Host 'C. downgrade refused, and a stale DisplayVersion corrected'
$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:UninstallKey, $true)
try { $key.SetValue('DisplayVersion', '0.0.1', [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
$code = Invoke-RoboCommand -ExePath $older -Arguments @('--install', '--quiet')
Assert-That ($code -eq 1) "older --install without --force exits 1 (got $code)"
Assert-InstalledBuild $newerHash $newerVersion 'C'
Assert-That ((Get-RoboConfigText) -ceq $configBefore) 'C: config.json is unchanged'
Assert-NoLeftovers 'C'

# ---------------------------------------------------------------------------
Write-Host 'D. downgrade with --force'
$code = Invoke-RoboCommand -ExePath $older -Arguments @('--install', '--force', '--quiet')
Assert-That ($code -eq 0) "older --install --force exits 0 (got $code)"
Assert-InstalledBuild $olderHash $olderVersion 'D'
Assert-That ((Get-RoboConfigText) -ceq $configBefore) 'D: config.json is unchanged'
Assert-NoLeftovers 'D'
Wait-TrayRunning

# ---------------------------------------------------------------------------
Write-Host 'E. a failed update rolls back'
$config = Get-Item -LiteralPath $script:E2E.ConfigFile
$config.Attributes = $config.Attributes -bor [IO.FileAttributes]::ReadOnly
try {
    $code = Invoke-RoboCommand -ExePath $newer -Arguments @('--install', '--autostart', '--quiet')
}
finally {
    $config = Get-Item -LiteralPath $script:E2E.ConfigFile
    $config.Attributes = $config.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
}
if ($code -eq 0) { throw 'INCONCLUSIVE: the install wrote over a read-only config.json, so no failure was injected' }
Assert-That ($code -eq 1) "the failed update exits 1 (got $code)"
Assert-InstalledBuild $olderHash $olderVersion 'E'
Assert-That ((Get-RoboConfigText) -ceq $configBefore) 'E: config.json is unchanged'
Assert-NoLeftovers 'E'
Wait-TrayRunning

# ---------------------------------------------------------------------------
Write-Host 'G. an update whose new tray cannot start still succeeds'
$sid = '*' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $script:E2E.InstallDir /deny "${sid}:(OI)(IO)(X)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'G: icacls could not add the deny-execute entry.' }
try {
    $code = Invoke-RoboCommand -ExePath $newer -Arguments @('--install', '--quiet')
    $traysG = Get-TrayIds
}
finally {
    icacls $script:E2E.InstallDir /remove:d $sid | Out-Null
    icacls $script:E2E.InstallDir /reset /T | Out-Null
}
if ($traysG.Count -gt 0) { throw 'INCONCLUSIVE: a tray started from the install folder anyway, so the start was not blocked' }
Assert-That ($code -eq 0) "G: the update whose tray could not start exits 0 (got $code)"
Assert-InstalledBuild $newerHash $newerVersion 'G'
Assert-That ((Get-RoboConfigText) -ceq $configBefore) 'G: config.json is unchanged'
Assert-NoLeftovers 'G'
Start-Process -FilePath $script:E2E.InstalledExe | Out-Null
Wait-TrayRunning

# ---------------------------------------------------------------------------
Write-Host 'F. uninstall removes a planted .old copy and the install temp files'
foreach ($planted in @($backupFile) + $tempFiles) {
    [IO.File]::WriteAllBytes($planted, [byte[]](1, 2, 3))
}
$code = Invoke-RoboCommand -ExePath $script:E2E.InstalledExe -Arguments @('--uninstall', '--quiet')
Assert-That ($code -eq 0) "--uninstall exits 0 (got $code)"
$deadline = (Get-Date).AddSeconds($FolderTimeoutSec)
while ((Get-Date) -lt $deadline -and (Test-Path -LiteralPath $script:E2E.InstallDir)) { Start-Sleep -Milliseconds 500 }
Assert-That (-not (Test-Path -LiteralPath $script:E2E.InstallDir)) 'F: the install folder is gone, planted files and all'
Assert-RegistryRemoved
Assert-That (@(Get-RoboProcesses).Count -eq 0) 'F: no RoboRightClick process is running'

Remove-TreeIfPresent $dist
Write-Host 'PASS: Update'
