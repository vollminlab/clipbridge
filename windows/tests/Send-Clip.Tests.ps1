BeforeAll {
    # Nested 2-arg Join-Path: the 3-arg form is PS6+, and CI's windows job runs
    # Windows PowerShell 5.1. A literal '..\Send-Clip.ps1' would also break on
    # Linux, where the backslash is part of the filename rather than a separator.
    $script:ScriptPath = Join-Path (Join-Path $PSScriptRoot '..') 'Send-Clip.ps1'
    . $script:ScriptPath -DotSourceOnly
}

Describe 'Get-ClipbridgeConfig' {
    BeforeEach {
        $script:CfgDir = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:CfgDir | Out-Null
    }
    AfterEach { Remove-Item -Recurse -Force $script:CfgDir -ErrorAction SilentlyContinue }

    It 'reads sshHost and transport from config.json' {
        '{ "sshHost": "clipbridge", "transport": "ssh" }' |
            Set-Content (Join-Path $script:CfgDir 'config.json')
        $cfg = Get-ClipbridgeConfig -ConfigDir $script:CfgDir
        $cfg.sshHost   | Should -Be 'clipbridge'
        $cfg.transport | Should -Be 'ssh'
    }

    It 'throws a named error when config.json is missing' {
        { Get-ClipbridgeConfig -ConfigDir $script:CfgDir } |
            Should -Throw -ExpectedMessage '*not found*'
    }

    It 'throws when transport is not ssh or wsl' {
        '{ "sshHost": "clipbridge", "transport": "carrier-pigeon" }' |
            Set-Content (Join-Path $script:CfgDir 'config.json')
        { Get-ClipbridgeConfig -ConfigDir $script:CfgDir } |
            Should -Throw -ExpectedMessage '*carrier-pigeon*'
    }

    It 'throws when sshHost is blank' {
        '{ "sshHost": "", "transport": "ssh" }' |
            Set-Content (Join-Path $script:CfgDir 'config.json')
        { Get-ClipbridgeConfig -ConfigDir $script:CfgDir } |
            Should -Throw -ExpectedMessage '*no sshHost*'
    }

    It 'names the config path when config.json is not valid JSON' {
        $cfgPath = Join-Path $script:CfgDir 'config.json'
        '{ "sshHost": "clipbridge", ' | Set-Content $cfgPath
        { Get-ClipbridgeConfig -ConfigDir $script:CfgDir } |
            Should -Throw -ExpectedMessage "*$cfgPath*"
    }
}

Describe 'Get-SshInvocation' {
    It 'uses ssh.exe with no prefix for the ssh transport, ending in the remote command' {
        $inv = Get-SshInvocation -Transport 'ssh' -SshHost 'clipbridge'
        $inv.Exe          | Should -Be 'ssh.exe'
        $inv.Arguments[0] | Should -Be 'clipbridge'
        $inv.Arguments[-1] | Should -Be '/home/vollmin/.local/bin/clipbridge-recv'
        $inv.Arguments     | Should -HaveCount 2
    }
    It 'uses wsl.exe with an -e ssh prefix for the wsl transport, ending in the remote command' {
        $inv = Get-SshInvocation -Transport 'wsl' -SshHost 'clipbridge'
        $inv.Exe          | Should -Be 'wsl.exe'
        $inv.Arguments[0] | Should -Be '-e'
        $inv.Arguments[1] | Should -Be 'ssh'
        $inv.Arguments[2] | Should -Be 'clipbridge'
        $inv.Arguments[-1] | Should -Be '/home/vollmin/.local/bin/clipbridge-recv'
        $inv.Arguments     | Should -HaveCount 4
    }
    It 'honors a custom -RemoteCommand, appended last, for both transports' {
        $sshInv = Get-SshInvocation -Transport 'ssh' -SshHost 'clipbridge' -RemoteCommand '/opt/custom/clipbridge-recv'
        $sshInv.Arguments[-1] | Should -Be '/opt/custom/clipbridge-recv'

        $wslInv = Get-SshInvocation -Transport 'wsl' -SshHost 'clipbridge' -RemoteCommand '/opt/custom/clipbridge-recv'
        $wslInv.Arguments[-1] | Should -Be '/opt/custom/clipbridge-recv'
    }
}

