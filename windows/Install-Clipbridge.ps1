#Requires -Version 5.1
<#
.SYNOPSIS
    Detect a working ssh transport, write clipbridge's ssh Host block and config.json.
.DESCRIPTION
    Probes ssh.exe then wsl.exe -e ssh against the target host and pins whichever one
    actually authenticates - the two are not interchangeable on every laptop (measured:
    ssh.exe authenticates while wsl.exe -e ssh returns "Permission denied (publickey)"
    on the same box, because the 1Password SSH agent is wired into Windows OpenSSH but
    not into WSL's ssh). Writes a `Host clipbridge` block in ~/.ssh/config pointed at the
    user's existing devsbx01 key with IdentitiesOnly, and config.json for Send-Clip.ps1.
    Safe to run more than once: it does not duplicate the Host block and every other file
    it writes is fully overwritten with deterministic content.
#>
[CmdletBinding()]
param(
    # Two DIFFERENT names, and conflating them breaks the probe.
    #
    # $ProbeHost is what we hand to ssh on the command line to test authentication. It
    # must be a name the user's EXISTING config has a Host block for, because ssh matches
    # Host patterns against the name typed on the command line -- not against the resolved
    # hostname. Probing 'devsbx01.vollminlab.com' falls through to their 'Host *' block,
    # which sets IdentitiesOnly yes with no IdentityFile, so ssh has nothing to offer and
    # the probe fails with 'Permission denied (publickey)' on a box that authenticates fine.
    [string] $ProbeHost = 'devsbx01',
    #
    # $TargetHost becomes HostName in the block we generate, and HostName IS resolved by
    # DNS directly, so it needs the FQDN. The generated block is self-contained (HostName,
    # User, IdentityFile, IdentitiesOnly), so it does not depend on their other blocks.
    [string] $TargetHost = 'devsbx01.vollminlab.com',
    [string] $TargetUser = 'vollmin',
    [string] $HostAlias  = 'clipbridge',

    # $env:USERPROFILE and $env:LOCALAPPDATA are unset when this is dot-sourced under
    # Pester on Linux for testing; Join-Path throws on a null/empty Path there.
    # PowerShell binds parameter defaults before the function body runs, so an
    # unguarded default throws at bind time and kills every test in the file - not
    # just the ones that touch these paths. Guarded the same way as Send-Clip.ps1's
    # $ConfigDir default. Real Windows invocations always have both env vars set, so
    # behavior there is unchanged. $HOME is set on both Linux and Windows PowerShell 5.1+.
    [string] $SshDir    = $(if ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.ssh' } else { Join-Path $HOME '.ssh' }),
    [string] $ConfigDir = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'clipbridge' } else { Join-Path $HOME '.clipbridge' }),

    # No dedicated clipbridge key any more - see New-SshConfigBlock below for why. This
    # points at the public half of the user's existing devsbx01 key, already authorized
    # for a full shell on that box and already known to the 1Password SSH agent.
    [string] $IdentityFile = $(Join-Path $SshDir 'devsbx01_id_ed25519.pub'),

    [switch] $DotSourceOnly
)

function Get-ClipbridgePaths {
    param(
        [Parameter(Mandatory)][string] $SshDir,
        [Parameter(Mandatory)][string] $ConfigDir
    )
    return [pscustomobject]@{
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
    # There used to be a dedicated, restricted clipbridge key here, pinned with
    # IdentitiesOnly so ssh could only ever offer that one shell-less credential. It was
    # removed: the key lived in the shared 1Password SSH agent, which offers every key it
    # holds to any client that doesn't pin identities - and mosh runs inside WSL, whose
    # ssh config has no such pinning (Windows' does). WSL offered the restricted key,
    # sshd accepted it, and its forced command's implicit no-pty killed mosh - locking
    # the user out of their own box. The marginal security was near zero anyway: the same
    # agent already holds a key (this one) that opens a full shell on this exact host, so
    # a shell-less credential next to it wasn't buying much. clipbridge now authenticates
    # with that ordinary key and names the remote command explicitly on the ssh command
    # line (see Send-Clip.ps1's -RemoteCommand) instead of restricting via authorized_keys.
    #
    # IdentitiesOnly yes still matters, for an unrelated reason: the agent holds roughly
    # two dozen keys, and without pinning, ssh offers them all in agent order - which can
    # burn the server's auth-attempt limit or authenticate as the wrong identity before
    # ever trying this one. IdentityFile points at the PUBLIC half on purpose: with an
    # agent in play, ssh only reads the public key on disk to decide which agent identity
    # to ask for - the private key itself never leaves 1Password.
    #
    # ForwardAgent no: clipbridge never authenticates onward from devsbx01, so there is
    # nothing for a forwarded agent to do here - it would just be unnecessary exposure of
    # every key in the agent to that host. (It does not fix any hang: agent forwarding was
    # tested on, `ssh devsbx01 true` still completed in 0.5s. This is belt-and-suspenders,
    # not a workaround.) Without this line the user's global `ForwardAgent yes` would
    # apply here too, since Host blocks don't opt out of global settings on their own.
    return @"

Host $HostAlias
    HostName $TargetHost
    User $TargetUser
    IdentityFile $IdentityFile
    IdentitiesOnly yes
    ForwardAgent no
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
        "your key is present in ~/.ssh/authorized_keys on $TargetHost."

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
    Write-Host "Probing ssh.exe against $ProbeHost..." -ForegroundColor Cyan
    $sshProbe   = Invoke-TransportProbe -Exe 'ssh.exe' -Prefix @() -TargetHost $ProbeHost
    $sshOutcome = Get-SshProbeOutcome -ExeFound $sshProbe.ExeFound -ExitCode $sshProbe.ExitCode -StdErr $sshProbe.StdErr

    if ($sshOutcome -eq 'Authenticated') {
        $wslOutcome = 'NotProbed'
    } else {
        Write-Host "ssh.exe did not authenticate ($sshOutcome); probing wsl.exe -e ssh..." -ForegroundColor Cyan
        $wslProbe   = Invoke-TransportProbe -Exe 'wsl.exe' -Prefix @('-e', 'ssh') -TargetHost $ProbeHost
        $wslOutcome = Get-SshProbeOutcome -ExeFound $wslProbe.ExeFound -ExitCode $wslProbe.ExitCode -StdErr $wslProbe.StdErr
    }

    $transport = Select-Transport -SshOutcome $sshOutcome -WslOutcome $wslOutcome -TargetHost $ProbeHost
    Write-Host "transport: $transport" -ForegroundColor Green

    $paths = Get-ClipbridgePaths -SshDir $SshDir -ConfigDir $ConfigDir
    New-Item -ItemType Directory -Path $SshDir -Force | Out-Null

    $existingConfig = ''
    if (Test-Path $paths.SshConfigPath) { $existingConfig = Get-Content $paths.SshConfigPath -Raw }

    if (Test-SshConfigHasHostBlock -ExistingConfig $existingConfig -HostAlias $HostAlias) {
        Write-Host "ssh config already has a '$HostAlias' Host block - leaving it alone" -ForegroundColor Yellow
    } else {
        $block = New-SshConfigBlock -HostAlias $HostAlias -TargetHost $TargetHost -TargetUser $TargetUser -IdentityFile $IdentityFile
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
