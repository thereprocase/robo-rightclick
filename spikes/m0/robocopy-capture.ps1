<#
M0 spike 2: what robocopy actually prints, and when, with the exact flags the
app uses. Produces fixtures for RobocopyOutputParser and answers:
  - Is redirected stdout UTF-16 with /UNICODE, and what is it without?
  - Under /MT:32, is a file's line printed when the file starts or when it finishes?
  - What do error lines look like for a file locked by another process?
Writes everything under $Root\out. Windows PowerShell 5.1; no modules.
#>
param([string]$Root = 'C:\m0')
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

public static class RoboCapture
{
    static ProcessStartInfo Info(string args)
    {
        var psi = new ProcessStartInfo("robocopy.exe", args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        return psi;
    }

    // Exact stdout bytes, no decoding, so the encoding can be inspected offline.
    public static int Raw(string args, string bytesPath)
    {
        using (var p = Process.Start(Info(args)))
        using (var file = File.Create(bytesPath))
        {
            p.StandardOutput.BaseStream.CopyTo(file);
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    // UTF-16 decoded lines with arrival times, plus destination file sizes
    // sampled every 100 ms, to tell "printed at start" from "printed at end".
    public static int Timed(string args, string linesPath, string pollDir, string pollPath, string lockPath)
    {
        FileStream held = null;
        if (!string.IsNullOrEmpty(lockPath))
        {
            held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        try
        {
            var psi = Info(args);
            psi.StandardOutputEncoding = Encoding.Unicode;
            var sw = Stopwatch.StartNew();
            var lines = new List<string>();
            var gate = new object();
            var p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) { lock (gate) { lines.Add(sw.ElapsedMilliseconds + "\t" + e.Data); } }
            };
            p.Start();
            p.BeginOutputReadLine();
            var poll = new List<string>();
            while (!p.WaitForExit(100))
            {
                if (!string.IsNullOrEmpty(pollDir) && Directory.Exists(pollDir))
                {
                    foreach (var f in Directory.GetFiles(pollDir))
                    {
                        poll.Add(sw.ElapsedMilliseconds + "\t" + new FileInfo(f).Length + "\t" + Path.GetFileName(f));
                    }
                }
            }
            p.WaitForExit();
            lock (gate)
            {
                lines.Add(sw.ElapsedMilliseconds + "\t<exit " + p.ExitCode + ">");
                File.WriteAllLines(linesPath, lines, new UTF8Encoding(false));
            }
            if (!string.IsNullOrEmpty(pollPath)) { File.WriteAllLines(pollPath, poll, new UTF8Encoding(false)); }
            return p.ExitCode;
        }
        finally
        {
            if (held != null) { held.Dispose(); }
        }
    }
}
'@

$src = Join-Path $Root 'cap-src'
$out = Join-Path $Root 'out'
foreach ($d in (Get-ChildItem $Root -Directory -Filter 'cap-dst*' -ErrorAction SilentlyContinue)) {
    Remove-Item -LiteralPath $d.FullName -Recurse -Force
}
if (Test-Path $src) { Remove-Item -LiteralPath $src -Recurse -Force }
New-Item -ItemType Directory -Force $src, $out | Out-Null

$names = @('plain.txt', ([string][char]0x65E5 + [char]0x672C + [char]0x8A9E + '.txt'),
           ('emoji ' + [char]::ConvertFromUtf32(0x1F4C1) + ' file.bin'), 'with space.dat',
           ([string][char]0xFC + 'n' + [char]0xEF + 'c' + [char]0xF8 + 'd' + [char]0xE9 + '.txt'))
foreach ($n in $names) { [IO.File]::WriteAllBytes((Join-Path $src $n), (New-Object byte[] 4096)) }
[IO.File]::WriteAllBytes((Join-Path $src 'locked.bin'), (New-Object byte[] 4096))
New-Item -ItemType Directory (Join-Path $src 'many') | Out-Null
1..300 | ForEach-Object { [IO.File]::WriteAllBytes((Join-Path $src "many\f$_.bin"), (New-Object byte[] 1024)) }
$big = Join-Path $src 'big'
New-Item -ItemType Directory $big | Out-Null
$rng = New-Object Random 42
$buf = New-Object byte[] (4MB)
foreach ($i in 1..3) {
    # Real data, not a sparse SetLength, so copy time reflects actual I/O.
    $fs = [IO.File]::Create((Join-Path $big "big$i.bin"))
    for ($k = 0; $k -lt 128; $k++) { $rng.NextBytes($buf); $fs.Write($buf, 0, $buf.Length) }
    $fs.Close()
}

$flags = '/MT:32 /R:0 /W:0 /COPY:DAT /DCOPY:DAT /UNICODE /NP /NDL /NC /NJH /NJS /BYTES /FP'
$noUnicode = $flags -replace ' /UNICODE', ''
$results = [ordered]@{}

$results.rawUnicode = [RoboCapture]::Raw("`"$src`" `"$Root\cap-dst-raw`" /E $flags", "$out\raw-unicode.bin")
$results.rawNoUnicode = [RoboCapture]::Raw("`"$src`" `"$Root\cap-dst-raw2`" /E $noUnicode", "$out\raw-nounicode.bin")
$results.timed = [RoboCapture]::Timed("`"$src`" `"$Root\cap-dst-timed`" /E $flags", "$out\timed-lines.txt",
    "$Root\cap-dst-timed\big", "$out\timed-poll.txt", $null)
$results.lockedFile = [RoboCapture]::Timed("`"$src`" `"$Root\cap-dst-locked`" locked.bin plain.txt $flags",
    "$out\locked-lines.txt", $null, $null, (Join-Path $src 'locked.bin'))
$results.windowsBuild = [Environment]::OSVersion.Version.ToString()
$results.robocopyVersion = (Get-Item "$env:WINDIR\System32\robocopy.exe").VersionInfo.FileVersion
$results | ConvertTo-Json | Set-Content -Encoding UTF8 "$out\capture-summary.json"
$results | ConvertTo-Json
