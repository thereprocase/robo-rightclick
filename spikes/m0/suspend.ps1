<#
M0 spike 3: does NtSuspendProcess actually stop a robocopy /MT:32 run, and
does NtResumeProcess bring it back to a clean finish?
Measures robocopy's I/O counters while suspended (should not move) and checks the
final copy byte-for-byte by size. Windows PowerShell 5.1; no modules.
#>
param([string]$Root = 'C:\m0', [int]$Files = 6, [int]$MegabytesEach = 768)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Nt
{
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);

    // Bytes the process has read plus written. Destination file length is no
    // use here: robocopy may size the file up front before writing any data.
    public static ulong Transferred(IntPtr handle)
    {
        IoCounters c;
        if (!GetProcessIoCounters(handle, out c)) { return 0; }
        return c.ReadTransferCount + c.WriteTransferCount;
    }
}
'@

$src = Join-Path $Root 'susp-src'
$dst = Join-Path $Root 'susp-dst'
$out = Join-Path $Root 'out'
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
if (-not (Test-Path $src)) {
    New-Item -ItemType Directory $src | Out-Null
    $rng = New-Object Random 7
    $buf = New-Object byte[] (4MB)
    foreach ($i in 1..$Files) {
        $fs = [IO.File]::Create((Join-Path $src "f$i.bin"))
        for ($k = 0; $k -lt ($MegabytesEach / 4); $k++) { $rng.NextBytes($buf); $fs.Write($buf, 0, $buf.Length) }
        $fs.Close()
    }
}

function DestBytes { if (Test-Path $dst) { (Get-ChildItem $dst -File | Measure-Object Length -Sum).Sum } else { 0 } }

$flags = '/MT:32 /R:0 /W:0 /COPY:DAT /DCOPY:DAT /UNICODE /NP /NDL /NC /NJH /NJS /BYTES /FP'
$psi = New-Object Diagnostics.ProcessStartInfo 'robocopy.exe', "`"$src`" `"$dst`" $flags"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.CreateNoWindow = $true
$p = [Diagnostics.Process]::Start($psi)
$drain = $p.StandardOutput.ReadToEndAsync()

Start-Sleep -Milliseconds 1500
$r = [ordered]@{}
$r.suspendStatus = [Nt]::NtSuspendProcess($p.Handle)
Start-Sleep -Milliseconds 500   # let writes already queued in the kernel land
$r.ioAtSuspend = [Nt]::Transferred($p.Handle)
$r.destLengthAtSuspend = DestBytes
Start-Sleep -Seconds 5
$r.ioAfter5sSuspended = [Nt]::Transferred($p.Handle)
$r.destLengthAfter5sSuspended = DestBytes
$r.resumeStatus = [Nt]::NtResumeProcess($p.Handle)
$p.WaitForExit()
[void]$drain.Wait(5000)
$r.exitCode = $p.ExitCode
$r.totalSourceBytes = (Get-ChildItem $src -File | Measure-Object Length -Sum).Sum
$r.totalDestBytes = DestBytes
$r.sizesMatch = ($r.totalSourceBytes -eq $r.totalDestBytes)
$r.ioWhileSuspended = $r.ioAfter5sSuspended - $r.ioAtSuspend
$r.stoppedWhileSuspended = ($r.ioWhileSuspended -eq 0)
# Read+write of everything is ~2x the source; less means the suspend landed mid-copy
# and the "stopped" result is meaningful.
$r.suspendedMidCopy = ($r.ioAtSuspend -lt (2 * $r.totalSourceBytes))
$r | ConvertTo-Json | Set-Content -Encoding UTF8 "$out\suspend-summary.json"
$r | ConvertTo-Json
