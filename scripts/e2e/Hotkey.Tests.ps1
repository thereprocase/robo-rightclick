<#
Robo-Paste hotkey: the scripted part of the release gate in docs/decisions/0001-paste-hotkey.md.

  A  the hotkey in a folder's file list pastes into that folder (gate 2)
  B  holding the keys for 2 s starts one paste (gate 4)
  C  a double tap starts one paste (gate 4)
  D  with the address bar or the search box focused the key passes and nothing is pasted (gate 3)
  E  with two tabs the paste goes to the active tab only (gate 2)
  F  the Documents library and a zip folder are refused: nothing is written (gate 5)
  G  "pasteHotkey": "" turns it off
  H  with the tray stopped, Ctrl+Shift+V in a file list does nothing (gates 1 and 13); if File
     Explorer itself pastes, the default must be reconsidered

"One paste" is checked through the conflict question: the test file is pasted into an empty
folder, so a second paste would find it there and ask Replace or Skip (conflictDefault "ask").
Toasts and the remaining gates (desktop, busy Explorer, elevated Explorer, layouts, menu
letters) are checked by hand.

Needs an interactive desktop session and an installed app. The script closes every File
Explorer window, more than once, and stops and restarts the tray.
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root
Assert-Installed

Write-Host 'Hotkey.Tests'
$work = Join-Path $script:E2E.Root 'hotkey'
Remove-TreeIfPresent $work
New-Item -ItemType Directory -Path $work | Out-Null

$marker = 'rrchotkey' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$source = Join-Path $work 'src'
New-Item -ItemType Directory -Path $source | Out-Null
$file = Join-Path $source "$marker.txt"
Set-Content -LiteralPath $file -Value 'pasted with the hotkey'
$name = [IO.Path]::GetFileName($file)
# How long a refused or passed press is given to do something it should not.
$quietSeconds = 4

function New-Destination([string]$Leaf) {
    $path = Join-Path $work $Leaf
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}

function Assert-Pasted([string]$Folder, [string]$Message) {
    Wait-PathExists (Join-Path $Folder $name) -TimeoutSec 30
    [void](Wait-Settled -Path $Folder -MinFiles 1)
    Assert-That ((Get-FileSha256 (Join-Path $Folder $name)) -eq (Get-FileSha256 $file)) $Message
}

function Assert-NotPasted([string]$Folder, [string]$Message) {
    Start-Sleep -Seconds $quietSeconds
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $Folder $name))) $Message
}

