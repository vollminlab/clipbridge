#Requires -Version 5.1
<#
.SYNOPSIS
    Put the clipboard image on devsbx01 and record where it landed.
.DESCRIPTION
    Extracts a PNG from the clipboard, streams it to clipbridge-recv over ssh,
    and writes the returned remote path to last-path.txt for clipbridge.ahk.
    Exit codes: 0 ok | 2 no image | 3 remote rejected input | 4 ssh failed
                5 remote cannot write | 6 no usable path returned
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
        try { $stream.Position = 0; $stream.CopyTo($fs) } finally { $fs.Dispose() }
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

if ($DotSourceOnly) { return }
