<#
Runs every end-to-end script in order, each in its own process (clean state, and an STA
thread for the clipboard), and prints a summary to stdout. Writes no files.
If Install fails the rest are not run, because they need the app installed (run
Uninstall.Tests.ps1 by hand if Install left anything behind). After that every test runs even
when an earlier one fails, and Uninstall runs last so the machine is left clean.

  .\Run-All.ps1 -Exe C:\path\to\RoboRightClick.exe [-Root D:\rrc-e2e] [-SecondVolume E:\scratch] [-SmallVolume F:\scratch]

Results are recorded in docs/testlog.md by hand.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    [string]$SecondVolume,
    # A folder on a nearly full volume, for CutSafety's full-destination scenario.
    [string]$SmallVolume,
    # Passed to Ephemeral.Tests.ps1: path substrings a person has judged to be unrelated noise.
    # Joined with '|' (never part of a Windows path), because -File passes one string.
    [string[]]$AllowPath = @()
)
$ErrorActionPreference = 'Stop'

$hostExe = (Get-Process -Id $PID).Path
$exeFull = (Resolve-Path -LiteralPath $Exe).ProviderPath

$plan = @(
    @{ Name = 'Install';    Args = @('-Exe', $exeFull, '-Root', $Root) },
    @{ Name = 'Verbs';      Args = @('-Root', $Root) },
    @{ Name = 'CutSafety';  Args = @('-Root', $Root) + $(if ($SecondVolume) { @('-SecondVolume', $SecondVolume) } else { @() }) + $(if ($SmallVolume) { @('-SmallVolume', $SmallVolume) } else { @() }) },
    @{ Name = 'Cancel';     Args = @('-Root', $Root) + $(if ($SecondVolume) { @('-SecondVolume', $SecondVolume) } else { @() }) },
    @{ Name = 'Ephemeral';  Args = @('-Root', $Root) + $(if ($AllowPath) { @('-AllowPath', ($AllowPath -join '|')) } else { @() }) },
    @{ Name = 'Security';   Args = @('-Exe', $exeFull, '-Root', $Root) },
    @{ Name = 'Uninstall';  Args = @('-Root', $Root) }
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