$originalConfig = Get-RoboConfigText
try {
    Set-RoboConfig @{ pasteHotkey = 'Ctrl+Shift+V'; conflictDefault = 'ask'; showProgressWindow = $false }
    Assert-That ((Invoke-Robo -Verb copy -Paths $file) -eq 0) 'Robo-Copy put the test file on the clipboard'
    Close-ExplorerWindows

    # ---- A: the file list of a folder window ---------------------------------------
    $destA = New-Destination 'a'
    [void](Open-ExplorerLocation $destA)
    Send-PasteHotkey
    Assert-Pasted $destA 'A: the hotkey pasted into the open folder'
    Close-ExplorerWindows

    # ---- B: held for 2 s ----------------------------------------------------------
    $destB = New-Destination 'b'
    [void](Open-ExplorerLocation $destB)
    Send-PasteHotkeyHeld -Milliseconds 2000
    Assert-Pasted $destB 'B: holding the keys pasted'
    Start-Sleep -Seconds $quietSeconds
    Assert-That (-not (Test-ConflictQuestionShown)) 'B: holding the keys for 2 s started one paste, not several'
    Close-ExplorerWindows

    # ---- C: double tap ------------------------------------------------------------
    $destC = New-Destination 'c'
    [void](Open-ExplorerLocation $destC)
    Send-PasteHotkey
    Start-Sleep -Milliseconds 150
    Send-PasteHotkey
    Assert-Pasted $destC 'C: a double tap pasted'
    Start-Sleep -Seconds $quietSeconds
    Assert-That (-not (Test-ConflictQuestionShown)) 'C: a double tap started one paste'
    Close-ExplorerWindows

    # ---- D: text fields keep the key ----------------------------------------------
    $destD = New-Destination 'd'
    $hwnd = Open-ExplorerLocation $destD
    [System.Windows.Forms.SendKeys]::SendWait('^l')
    Start-Sleep -Milliseconds 700
    Send-PasteHotkey
    Assert-NotPasted $destD 'D: with the address bar in edit mode the key passed and nothing was pasted'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}{ESC}')
    Set-ExplorerFileListFocus $hwnd
    [System.Windows.Forms.SendKeys]::SendWait('^e')
    Start-Sleep -Milliseconds 700
    Send-PasteHotkey
    Assert-NotPasted $destD 'D: with the search box focused the key passed and nothing was pasted'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}{ESC}')
    Close-ExplorerWindows

    # ---- E: two tabs, the active one gets the paste -------------------------------
    $destE1 = New-Destination 'e1'
    $destE2 = New-Destination 'e2'
    $hwnd = Open-ExplorerLocation $destE1
    [System.Windows.Forms.SendKeys]::SendWait('^t')
    Start-Sleep -Milliseconds 1500
    [System.Windows.Forms.SendKeys]::SendWait('^l')
    Start-Sleep -Milliseconds 700
    [System.Windows.Forms.SendKeys]::SendWait((ConvertTo-SendKeysText $destE2) + '{ENTER}')
    [void](Wait-ExplorerShows $destE2)
    Set-ExplorerFileListFocus $hwnd
    Send-PasteHotkey
    Assert-Pasted $destE2 'E: the paste went to the active tab'
    Assert-NotPasted $destE1 'E: the other tab of the same window got nothing'
    Close-ExplorerWindows

    # ---- F: refused locations write nothing ---------------------------------------
    $documents = [Environment]::GetFolderPath('MyDocuments')
    $inDocuments = Join-Path $documents $name
    Assert-That (-not (Test-Path -LiteralPath $inDocuments)) 'F: precondition, the test file is not in Documents'
    [void](Open-ExplorerLocation 'shell:DocumentsLibrary')
    Send-PasteHotkey
    Start-Sleep -Seconds $quietSeconds
    Assert-That (-not (Test-Path -LiteralPath $inDocuments)) 'F: the Documents library was refused (nothing in Documents)'
    Close-ExplorerWindows

    $zipSource = Join-Path $work 'zip-content'
    New-Item -ItemType Directory -Path $zipSource | Out-Null
    Set-Content -LiteralPath (Join-Path $zipSource 'inside.txt') -Value 'inside the zip'
    $zip = Join-Path $work 'f.zip'
    Compress-Archive -Path (Join-Path $zipSource 'inside.txt') -DestinationPath $zip
    $zipHash = Get-FileSha256 $zip
    [void](Open-ExplorerLocation $zip)
    Send-PasteHotkey
    Start-Sleep -Seconds $quietSeconds
    Assert-That ((Get-FileSha256 $zip) -eq $zipHash) 'F: the zip folder was refused (the zip is unchanged)'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $work $name))) 'F: nothing was pasted next to the zip'
    Close-ExplorerWindows

    # ---- G: turned off ------------------------------------------------------------
    Set-RoboConfig @{ pasteHotkey = '' }
    $destG = New-Destination 'g'
    [void](Open-ExplorerLocation $destG)
    Send-PasteHotkey
    Assert-NotPasted $destG 'G: with "pasteHotkey": "" the key does nothing'
    Close-ExplorerWindows
    Set-RoboConfig @{ pasteHotkey = 'Ctrl+Shift+V' }

    # ---- H: the tray stopped ------------------------------------------------------
    foreach ($p in Get-RoboProcesses) { Stop-Process -Id $p.Id -Force; [void]$p.WaitForExit(10000) }
    $destH = New-Destination 'h'
    [void](Open-ExplorerLocation $destH)
    Send-PasteHotkey
    Assert-NotPasted $destH 'H: with the tray stopped, Ctrl+Shift+V in a file list does nothing (File Explorer itself does not paste)'
    Close-ExplorerWindows
    Start-Process -FilePath $script:E2E.InstalledExe | Out-Null
    Wait-TrayRunning
}
finally {
    Close-ExplorerWindows
    if (@(Get-RoboProcesses).Count -eq 0) { Start-Process -FilePath $script:E2E.InstalledExe | Out-Null }
    Restore-RoboConfig $originalConfig
}

Write-Host 'PASS: Hotkey'
