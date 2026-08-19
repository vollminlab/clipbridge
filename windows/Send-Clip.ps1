#Requires -Version 5.1
<#
.SYNOPSIS
    Put the clipboard image on devsbx01 and record where it landed.
.DESCRIPTION
    Extracts a PNG from the clipboard, streams it to clipbridge-recv over ssh,
    and writes the returned remote path to last-path.txt for clipbridge.ahk.
    Exit codes: 0 ok | 2 no image | 3 remote rejected input | 4 ssh failed
                5 remote cannot write | 6 no usable path returned
                7 cannot write local temp file | 8 configuration problem
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
        [Parameter(Mandatory)][string] $SshHost,
        # No dedicated key + forced command on the server side any more (see
        # Install-Clipbridge.ps1's New-SshConfigBlock for why), so the remote command has
        # to be named explicitly here instead of relying on authorized_keys to supply it.
        # Absolute path: a non-interactive ssh command does not reliably have
        # ~/.local/bin on PATH even though an interactive login shell does.
        [string] $RemoteCommand = '/home/vollmin/.local/bin/clipbridge-recv'
    )
    if ($Transport -eq 'wsl') {
        return [pscustomobject]@{ Exe = 'wsl.exe'; Arguments = @('-e', 'ssh', $SshHost, $RemoteCommand) }
    }
    return [pscustomobject]@{ Exe = 'ssh.exe'; Arguments = @($SshHost, $RemoteCommand) }
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
    $logPath = Join-Path $ConfigDir 'clipbridge.log'
    $stamp   = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
    $line    = "$stamp  $Message"

    # Capped at 7 days, same rule as the images (design spec). Each line starts with
    # a fixed-width, sortable yyyy-MM-ddTHH:mm:ss stamp, so a plain lexical string
    # compare against a cutoff stamp reproduces chronological order - this is a
    # filter, not a parse, which matters because it runs on every hotkey press.
    $cutoff = (Get-Date).AddDays(-7).ToString('yyyy-MM-ddTHH:mm:ss')
    $kept = @()
    if (Test-Path $logPath) {
        $kept = @(Get-Content $logPath | Where-Object { $_.Length -ge 19 -and $_.Substring(0, 19) -ge $cutoff })
    }
    $kept += $line
    Set-Content -Path $logPath -Value $kept
}

function Test-RemotePath {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    # One line, absolute, printable ASCII only: the path is typed unquoted into a
    # prompt, so anything else is not safe to hand to SendText.
    # \x21-\x7E excludes space (0x20), C0 control characters (below 0x21, e.g. a
    # bell or embedded CR), and anything non-ASCII. Rejecting non-ASCII here matters:
    # Set-Content -Encoding ASCII on last-path.txt below does not throw on a
    # non-ASCII character, it silently substitutes '?' - so a path like
    # '/home/x/café.png' would otherwise pass validation and then get corrupted
    # on write with no error and no log line. The fix belongs at validation, not by
    # widening the encoding: the path is genuinely meant to be ASCII, and widening
    # would only move the same mismatch into the not-yet-written AHK side, which
    # would then need a matching encoding for FileRead.
    # \z (absolute end of string), not $: .NET regex's $ matches either the true end
    # of the string OR just before a single trailing newline - so a path with one bare
    # trailing newline would otherwise slip past this check even though it is not a
    # clean single line. \z has no such exception.
    return ($Path -match '^/[\x21-\x7E]+\z')
}

function Get-NonBlankLines {
    param([string] $Text)
    # A separate, testable step: the main body used to fold this straight into
    # 'Select-Object -First 1', which silently discarded any line past the first.
    # The receiver emits exactly one line today, but nothing enforces that, and
    # this file's discipline is that no failure is silent - so the main body checks
    # this array's Count itself rather than only ever seeing the first element.
    return @(($Text -split "`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Resolve-RemotePath {
    param([string] $StdOut)
    # This exists so the main body holds no parsing logic it cannot test. The bug it
    # replaces: the main body did `$lines = Get-NonBlankLines ...` without @(), and
    # PowerShell unrolls a single-element array on function output -- so $lines was the
    # path STRING, .Count was 1 (strings report 1, so the count check passed), and
    # $lines[0] was the first CHARACTER, "/". Every real path was rejected while the log
    # printed a perfect-looking $stdout. Wrapping lives HERE, where tests reach it.
    $lines = @(Get-NonBlankLines -Text $StdOut)
    if ($lines.Count -ne 1) {
        return [pscustomobject]@{ Path = $null; Reason = "receiver returned $($lines.Count) non-blank line(s), expected exactly 1: '$StdOut'" }
    }
    if (-not (Test-RemotePath $lines[0])) {
        return [pscustomobject]@{ Path = $null; Reason = "unusable path from receiver: '$StdOut'" }
    }
    return [pscustomobject]@{ Path = $lines[0]; Reason = $null }
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

    # Scoped narrowly around just the config read, for the same reason as the
    # Save-ClipboardPng try above: a missing/invalid config.json, an unknown
    # transport, or a blank sshHost is a local configuration problem, not a
    # transport failure. Before this, all four landed in the generic catch below
    # as exit 4 ("ssh failed") - a first run before Install-Clipbridge.ps1 has ever
    # executed would log "unhandled: clipbridge config not found..." under the ssh
    # failure code, sending debugging in the wrong direction. Get-ClipbridgeConfig's
    # own throw messages already name the specific cause (and, for a missing or
    # malformed file, the path), so nothing further is needed here to satisfy that.
    try {
        $cfg = Get-ClipbridgeConfig -ConfigDir $ConfigDir
    } catch {
        Write-ClipbridgeLog -ConfigDir $ConfigDir -Message "configuration problem: $($_.Exception.Message)"
        exit 8
    }
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

    # @(...) at the CALL SITE is required, not decorative. PowerShell unrolls a
    # single-element array on function output, so without it $lines is the path
    # STRING, not an array holding it: .Count is 1 (strings report Count 1, so the
    # check below still passes) and $lines[0] indexes the first CHARACTER -- "/".
    # Test-RemotePath then rejects "/" and the log prints $stdout, which looks
    # perfect, while the value actually tested was one byte long.
    $resolved = Resolve-RemotePath -StdOut $stdout
    if (-not $resolved.Path) {
        Write-ClipbridgeLog -ConfigDir $ConfigDir -Message $resolved.Reason
        exit 6
    }
    $remote = $resolved.Path
    if (-not (Test-Path $ConfigDir)) { New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null }
    # last-path.txt is only safe to read after this process has exited: Set-Content
    # truncates then writes, so the file is briefly empty mid-write, and this write
    # completes before the exit 0 below. clipbridge.ahk must launch this script with
    # RunWait (which blocks until the process exits), never a poll loop that could
    # observe the file mid-truncation.
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