Describe 'Save-ClipboardPng' {
    It 'returns $null when the clipboard holds no image' {
        Mock -CommandName Get-ClipboardDataObject -MockWith { $null }
        Save-ClipboardPng -Path (Join-Path ([System.IO.Path]::GetTempPath()) 'never-written.png') | Should -BeNullOrEmpty
    }
    It 'prefers the PNG stream over the bitmap when both are present' {
        $bytes = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x41,0x42)
        $script:ms = New-Object System.IO.MemoryStream(,$bytes)
        Mock -CommandName Get-ClipboardDataObject -MockWith {
            $o = New-Object psobject
            $o | Add-Member ScriptMethod GetDataPresent { param($f) $f -eq 'PNG' } -PassThru |
                 Add-Member ScriptMethod GetData        { param($f) $script:ms }  -PassThru
        }
        $out = Join-Path ([System.IO.Path]::GetTempPath()) 'clipbridge-test-stream.png'
        Save-ClipboardPng -Path $out | Should -Be $out
        (Get-Item $out).Length | Should -Be 10
        [System.IO.File]::ReadAllBytes($out)[0..7] |
            Should -Be @(0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)
        Remove-Item $out -Force
    }

    # $env:OS, not $IsWindows: $IsWindows is a PS6+ automatic variable and does not
    # exist under Windows PowerShell 5.1, which is what CI's Pester job actually runs
    # (`shell: powershell` on windows-latest - see the workflow). There, referencing
    # the undefined $IsWindows silently yields $null, so '-Skip:(-not $IsWindows)'
    # would evaluate true and skip the test forever even in real CI - the opposite of
    # the point of gating it. $env:OS is 'Windows_NT' on every Windows PowerShell
    # edition and unset elsewhere, so it works under both 5.1 and pwsh Core.
    #
    # This is a real round trip, not a mock of the fallback itself: it puts a real
    # bitmap on the live clipboard via Clipboard.SetImage, mocks only
    # Get-ClipboardDataObject (forcing GetDataPresent('PNG') false so the DIB branch
    # runs), then asserts the file Save-ClipboardPng wrote is a valid PNG with the
    # right pixel dimensions. A wrong path, wrong ImageFormat, or a swallowed
    # exception in that branch would fail this for real on a Windows runner.
    It 'falls back to Clipboard.GetImage() and PNG-encodes it when no PNG stream is present' -Skip:($env:OS -ne 'Windows_NT') {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $bmp = New-Object System.Drawing.Bitmap 4, 3
        for ($x = 0; $x -lt $bmp.Width; $x++) {
            for ($y = 0; $y -lt $bmp.Height; $y++) {
                $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, 200, 100, 50))
            }
        }
        [System.Windows.Forms.Clipboard]::SetImage($bmp)
        $bmp.Dispose()

        Mock -CommandName Get-ClipboardDataObject -MockWith {
            $o = New-Object psobject
            $o | Add-Member ScriptMethod GetDataPresent { param($f) $false } -PassThru
        }

        $out = Join-Path ([System.IO.Path]::GetTempPath()) 'clipbridge-test-dib.png'
        Save-ClipboardPng -Path $out | Should -Be $out

        $bytes = [System.IO.File]::ReadAllBytes($out)
        $bytes[0..7] | Should -Be @(0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)

        $loaded = [System.Drawing.Bitmap]::FromFile($out)
        try {
            $loaded.Width  | Should -Be 4
            $loaded.Height | Should -Be 3
        } finally {
            $loaded.Dispose()
        }
        Remove-Item $out -Force
    }
}

Describe 'Write-ClipbridgeLog' {
    It 'appends a timestamped line' {
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid())
        New-Item -ItemType Directory -Path $dir | Out-Null
        Write-ClipbridgeLog -ConfigDir $dir -Message 'ssh exploded'
        $line = Get-Content (Join-Path $dir 'clipbridge.log') -Tail 1
        $line | Should -Match '^\d{4}-\d{2}-\d{2}T'
        $line | Should -Match 'ssh exploded'
        Remove-Item -Recurse -Force $dir
    }

    It 'drops lines older than 7 days and keeps fresh ones' {
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid())
        New-Item -ItemType Directory -Path $dir | Out-Null
        $logPath = Join-Path $dir 'clipbridge.log'
        $staleStamp = (Get-Date).AddDays(-10).ToString('yyyy-MM-ddTHH:mm:ss')
        $freshStamp = (Get-Date).AddDays(-1).ToString('yyyy-MM-ddTHH:mm:ss')
        Set-Content -Path $logPath -Value @(
            "$staleStamp  old event",
            "$freshStamp  recent event"
        )

        Write-ClipbridgeLog -ConfigDir $dir -Message 'new event'

        $text = (Get-Content $logPath) -join "`n"
        $text | Should -Not -Match 'old event'
        $text | Should -Match 'recent event'
        $text | Should -Match 'new event'
        Remove-Item -Recurse -Force $dir
    }
}

