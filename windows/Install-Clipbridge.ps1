#Requires -Version 5.1
<#
.SYNOPSIS
    Detect a working ssh transport, write clipbridge's ssh Host block and config.json.
.DESCRIPTION
    Probes ssh.exe then wsl.exe -e ssh against the target host and pins whichever one
    actually authenticates - the two are not interchangeable on every laptop (measured:
    ssh.exe authenticates while wsl.exe -e ssh returns "Permission denied (publickey)"
    on the same box, because the 1Password SSH agent is wired into Windows OpenSSH but
    not into WSL's ssh). Writes the clipbridge public key, a `Host clipbridge` block in
    ~/.ssh/config pinned to that key with IdentitiesOnly, and config.json for
    Send-Clip.ps1. Safe to run more than once: it does not duplicate the Host block and
    every other file it writes is fully overwritten with deterministic content.
#>
[CmdletBinding()]
param(
    [string] $TargetHost = 'devsbx01',
    [string] $TargetUser = 'vollmin',
    [string] $HostAlias  = 'clipbridge',

    # The clipbridge keypair lives in 1Password ("Clipbridge SSH Key", Homelab vault).
    # This is the public half only - the private half never touches this laptop's disk,
    # it stays in the 1Password SSH agent. See New-SshConfigBlock below for why the ssh
    # config still points IdentityFile at this public key even though an agent is in use.
    [string] $PublicKey = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEJaXXzlVboajGm3+HtmhkHm33ynf3gJPZ9oHZpJTn/u',

    # $env:USERPROFILE and $env:LOCALAPPDATA are unset when this is dot-sourced under
    # Pester on Linux for testing; Join-Path throws on a null/empty Path there.
    # PowerShell binds parameter defaults before the function body runs, so an
    # unguarded default throws at bind time and kills every test in the file - not
    # just the ones that touch these paths. Guarded the same way as Send-Clip.ps1's
    # $ConfigDir default. Real Windows invocations always have both env vars set, so
    # behavior there is unchanged. $HOME is set on both Linux and Windows PowerShell 5.1+.
    [string] $SshDir    = $(if ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.ssh' } else { Join-Path $HOME '.ssh' }),
    [string] $ConfigDir = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'clipbridge' } else { Join-Path $HOME '.clipbridge' }),

    [switch] $DotSourceOnly
)

function Get-ClipbridgePaths {
    param(
        [Parameter(Mandatory)][string] $SshDir,
        [Parameter(Mandatory)][string] $ConfigDir
    )
    return [pscustomobject]@{
        PubKeyPath     = Join-Path $SshDir 'clipbridge_ed25519.pub'
        SshConfigPath  = Join-Path $SshDir 'config'
        ConfigJsonPath = Join-Path $ConfigDir 'config.json'
    }
}

function New-SshConfigBlock {
    param(
        [Parameter(Mandatory)][string] $HostAlias,
        [Parameter(Mandatory)][string] $TargetHost,
        [Parameter(Mandatory)][string] $TargetUser,
        [Parameter(Mandatory)][string] $IdentityFile
    )
    # IdentitiesOnly yes is the security-critical line in this block. The user's real
    # keys live in the 1Password SSH agent, and an ssh agent offers every identity it
    # holds to every connection by default. The forced command on the server side
    # (command="..." in authorized_keys, already in place on devsbx01) only restricts
    # what the ATTACHED key is allowed to do once it authenticates - it does nothing to
    # stop ssh from trying a different, unrestricted key first and getting a full shell
    # instead of the restricted clipbridge-recv command. Without IdentitiesOnly (plus
    # IdentityFile naming exactly the clipbridge key), the restriction would appear to
    # be in place - the server-side command= is real - while actually providing none of
    # the intended protection, because ssh would never be forced to offer that key.
    #
    # IdentityFile points at the PUBLIC half (clipbridge_ed25519.pub) on purpose: with
    # an agent in play, ssh only reads the public key on disk to decide which agent
    # identity to ask for - the private key itself never leaves 1Password and is never
    # written to this laptop.
    return @"

Host $HostAlias
    HostName $TargetHost
    User $TargetUser
    IdentityFile $IdentityFile
    IdentitiesOnly yes
"@
}

function Test-SshConfigHasHostBlock {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $ExistingConfig,
        [Parameter(Mandatory)][string] $HostAlias
    )
    # Matched per-line, anchored on both ends (case-insensitive, PowerShell -match's
    # default), so 'Host clipbridge' matches but 'Host clipbridge-laptop' or
    # 'SomeHost clipbridge' do not - a substring match here would either wrongly call
    # an unrelated block "already installed" or silently add a second, shadowing block.
    $pattern = '^\s*Host\s+' + [regex]::Escape($HostAlias) + '\s*$'
    foreach ($line in ($ExistingConfig -split "`r?`n")) {
        if ($line -match $pattern) { return $true }
    }
    return $false
}

