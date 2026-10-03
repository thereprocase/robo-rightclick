<#
Update: the version-aware install over an existing one, with the tray running. Needs the app
absent at the start (run it after Uninstall.Tests.ps1, as Run-All does) and two builds: -OlderExe
and -Exe, whose product versions differ, the older one first. Without -OlderExe it checks nothing
and exits with the skip code.

  A. Install the older build, then run the newer one with --install while the tray runs:
     exit 0, the installed exe is the newer one (hash), DisplayVersion follows, config.json is
     byte for byte unchanged, no .old or .tmp file is left, and the tray runs again.
  B. Same version again (repair): exit 0, nothing else changes.
  C. The older build without --force: exit 1, nothing changes. With DisplayVersion made stale
     first, the refusal also puts the installed exe's version back into it.
  D. The older build with --force: exit 0, the older exe and its version are installed.
  E. A failure after the new exe is in place rolls back: config.json is made read-only and the
     newer build is run with --autostart, so writing config.json fails (LIKELY: MoveFileEx will
     not replace a read-only file; the script says INCONCLUSIVE when the install succeeds
     anyway). Expect exit 1, the older exe and version back, the tray running, no .old left.
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

# ---------------------------------------------------------------------------
Write-Host 'A. update over a running older install'
$code = Invoke-RoboCommand -ExePath $older -Arguments @('--install', '--quiet')
Assert-That ($code -eq 0) "older --install exits 0 (got $code)"
Wait-TrayRunning
Assert-InstalledBuild $olderHash $olderVersion 'A, before'
$configBefore = Get-RoboConfigText
Assert-That ($null -ne $configBefore) 'config.json exists after the first install'
$trayBefore = Get-TrayIds

$code = Invoke-RoboCommand -ExePath $newer -Arguments @('--install', '--quiet')
Assert-That ($code -eq 0) "newer --install exits 0 (got $code)"
Assert-InstalledBuild $newerHash $newerVersion 'A'
Assert-That ((Get-RoboConfigText) -ceq $configBefore) 'A: config.json is unchanged'
Assert-NoLeftovers 'A'
Wait-TrayRunning
$trayAfter = Get-TrayIds
Assert-That (@($trayAfter | Where-Object { $trayBefore -contains $_ }).Count -eq 0) 'A: the old tray exited and a new one runs'

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