Describe 'Test-RemotePath' {
    It 'accepts a single absolute POSIX path' {
        Test-RemotePath "/home/vollmin/.clipbridge/20260818-041500.png" | Should -BeTrue
    }
    It 'rejects empty output' { Test-RemotePath '' | Should -BeFalse }
    It 'rejects a relative path' { Test-RemotePath 'clipbridge/x.png' | Should -BeFalse }
    It 'rejects a path with a space, which would break unquoted typing' {
        Test-RemotePath '/home/vollmin/my screenshots/x.png' | Should -BeFalse
    }
    It 'rejects a trailing newline' {
        Test-RemotePath "/home/vollmin/.clipbridge/x.png`n" | Should -BeFalse
    }
    It 'rejects a trailing CR' {
        Test-RemotePath "/home/vollmin/.clipbridge/x.png`r" | Should -BeFalse
    }
    It 'rejects multi-line output' {
        Test-RemotePath "/home/vollmin/.clipbridge/x.png`n/another/line" | Should -BeFalse
    }
    It 'rejects a non-ASCII path, which Set-Content -Encoding ASCII would silently mangle' {
        Test-RemotePath "/home/vollmin/.clipbridge/caf$([char]0xE9)-x.png" | Should -BeFalse
    }
    It 'rejects a path containing a control character' {
        Test-RemotePath "/home/vollmin/.clipbridge/x$([char]0x07).png" | Should -BeFalse
    }
}

Describe 'Get-NonBlankLines' {
    It 'returns exactly one element for well-formed single-line output' {
        Get-NonBlankLines "/home/vollmin/.clipbridge/x.png`n" | Should -HaveCount 1
    }
    It 'returns every non-blank line, so a second line is never silently discarded' {
        $lines = Get-NonBlankLines "/home/vollmin/.clipbridge/x.png`n/another/line`n"
        $lines | Should -HaveCount 2
        $lines[1] | Should -Be '/another/line'
    }
    It 'drops blank lines' {
        Get-NonBlankLines "`n/home/vollmin/.clipbridge/x.png`n`n" | Should -HaveCount 1
    }
}

Describe 'Resolve-RemotePath' {
    # Literals are inlined on purpose. A Describe-scoped $path does NOT reach these
    # It blocks -- Pester 5+ runs the Describe body at discovery time, so the variable
    # is empty at run time and assertions compare empty to empty and pass vacuously.
    # These test the resolver directly; an earlier version wrapped with @() itself and
    # therefore passed with AND without the fix, certifying the bug instead of catching it.
    It 'returns the whole path for one line, not its first character' {
        $r = Resolve-RemotePath -StdOut "/home/vollmin/.clipbridge/20260819-032734.png`n"
        $r.Path   | Should -Be '/home/vollmin/.clipbridge/20260819-032734.png'
        $r.Reason | Should -BeNullOrEmpty
    }
    It 'survives CRLF, which ssh on Windows can deliver' {
        $r = Resolve-RemotePath -StdOut "/home/vollmin/.clipbridge/20260819-032734.png`r`n"
        $r.Path | Should -Be '/home/vollmin/.clipbridge/20260819-032734.png'
    }
    It 'handles output with no trailing newline' {
        $r = Resolve-RemotePath -StdOut '/home/vollmin/.clipbridge/20260819-032734.png'
        $r.Path | Should -Be '/home/vollmin/.clipbridge/20260819-032734.png'
    }
    It 'rejects two real lines and says how many it saw' {
        $r = Resolve-RemotePath -StdOut "/home/vollmin/.clipbridge/a.png`n/home/vollmin/.clipbridge/b.png`n"
        $r.Path   | Should -BeNullOrEmpty
        $r.Reason | Should -Match '2 non-blank line'
    }
    It 'rejects a relative path' {
        (Resolve-RemotePath -StdOut "clipbridge/x.png`n").Path | Should -BeNullOrEmpty
    }
    It 'rejects empty output' {
        (Resolve-RemotePath -StdOut '').Path | Should -BeNullOrEmpty
    }
}