function New-ClipbridgeConfigObject {
    param(
        [Parameter(Mandatory)][string] $HostAlias,
        [Parameter(Mandatory)][ValidateSet('ssh', 'wsl')][string] $Transport
    )
    # sshHost is deliberately the ssh config alias (e.g. 'clipbridge'), not the real
    # target hostname - Send-Clip.ps1 passes this straight to ssh.exe / wsl.exe -e ssh,
    # which resolves it through ~/.ssh/config, picking up HostName/User/IdentityFile/
    # IdentitiesOnly from the block above. Passing the real hostname here would bypass
    # that Host block entirely and lose IdentitiesOnly.
    return [pscustomobject]@{ sshHost = $HostAlias; transport = $Transport }
}

# --- transport probing -------------------------------------------------------

function Get-SshProbeOutcome {
    param(
        [Parameter(Mandatory)][bool] $ExeFound,
        [int] $ExitCode = -1,
        [string] $StdErr = ''
    )
    # Pure classification of a probe result. Kept separate from the process spawn
    # (Invoke-TransportProbe below) so the branching logic - which is where a wrong
    # diagnosis actually hurts the user - is exercised directly by Pester instead of
    # only ever being reachable by running real ssh against a real target.
    if (-not $ExeFound) { return 'ExeNotFound' }
    if ($ExitCode -eq 0) { return 'Authenticated' }
    if ($StdErr -match 'Permission denied') { return 'PermissionDenied' }
    if ($StdErr -match '(?i)timed out|timeout') { return 'Timeout' }
    return 'OtherFailure'
}

function Get-TransportFailureMessage {
    param(
        [Parameter(Mandatory)][string] $SshOutcome,
        [Parameter(Mandatory)][string] $WslOutcome,
        [Parameter(Mandatory)][string] $TargetHost
    )
    # Ordered so the most actionable, most likely diagnosis wins. "Permission denied"
    # on this laptop's known setup (keys in the 1Password agent, not on disk) is
    # overwhelmingly a locked/stopped agent, not a misconfigured server - so that case
    # is named explicitly and first, rather than the generic "no ssh client
    # authenticated" that used to send debugging toward the network or the server.
    $agentHint = "This almost always means the 1Password SSH agent is locked or not " +
        "running - it offers every key it holds on unlock, so a locked or stopped agent " +
        "offers none and the server correctly reports no valid key. Unlock 1Password " +
        "(or start it) so it can serve your key, then re-run this script. If the key " +
        "genuinely is not authorized, confirm the clipbridge public key is present in " +
        "~/.ssh/authorized_keys on $TargetHost."

    if ($SshOutcome -eq 'PermissionDenied' -and $WslOutcome -eq 'PermissionDenied') {
        return "Both ssh.exe and wsl.exe -e ssh reached $TargetHost and were told " +
            "'Permission denied (publickey)'. $agentHint"
    }
    if ($SshOutcome -eq 'PermissionDenied' -or $WslOutcome -eq 'PermissionDenied') {
        $which = if ($SshOutcome -eq 'PermissionDenied') { 'ssh.exe' } else { 'wsl.exe -e ssh' }
        return "$which reached $TargetHost and was told 'Permission denied (publickey)'. $agentHint"
    }
    if ($SshOutcome -eq 'Timeout' -or $WslOutcome -eq 'Timeout') {
        return "Connection to $TargetHost timed out - the box may be unreachable, " +
            "powered off, or on a different network than this laptop. This is a " +
            "connectivity problem, not an authentication one. Fix connectivity to " +
            "$TargetHost first, then re-run."
    }
    if ($SshOutcome -eq 'ExeNotFound' -and $WslOutcome -eq 'ExeNotFound') {
        return "Neither ssh.exe nor wsl.exe was found on PATH. Install the OpenSSH " +
            "client (Settings > Apps > Optional Features > OpenSSH Client) or WSL, " +
            "then re-run."
    }
    return "No ssh client authenticated to $TargetHost. ssh.exe: $SshOutcome, " +
        "wsl.exe -e ssh: $WslOutcome. Fix ssh first, then re-run."
}

function Select-Transport {
    param(
        [Parameter(Mandatory)][string] $SshOutcome,
        [Parameter(Mandatory)][string] $WslOutcome,
        [Parameter(Mandatory)][string] $TargetHost
    )
    if ($SshOutcome -eq 'Authenticated') { return 'ssh' }
    if ($WslOutcome -eq 'Authenticated') { return 'wsl' }
    throw (Get-TransportFailureMessage -SshOutcome $SshOutcome -WslOutcome $WslOutcome -TargetHost $TargetHost)
}

