#Requires -Version 5.1
<#
.SYNOPSIS
    Put the clipboard image on devsbx01 and record where it landed.
.DESCRIPTION
    Extracts a PNG from the clipboard, streams it to clipbridge-recv over ssh,
    and writes the returned remote path to last-path.txt for clipbridge.ahk.
    Exit codes: 0 ok | 2 no image | 3 remote rejected input | 4 ssh failed
                5 remote cannot write | 6 no usable path returned
                7 cannot write local temp file
#>
[CmdletBinding()]
param(
    # $env:LOCALAPPDATA is unset when this is dot-sourced under Pester on Linux
    # for testing; Join-Path throws on a null/empty Path there. Guard so the
    # default expression never throws during parameter binding - real Windows
    # invocations always have LOCALAPPDATA set, so behavior there is unchanged.
    # The fallback must be absolute: a relative 'clipbridge' would let a later
    # New-Item -Force silently create the directory under whatever the
    # process's current directory happens to be, instead of erroring loudly.
    # $HOME is set on both Linux and Windows PowerShell 5.1+.
    [string] $ConfigDir     = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'clipbridge' } else { Join-Path $HOME '.clipbridge' }),
    [switch] $DotSourceOnly
)

function Get-ClipbridgeConfig {
    param([Parameter(Mandatory)][string] $ConfigDir)

    $path = Join-Path $ConfigDir 'config.json'
    if (-not (Test-Path $path)) {
        throw "clipbridge config not found at $path - run Install-Clipbridge.ps1"
    }
    try {
        $cfg = Get-Content $path -Raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "clipbridge config at $path is not valid JSON - $($_.Exception.Message)"
    }
    if ($cfg.transport -notin @('ssh', 'wsl')) {
        throw "clipbridge config has an unknown transport '$($cfg.transport)' - expected ssh or wsl"
    }
    if ([string]::IsNullOrWhiteSpace($cfg.sshHost)) {
        throw "clipbridge config has no sshHost"
    }
    return $cfg
}

function Get-SshInvocation {
    param(
        [Parameter(Mandatory)][ValidateSet('ssh', 'wsl')][string] $Transport,
        [Parameter(Mandatory)][string] $SshHost
    )
    if ($Transport -eq 'wsl') {
        return [pscustomobject]@{ Exe = 'wsl.exe'; Arguments = @('-e', 'ssh', $SshHost) }
    }
    return [pscustomobject]@{ Exe = 'ssh.exe'; Arguments = @($SshHost) }
}

# The single seam where Windows-only APIs are quarantined. Tests mock this, which
# is also what keeps the file dot-sourceable on Linux: System.Windows.Forms is
# Windows-only and would throw at load time if these Add-Types were at file scope.
function Get-ClipboardDataObject {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    return [System.Windows.Forms.Clipboard]::GetDataObject()
}

function Save-ClipboardPng {
    param([Parameter(Mandatory)][string] $Path)

    $dobj = Get-ClipboardDataObject
    if ($null -eq $dobj) { return $null }

    if ($dobj.GetDataPresent('PNG')) {
        # A real PNG stream: use it verbatim. GetImage() routes through a
        # device-independent bitmap and flattens transparency to black.
        $stream = $dobj.GetData('PNG')
        $fs = [System.IO.File]::Create($Path)
        try { $stream.Position = 0; $stream.CopyTo($fs) } finally { $fs.Dispose(); $stream.Dispose() }
        return $Path
    }

    # Windows-only, and never reached in tests: the no-image case returns above on a
    # null data object, and the PNG case takes the branch above. PowerShell resolves
    # types at runtime, so naming them here is harmless on Linux as long as this line
    # does not execute.
    $img = [System.Windows.Forms.Clipboard]::GetImage()
    if ($null -eq $img) { return $null }
    $img.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    return $Path
}

