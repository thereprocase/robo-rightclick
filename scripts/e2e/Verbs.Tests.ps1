<#
Verbs: Robo-Copy plus Robo-Paste against Explorer's own copy engine on the same tree, Ctrl+C
interop, same-folder naming, and the refusal to paste a folder into itself.
Needs an interactive desktop session (clipboard, Shell.Application) and an installed app.
The only differences from Explorer that are accepted are the ones in docs/parity.md.
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    [int]$LargeMB = 64
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root
Assert-Installed

Write-Host 'Verbs.Tests'
$work = Join-Path $script:E2E.Root 'verbs'
Remove-TreeIfPresent $work
New-Item -ItemType Directory -Path $work | Out-Null

# ---- parity with Explorer ---------------------------------------------------
$src = Join-Path $work 'tree'
New-Tree -Path $src -LargeMB $LargeMB
$explorerOut = Join-Path $work 'out-explorer'
$roboOut = Join-Path $work 'out-robo'
New-Item -ItemType Directory -Path $explorerOut, $roboOut | Out-Null

# 4 = no progress dialog, 16 = yes to all, 1024 = no error UI. The same flags as the M0 parity run.
$shell = New-Object -ComObject Shell.Application
$shell.NameSpace($explorerOut).CopyHere($src, (4 -bor 16 -bor 1024))
[void](Wait-Settled -Path (Join-Path $explorerOut 'tree') -MinFiles 5 -TimeoutSec 300)
Write-Step 'Explorer copy finished'

Assert-That ((Invoke-Robo -Verb copy -Paths $src) -eq 0) 'Robo-Copy accepted the tree'
Assert-That ((Invoke-Robo -Verb paste -Paths $roboOut) -eq 0) 'Robo-Paste accepted the destination'
[void](Wait-Settled -Path (Join-Path $roboOut 'tree') -MinFiles 5 -TimeoutSec 300)
Write-Step 'Robo copy finished'

# Against the source, nothing may differ: a copy must keep names, data, attributes and streams.
# The one exception is the source's junction. Get-TreeEntries leaves reparse points out, but the
# job recreates an empty folder with the link's name, as Explorer does (JobScan.LinkFolders), so
# that folder is expected in the copy. It must be a plain, empty folder.
$roboTree = Join-Path $roboOut 'tree'
# Compare-Tree returns one array object; assign it before piping so the filter sees each entry.
$vsSourceAll = Compare-Tree -Reference $src -Difference $roboTree -IgnoreArchive
$vsSource = @($vsSourceAll | Where-Object {
        -not ($_.Kind -eq 'ExtraInDifference' -and $_.Rel -eq 'junction-to-target')
    })
Assert-That ($vsSource.Count -eq 0) ('Robo copy equals the source' + [Environment]::NewLine + (Format-Diffs $vsSource))
$standIn = Join-Path $roboTree 'junction-to-target'
if (Test-Path -LiteralPath $standIn) {
    $standInAttr = [IO.File]::GetAttributes($standIn)
    Assert-That (-not ($standInAttr -band [IO.FileAttributes]::ReparsePoint)) 'the junction was not copied as a link'
    Assert-That (@(Get-ChildItem -LiteralPath $standIn -Force).Count -eq 0) "the junction's stand-in folder is empty (its target was not followed)"
}

# Against Explorer, only the listed deviations are accepted.
$explorerTree = Join-Path $explorerOut 'tree'
$vsExplorer = Compare-Tree -Reference $explorerTree -Difference $roboTree
$unexpected = @($vsExplorer | Where-Object {
        $allowed = $false
        # Folder links: Explorer leaves an empty folder; docs/parity.md lists robo leaving none.
        if ($_.Kind -eq 'MissingInDifference' -and $_.Rel -eq 'junction-to-target') { $allowed = $true }
        # Long paths: Explorer skipped them in the measured run (beyond about 200 characters of
        # its own path); robo copies them. The length that matters is the path Explorer would
        # have written, which is longer than robo's ("out-explorer" versus "out-robo").
        if ($_.Kind -eq 'ExtraInDifference' -and (Join-Path $explorerTree $_.Rel).Length -gt 200) { $allowed = $true }
        -not $allowed
    })
