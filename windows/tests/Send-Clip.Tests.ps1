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
    It 'uses ssh.exe with no prefix for the ssh transport' {
        $inv = Get-SshInvocation -Transport 'ssh' -SshHost 'clipbridge'
        $inv.Exe          | Should -Be 'ssh.exe'
        $inv.Arguments[0] | Should -Be 'clipbridge'
    }
    It 'uses wsl.exe with an -e ssh prefix for the wsl transport' {
        $inv = Get-SshInvocation -Transport 'wsl' -SshHost 'clipbridge'
        $inv.Exe          | Should -Be 'wsl.exe'
        $inv.Arguments[0] | Should -Be '-e'
        $inv.Arguments[1] | Should -Be 'ssh'
        $inv.Arguments[2] | Should -Be 'clipbridge'
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
}