function Write-ClipbridgeLog {
    param(
        [Parameter(Mandatory)][string] $ConfigDir,
        [Parameter(Mandatory)][string] $Message
    )
    if (-not (Test-Path $ConfigDir)) { New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null }
    $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
    Add-Content -Path (Join-Path $ConfigDir 'clipbridge.log') -Value "$stamp  $Message"
}

function Test-RemotePath {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    # One line, absolute, no whitespace: the path is typed unquoted into a prompt,
    # so anything else is not safe to hand to SendText.
    # \z (absolute end of string), not $: .NET regex's $ matches either the true end
    # of the string OR just before a single trailing newline - so a path with one bare
    # trailing newline would otherwise slip past this check even though it is not a
    # clean single line. \z has no such exception.
    return ($Path -match '^/[^\s]+\z')
}

if ($DotSourceOnly) { return }

# --------------------------- main -----------------------------------------
$tmpPng = Join-Path ([System.IO.Path]::GetTempPath()) ('clipbridge-{0}.png' -f ([guid]::NewGuid().ToString('N')))
try {
    # Scoped narrowly around just the local clipboard-to-file step: everything in this
    # block runs before ssh is ever invoked, so any exception here is a local failure
    # (clipboard access, disk full, permissions, bad temp path) - never a transport
    # problem. Distinguishing it from the generic catch below (exit 4, documented as
    # "ssh failed") matters because behind a hotkey the only feedback is a beep; exit 4
    # would send whoever's debugging looking at ssh/network when the real fault never
    # left the laptop.
    try {
        $saved = Save-ClipboardPng -Path $tmpPng
    } catch {
        Write-ClipbridgeLog -ConfigDir $ConfigDir -Message "cannot write local temp file $tmpPng - $($_.Exception.Message)"
        exit 7
    }
    if (-not $saved) { exit 2 }   # no image: not an error

    $cfg = Get-ClipbridgeConfig -ConfigDir $ConfigDir
    $inv = Get-SshInvocation -Transport $cfg.transport -SshHost $cfg.sshHost

    $out = Join-Path ([System.IO.Path]::GetTempPath()) 'clipbridge-xfer.out'
    $err = Join-Path ([System.IO.Path]::GetTempPath()) 'clipbridge-xfer.err'

    # -RedirectStandardInput takes a FILE PATH, so the bytes never pass through a
    # PowerShell pipe. A pipe would stringify them and corrupt the image.
    $p = Start-Process -FilePath $inv.Exe -ArgumentList $inv.Arguments `
                       -RedirectStandardInput $tmpPng `
                       -RedirectStandardOutput $out -RedirectStandardError $err `
                       -NoNewWindow -Wait -PassThru

    $stdout = (Get-Content $out -Raw -ErrorAction SilentlyContinue)
    $stderr = (Get-Content $err -Raw -ErrorAction SilentlyContinue)

    if ($p.ExitCode -ne 0) {
        Write-ClipbridgeLog -ConfigDir $ConfigDir -Message "ssh exit $($p.ExitCode): $stderr"
        # 3 and 5 come from clipbridge-recv; anything else is a transport failure.
        if ($p.ExitCode -in @(3, 5)) { exit $p.ExitCode }
        exit 4
    }

    $remote = ($stdout -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 1)
    if (-not (Test-RemotePath $remote)) {
        Write-ClipbridgeLog -ConfigDir $ConfigDir -Message "unusable path from receiver: '$stdout'"
        exit 6
    }

    if (-not (Test-Path $ConfigDir)) { New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null }
    Set-Content -Path (Join-Path $ConfigDir 'last-path.txt') -Value $remote -NoNewline -Encoding ASCII
    exit 0
}
catch {
    Write-ClipbridgeLog -ConfigDir $ConfigDir -Message "unhandled: $($_.Exception.Message)"
    exit 4
}
finally {
    Remove-Item $tmpPng -Force -ErrorAction SilentlyContinue
}
