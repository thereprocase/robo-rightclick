<#
Runs every end-to-end script in order, each in its own process (clean state, and an STA
thread for the clipboard), and prints a summary to stdout. Writes no files.
If Install fails the rest are not run, because they need the app installed (run
Uninstall.Tests.ps1 by hand if Install left anything behind). After that every test runs even
when an earlier one fails, and Uninstall runs after the scripts that need the app, then Update
(which needs two builds, -OlderExe and -Exe, and skips without -OlderExe) and Footprint, each of
which installs and removes the app again, so the machine is left clean.

  .\Run-All.ps1 -Exe C:\path\to\RoboRightClick.exe [-OlderExe C:\path\to\older\RoboRightClick.exe] [-Root D:\rrc-e2e] [-SecondVolume E:\scratch] [-SmallVolume F:\scratch]

Results are recorded in docs/testlog.md by hand.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    [string]$SecondVolume,
    # A build with a lower version than -Exe, for Update.Tests.ps1; without it Update reports SKIP.
    [string]$OlderExe,
    # A folder on a nearly full volume, for CutSafety's full-destination scenario.
    [string]$SmallVolume,
    # Passed to Ephemeral.Tests.ps1: path substrings a person has judged to be unrelated noise.
    # Joined with '|' (never part of a Windows path), because -File passes one string.
    [string[]]$AllowPath = @()
)
$ErrorActionPreference = 'Stop'

$hostExe = (Get-Process -Id $PID).Path
$exeFull = (Resolve-Path -LiteralPath $Exe).ProviderPath
$olderFull = if ($OlderExe) { (Resolve-Path -LiteralPath $OlderExe).ProviderPath } else { $null }

$plan = @(
    @{ Name = 'Install';    Args = @('-Exe', $exeFull, '-Root', $Root) },
    @{ Name = 'Verbs';      Args = @('-Root', $Root) },
    @{ Name = 'CutSafety';  Args = @('-Root', $Root) + $(if ($SecondVolume) { @('-SecondVolume', $SecondVolume) } else { @() }) + $(if ($SmallVolume) { @('-SmallVolume', $SmallVolume) } else { @() }) },
    @{ Name = 'Cancel';     Args = @('-Root', $Root) + $(if ($SecondVolume) { @('-SecondVolume', $SecondVolume) } else { @() }) },
    @{ Name = 'Hotkey';     Args = @('-Root', $Root) },
    @{ Name = 'Ephemeral';  Args = @('-Root', $Root) + $(if ($SecondVolume) { @('-SecondVolume', $SecondVolume) } else { @() }) + $(if ($AllowPath) { @('-AllowPath', ($AllowPath -join '|')) } else { @() }) },
    @{ Name = 'Security';   Args = @('-Exe', $exeFull, '-Root', $Root) + $(if ($SecondVolume) { @('-SecondVolume', $SecondVolume) } else { @() }) },
    @{ Name = 'Uninstall';  Args = @('-Root', $Root) },
    # Needs the app absent and leaves it absent: install older, update, repair, refuse, force, roll back, uninstall.
    @{ Name = 'Update';     Args = @('-Exe', $exeFull, '-Root', $Root) + $(if ($olderFull) { @('-OlderExe', $olderFull) } else { @() }) },
    # Installs and uninstalls once more and diffs HKCU and the folders around it; needs the app absent.
    @{ Name = 'Footprint';  Args = @('-Exe', $exeFull, '-Root', $Root) }
)

$results = @()
$installFailed = $false
foreach ($step in $plan) {
    $name = $step.Name
    if ($installFailed) {
        $results += [pscustomobject]@{ Test = $name; Result = 'NOT RUN'; Seconds = 0 }
        continue
    }
    Write-Output ''
    Write-Output "=== $name ==="
    $started = Get-Date
    $script = Join-Path $PSScriptRoot "$name.Tests.ps1"
    $stepArgs = $step.Args
    & $hostExe -NoProfile -ExecutionPolicy Bypass -STA -File $script @stepArgs
    $code = $LASTEXITCODE
    $result = switch ($code) { 0 { 'PASS' } 3 { 'SKIP' } default { "FAIL ($code)" } }
    if ($name -eq 'Install' -and $result -ne 'PASS') { $installFailed = $true }
    $results += [pscustomobject]@{ Test = $name; Result = $result; Seconds = [int]((Get-Date) - $started).TotalSeconds }
}

Write-Output ''
Write-Output '=== Summary ==='
$results | Format-Table -AutoSize | Out-String -Width 120 | Write-Output
Write-Output 'Record the outcome in docs/testlog.md by hand (Windows build, machine, what ran, what was observed).'

if (@($results | Where-Object { $_.Result -like 'FAIL*' }).Count -gt 0) { exit 1 }
exit 0
