<#
Security: what an attacker who can run code as the user, or write to the clipboard, can and
cannot do through the tray. Needs the installed app and a desktop session. Sections:

  1. Integrity. A copy of the exe marked low integrity cannot run a verb (exit 1); a normal copy
     can (exit 0). Any other code from the low copy is reported as a test failure, not a pass.
  2. COM. A low-integrity COM client is refused at CoCreateInstance (E_ACCESSDENIED), and still
     is with both AppID descriptors removed (restored in a finally block), so the refusal does not
     rest on the registry values alone. A medium client gets the object.
  3. Output pipe. With robocopy held at its start (Start-RobocopyCatcher) the pipe exists and
     robocopy has not connected: its DACL is the current user only with NETWORK denied; a second
     server on the name is refused (squatting); a same-user client that connects is disconnected
     (the client PID check) and nothing it sends reaches robocopy.log; the real robocopy's
     output is still processed; a later client is refused while robocopy holds the pipe.
  4. extraArgs. Hostile values in config.json never reach robocopy's command line; an allowed
     one does (control). No injected log file, no purge of the destination, no copy turned into a move.
  5. Hostile clipboard. Device, long-path, traversal, stream, wildcard, quote and aliasing paths,
     malformed and oversized DROPFILES blocks, with a Move effect so an accepted path would
     delete the canary. Nothing is copied or moved, no robocopy starts, the tray stays up and
     logs no crash. A plain valid path is the control: it must copy.
  6. File names that robocopy would read as switches ("-E", "-MOV") in a selection: refused, the
     others copy, nothing is moved or recursed. 6b: "-E" inside a folder that a keep-both answer
     splits into named batches: refused and listed in the summary, the rest of the folder
     arrives, and the job does not end Failed.

Run alone with a real exe: the COM and pipe sections use the installed copy.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Root = (Join-Path $env:TEMP 'rrc-e2e'),
    # Optional: a writable folder on another volume for the cross-volume cut check in section 6.
    [string]$SecondVolume,
    # How many paths the "many but under the limit" clipboard case holds.
    [int]$ManyPaths = 100000,
    # Which sections to run (default all). A rule is checked by breaking the code and running the
    # section that covers it, so each must be runnable alone.
    [ValidateSet('1', '2', '3', '4', '5', '6')][string[]]$Sections = @('1', '2', '3', '4', '5', '6')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-E2E -Root $Root -Exe $Exe
Assert-Installed

Write-Host 'Security.Tests'
$work = Join-Path $script:E2E.Root 'security'
Remove-TreeIfPresent $work
New-Item -ItemType Directory -Path (Join-Path $work 'normal'), (Join-Path $work 'low') | Out-Null
# Lowering a file's integrity label needs WRITE_OWNER on it. Under a -Root where the user has
# only Modify (for example a folder an administrator created at the root of C:), icacls fails
# with "Access is denied", so the user grants itself full control of its own work folder first.
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
function Test-Section([string]$Number) { return $Sections -contains $Number }
$originalConfig = Get-RoboConfigText
$grant = & icacls.exe $work /grant ('*' + $me + ':(OI)(CI)F') 2>&1
if ($LASTEXITCODE -ne 0) { throw "icacls could not grant full control of ${work}: $grant" }

# ===========================================================================
# 1. Integrity
# ===========================================================================
if (Test-Section '1') {
    Write-Host '[1] integrity'
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
}

