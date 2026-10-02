<#
M0 spike 2b. Redirected stdout cannot carry non-ASCII names: with /UNICODE,
robocopy writes a UTF-16 BOM followed by narrow text with '?' substitutions.
This tests the alternative: /UNILOG pointed at a named pipe the app owns.
That gives true UTF-16 output with nothing written to disk.
Records each line's arrival time and the robocopy process's I/O counters, to
see when file lines are printed under /MT and whether the counters can drive
byte progress. Requires a prior run of robocopy-capture.ps1 (reuses cap-src).
#>
param([string]$Root = 'C:\m0')
$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class PipeCapture
{
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);

    public static int Run(string args, string pipeName, string linesPath, string ioPath, string rawPath, string stdoutPath, string lockPath)
    {
        FileStream held = null;
        if (!string.IsNullOrEmpty(lockPath))
        {
            held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        try
        {
            var sw = Stopwatch.StartNew();
            var lines = new List<string>();
            var raw = new MemoryStream();
            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var psi = new ProcessStartInfo("robocopy.exe", args + " /UNILOG:\\\\.\\pipe\\" + pipeName);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                var stdoutTask = p.StandardOutput.ReadToEndAsync();

                var connect = server.BeginWaitForConnection(null, null);
                if (!connect.AsyncWaitHandle.WaitOne(10000))
                {
                    p.WaitForExit();
                    File.WriteAllText(linesPath, "robocopy never connected to the pipe; exit " + p.ExitCode + "\r\n" + stdoutTask.Result);
                    return -1000;
                }
                server.EndWaitForConnection(connect);

                var io = new List<string>();
                var sampler = new Thread(() =>
                {
                    while (!p.HasExited)
                    {
                        IoCounters c;
                        if (GetProcessIoCounters(p.Handle, out c))
                        {
                            lock (io) { io.Add(sw.ElapsedMilliseconds + "\t" + c.ReadBytes + "\t" + c.WriteBytes); }
                        }
                        Thread.Sleep(100);
                    }
                });
                sampler.Start();

                // Decode as we go, splitting on '\n', so each line gets its own arrival time.
                var decoder = Encoding.Unicode.GetDecoder();
                var buf = new byte[4096];
                var chars = new char[4096];
                var pending = new StringBuilder();
                int n;
                while ((n = server.Read(buf, 0, buf.Length)) > 0)
                {
                    raw.Write(buf, 0, n);
                    var count = decoder.GetChars(buf, 0, n, chars, 0);
                    for (var i = 0; i < count; i++)
                    {
                        if (chars[i] == '\n')
                        {
                            lines.Add(sw.ElapsedMilliseconds + "\t" + pending.ToString().TrimEnd('\r'));
                            pending.Clear();
                        }
                        else
                        {
                            pending.Append(chars[i]);
                        }
                    }
                }
                if (pending.Length > 0) { lines.Add(sw.ElapsedMilliseconds + "\t" + pending); }
                p.WaitForExit();
                sampler.Join();
                lines.Add(sw.ElapsedMilliseconds + "\t<exit " + p.ExitCode + ">");
                File.WriteAllLines(linesPath, lines, new UTF8Encoding(false));
                File.WriteAllLines(ioPath, io, new UTF8Encoding(false));
                File.WriteAllBytes(rawPath, raw.ToArray());
                File.WriteAllText(stdoutPath, stdoutTask.Result);
                return p.ExitCode;
            }
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
if (-not (Test-Path $src)) { throw "run robocopy-capture.ps1 first" }
foreach ($d in (Get-ChildItem $Root -Directory -Filter 'pipe-dst*' -ErrorAction SilentlyContinue)) {
    Remove-Item -LiteralPath $d.FullName -Recurse -Force
}

# Same flags as the app, minus /UNICODE (stdout is no longer the data channel).
$flags = '/MT:32 /R:0 /W:0 /COPY:DAT /DCOPY:DAT /NP /NDL /NC /NJH /NJS /BYTES /FP'
$r = [ordered]@{}
$r.full = [PipeCapture]::Run("`"$src`" `"$Root\pipe-dst`" /E $flags", 'rrc-m0-full',
    "$out\pipe-lines.txt", "$out\pipe-io.txt", "$out\pipe-raw.bin", "$out\pipe-stdout.txt", $null)
$r.locked = [PipeCapture]::Run("`"$src`" `"$Root\pipe-dst-locked`" locked.bin plain.txt $flags", 'rrc-m0-locked',
    "$out\pipe-locked-lines.txt", "$out\pipe-locked-io.txt", "$out\pipe-locked-raw.bin", "$out\pipe-locked-stdout.txt",
    (Join-Path $src 'locked.bin'))
# Does anything named like a log appear on disk? There should be no file at all.
$r.strayLogFiles = @(Get-ChildItem $Root, $env:TEMP -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'rrc-m0*' } | ForEach-Object FullName)
$r | ConvertTo-Json | Set-Content -Encoding UTF8 "$out\pipe-summary.json"
$r | ConvertTo-Json
