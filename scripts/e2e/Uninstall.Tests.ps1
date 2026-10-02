<#
Uninstall: --uninstall removes every registry key and the Run value, the config, data and
install folders, and the tray process. Run last.
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    [int]$FolderTimeoutSec = 60
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root
Assert-Installed

Write-Host 'Uninstall.Tests'
$code = Invoke-RoboCommand -ExePath $script:E2E.InstalledExe -Arguments @('--uninstall')
Assert-That ($code -eq 0) "--uninstall exits 0 (got $code)"

# The install folder is deleted by a helper process after this one exits, so poll for it.
$deadline = (Get-Date).AddSeconds($FolderTimeoutSec)
$folders = @($script:E2E.InstallDir, $script:E2E.ConfigDir, $script:E2E.DataDir)
while ((Get-Date) -lt $deadline -and ($folders | Where-Object { Test-Path -LiteralPath $_ })) { Start-Sleep -Milliseconds 500 }
foreach ($folder in $folders) { Assert-That (-not (Test-Path -LiteralPath $folder)) "folder is gone: $folder" }

Assert-RegistryRemoved
Assert-That ((Get-RoboProcesses).Count -eq 0) 'no RoboRightClick process is running'
Write-Host 'PASS: Uninstall'