# ===========================================================================
# 2. COM
# ===========================================================================
if (Test-Section '2') {
    Write-Host '[2] COM'
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        Write-Step 'SKIPPED: the COM client is compiled with Windows PowerShell 5.1 (-OutputType); rerun there'
    }
    else {
        $comDir = Join-Path $work 'com'
        New-Item -ItemType Directory -Path (Join-Path $comDir 'n'), (Join-Path $comDir 'l') | Out-Null
        $grant = & icacls.exe $comDir /grant ('*' + $me + ':(OI)(CI)F') 2>&1
        if ($LASTEXITCODE -ne 0) { throw "icacls could not grant full control of ${comDir}: $grant" }
        # A bare COM client: CoCreateInstance(CLSCTX_LOCAL_SERVER, IUnknown), then QueryInterface for
        # IExecuteCommand. It runs no verb. Compiled here, so no binary is kept in the repository.
        Add-Type -OutputAssembly (Join-Path $comDir 'n\com.exe') -OutputType ConsoleApplication -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class P {
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr r, uint f);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr obj);
    public static int Main(string[] a) {
        CoInitializeEx(IntPtr.Zero, 2);
        Guid clsid = new Guid(a[0]); Guid iid = new Guid("00000000-0000-0000-C000-000000000046");
        IntPtr o;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out o);
        Console.WriteLine("CoCreateInstance=0x" + hr.ToString("X8"));
        if (hr == 0) {
            Guid ec = new Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54"); IntPtr q;
            int hr2 = Marshal.QueryInterface(o, ref ec, out q);
            Console.WriteLine("QueryInterface=0x" + hr2.ToString("X8"));
            if (q != IntPtr.Zero) { Marshal.Release(q); }
            Marshal.Release(o);
        }
        return 0;
    }
}
'@
        Copy-Item (Join-Path $comDir 'n\com.exe') (Join-Path $comDir 'l\com.exe')
        $icacls = & icacls.exe (Join-Path $comDir 'l\com.exe') /setintegritylevel low 2>&1
        if ($LASTEXITCODE -ne 0) { throw "icacls failed: $icacls" }
        $clsid = $script:Verbs[0].Clsid
        $normalOut = (& (Join-Path $comDir 'n\com.exe') $clsid 2>&1) -join ' '
        Assert-That ($normalOut -match 'CoCreateInstance=0x00000000' -and $normalOut -match 'QueryInterface=0x00000000') "control: a medium-integrity COM client gets the object and IExecuteCommand ($normalOut)"
        $lowOut = (& (Join-Path $comDir 'l\com.exe') $clsid 2>&1) -join ' '
        Assert-That ($lowOut -match 'CoCreateInstance=0x80070005') "a low-integrity COM client is refused at CoCreateInstance with E_ACCESSDENIED ($lowOut)"

        # Without the two AppID descriptors the refusal must still hold (machine default launch
        # permission, or the tray's own CoInitializeSecurity access check). The values are put back
        # in the finally block, byte for byte.
        $appIdKey = "HKCU:\Software\Classes\AppID\$($script:AppId)"
        $saved = @{}
        foreach ($name in 'LaunchPermission', 'AccessPermission') { $saved[$name] = (Get-ItemProperty -LiteralPath $appIdKey).$name }
        try {
            foreach ($name in $saved.Keys) { Remove-ItemProperty -LiteralPath $appIdKey -Name $name }
            $lowNoDescriptors = (& (Join-Path $comDir 'l\com.exe') $clsid 2>&1) -join ' '
            $normalNoDescriptors = (& (Join-Path $comDir 'n\com.exe') $clsid 2>&1) -join ' '
        }
        finally {
            foreach ($name in $saved.Keys) { New-ItemProperty -LiteralPath $appIdKey -Name $name -PropertyType Binary -Value $saved[$name] -Force | Out-Null }
        }
        foreach ($name in $saved.Keys) {
            $now = (Get-ItemProperty -LiteralPath $appIdKey).$name
            Assert-That ((($now -join ',') -eq ($saved[$name] -join ','))) "AppID $name is restored byte for byte"
        }
        Assert-That ($lowNoDescriptors -match 'CoCreateInstance=0x80070005') "with both AppID descriptors removed a low-integrity client is still refused ($lowNoDescriptors)"
        Assert-That ($normalNoDescriptors -match 'CoCreateInstance=0x00000000') "with both removed a medium-integrity client is still served ($normalNoDescriptors)"
    }
}

