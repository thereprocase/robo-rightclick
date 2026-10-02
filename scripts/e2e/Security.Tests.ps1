<#
Security: a low-integrity process must not be able to drive the tray.

A copy of the exe is placed in a temp folder and run once as a control (normal integrity,
expect exit 0). A second copy is marked low integrity with icacls and run (expect exit 1,
CliExitCodes.Failed: access denied by the AppID permissions or the integrity check, or a COM
activation failure). Any other non-zero code is reported as inconclusive, not as a pass.
Needs the installed app (the COM classes point at the installed exe) and a desktop session.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root -Exe $Exe
Assert-Installed

Write-Host 'Security.Tests'
$work = Join-Path $script:E2E.Root 'security'
Remove-TreeIfPresent $work
New-Item -ItemType Directory -Path (Join-Path $work 'normal'), (Join-Path $work 'low') | Out-Null

$sample = Join-Path $work 'sample.txt'
Set-Content -LiteralPath $sample -Value 'sample'

$normalCopy = Join-Path $work 'normal\RoboRightClick.exe'
$lowCopy = Join-Path $work 'low\RoboRightClick.exe'
Copy-Item -LiteralPath $Exe -Destination $normalCopy
Copy-Item -LiteralPath $Exe -Destination $lowCopy

$control = Invoke-Robo -Verb copy -Paths $sample -ExePath $normalCopy
Assert-That ($control -eq 0) "control: a normal-integrity copy of the exe can drive the tray (exit $control)"

$icacls = & icacls.exe $lowCopy /setintegritylevel low 2>&1
if ($LASTEXITCODE -ne 0) { throw "icacls failed: $icacls" }
Write-Step 'marked the second copy low integrity'

# Only exit code 1 (CliExitCodes.Failed: the tray refused the call, or activation was denied)
# shows the refusal. Any other non-zero code means the process did not get as far as asking:
# for example the single-file host failing to start at low integrity. That proves nothing
# about the server's security, so it is a failure of the test, reported with the code.
$p = Start-Process -FilePath $lowCopy -ArgumentList ('copy ' + (Quote-Arg $sample)) -PassThru
$null = $p.Handle
if (-not $p.WaitForExit(60000)) {
    $p.Kill()
    throw 'The low-integrity process did not exit within 60 s. Look for a window or prompt it opened.'
}
$lowExit = $p.ExitCode
Write-Step "low-integrity exit code: $lowExit"
if ($lowExit -eq 0) { throw 'A low-integrity process ran a verb (exit 0). The integrity check or the AppID permissions are not working.' }
if ($lowExit -ne 1) {
    throw ("The low-integrity process exited with $lowExit, not 1. It most likely failed before calling the tray " +
        '(start-up, runtime extraction), so this run says nothing about the security check. Investigate and rerun.')
}
Assert-That $true 'a low-integrity process cannot run a verb (exit 1, refused)'

Write-Host 'PASS: Security'
