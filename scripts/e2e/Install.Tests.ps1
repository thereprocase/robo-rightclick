<#
Install: --install writes every registry value in the footprint, including the Uninstall
entry and the AppID security descriptors, copies the exe, and starts the tray.
Run first. Refuses to run over an existing install.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root -Exe $Exe

Write-Host 'Install.Tests'
if (-not (Test-Path -LiteralPath $Exe)) { throw "Exe not found: $Exe" }
if (Test-Path -LiteralPath $script:E2E.InstalledExe) { throw 'Already installed. Run Uninstall.Tests.ps1 (or --uninstall) first; this script will not overwrite an install.' }
if (Get-RoboProcesses) { throw 'A RoboRightClick process is already running.' }

# Run from a copy outside the install folder, as a downloaded exe would be.
$dist = Join-Path $script:E2E.Root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$downloaded = Join-Path $dist 'RoboRightClick.exe'
Copy-Item -LiteralPath $Exe -Destination $downloaded -Force

$code = Invoke-RoboCommand -ExePath $downloaded -Arguments @('--install')
Assert-That ($code -eq 0) "--install exits 0 (got $code)"
Assert-That (Test-Path -LiteralPath $script:E2E.InstalledExe) 'the exe was copied to the install folder'

$version = (Get-Item -LiteralPath $script:E2E.InstalledExe).VersionInfo.ProductVersion
Assert-That (-not [string]::IsNullOrEmpty($version)) "the exe carries a version ($version)"

# Autostart follows the config that install wrote.
Assert-That (Test-Path -LiteralPath $script:E2E.ConfigFile) 'config.json exists'
$config = Get-RoboConfigText | ConvertFrom-Json
$expectRun = [bool]$config.startWithWindows
Assert-RegistryInstalled -ExePath $script:E2E.InstalledExe -Version $version -ExpectRun $expectRun

Wait-TrayRunning
Assert-That $true 'the tray process runs from the install folder'
Write-Host 'PASS: Install'