# ===========================================================================
# 3. Output pipe
# ===========================================================================
if (Test-Section '3') {
    Write-Host '[3] robocopy output pipe'

    if (-not ('RrcE2E.PipeAcl' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace RrcE2E {
    public static class PipeAcl {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetKernelObjectSecurity(IntPtr h, uint si, byte[] sd, uint len, out uint need);
        [DllImport("advapi32.dll")] static extern uint GetSecurityDescriptorLength(byte[] sd);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        // The DACL of a named pipe, read through a READ_CONTROL-only handle.
        public static byte[] Dacl(string path) {
            IntPtr h = CreateFile(path, 0x20000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == (IntPtr)(-1)) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            try {
                uint need; byte[] b = new byte[4096];
                if (!GetKernelObjectSecurity(h, 4, b, (uint)b.Length, out need)) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
                Array.Resize(ref b, (int)GetSecurityDescriptorLength(b));
                return b;
            }
            finally { CloseHandle(h); }
        }
    }
}
'@
    }

    $pipeDir = Join-Path $work 'pipe'
    $pipeSrc = Join-Path $pipeDir 'src'
    $pipeDst = Join-Path $pipeDir 'dst'
    New-Item -ItemType Directory -Path $pipeSrc, $pipeDst | Out-Null
    # Slow enough (robocopy /IORATE) that the job outlasts the checks even after the catcher resumes it.
    New-DataFile (Join-Path $pipeSrc 'big.bin') (48MB) 7
    $attackerMarker = 'ATTACKER' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
    try {
        Set-RoboConfig @{ logging = 'normal'; showProgressWindow = $false; extraArgs = @{ copy = '/IORATE:8M'; move = '/IORATE:8M' } }
        Assert-That ((Invoke-Robo -Verb copy -Paths (Join-Path $pipeSrc 'big.bin')) -eq 0) 'Robo-Copy accepted the 48 MB file'
        $catcher = Start-RobocopyCatcher
        try {
            Assert-That ((Invoke-Robo -Verb paste -Paths $pipeDst) -eq 0) 'Robo-Paste accepted the destination'
            if (-not $catcher.WaitCaught(30000)) { throw 'robocopy did not start within 30 s.' }
            $commandLine = Get-ProcessCommandLine $catcher.ProcessId
            if ($commandLine -notmatch 'UNILOG:\\\\\.\\pipe\\(RoboRightClick-[0-9a-f]{32}-\d+-[0-9a-f]{16})(\s|$)') { throw 'robocopy was not given a /UNILOG pipe of the expected form.' }
            $pipeName = $Matches[1]
            Assert-That $true 'the pipe name is the app name, the job id, the step and a 64-bit nonce'
            $pipePath = "\\.\pipe\$pipeName"

            # Squatting: a second server on the live name, with and without an instance limit.
            foreach ($max in 1, -1) {
                $squatter = $null
                try { $squatter = New-Object IO.Pipes.NamedPipeServerStream($pipeName, 'In', $max, 'Byte', 'Asynchronous'); $squatted = $true }
                catch { $squatted = $false }
                finally { if ($squatter) { $squatter.Dispose() } }
                Assert-That (-not $squatted) "a second server instance on the live name is refused (maxInstances $max)"
            }

            # Impersonation: a same-user client is let in by the DACL, then dropped by the PID check.
            $client = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, 'Out')
        $dropped = $false
        $connected = $false
        try {
            try { $client.Connect(3000); $connected = $true } catch { $connected = $false }
            if ($connected) {
                $line = [Text.Encoding]::Unicode.GetBytes("`t`t  3`tC:\attacker\$attackerMarker.txt`r`n")
                for ($i = 0; $i -lt 50 -and -not $dropped; $i++) {
                    try { $client.Write($line, 0, $line.Length); $client.Flush() } catch { $dropped = $true }
                    Start-Sleep -Milliseconds 100
                }
            }
        }
        finally { $client.Dispose() }
        Assert-That $connected 'a same-user client can open the pipe (the DACL lets the user in), so only the PID check stands between it and the output'
        Assert-That $dropped 'a same-user client that is not robocopy is disconnected by the PID check'

            # DACL: deny NETWORK, allow only the current user, nobody else. Read last: opening the pipe
            # to read it counts as a client, and would hide whether the PID check works.
            $descriptor = New-Object Security.AccessControl.RawSecurityDescriptor ([RrcE2E.PipeAcl]::Dacl($pipePath)), 0
            $aces = @($descriptor.DiscretionaryAcl)
            Assert-That ($aces.Count -eq 2) "the pipe DACL has exactly two entries ($($descriptor.GetSddlForm('Access')))"
            $deny = $aces | Where-Object { $_.AceType -eq 'AccessDenied' }
            $allow = $aces | Where-Object { $_.AceType -eq 'AccessAllowed' }
            Assert-That ($null -ne $deny -and $deny.SecurityIdentifier.Value -eq 'S-1-5-2') 'one entry denies NETWORK (S-1-5-2)'
            Assert-That ($null -ne $allow -and $allow.SecurityIdentifier.Value -eq $me) 'the only allow entry is the current user'

            $catcher.Resume() | Out-Null
            Start-Sleep -Seconds 2
            $late = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, 'Out')
            try { $late.Connect(500); $lateConnected = $true } catch { $lateConnected = $false } finally { $late.Dispose() }
            Assert-That (-not $lateConnected) 'a client is refused while robocopy holds the pipe'
            [void](Wait-Settled -Path $pipeDst -MinFiles 1 -TimeoutSec 180)
        }
        finally { $catcher.Dispose() }

        Assert-That ((Get-FileSha256 (Join-Path $pipeSrc 'big.bin')) -eq (Get-FileSha256 (Join-Path $pipeDst 'big.bin'))) 'the copy is intact: the real robocopy output was processed despite the intruder'
        Start-Sleep -Seconds 2
        $logs = @(Get-ChildItem -LiteralPath (Join-Path $script:E2E.DataDir 'jobs') -Recurse -Filter robocopy.log -ErrorAction SilentlyContinue)
        Assert-That ($logs.Count -gt 0) 'normal mode wrote a robocopy.log for the job'
        $leaked = @($logs | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match $attackerMarker })
        Assert-That ($leaked.Count -eq 0) 'nothing the intruder wrote reached any robocopy.log'
    }
    finally { Restore-RoboConfig $originalConfig }
}