Assert-That ($unexpected.Count -eq 0) ('Robo equals Explorer apart from the deviations in docs/parity.md' + [Environment]::NewLine + (Format-Diffs $unexpected))

# ---- Ctrl+C interop ---------------------------------------------------------
$interop = Join-Path $work 'interop'
New-Item -ItemType Directory -Path $interop, (Join-Path $interop 'dest') | Out-Null
$plain = Join-Path $interop 'copied-with-ctrl-c.txt'
Set-Content -LiteralPath $plain -Value 'placed on the clipboard by the shell, not by Robo-Copy'
# Set-Clipboard -Path exists only in Windows PowerShell 5.1; PowerShell 7 dropped it. Both
# write a plain CF_HDROP file list, as a Ctrl+C in Explorer does.
if ((Get-Command Set-Clipboard).Parameters.ContainsKey('Path')) { Set-Clipboard -Path $plain }
else { Set-ClipboardFiles @($plain) }
Assert-That (((Get-ClipboardFileNames) -join '|') -eq 'copied-with-ctrl-c.txt') 'the clipboard holds the Ctrl+C file'
Assert-That ((Invoke-Robo -Verb paste -Paths (Join-Path $interop 'dest')) -eq 0) 'Robo-Paste accepted a clipboard written by Set-Clipboard'
[void](Wait-Settled -Path (Join-Path $interop 'dest'))
Assert-That ((Get-FileSha256 (Join-Path $interop 'dest\copied-with-ctrl-c.txt')) -eq (Get-FileSha256 $plain)) 'the Ctrl+C file arrived intact'

# ---- same-folder copy naming ------------------------------------------------
$same = Join-Path $work 'same'
New-Item -ItemType Directory -Path $same, (Join-Path $same 'folder') | Out-Null
Set-Content -LiteralPath (Join-Path $same 'doc.txt') -Value 'same-folder copy'
Set-Content -LiteralPath (Join-Path $same 'folder\inner.txt') -Value 'inner'
Assert-That ((Invoke-Robo -Verb copy -Paths (Join-Path $same 'doc.txt')) -eq 0) 'copied doc.txt'
Assert-That ((Invoke-Robo -Verb paste -Paths $same) -eq 0) 'pasted into the same folder'
[void](Wait-Settled -Path $same -MinFiles 3)
Assert-That (Test-Path -LiteralPath (Join-Path $same 'doc - Copy.txt')) 'a file pasted into its own folder is named "X - Copy"'
Assert-That ((Get-FileSha256 (Join-Path $same 'doc - Copy.txt')) -eq (Get-FileSha256 (Join-Path $same 'doc.txt'))) 'the renamed copy has the same content'

Assert-That ((Invoke-Robo -Verb copy -Paths (Join-Path $same 'folder')) -eq 0) 'copied the folder'
Assert-That ((Invoke-Robo -Verb paste -Paths $same) -eq 0) 'pasted the folder into its parent'
[void](Wait-Settled -Path (Join-Path $same 'folder - Copy'))
Assert-That (Test-Path -LiteralPath (Join-Path $same 'folder - Copy\inner.txt')) 'a folder pasted beside itself is named "X - Copy"'

# ---- paste into own subfolder is refused ------------------------------------
$parent = Join-Path $work 'parent'
$child = Join-Path $parent 'child'
New-Item -ItemType Directory -Path $child | Out-Null
Set-Content -LiteralPath (Join-Path $parent 'file.txt') -Value 'parent file'
$before = (Get-TreeEntries $parent).Keys | Sort-Object
Assert-That ((Invoke-Robo -Verb copy -Paths $parent) -eq 0) 'copied the parent folder'
[void](Invoke-Robo -Verb paste -Paths $child)
Start-Sleep -Seconds 5
Close-TrayDialogs
Wait-RobocopyGone -TimeoutSec 30
$after = (Get-TreeEntries $parent).Keys | Sort-Object
Assert-That ((($before -join '|') -eq ($after -join '|'))) 'pasting a folder into its own subfolder created nothing'

Write-Host 'PASS: Verbs'
