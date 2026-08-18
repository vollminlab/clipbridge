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

if ($DotSourceOnly) { return }
