#Requires -Version 5.1
<#
.SYNOPSIS
    Pre-build verification probe for clipbridge. Builds nothing, installs nothing,
    changes nothing on either machine except one temp file on each.

.DESCRIPTION
    Answers items 2-4 of the verification plan in docs/superpowers/specs/clipbridge-design.md:

      2. Which clipboard formats does your screenshot tool actually publish?
      3. Which ssh client authenticates to the box - ssh.exe or wsl.exe -e ssh?
      4. Does a PNG survive `ssh` stdin byte-for-byte via Start-Process redirection?

    Item 1 (SendText through mosh) is a separate probe: verify/probe-sendtext.ahk

.EXAMPLE
    # Take a screenshot first, so there is an image on the clipboard.
    powershell.exe -STA -ExecutionPolicy Bypass -File .\Probe-Clipbridge.ps1
#>
[CmdletBinding()]
param(
    [string] $SshHost   = 'devsbx01',
    [string] $RemoteTmp = '/tmp/clipbridge-probe.png'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Write-Section { param([string] $Title) Write-Host "`n=== $Title ===" -ForegroundColor Cyan }
function Write-Pass    { param([string] $M) Write-Host "  PASS  $M" -ForegroundColor Green }
function Write-Fail    { param([string] $M) Write-Host "  FAIL  $M" -ForegroundColor Red }
function Write-Info    { param([string] $M) Write-Host "  ....  $M" -ForegroundColor DarkGray }

if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    Write-Fail 'Not running in STA. Re-run with: powershell.exe -STA -File .\Probe-Clipbridge.ps1'
    exit 1
}

# --------------------------------------------------------------------------
Write-Section '2. Clipboard formats'
# --------------------------------------------------------------------------

$dobj = [System.Windows.Forms.Clipboard]::GetDataObject()
if ($null -eq $dobj) {
    Write-Fail 'Clipboard is empty. Take a screenshot (Win+Shift+S) and re-run.'
    exit 2
}

Write-Info 'Formats advertised by the clipboard:'
$dobj.GetFormats() | ForEach-Object { Write-Host "          $_" -ForegroundColor DarkGray }

$hasPngStream = $dobj.GetDataPresent('PNG')
$bitmap       = [System.Windows.Forms.Clipboard]::GetImage()

if ($hasPngStream) {
    Write-Pass 'A real PNG stream is present - the lossless branch is the live path.'
} elseif ($null -ne $bitmap) {
    Write-Pass 'No PNG stream, but GetImage() returns a bitmap - the DIB fallback is the live path.'
} else {
    Write-Fail 'Neither a PNG stream nor a bitmap. Nothing image-shaped is on the clipboard.'
    exit 2
}

# Extract exactly the way Send-Clip.ps1 will, so this probe exercises the real code path.
$localPng = Join-Path $env:TEMP 'clipbridge-probe.png'
if ($hasPngStream) {
    $stream = $dobj.GetData('PNG')
    $fs = [System.IO.File]::Create($localPng)
    try { $stream.Position = 0; $stream.CopyTo($fs) } finally { $fs.Dispose() }
} else {
    $bitmap.Save($localPng, [System.Drawing.Imaging.ImageFormat]::Png)
}

$localHash = (Get-FileHash -Algorithm SHA256 -Path $localPng).Hash.ToLower()
$localSize = (Get-Item $localPng).Length
Write-Info "Wrote $localPng ($localSize bytes)"
Write-Info "  sha256 $localHash"

# --------------------------------------------------------------------------
Write-Section '3. Which ssh client authenticates'
# --------------------------------------------------------------------------

$candidates = @(
    [pscustomobject]@{ Name = 'ssh';  Exe = 'ssh.exe';  Prefix = @() }
    [pscustomobject]@{ Name = 'wsl';  Exe = 'wsl.exe';  Prefix = @('-e', 'ssh') }
)

$working = $null
foreach ($c in $candidates) {
    if (-not (Get-Command $c.Exe -ErrorAction SilentlyContinue)) {
        Write-Info "$($c.Exe) not found on PATH"
        continue
    }
    $sshArgs = $c.Prefix + @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=5', $SshHost, 'echo clipbridge-ok')
    $out  = Join-Path $env:TEMP "clipbridge-probe-$($c.Name).out"
    $err  = Join-Path $env:TEMP "clipbridge-probe-$($c.Name).err"

    $p = Start-Process -FilePath $c.Exe -ArgumentList $sshArgs -NoNewWindow -Wait -PassThru `
                       -RedirectStandardOutput $out -RedirectStandardError $err
    $stdout = (Get-Content $out -Raw -ErrorAction SilentlyContinue)

    if ($p.ExitCode -eq 0 -and $stdout -match 'clipbridge-ok') {
        Write-Pass "$($c.Exe) $($c.Prefix -join ' ') -> authenticated to $SshHost"
        if (-not $working) { $working = $c }
    } else {
        $stderr = (Get-Content $err -Raw -ErrorAction SilentlyContinue)
        Write-Fail "$($c.Exe) exit $($p.ExitCode): $(($stderr -split "`n" | Select-Object -First 1))"
    }
}

if (-not $working) {
    Write-Fail "No ssh client authenticated to $SshHost. Item 4 cannot run."
    exit 3
}

# --------------------------------------------------------------------------
Write-Section '4. Binary integrity over ssh stdin'
# --------------------------------------------------------------------------

# This is the production mechanism verbatim: -RedirectStandardInput takes a FILE PATH,
# so the bytes never pass through a PowerShell pipe (which would corrupt them).
$sshArgs = $working.Prefix + @($SshHost, "cat > $RemoteTmp && sha256sum $RemoteTmp | cut -d' ' -f1")
$out  = Join-Path $env:TEMP 'clipbridge-probe-xfer.out'
$err  = Join-Path $env:TEMP 'clipbridge-probe-xfer.err'

$p = Start-Process -FilePath $working.Exe -ArgumentList $sshArgs -NoNewWindow -Wait -PassThru `
                   -RedirectStandardInput $localPng -RedirectStandardOutput $out -RedirectStandardError $err

$remoteHash = ((Get-Content $out -Raw -ErrorAction SilentlyContinue) -replace '\s', '').ToLower()

if ($p.ExitCode -ne 0) {
    Write-Fail "transfer exited $($p.ExitCode): $(Get-Content $err -Raw -ErrorAction SilentlyContinue)"
    exit 4
}

Write-Info "local  sha256 $localHash"
Write-Info "remote sha256 $remoteHash"

if ($remoteHash -eq $localHash) {
    Write-Pass "$localSize bytes arrived byte-for-byte. Start-Process stdin redirection is sound."
} else {
    Write-Fail 'Hash mismatch - the bytes were altered in transit. Do NOT build on this transport.'
    exit 4
}

Write-Host "`nItems 2-4 verified. Item 1 (SendText through mosh) is verify\probe-sendtext.ahk`n" -ForegroundColor Cyan
Write-Info "Remote leftover to clean up when done: $RemoteTmp"
