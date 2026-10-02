<#
M0 spike 4: what Explorer's copy engine actually preserves, measured against
robocopy with the app's flags on the same tree.

Stages (run separately so the Explorer stage can run on the interactive desktop):
  build     create the test tree under $Root\par-src
  explorer  copy it with Shell.Application.CopyHere (Explorer's own engine) -> par-explorer
  robo      copy it with robocopy and the app's default flags                 -> par-robo
  dump      write a metadata listing of all three trees to $Root\out\parity-*.json

Windows PowerShell 5.1; no modules. Symlink creation needs an elevated token or
Developer Mode; the build stage records whether it succeeded.
#>
param(
    [Parameter(Mandatory)][ValidateSet('build', 'explorer', 'robo', 'dump')][string]$Stage,
    [string]$Root = 'C:\m0',
    # Robocopy flags for the robo stage; the default is the first candidate set.
    [string]$Flags = '/MT:32 /R:0 /W:0 /COPY:DAT /DCOPY:DAT /NP /NDL /NC /NJH /NJS /BYTES /FP',
    [string]$RoboName = 'par-robo'
)
$ErrorActionPreference = 'Stop'
$src = Join-Path $Root 'par-src'
$out = Join-Path $Root 'out'
New-Item -ItemType Directory -Force $out | Out-Null

$old = [DateTime]::new(2020, 1, 2, 3, 4, 5, [DateTimeKind]::Utc)
$older = [DateTime]::new(2019, 6, 7, 8, 9, 10, [DateTimeKind]::Utc)

function Set-Times([string]$path, [bool]$dir) {
    if ($dir) {
        [IO.Directory]::SetCreationTimeUtc($path, $older)
        [IO.Directory]::SetLastWriteTimeUtc($path, $old)
    } else {
        [IO.File]::SetCreationTimeUtc($path, $older)
        [IO.File]::SetLastWriteTimeUtc($path, $old)
    }
}

switch ($Stage) {
    'build' {
        if (Test-Path $src) { cmd /c rmdir /s /q "$src" }
        $notes = [ordered]@{}
        New-Item -ItemType Directory $src | Out-Null
        foreach ($d in 'plain', 'empty', 'target', 'deep') { New-Item -ItemType Directory (Join-Path $src $d) | Out-Null }

        Set-Content -LiteralPath "$src\plain\normal.txt" -Value 'normal'
        Set-Content -LiteralPath "$src\plain\readonly.txt" -Value 'readonly'
        Set-Content -LiteralPath "$src\plain\hidden.txt" -Value 'hidden'
        Set-Content -LiteralPath "$src\plain\system.txt" -Value 'system'
        Set-Content -LiteralPath "$src\plain\archive-off.txt" -Value 'no archive bit'
        Set-Content -LiteralPath "$src\plain\with-ads.txt" -Value 'has streams'
        Set-Content -LiteralPath "$src\plain\with-ads.txt" -Stream 'Zone.Identifier' -Value "[ZoneTransfer]`r`nZoneId=3"
        Set-Content -LiteralPath "$src\plain\with-ads.txt" -Stream 'custom' -Value 'extra stream'
        Set-Content -LiteralPath "$src\target\inside-target.txt" -Value 'reached through links'
        foreach ($f in Get-ChildItem -LiteralPath "$src\plain", "$src\target" -File) { Set-Times $f.FullName $false }

        # Attributes after timestamps: read-only blocks SetLastWriteTime.
        (Get-Item -LiteralPath "$src\plain\readonly.txt").Attributes = 'ReadOnly, Archive'
        (Get-Item -LiteralPath "$src\plain\hidden.txt" -Force).Attributes = 'Hidden, Archive'
        (Get-Item -LiteralPath "$src\plain\system.txt" -Force).Attributes = 'System, Archive'
        (Get-Item -LiteralPath "$src\plain\archive-off.txt").Attributes = 'Normal'

        # A path well past MAX_PATH, created through the \\?\ prefix.
        $deep = "$src\deep"
        $segment = 'd' * 60
        for ($i = 0; $i -lt 5; $i++) { $deep = "$deep\$segment"; [void][IO.Directory]::CreateDirectory("\\?\$deep") }
        [IO.File]::WriteAllText("\\?\$deep\long-path-file.txt", 'deep')
        $notes.longPathLength = "$deep\long-path-file.txt".Length

        cmd /c mklink /J "$src\junction-to-target" "$src\target" | Out-Null
        $notes.junction = Test-Path "$src\junction-to-target"
        cmd /c mklink /D "$src\dirlink-to-target" "$src\target" 2>&1 | Out-Null
        $notes.dirSymlink = Test-Path "$src\dirlink-to-target"
        cmd /c mklink "$src\plain\filelink.txt" "$src\plain\normal.txt" 2>&1 | Out-Null
        $notes.fileSymlink = Test-Path "$src\plain\filelink.txt"

        foreach ($d in 'plain', 'empty', 'target') { Set-Times (Join-Path $src $d) $true }
        Set-Times $src $true
        $notes | ConvertTo-Json | Set-Content -Encoding UTF8 "$out\parity-build.json"
        $notes | ConvertTo-Json
    }
    'explorer' {
        $dst = Join-Path $Root 'par-explorer'
        if (Test-Path $dst) { cmd /c rmdir /s /q "$dst" }
        New-Item -ItemType Directory $dst | Out-Null
        $shell = New-Object -ComObject Shell.Application
        # 16 = yes to all, 1024 = no error UI. Neither flag changes what gets copied,
        # they only stop dialogs from blocking an unattended run.
        $shell.Namespace($dst).CopyHere($src, 16 + 1024)
        # CopyHere returns before the copy finishes; wait until the tree stops changing.
        $last = -1; $stable = 0
        for ($i = 0; $i -lt 600 -and $stable -lt 5; $i++) {
            Start-Sleep -Milliseconds 500
            $n = (cmd /c dir /s /b /a "$dst" 2>$null | Measure-Object).Count
            if ($n -eq $last) { $stable++ } else { $stable = 0; $last = $n }
        }
        "explorer copy settled with $last entries" | Set-Content -Encoding UTF8 "$out\parity-explorer.txt"
    }
    'robo' {
        $dst = Join-Path $Root $RoboName
        if (Test-Path $dst) { cmd /c rmdir /s /q "$dst" }
        $cmd = "robocopy `"$src`" `"$dst\par-src`" /E $Flags"
        cmd /c $cmd | Out-Null
        "robocopy exit $LASTEXITCODE with $Flags" | Set-Content -Encoding UTF8 "$out\parity-$RoboName.txt"
    }
    'dump' {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
public static class Walk
{
    // Lists every entry without following reparse points, so junction loops
    // cannot recurse and links are reported as links.
    public static List<string> Entries(string root)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(@"\\?\" + root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                result.Add(entry);
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.Directory) != 0 && (attrs & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(entry);
                }
            }
        }
        return result;
    }
}
'@
        $trees = @('par-src') + @(Get-ChildItem $Root -Directory -Filter 'par-*' | Where-Object Name -ne 'par-src' |
            ForEach-Object { "$($_.Name)\par-src" })
        foreach ($name in $trees) {
            $treeRoot = Join-Path $Root $name
            if (-not (Test-Path -LiteralPath $treeRoot)) { continue }
            $rows = foreach ($entry in [Walk]::Entries($treeRoot)) {
                $item = Get-Item -LiteralPath $entry -Force
                $isDir = $item.PSIsContainer
                $streams = @()
                if (-not $isDir) {
                    $streams = @(Get-Item -LiteralPath $entry -Stream * -ErrorAction SilentlyContinue |
                        Where-Object Stream -ne ':$DATA' | ForEach-Object { "$($_.Stream):$($_.Length)" })
                }
                [ordered]@{
                    path = $entry.Substring(("\\?\" + $treeRoot).Length + 1)
                    dir = $isDir
                    attributes = $item.Attributes.ToString()
                    length = if ($isDir) { $null } else { $item.Length }
                    created = $item.CreationTimeUtc.ToString('o')
                    modified = $item.LastWriteTimeUtc.ToString('o')
                    linkType = $item.LinkType
                    target = if ($item.Target) { @($item.Target) -join ';' } else { $null }
                    streams = $streams
                }
            }
            $file = ($name -replace '\\par-src', '') -replace 'par-', 'parity-tree-'
            ConvertTo-Json -InputObject @($rows) -Depth 4 | Set-Content -Encoding UTF8 "$out\$file.json"
        }
        Get-ChildItem $out -Filter 'parity-tree-*.json' | Select-Object Name, Length
    }
}