# ===========================================================================
# 4. extraArgs
# ===========================================================================
if (Test-Section '4') {
    Write-Host '[4] extraArgs'
    $extraDir = Join-Path $work 'extra'
    $extraSrc = Join-Path $extraDir 'src'
    $extraDst = Join-Path $extraDir 'dst'
    New-Item -ItemType Directory -Path $extraSrc, $extraDst | Out-Null
    $extraFile = Join-Path $extraSrc 'payload.txt'
    Set-Content -LiteralPath $extraFile -Value 'payload'
    Set-Content -LiteralPath (Join-Path $extraSrc 'other.txt') -Value 'other'
    Set-Content -LiteralPath (Join-Path $extraDst 'victim.txt') -Value 'victim: a /PURGE or /MIR would delete this'
    $victimHash = Get-FileSha256 (Join-Path $extraDst 'victim.txt')
    $injectedLog = Join-Path $extraDir 'injected.log'

    # Runs one paste with the given extraArgs.copy value and returns robocopy's command line, read
    # while robocopy is held at its start, then checks the side effects of the finished run.
    function Invoke-ExtraArgsPaste([string]$Value, [string]$Label) {
        Remove-Item -LiteralPath (Join-Path $extraDst 'payload.txt') -Force -ErrorAction SilentlyContinue
        Set-RoboConfig @{ logging = 'normal'; showProgressWindow = $false; conflictDefault = 'ask'; extraArgs = @{ copy = $Value; move = '' } }
        Assert-That ((Invoke-Robo -Verb copy -Paths $extraFile) -eq 0) "[$Label] Robo-Copy accepted"
        $catcher = Start-RobocopyCatcher
        try {
            Assert-That ((Invoke-Robo -Verb paste -Paths $extraDst) -eq 0) "[$Label] Robo-Paste accepted"
            if (-not $catcher.WaitCaught(30000)) { throw "[$Label] robocopy did not start." }
            $line = Get-ProcessCommandLine $catcher.ProcessId
            $catcher.Resume() | Out-Null
            [void](Wait-Settled -Path $extraDst -MinFiles 2 -TimeoutSec 60)
        }
        finally { $catcher.Dispose() }
        Close-TrayDialogs
        Assert-That (Test-Path -LiteralPath (Join-Path $extraDst 'payload.txt')) "[$Label] the file was copied"
        Assert-That (Test-Path -LiteralPath $extraFile) "[$Label] the source is still there (a copy was not turned into a move)"
        Assert-That ((Get-FileSha256 (Join-Path $extraDst 'victim.txt')) -eq $victimHash) "[$Label] the unrelated file in the destination is untouched"
        Assert-That (-not (Test-Path -LiteralPath $injectedLog)) "[$Label] no log file was written to an attacker-chosen path"
        Assert-That (-not (Test-Path -LiteralPath (Join-Path $extraDst 'other.txt'))) "[$Label] only the selected file was copied"
        return $line
    }

    $tailBase = '/XC /XN /XO'
    try {
        $control = Invoke-ExtraArgsPaste '/IORATE:8M /J' 'allowed switches'
        Assert-That ($control.EndsWith("$tailBase /IORATE:8M /J")) 'control: an allowed value is appended after the fixed flags'

        $hostile = @(
            '/MIR', '"/MIR"', '/IORATE:8M /MIR', '/J /PURGE', '/MOVE', '/MOV', '-MIR', '-MOV',
            '/XF *', '/E', "/LOG:$injectedLog", "/UNILOG+:$injectedLog", '/IORATE:8M"', '/IORATE:8M^&calc', '/IORATE:8M&calc',
            "/J`t/MIR", "/J`n/MIR", '/IORATE:99999999999999M', 'C:\Windows', '/JOB:C:\x.rcj', ('/J ' * 400)
        )
        foreach ($value in $hostile) {
            $label = 'hostile ' + $(if ($value.Length -gt 24) { $value.Substring(0, 24) + '...' } else { $value -replace "[`t`n]", ' ' })
            $line = Invoke-ExtraArgsPaste $value $label
            Assert-That ($line.EndsWith($tailBase)) "[$label] nothing was appended to the fixed flags"
        }
    }
    finally { Restore-RoboConfig $originalConfig }
}