# The single seam where a real process gets spawned. Everything above this point is a
# pure function tested directly under Pester on Linux; this wrapper - and the two
# writer functions below it - are thin, uncalled-in-tests I/O and are only exercised
# by actually running the installer on Windows.
function Invoke-TransportProbe {
    param(
        [Parameter(Mandatory)][string] $Exe,
        # AllowEmptyCollection is required, not decorative: ssh.exe takes no prefix and
        # is called with @(). PowerShell treats an empty array as "not supplied" for a
        # Mandatory parameter and fails the bind with "Cannot bind argument to parameter
        # 'Prefix' because it is an empty array" - so without this the FIRST probe, and
        # the one that actually works on the target laptop, dies before it runs.
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Prefix,
        [Parameter(Mandatory)][string] $TargetHost,
        [int] $ConnectTimeoutSeconds = 5
    )
    if (-not (Get-Command $Exe -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ ExeFound = $false; ExitCode = -1; StdErr = '' }
    }

    $stamp = [guid]::NewGuid().ToString('N')
    $out = Join-Path ([System.IO.Path]::GetTempPath()) "clipbridge-detect-$stamp.out"
    $err = Join-Path ([System.IO.Path]::GetTempPath()) "clipbridge-detect-$stamp.err"
    try {
        $sshArgs = $Prefix + @('-o', 'BatchMode=yes', '-o', "ConnectTimeout=$ConnectTimeoutSeconds", $TargetHost, 'echo clipbridge-ok')
        $p = Start-Process -FilePath $Exe -ArgumentList $sshArgs -NoNewWindow -Wait -PassThru `
                           -RedirectStandardOutput $out -RedirectStandardError $err
        $stdout = Get-Content $out -Raw -ErrorAction SilentlyContinue
        $stderr = Get-Content $err -Raw -ErrorAction SilentlyContinue
        # Belt-and-suspenders: a 0 exit with unexpected stdout is not a trustworthy
        # "authenticated". Downgraded to OtherFailure territory by forcing a nonzero
        # code rather than trusted at face value.
        $exitCode = if ($p.ExitCode -eq 0 -and $stdout -notmatch 'clipbridge-ok') { -1 } else { $p.ExitCode }
        return [pscustomobject]@{ ExeFound = $true; ExitCode = $exitCode; StdErr = $stderr }
    } finally {
        Remove-Item $out, $err -Force -ErrorAction SilentlyContinue
    }
}

if ($DotSourceOnly) { return }

# --------------------------- main -----------------------------------------
try {
    Write-Host "Probing ssh.exe against $TargetHost..." -ForegroundColor Cyan
    $sshProbe   = Invoke-TransportProbe -Exe 'ssh.exe' -Prefix @() -TargetHost $TargetHost
    $sshOutcome = Get-SshProbeOutcome -ExeFound $sshProbe.ExeFound -ExitCode $sshProbe.ExitCode -StdErr $sshProbe.StdErr

    if ($sshOutcome -eq 'Authenticated') {
        $wslOutcome = 'NotProbed'
    } else {
        Write-Host "ssh.exe did not authenticate ($sshOutcome); probing wsl.exe -e ssh..." -ForegroundColor Cyan
        $wslProbe   = Invoke-TransportProbe -Exe 'wsl.exe' -Prefix @('-e', 'ssh') -TargetHost $TargetHost
        $wslOutcome = Get-SshProbeOutcome -ExeFound $wslProbe.ExeFound -ExitCode $wslProbe.ExitCode -StdErr $wslProbe.StdErr
    }

    $transport = Select-Transport -SshOutcome $sshOutcome -WslOutcome $wslOutcome -TargetHost $TargetHost
    Write-Host "transport: $transport" -ForegroundColor Green

    $paths = Get-ClipbridgePaths -SshDir $SshDir -ConfigDir $ConfigDir
    New-Item -ItemType Directory -Path $SshDir -Force | Out-Null

    Set-Content -Path $paths.PubKeyPath -Value $PublicKey -NoNewline -Encoding ASCII
    Write-Host "wrote $($paths.PubKeyPath)" -ForegroundColor Green

    $existingConfig = ''
    if (Test-Path $paths.SshConfigPath) { $existingConfig = Get-Content $paths.SshConfigPath -Raw }

    if (Test-SshConfigHasHostBlock -ExistingConfig $existingConfig -HostAlias $HostAlias) {
        Write-Host "ssh config already has a '$HostAlias' Host block - leaving it alone" -ForegroundColor Yellow
    } else {
        $block = New-SshConfigBlock -HostAlias $HostAlias -TargetHost $TargetHost -TargetUser $TargetUser -IdentityFile $paths.PubKeyPath
        Add-Content -Path $paths.SshConfigPath -Value $block
        Write-Host "added Host $HostAlias to $($paths.SshConfigPath)" -ForegroundColor Green
    }

    New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
    $cfg = New-ClipbridgeConfigObject -HostAlias $HostAlias -Transport $transport
    $cfg | ConvertTo-Json | Set-Content -Path $paths.ConfigJsonPath -Encoding ASCII
    Write-Host "wrote $($paths.ConfigJsonPath)" -ForegroundColor Green

    Write-Host "`nNext: put windows\clipbridge.ahk in shell:startup so it loads at login." -ForegroundColor Cyan
    exit 0
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
