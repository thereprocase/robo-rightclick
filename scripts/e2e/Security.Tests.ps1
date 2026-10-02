<#
Security: a low-integrity process must not be able to drive the tray.

A copy of the exe is placed in a temp folder and run once as a control (normal integrity,
expect exit 0), then marked low integrity with icacls and run again (expect non-zero:
access denied by the AppID permissions, or COM activation failure). The control shows that
the second failure comes from the integrity level and not from the copy being unable to start.
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

$lowExit = $null
try {
    $p = Start-Process -FilePath $lowCopy -ArgumentList ('copy ' + (Quote-Arg $sample)) -Wait -PassThru
    $lowExit = $p.ExitCode
}
catch {
    # The process could not start at all. The control above rules out a broken exe, so this
    # also counts as refused, and it is reported as such rather than hidden.
    Write-Step "low-integrity process failed to start: $($_.Exception.Message)"
    $lowExit = -1
}
Assert-That ($lowExit -ne 0) "a low-integrity process cannot run a verb (exit $lowExit)"

Write-Host 'PASS: Security'