# ===========================================================================
# 5. Hostile clipboard
# ===========================================================================
if (Test-Section '5') {
    Write-Host '[5] hostile clipboard'
    $clipDir = Join-Path $work 'clip'
    $canaryDir = Join-Path $clipDir 'canary'
    $clipDst = Join-Path $clipDir 'dst'
    New-Item -ItemType Directory -Path $canaryDir, (Join-Path $canaryDir 'sub'), $clipDst | Out-Null
    $canary = Join-Path $canaryDir 'x.txt'
    Set-Content -LiteralPath $canary -Value 'canary: an accepted hostile path would copy or move this'
    Set-Content -LiteralPath $canary -Stream 'ads' -Value 'stream'
    Set-Content -LiteralPath (Join-Path $canaryDir 'sub\deep.txt') -Value 'deep'
    $canaryHash = Get-FileSha256 $canary
    $device = Get-VolumeDevicePath $canary
    $drive = $canary.Substring(0, 2)
    $rel = $canary.Substring(3)
    $relDir = $canaryDir.Substring(3)

    function Assert-ClipboardRefused([string]$Label, [byte[]]$Block, [uint32]$Effect = 2) {
        $trayBefore = Get-TrayProcessId
        $crashBefore = Get-CrashLogSize
        Set-RawClipboardFiles $Block $Effect
        $catcher = Start-RobocopyCatcher
        try {
            $code = (Start-Process -FilePath $script:E2E.InstalledExe -ArgumentList ('paste ' + (Quote-Arg $clipDst)) -Wait -PassThru).ExitCode
            $started = $catcher.WaitCaught(3000)
        }
        finally { $catcher.Dispose() }
        Start-Sleep -Seconds 1
        Close-TrayDialogs
        Assert-That (-not $started) "[$Label] no robocopy was started"
        Assert-That (@(Get-ChildItem -LiteralPath $clipDst -Force).Count -eq 0) "[$Label] nothing reached the destination"
        Assert-That ((Test-Path -LiteralPath $canary) -and (Get-FileSha256 $canary) -eq $canaryHash) "[$Label] the canary is still there and unchanged (not moved)"
        Assert-That ((Get-TrayProcessId) -eq $trayBefore -and $null -ne $trayBefore) "[$Label] the tray is still the same running process"
        Assert-That ((Get-CrashLogSize) -eq $crashBefore) "[$Label] no crash was logged"
    }

    # Control: the canary by its plain path is copied, so a refusal below is the policy and not a broken harness.
    Set-RawClipboardFiles (New-DropFilesBlock -Paths @($canary)) 1
    Assert-That ((Start-Process -FilePath $script:E2E.InstalledExe -ArgumentList ('paste ' + (Quote-Arg $clipDst)) -Wait -PassThru).ExitCode -eq 0) 'control: Robo-Paste accepted the plain canary path'
    [void](Wait-Settled -Path $clipDst -MinFiles 1 -TimeoutSec 60)
    Close-TrayDialogs
    Assert-That (Test-Path -LiteralPath (Join-Path $clipDst 'x.txt')) 'control: the plain canary path was copied'
    Remove-Item -LiteralPath (Join-Path $clipDst 'x.txt') -Force

    $hostilePaths = [ordered]@{
        'GLOBALROOT device path' = "\\?\GLOBALROOT$device\$rel"
        'extended-length path' = "\\?\$canary"
        'device namespace path' = "\\.\$canary"
        'NT object path' = "\??\$canary"
        'extended UNC path' = "\\?\UNC\localhost\$($drive[0])`$\$rel"
        'dot-dot traversal' = "$canaryDir\sub\..\x.txt"
        'single dot segment' = "$canaryDir\.\x.txt"
        'doubled separator' = "$canaryDir\\x.txt"
        'forward slashes' = $canary.Replace('\', '/')
        'drive-relative' = "$drive$rel"
        'relative path' = "..\$($canaryDir | Split-Path -Leaf)\x.txt"
        'alternate data stream' = "${canary}:ads"
        'default data stream' = "${canary}::`$DATA"
        'trailing dot' = "$canary."
        'trailing space' = "$canary "
        'wildcard star' = "$canaryDir\*"
        'wildcard question' = "$canaryDir\x.tx?"
        'quote injection' = "$canary`" /MIR `"$clipDst"
        'angle and pipe' = "$canaryDir\x<.txt>|"
        'reserved device name' = "$canaryDir\CON"
        'control character' = "$canaryDir\x`a.txt"
        'drive root' = "$drive\"
        'empty segment name' = "$drive\\"
        'server only UNC' = '\\server'
        'dot server UNC' = '\\.\pipe\x'
    }
    foreach ($case in $hostilePaths.Keys) {
        Assert-ClipboardRefused $case (New-DropFilesBlock -Paths @($hostilePaths[$case]))
    }

    # The same hostile path in the legacy narrow (ANSI) form.
    Assert-ClipboardRefused 'ANSI form with traversal' (New-DropFilesBlock -Paths @("$canaryDir\sub\..\x.txt") -Wide $false)

    # A hostile entry next to a plain, valid one: the valid one may run; the hostile one never does.
    # (Checked separately in section 6 for names; here only the destination must stay clean of x.txt
    # and the canary intact. The valid sibling is a file in sub.)
    $mixedDst = Join-Path $clipDir 'mixed'
    New-Item -ItemType Directory -Path $mixedDst | Out-Null
    Set-RawClipboardFiles (New-DropFilesBlock -Paths @((Join-Path $canaryDir 'sub\deep.txt'), "\\?\GLOBALROOT$device\$rel")) 1
    Assert-That ((Start-Process -FilePath $script:E2E.InstalledExe -ArgumentList ('paste ' + (Quote-Arg $mixedDst)) -Wait -PassThru).ExitCode -eq 0) 'mixed list: Robo-Paste accepted'
    [void](Wait-Settled -Path $mixedDst -MinFiles 1 -TimeoutSec 60)
    Start-Sleep -Seconds 1
    Close-TrayDialogs
    $mixed = @(Get-ChildItem -LiteralPath $mixedDst -Force | ForEach-Object Name)
    Assert-That ($mixed.Count -eq 1 -and $mixed[0] -eq 'deep.txt') "mixed list: only the valid item was copied ($($mixed -join ', '))"

    # Malformed and oversized blocks.
    $good = New-DropFilesBlock -Paths @($canary)
    Assert-ClipboardRefused 'block shorter than its header' ([byte[]]$good[0..18])
    Assert-ClipboardRefused 'pFiles beyond the block' (New-DropFilesBlock -Paths @($canary) -PFiles ([uint32]::MaxValue))
    Assert-ClipboardRefused 'pFiles at the block end' (New-DropFilesBlock -Paths @($canary) -PFiles ([uint32]$good.Length))
    Assert-ClipboardRefused 'pFiles inside the header' (New-DropFilesBlock -Paths @($canary) -PFiles ([uint32]4))
    Assert-ClipboardRefused 'name without its terminator' (New-DropFilesBlock -Paths @($canary) -NoTerminator)
    Assert-ClipboardRefused 'header only' ([byte[]]$good[0..19])
    # Cut off inside the last name's terminator: one NUL byte of two, so the name never ends.
    $cut = New-DropFilesBlock -Paths @($canary) -NoTerminator
    $odd = New-Object byte[] ($cut.Length + 1); $cut.CopyTo($odd, 0)
    Assert-ClipboardRefused 'block cut inside the terminator (odd byte count)' $odd

    # Over the item limit (250,000): the whole block is refused, not truncated.
    $manyList = New-Object 'System.Collections.Generic.List[string]'
    for ($i = 0; $i -lt 250001; $i++) { $manyList.Add("C:\rrc-none\f$i") }
    Assert-ClipboardRefused 'one path over the item limit' (New-DropFilesBlock -Paths $manyList.ToArray())
    $manyList = $null

    # Over the byte limit (64 MB): a few very long paths. A path is capped at 32,767 characters, so
    # the block is built from 2,200 of them.
    $longName = 'C:\' + ('a' * 30000)
    $longList = New-Object 'System.Collections.Generic.List[string]'
    for ($i = 0; $i -lt 1200; $i++) { $longList.Add($longName) }
    $longBlock = New-DropFilesBlock -Paths $longList.ToArray()
    Assert-That ($longBlock.Length -gt 64MB) "the oversized block really is over 64 MB ($([int]($longBlock.Length / 1MB)) MB)"
    Assert-ClipboardRefused 'block over the byte limit' $longBlock
    $longList = $null; $longBlock = $null; [GC]::Collect()

    # Many items under the limit: every one is refused as missing; the tray must stay responsive.
    $underList = New-Object 'System.Collections.Generic.List[string]'
    for ($i = 0; $i -lt $ManyPaths; $i++) { $underList.Add("C:\rrc-none\f$i") }
    $memoryBefore = (Get-Process -Id (Get-TrayProcessId)).WorkingSet64
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Assert-ClipboardRefused "$ManyPaths missing paths under the limit" (New-DropFilesBlock -Paths $underList.ToArray())
    $memoryAfter = (Get-Process -Id (Get-TrayProcessId)).WorkingSet64
    Write-Step ("{0} missing paths took {1:N1} s in all; the tray's working set went from {2} MB to {3} MB" -f $ManyPaths, $timer.Elapsed.TotalSeconds, [int]($memoryBefore / 1MB), [int]($memoryAfter / 1MB))
    Assert-That (($memoryAfter - $memoryBefore) -lt 1GB) 'the tray did not take more than 1 GB for that list'
    $underList = $null

    # A hostile destination on the command line is refused the same way.
    foreach ($destination in @("\\?\$clipDst", "\\?\GLOBALROOT$device\$($clipDst.Substring(3))")) {
        Set-RawClipboardFiles (New-DropFilesBlock -Paths @($canary)) 2
        Start-Process -FilePath $script:E2E.InstalledExe -ArgumentList ('paste ' + (Quote-Arg $destination)) -Wait | Out-Null
        Start-Sleep -Seconds 2
        Close-TrayDialogs
        Assert-That ((@(Get-ChildItem -LiteralPath $clipDst -Force).Count -eq 0) -and (Test-Path -LiteralPath $canary)) "a device-path destination is refused and the canary is untouched ($($destination.Substring(0, 12))...)"
    }
}

# ===========================================================================
# 6. File names robocopy would read as switches
# ===========================================================================
if (Test-Section '6') {
    Write-Host '[6] switch-like file names'
    $swSrc = Join-Path $work 'switch\src'
    $swDst = Join-Path $work 'switch\dst'
    New-Item -ItemType Directory -Path (Join-Path $swSrc 'sub'), $swDst | Out-Null
    Set-Content -LiteralPath (Join-Path $swSrc 'ok.txt') -Value 'ok'
    Set-Content -LiteralPath (Join-Path $swSrc '-MOV') -Value 'switch-like: would turn the copy into a move'
    Set-Content -LiteralPath (Join-Path $swSrc '-E') -Value 'switch-like: would add the subfolders'
    Set-Content -LiteralPath (Join-Path $swSrc 'sub\deep.txt') -Value 'deep'
    Set-Content -LiteralPath (Join-Path $swDst 'victim.txt') -Value 'victim'

    $selection = @((Join-Path $swSrc 'ok.txt'), (Join-Path $swSrc '-MOV'), (Join-Path $swSrc '-E'))
    Assert-That ((Invoke-Robo -Verb copy -Paths $selection) -eq 0) 'Robo-Copy accepted the selection'
    $catcher = Start-RobocopyCatcher
    try {
        Assert-That ((Invoke-Robo -Verb paste -Paths $swDst) -eq 0) 'Robo-Paste accepted the destination'
        if (-not $catcher.WaitCaught(30000)) { throw 'robocopy did not start.' }
        $swLine = Get-ProcessCommandLine $catcher.ProcessId
        $catcher.Resume() | Out-Null
        [void](Wait-Settled -Path $swDst -MinFiles 2 -TimeoutSec 60)
    }
    finally { $catcher.Dispose() }
    Start-Sleep -Seconds 2
    Close-TrayDialogs
    Assert-That ($swLine -match '"ok\.txt"' -and $swLine -notmatch '"-MOV"' -and $swLine -notmatch '"-E"') 'robocopy was given ok.txt and neither switch-like name'
    $swNames = @(Get-ChildItem -LiteralPath $swDst -Recurse -Force | ForEach-Object { $_.FullName.Substring($swDst.Length + 1) } | Sort-Object)
    Assert-That (($swNames -join '|') -eq 'ok.txt|victim.txt') "only ok.txt was copied, no subfolder, no switch-like file ($($swNames -join ', '))"
    Assert-That ((Test-Path -LiteralPath (Join-Path $swSrc 'ok.txt')) -and (Test-Path -LiteralPath (Join-Path $swSrc '-MOV'))) 'every source is still in place (the copy did not become a move)'

    if ($SecondVolume) {
        # A cut across volumes goes through robocopy /MOV: the same names must be refused there.
        $swDst2 = Join-Path $SecondVolume 'rrc-switch-dst'
        Remove-TreeIfPresent $swDst2
        New-Item -ItemType Directory -Path $swDst2 | Out-Null
        try {
            Assert-That ((Invoke-Robo -Verb cut -Paths $selection) -eq 0) 'Robo-Cut accepted the selection'
            Assert-That ((Invoke-Robo -Verb paste -Paths $swDst2) -eq 0) 'Robo-Paste accepted the other-volume destination'
            Wait-PathExists (Join-Path $swDst2 'ok.txt') 60
            [void](Wait-Settled -Path $swDst2 -MinFiles 1 -TimeoutSec 60)
            Start-Sleep -Seconds 2
            Close-TrayDialogs
            $names2 = @(Get-ChildItem -LiteralPath $swDst2 -Recurse -Force | ForEach-Object Name | Sort-Object)
            Assert-That (($names2 -join '|') -eq 'ok.txt') "cross-volume cut: only ok.txt moved ($($names2 -join ', '))"
            Assert-That ((Test-Path -LiteralPath (Join-Path $swSrc '-MOV')) -and (Test-Path -LiteralPath (Join-Path $swSrc '-E')) -and (Test-Path -LiteralPath (Join-Path $swSrc 'sub\deep.txt'))) 'cross-volume cut: the switch-like files and the subfolder stayed in the source'
        }
        finally { Remove-TreeIfPresent $swDst2 }
    }
    else {
        Write-Step 'SKIPPED: cross-volume cut of switch-like names (needs -SecondVolume)'
    }

    # 6b. A switch-like name inside a folder the planner splits: a keep-both answer cuts the
    # tree into named batches, so '-E' would have to be named. It is refused, the rest of the
    # folder still arrives, and the job ends with a refusal (DoneWithErrors), not Failed.
    Write-Host '[6b] switch-like name in a split folder'
    $splitSrc = Join-Path $work 'switch-split\src\T'
    $splitParent = Join-Path $work 'switch-split\dst'
    $splitDst = Join-Path $splitParent 'T'
    New-Item -ItemType Directory -Path (Join-Path $splitSrc 'sub'), $splitDst | Out-Null
    Set-Content -LiteralPath (Join-Path $splitSrc '-E') -Value 'switch-like, inside a split folder'
    Set-Content -LiteralPath (Join-Path $splitSrc 'free.txt') -Value 'no conflict'
    Set-Content -LiteralPath (Join-Path $splitSrc 'keep.txt') -Value 'the version being pasted'
    Set-Content -LiteralPath (Join-Path $splitSrc 'sub\deep.txt') -Value 'deep'
    Set-Content -LiteralPath (Join-Path $splitDst 'keep.txt') -Value 'the version already there'
    $keepHash = Get-FileSha256 (Join-Path $splitDst 'keep.txt')
    $configText = Get-RoboConfigText
    Set-RoboConfig @{ conflictDefault = 'ask' }
    try {
        Assert-That ((Invoke-Robo -Verb copy -Paths $splitSrc) -eq 0) '6b: Robo-Copy accepted the folder'
        Assert-That ((Invoke-Robo -Verb paste -Paths $splitParent) -eq 0) '6b: Robo-Paste accepted the destination'
        Invoke-UiaButton (Wait-TrayElement 'Decide')
        Set-UiaToggle (Wait-TrayElement 'SelectAllSource') $true
        Set-UiaToggle (Wait-TrayElement 'SelectAllDestination') $true
        Invoke-UiaButton (Wait-TrayElement 'Continue')
        foreach ($name in 'free.txt', 'sub\deep.txt', 'keep (2).txt') { Wait-PathExists (Join-Path $splitDst $name) 60 }
        Wait-RobocopyGone -TimeoutSec 60
        $refused = Wait-TrayElement 'Refused' 30
        $tray = Get-TrayProcessId
        Assert-That ($null -eq (Find-UiaElement $tray 'FailureReason')) '6b: the job did not end Failed'
        $rows = Get-UiaListItemNames $refused
        Assert-That (@($rows | Where-Object { $_ -like '*\-E*' }).Count -eq 1) "6b: '-E' is listed as refused ($($rows -join ' | '))"
        Assert-That (-not (Test-Path -LiteralPath (Join-Path $splitDst '-E'))) "6b: '-E' was not copied"
        Assert-That ((Get-FileSha256 (Join-Path $splitDst 'keep.txt')) -eq $keepHash) '6b: the file already there is unchanged'
        Invoke-UiaButton (Wait-TrayElement 'SkipErrors')
    }
    finally { Restore-RoboConfig $configText }
}

Write-Host 'PASS: Security'
