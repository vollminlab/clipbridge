# clipbridge v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `windows/Send-Clip.ps1` + `windows/Install-Clipbridge.ps1` + `windows/clipbridge.ahk` with a single AOT-compiled `clipbridge.exe` that does the same job resident in the tray, cutting the per-paste cost from ~3.0s (mostly `powershell.exe` startup) to ~0.5s (the ssh round trip alone), while making the previously-untestable hotkey logic unit-testable.

**Architecture:** One process, resident in the tray (`win-x64`, `PublishAot`, self-contained). A `ClipBridge.Core` library holds every decision (path validation, config parsing, ssh argument building, the DIB→PNG fallback encoder, and `PasteOrchestrator` — the full paste/transfer/failure state machine) with zero Windows API dependencies, so it builds and tests on Linux with `dotnet-sdk-10.0`. A `ClipBridge.Win32` library holds thin, branchless P/Invoke shims (clipboard, foreground window, `SendInput`, the `WH_KEYBOARD_LL` hook, `ssh.exe` process spawning) that only run and only get tested on `windows-latest`. `ClipBridge.App` wires the two together: a raw Win32 message loop (no `System.Windows.Forms`), a minimal tray icon, `--install`, and a registry `Run` key for startup.

**Tech Stack:** .NET 10 (`net10.0` / `net10.0-windows`), C#, `PublishAot`, xUnit, SixLabors.ImageSharp (DIB→PNG fallback), raw Win32 P/Invoke (`user32.dll`, `kernel32.dll`, `shell32.dll`), `ssh.exe` (unchanged transport), GitHub Actions (`ubuntu-latest` + `windows-latest`).

**Design spec:** `docs/superpowers/specs/clipbridge-csharp-design.md`. Read it before starting — this plan implements it task by task and does not re-litigate its decisions.

**v1 test baseline this plan must preserve as acceptance criteria:** `windows/tests/Send-Clip.Tests.ps1` (31 `It` blocks) and `windows/tests/Install-Clipbridge.Tests.ps1` (34 `It` blocks) — read together with `windows/Send-Clip.ps1`, `windows/Install-Clipbridge.ps1`, and `windows/clipbridge.ahk`, which is what each C# component replaces.

---

## File structure

| File | Responsibility |
|---|---|
| `dotnet/clipbridge.sln` | Solution file referencing all five projects below. |
| `dotnet/ClipBridge.Core/PathValidator.cs` | Port of `Test-RemotePath` — the unquoted-typing safety check. |
| `dotnet/ClipBridge.Core/RemotePathResolver.cs` | Port of `Get-NonBlankLines` + `Resolve-RemotePath`. |
| `dotnet/ClipBridge.Core/SshArgumentBuilder.cs` | Port of `Get-SshInvocation`. |
| `dotnet/ClipBridge.Core/ClipbridgeConfig.cs` | Port of `Get-ClipbridgeConfig` (reader) + config record + `ClipbridgeConfigException`. |
| `dotnet/ClipBridge.Core/ClipbridgeLogger.cs` | Port of `Write-ClipbridgeLog` (7-day retention, lexical stamp compare). |
| `dotnet/ClipBridge.Core/DibToPngConverter.cs` | CF_DIB→BMP-header synth→ImageSharp PNG encode. Pure bytes, Linux-testable. |
| `dotnet/ClipBridge.Core/ClipboardImageExtractor.cs` | Port of `Save-ClipboardPng`'s PNG-over-DIB preference, minus the Win32 extraction. |
| `dotnet/ClipBridge.Core/SshConfigBlockBuilder.cs` | Port of `New-SshConfigBlock` + `Test-SshConfigHasHostBlock` + `Get-ClipbridgePaths`. |
| `dotnet/ClipBridge.Core/ClipbridgeConfigFactory.cs` | Port of `New-ClipbridgeConfigObject` + hand-written JSON writer (AOT-safe, no reflection). |
| `dotnet/ClipBridge.Core/TransportProbeClassifier.cs` | Port of `Get-SshProbeOutcome` + `Get-TransportFailureMessage` + `Select-Transport`. |
| `dotnet/ClipBridge.Core/Interfaces.cs` | `IClipboard`, `IPasteSink`, `IForegroundWindow`, `ISshTransport`, `IKeyboardHook`, result/record types. |
| `dotnet/ClipBridge.Core/HotkeyDecision.cs` | Pure 3-step swallow decision the hook callback delegates to. |
| `dotnet/ClipBridge.Core/PasteOrchestrator.cs` | The actual logic — port of `Send-Clip.ps1`'s main body + `clipbridge.ahk`'s `RunClipbridge`. |
| `dotnet/ClipBridge.Core.Tests/*` | xUnit, runs on Linux, no fakes needed for I/O since Core touches no OS API. |
| `dotnet/ClipBridge.Win32/NativeMethods.cs` | Every P/Invoke signature and struct, in one place. |
| `dotnet/ClipBridge.Win32/Win32Clipboard.cs` | `IClipboard` — raw PNG/DIB extraction, capture/restore, set-text. |
| `dotnet/ClipBridge.Win32/Win32ForegroundWindow.cs` | `IForegroundWindow` — `GetForegroundWindow` + process name. |
| `dotnet/ClipBridge.Win32/Win32PasteSink.cs` | `IPasteSink` — `SendInput` synthesizing Ctrl+V. |
| `dotnet/ClipBridge.Win32/SshTransport.cs` | `ISshTransport` — spawns `ssh.exe`, stdin from a file, never a string-mangling pipe. |
| `dotnet/ClipBridge.Win32/KeyboardHook.cs` | `IKeyboardHook` — `WH_KEYBOARD_LL`, the highest-risk component. |
| `dotnet/ClipBridge.Win32/TrayIcon.cs` | `Shell_NotifyIcon` tray icon: Exit, Open log, Reinstall. |
| `dotnet/ClipBridge.Win32/SingleThreadDispatcher.cs` | Worker thread the hook callback posts to instead of blocking. |
| `dotnet/ClipBridge.Win32.Tests/*` | xUnit, real Win32 calls, only meaningful on `windows-latest`. |
| `dotnet/ClipBridge.App/Program.cs` | Composition root: message loop, watchdog timer, registry `Run` key, `--install`. |
| `dotnet/ClipBridge.App/InstallCommand.cs` | Port of `Install-Clipbridge.ps1`'s main body. |
| `.github/workflows/test.yml` | Modify: add a `dotnet` job group (Core on ubuntu, Win32+AOT publish on windows). |
| `github-admin/terraform/main.tf` | Modify: add the new CI job names to `clipbridge_main`'s `required_status_checks.contexts`. |
| `docs/clipbridge-architecture.md` | Modify: document v2 alongside v1 until cutover, then v2 only. |
| `CLAUDE.md` | Modify: repo layout table, gotchas that carry over, gotchas that don't. |

**Git workflow:** one branch per task group, PR per branch, never push to `main`. Branch names: `spike/dotnet-aot` (Task 0, throwaway, never merged), `feat/dotnet-core` (Tasks 1–11), `feat/dotnet-win32` (Tasks 12–20), `feat/dotnet-ci` (Tasks 21–22), `docs/dotnet-v2` (Task 23), `chore/remove-v1` (Task 24, last, gated).

---

## Task 0: Environment + AOT/P-Invoke/ImageSharp spike (BLOCKS EVERYTHING ELSE)

**This task requires the user.** `dotnet` is not installed on devsbx01. Nothing in Tasks 1–24 can start until Step 1 completes.

**Files:** none in the repo — this is a throwaway spike outside `dotnet/`, deleted at the end.

- [ ] **Step 1: Install the SDK (requires the user)**

Ask the user to run, or run with explicit confirmation since it needs `sudo`:

```bash
sudo apt install -y dotnet-sdk-10.0
```

- [ ] **Step 2: Verify**

```bash
dotnet --version
```

Expected: a `10.0.x` version string. If this fails, stop — nothing below works without it.

- [ ] **Step 3: Scaffold the throwaway spike project**

```bash
SPIKE=$(mktemp -d)
cd "$SPIKE"
dotnet new console -n spike -f net10.0-windows -o spike
cd spike
dotnet add package SixLabors.ImageSharp
```

- [ ] **Step 4: Write the spike program**

Replace `Program.cs`:

```csharp
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

[DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hWndNewOwner);
[DllImport("user32.dll")] static extern bool CloseClipboard();

// Prove ImageSharp itself survives AOT trimming: encode a 2x2 image to PNG
// and check the magic bytes come back correct.
using (var image = new Image<Rgba32>(2, 2))
using (var ms = new MemoryStream())
{
    image.SaveAsPng(ms);
    var bytes = ms.ToArray();
    if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50)
    {
        Console.Error.WriteLine("FAIL: ImageSharp did not produce a PNG under AOT");
        return 1;
    }
}

// Prove the P/Invoke survives AOT and doesn't throw a marshalling exception.
// OpenClipboard returning false (no console session, or another process
// owns it) is an acceptable outcome; a MarshalDirectiveException or a
// missing-entry-point failure is not.
try
{
    var opened = OpenClipboard(IntPtr.Zero);
    if (opened) CloseClipboard();
    Console.WriteLine($"OpenClipboard P/Invoke resolved and ran, returned {opened}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: P/Invoke threw under AOT - {ex}");
    return 1;
}

Console.WriteLine("SPIKE PASSED: AOT + P/Invoke + ImageSharp all survive publish");
return 0;
```

- [ ] **Step 5: Attempt a local AOT publish**

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishAot=true
```

**This step's outcome is not assumed — it is the point of the spike.** NativeAOT cross-compilation from a Linux host to a `win-x64` target has historically required the target platform's native linker, which devsbx01 does not have. Two possible outcomes, both informative:

  - **It succeeds** (a `.NET 10` cross-toolchain improvement landed): note this, keep the working command for Task 21.
  - **It fails** with a linker/toolchain error (e.g. referencing a missing `link.exe`, or an explicit "cross-OS publish not supported" message): this confirms AOT publishing for this project must happen on a Windows host or `windows-latest` CI, never on devsbx01. This does **not** block iterative dev/test — only the final publish step.

- [ ] **Step 6: If Step 5 failed, prove it via `windows-latest` CI instead**

```bash
cd "$SPIKE/spike"
git init -q
git add -A
git commit -q -m "throwaway: AOT + P/Invoke + ImageSharp spike"
```

Push to a scratch branch in the `clipbridge` repo (`spike/dotnet-aot`) with a one-off workflow file that runs on `windows-latest`:

```yaml
name: aot-spike
on: push
jobs:
  spike:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - run: dotnet publish -c Release -r win-x64 --self-contained -p:PublishAot=true
      - run: .\bin\Release\net10.0-windows\win-x64\publish\spike.exe
```

Confirm the workflow run shows `SPIKE PASSED` in the log and exits 0.

- [ ] **Step 7: Record the result and clean up**

Note in the branch's PR description (or just in the session) which path proved it (Step 5 or Step 6) and the exact ImageSharp version that resolved (`dotnet list package` in the spike dir) — Task 12 pins this version, not a guess.

```bash
rm -rf "$SPIKE"
```

If a `spike/dotnet-aot` branch and throwaway workflow were pushed, delete the branch once the result is recorded — it must never merge to `main`.

- [ ] **Step 8: Decision gate**

If either Step 5 or Step 6 failed for a reason other than the expected cross-OS toolchain gap (e.g. ImageSharp itself throws under AOT, or the P/Invoke marshals incorrectly), **stop and re-open the design** — the design spec's own risk section calls this "the least-travelled combination here" and says exactly that: find out on day one, not after Tasks 1–24 are built on top of an assumption that doesn't hold.

---

## Task 1: Solution and project scaffold

**Files:**
- Create: `dotnet/clipbridge.sln`
- Create: `dotnet/ClipBridge.Core/ClipBridge.Core.csproj`
- Create: `dotnet/ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj`
- Create: `dotnet/ClipBridge.Win32/ClipBridge.Win32.csproj`
- Create: `dotnet/ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj`
- Create: `dotnet/ClipBridge.App/ClipBridge.App.csproj`

Start branch: `git checkout main && git pull && git checkout -b feat/dotnet-core`

- [ ] **Step 1: Scaffold every project**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge
mkdir dotnet && cd dotnet
dotnet new sln -n clipbridge
dotnet new classlib -n ClipBridge.Core -f net10.0 -o ClipBridge.Core
dotnet new xunit -n ClipBridge.Core.Tests -f net10.0 -o ClipBridge.Core.Tests
dotnet new classlib -n ClipBridge.Win32 -f net10.0-windows -o ClipBridge.Win32
dotnet new xunit -n ClipBridge.Win32.Tests -f net10.0-windows -o ClipBridge.Win32.Tests
dotnet new console -n ClipBridge.App -f net10.0-windows -o ClipBridge.App
```

- [ ] **Step 2: Wire the solution and references**

```bash
dotnet sln add ClipBridge.Core/ClipBridge.Core.csproj \
  ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj \
  ClipBridge.Win32/ClipBridge.Win32.csproj \
  ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj \
  ClipBridge.App/ClipBridge.App.csproj

dotnet add ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj reference ClipBridge.Core/ClipBridge.Core.csproj
dotnet add ClipBridge.Win32/ClipBridge.Win32.csproj reference ClipBridge.Core/ClipBridge.Core.csproj
dotnet add ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj reference ClipBridge.Win32/ClipBridge.Win32.csproj
dotnet add ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj reference ClipBridge.Core/ClipBridge.Core.csproj
dotnet add ClipBridge.App/ClipBridge.App.csproj reference ClipBridge.Core/ClipBridge.Core.csproj
dotnet add ClipBridge.App/ClipBridge.App.csproj reference ClipBridge.Win32/ClipBridge.Win32.csproj

dotnet add ClipBridge.Core/ClipBridge.Core.csproj package SixLabors.ImageSharp
```

`ClipBridge.Win32` and `ClipBridge.App` get ImageSharp transitively via their project reference to `ClipBridge.Core` — no separate package add needed there.

- [ ] **Step 3: Confirm everything builds on Linux**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet build
```

Expected: `Build succeeded.` for all five projects, including the two `net10.0-windows` ones — the `-windows` TFM suffix only gates `[SupportedOSPlatform]` analyzer warnings, it does not require an actual Windows machine to compile.

- [ ] **Step 4: Commit**

```bash
git add dotnet/clipbridge.sln dotnet/ClipBridge.Core dotnet/ClipBridge.Core.Tests \
  dotnet/ClipBridge.Win32 dotnet/ClipBridge.Win32.Tests dotnet/ClipBridge.App
git commit -m "chore(dotnet): scaffold clipbridge v2 solution and five projects"
```

---

## Task 2: `PathValidator` — port of `Test-RemotePath`

**Files:**
- Create: `dotnet/ClipBridge.Core/PathValidator.cs`
- Test: `dotnet/ClipBridge.Core.Tests/PathValidatorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class PathValidatorTests
{
    [Theory]
    [InlineData("/home/vollmin/.clipbridge/20260818-041500.png", true)]
    [InlineData("", false)]
    [InlineData("clipbridge/x.png", false)]                                // relative
    [InlineData("/home/vollmin/my screenshots/x.png", false)]              // space
    [InlineData("/home/vollmin/.clipbridge/x.png\n", false)]               // trailing LF
    [InlineData("/home/vollmin/.clipbridge/x.png\r", false)]               // trailing CR
    [InlineData("/home/vollmin/.clipbridge/x.png\r\n", false)]             // trailing CRLF
    [InlineData("/home/vollmin/.clipbridge/x.png\n/another/line", false)]  // embedded newline
    public void Validates_against_v1_cases(string path, bool expected)
    {
        Assert.Equal(expected, PathValidator.IsValid(path));
    }

    [Fact]
    public void Rejects_non_ascii()
    {
        Assert.False(PathValidator.IsValid("/home/vollmin/.clipbridge/café-x.png"));
    }

    [Fact]
    public void Rejects_a_control_character()
    {
        Assert.False(PathValidator.IsValid("/home/vollmin/.clipbridge/x.png"));
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter PathValidatorTests
```

Expected: build error — `PathValidator` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
using System.Text.RegularExpressions;

namespace ClipBridge.Core;

public static partial class PathValidator
{
    // Single line, absolute, printable ASCII only. \x21-\x7E excludes space
    // (0x20), C0 control chars (below 0x21), and anything non-ASCII. The
    // path is typed into a prompt unquoted, so anything else isn't safe.
    // \z (absolute end of string), not $: C#'s Regex $ has the same
    // trailing-newline exception as .NET's PowerShell regex - it matches
    // immediately before a single trailing \n too, so a path with one bare
    // trailing newline would slip past a $-anchored check and type as
    // Enter, submitting the prompt early. See CLAUDE.md Gotcha #6.
    [GeneratedRegex(@"^/[\x21-\x7E]+\z")]
    private static partial Regex Pattern();

    public static bool IsValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Pattern().IsMatch(path);
    }
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter PathValidatorTests
```

Expected: `Passed! - Failed: 0, Passed: 10`.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/PathValidator.cs dotnet/ClipBridge.Core.Tests/PathValidatorTests.cs
git commit -m "feat(dotnet): port Test-RemotePath to PathValidator"
```

---

## Task 3: `RemotePathResolver` — port of `Get-NonBlankLines` + `Resolve-RemotePath`

**Files:**
- Create: `dotnet/ClipBridge.Core/RemotePathResolver.cs`
- Test: `dotnet/ClipBridge.Core.Tests/RemotePathResolverTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class RemotePathResolverTests
{
    [Fact]
    public void Returns_the_whole_path_for_one_line_not_its_first_character()
    {
        var r = RemotePathResolver.Resolve("/home/vollmin/.clipbridge/20260819-032734.png\n");
        Assert.Equal("/home/vollmin/.clipbridge/20260819-032734.png", r.Path);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Survives_crlf()
    {
        var r = RemotePathResolver.Resolve("/home/vollmin/.clipbridge/20260819-032734.png\r\n");
        Assert.Equal("/home/vollmin/.clipbridge/20260819-032734.png", r.Path);
    }

    [Fact]
    public void Handles_output_with_no_trailing_newline()
    {
        var r = RemotePathResolver.Resolve("/home/vollmin/.clipbridge/20260819-032734.png");
        Assert.Equal("/home/vollmin/.clipbridge/20260819-032734.png", r.Path);
    }

    [Fact]
    public void Rejects_two_real_lines_and_says_how_many_it_saw()
    {
        var r = RemotePathResolver.Resolve("/home/vollmin/.clipbridge/a.png\n/home/vollmin/.clipbridge/b.png\n");
        Assert.Null(r.Path);
        Assert.Contains("2 non-blank line", r.Reason);
    }

    [Fact]
    public void Rejects_a_relative_path()
    {
        Assert.Null(RemotePathResolver.Resolve("clipbridge/x.png\n").Path);
    }

    [Fact]
    public void Rejects_empty_output()
    {
        Assert.Null(RemotePathResolver.Resolve("").Path);
    }

    [Fact]
    public void Non_blank_lines_returns_every_line_not_just_the_first()
    {
        var lines = RemotePathResolver.NonBlankLines("/home/vollmin/.clipbridge/x.png\n/another/line\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal("/another/line", lines[1]);
    }

    [Fact]
    public void Non_blank_lines_drops_blank_lines()
    {
        Assert.Single(RemotePathResolver.NonBlankLines("\n/home/vollmin/.clipbridge/x.png\n\n"));
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter RemotePathResolverTests
```

Expected: build error — `RemotePathResolver` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
namespace ClipBridge.Core;

public static class RemotePathResolver
{
    // C# has no equivalent of the PowerShell bug this replaces (a
    // single-element array unrolling to the bare string on function
    // return, so .Count reported 1 and [0] indexed the first CHARACTER).
    // IReadOnlyList<string> here never silently unrolls, so that entire
    // class of bug does not reproduce in C#.
    public static IReadOnlyList<string> NonBlankLines(string text) =>
        text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    public static ResolvedPath Resolve(string stdOut)
    {
        var lines = NonBlankLines(stdOut);
        if (lines.Count != 1)
        {
            return ResolvedPath.Failure(
                $"receiver returned {lines.Count} non-blank line(s), expected exactly 1: '{stdOut}'");
        }
        if (!PathValidator.IsValid(lines[0]))
        {
            return ResolvedPath.Failure($"unusable path from receiver: '{stdOut}'");
        }
        return ResolvedPath.Ok(lines[0]);
    }
}

public sealed record ResolvedPath(string? Path, string? Reason)
{
    public static ResolvedPath Ok(string path) => new(path, null);
    public static ResolvedPath Failure(string reason) => new(null, reason);
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter RemotePathResolverTests
```

Expected: `Passed! - Failed: 0, Passed: 8`.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/RemotePathResolver.cs dotnet/ClipBridge.Core.Tests/RemotePathResolverTests.cs
git commit -m "feat(dotnet): port Get-NonBlankLines and Resolve-RemotePath to RemotePathResolver"
```

---

## Task 4: `SshArgumentBuilder` — port of `Get-SshInvocation`

**Files:**
- Create: `dotnet/ClipBridge.Core/SshArgumentBuilder.cs`
- Test: `dotnet/ClipBridge.Core.Tests/SshArgumentBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class SshArgumentBuilderTests
{
    [Fact]
    public void Ssh_transport_uses_ssh_exe_with_no_prefix_ending_in_the_remote_command()
    {
        var inv = SshArgumentBuilder.Build("ssh", "clipbridge");
        Assert.Equal("ssh.exe", inv.Exe);
        Assert.Equal(new[] { "clipbridge", "/home/vollmin/.local/bin/clipbridge-recv" }, inv.Arguments);
    }

    [Fact]
    public void Wsl_transport_prefixes_with_dash_e_ssh_ending_in_the_remote_command()
    {
        var inv = SshArgumentBuilder.Build("wsl", "clipbridge");
        Assert.Equal("wsl.exe", inv.Exe);
        Assert.Equal(new[] { "-e", "ssh", "clipbridge", "/home/vollmin/.local/bin/clipbridge-recv" }, inv.Arguments);
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("wsl")]
    public void Custom_remote_command_is_appended_last_for_both_transports(string transport)
    {
        var inv = SshArgumentBuilder.Build(transport, "clipbridge", "/opt/custom/clipbridge-recv");
        Assert.Equal("/opt/custom/clipbridge-recv", inv.Arguments[^1]);
    }

    [Fact]
    public void Unknown_transport_throws()
    {
        Assert.Throws<ArgumentException>(() => SshArgumentBuilder.Build("carrier-pigeon", "clipbridge"));
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter SshArgumentBuilderTests
```

Expected: build error — `SshArgumentBuilder` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
namespace ClipBridge.Core;

public static class SshArgumentBuilder
{
    // Absolute path: a non-interactive ssh command does not reliably have
    // ~/.local/bin on PATH even though an interactive login shell does.
    public const string DefaultRemoteCommand = "/home/vollmin/.local/bin/clipbridge-recv";

    public static SshInvocation Build(string transport, string sshHost, string remoteCommand = DefaultRemoteCommand) =>
        transport switch
        {
            "wsl" => new SshInvocation("wsl.exe", new[] { "-e", "ssh", sshHost, remoteCommand }),
            "ssh" => new SshInvocation("ssh.exe", new[] { sshHost, remoteCommand }),
            _ => throw new ArgumentException($"unknown transport '{transport}' - expected ssh or wsl", nameof(transport)),
        };
}

public sealed record SshInvocation(string Exe, IReadOnlyList<string> Arguments);
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter SshArgumentBuilderTests
```

Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/SshArgumentBuilder.cs dotnet/ClipBridge.Core.Tests/SshArgumentBuilderTests.cs
git commit -m "feat(dotnet): port Get-SshInvocation to SshArgumentBuilder"
```

---

## Task 5: `ClipbridgeConfig` — port of `Get-ClipbridgeConfig`

**Files:**
- Create: `dotnet/ClipBridge.Core/ClipbridgeConfig.cs`
- Test: `dotnet/ClipBridge.Core.Tests/ClipbridgeConfigReaderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClipbridgeConfigReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Reads_sshhost_and_transport_from_config_json()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "sshHost": "clipbridge", "transport": "ssh" }""");
        var cfg = ClipbridgeConfigReader.Load(_dir);
        Assert.Equal("clipbridge", cfg.SshHost);
        Assert.Equal("ssh", cfg.Transport);
    }

    [Fact]
    public void Throws_a_named_error_when_config_json_is_missing()
    {
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Throws_when_transport_is_not_ssh_or_wsl()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "sshHost": "clipbridge", "transport": "carrier-pigeon" }""");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains("carrier-pigeon", ex.Message);
    }

    [Fact]
    public void Throws_when_sshhost_is_blank()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "sshHost": "", "transport": "ssh" }""");
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains("no sshHost", ex.Message);
    }

    [Fact]
    public void Names_the_config_path_when_it_is_not_valid_json()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, """{ "sshHost": "clipbridge", """);
        var ex = Assert.Throws<ClipbridgeConfigException>(() => ClipbridgeConfigReader.Load(_dir));
        Assert.Contains(cfgPath, ex.Message);
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter ClipbridgeConfigReaderTests
```

Expected: build error — `ClipbridgeConfigReader` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
using System.Text.Json;

namespace ClipBridge.Core;

public sealed record ClipbridgeConfig(string SshHost, string Transport);

public sealed class ClipbridgeConfigException : Exception
{
    public ClipbridgeConfigException(string message) : base(message) { }
}

// Unlike v1's PowerShell parameter-default gotcha (CLAUDE.md Gotcha #2 - a
// default expression touching $env:LOCALAPPDATA throws at parameter-bind
// time on Linux, before the script body or an early return ever runs),
// C# has no equivalent eager-bind-time evaluation trap: configDir is an
// ordinary method argument, resolved by the caller (App/Program.cs) at the
// point of the call, not as a class-level default. That whole class of bug
// does not reproduce here.
public static class ClipbridgeConfigReader
{
    public static ClipbridgeConfig Load(string configDir)
    {
        var path = Path.Combine(configDir, "config.json");
        if (!File.Exists(path))
        {
            throw new ClipbridgeConfigException($"clipbridge config not found at {path} - run clipbridge.exe --install");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ClipbridgeConfigException($"clipbridge config at {path} is not valid JSON - {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var transport = root.TryGetProperty("transport", out var t) ? t.GetString() : null;
            if (transport is not ("ssh" or "wsl"))
            {
                throw new ClipbridgeConfigException($"clipbridge config has an unknown transport '{transport}' - expected ssh or wsl");
            }
            var sshHost = root.TryGetProperty("sshHost", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(sshHost))
            {
                throw new ClipbridgeConfigException("clipbridge config has no sshHost");
            }
            return new ClipbridgeConfig(sshHost, transport);
        }
    }
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter ClipbridgeConfigReaderTests
```

Expected: `Passed! - Failed: 0, Passed: 5`. Note: `JsonDocument.Parse` is the low-level DOM parser, not the reflection-based `JsonSerializer.Deserialize<T>` — it needs no source-generated `JsonSerializerContext` to stay AOT-safe, which matters because this project publishes with `PublishAot=true` (Task 21).

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/ClipbridgeConfig.cs dotnet/ClipBridge.Core.Tests/ClipbridgeConfigReaderTests.cs
git commit -m "feat(dotnet): port Get-ClipbridgeConfig to ClipbridgeConfigReader"
```

---

## Task 6: `ClipbridgeLogger` — port of `Write-ClipbridgeLog`

**Files:**
- Create: `dotnet/ClipBridge.Core/ClipbridgeLogger.cs`
- Test: `dotnet/ClipBridge.Core.Tests/ClipbridgeLoggerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClipbridgeLoggerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Appends_a_timestamped_line()
    {
        ClipbridgeLogger.Append(_dir, "ssh exploded");
        var line = File.ReadAllLines(Path.Combine(_dir, "clipbridge.log")).Last();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", line);
        Assert.Contains("ssh exploded", line);
    }

    [Fact]
    public void Drops_lines_older_than_7_days_and_keeps_fresh_ones()
    {
        var logPath = Path.Combine(_dir, "clipbridge.log");
        var stale = DateTime.Now.AddDays(-10).ToString("yyyy-MM-ddTHH:mm:ss");
        var fresh = DateTime.Now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        File.WriteAllLines(logPath, new[] { $"{stale}  old event", $"{fresh}  recent event" });

        ClipbridgeLogger.Append(_dir, "new event");

        var text = string.Join("\n", File.ReadAllLines(logPath));
        Assert.DoesNotContain("old event", text);
        Assert.Contains("recent event", text);
        Assert.Contains("new event", text);
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter ClipbridgeLoggerTests
```

Expected: build error — `ClipbridgeLogger` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
namespace ClipBridge.Core;

public static class ClipbridgeLogger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const string StampFormat = "yyyy-MM-ddTHH:mm:ss";

    // Capped at 7 days, same rule as the images (design spec). Each line
    // starts with a fixed-width, sortable stamp, so a plain ordinal string
    // compare against a cutoff stamp reproduces chronological order - a
    // filter, not a parse, which matters because this runs on every hotkey
    // press.
    public static void Append(string configDir, string message, DateTime? now = null)
    {
        var effectiveNow = now ?? DateTime.Now;
        Directory.CreateDirectory(configDir);
        var logPath = Path.Combine(configDir, "clipbridge.log");
        var line = $"{effectiveNow.ToString(StampFormat)}  {message}";
        var cutoff = effectiveNow.Subtract(Retention).ToString(StampFormat);

        var kept = new List<string>();
        if (File.Exists(logPath))
        {
            foreach (var existing in File.ReadAllLines(logPath))
            {
                if (existing.Length >= 19 && string.CompareOrdinal(existing[..19], cutoff) >= 0)
                {
                    kept.Add(existing);
                }
            }
        }
        kept.Add(line);
        File.WriteAllLines(logPath, kept);
    }
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter ClipbridgeLoggerTests
```

Expected: `Passed! - Failed: 0, Passed: 2`.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/ClipbridgeLogger.cs dotnet/ClipBridge.Core.Tests/ClipbridgeLoggerTests.cs
git commit -m "feat(dotnet): port Write-ClipbridgeLog to ClipbridgeLogger"
```

---

## Task 7: `DibToPngConverter` + `ClipboardImageExtractor` — the DIB fallback, Linux-testable

**Files:**
- Create: `dotnet/ClipBridge.Core/DibToPngConverter.cs`
- Create: `dotnet/ClipBridge.Core/ClipboardImageExtractor.cs`
- Test: `dotnet/ClipBridge.Core.Tests/DibFixtures.cs`
- Test: `dotnet/ClipBridge.Core.Tests/DibToPngConverterTests.cs`
- Test: `dotnet/ClipBridge.Core.Tests/ClipboardImageExtractorTests.cs`

v1 could never test this branch — it's gated behind `-Skip:($env:OS -ne 'Windows_NT')` in `Send-Clip.Tests.ps1` and only runs in `windows-latest` CI. Splitting the byte-transform (CF_DIB → BMP header synth → PNG) out of the Win32 extraction means it's pure bytes with zero Win32 dependency, so it moves to Core and gets real coverage on Linux — coverage v1 structurally could not have.

- [ ] **Step 1: Write the test fixture and failing tests**

```csharp
// DibFixtures.cs
namespace ClipBridge.Core.Tests;

internal static class DibFixtures
{
    // 24bpp BI_RGB, no palette, rows padded to a 4-byte boundary, bottom-up -
    // the standard Windows DIB layout, which is also what ImageSharp's BMP
    // decoder expects once a BITMAPFILEHEADER is prepended.
    public static byte[] Build(int width, int height, byte r, byte g, byte b)
    {
        int rowSize = ((width * 3 + 3) / 4) * 4;
        int pixelDataSize = rowSize * height;
        var header = new byte[40];
        BitConverter.GetBytes(40u).CopyTo(header, 0);          // biSize
        BitConverter.GetBytes(width).CopyTo(header, 4);         // biWidth
        BitConverter.GetBytes(height).CopyTo(header, 8);        // biHeight (positive = bottom-up)
        BitConverter.GetBytes((ushort)1).CopyTo(header, 12);    // biPlanes
        BitConverter.GetBytes((ushort)24).CopyTo(header, 14);   // biBitCount
        BitConverter.GetBytes(0u).CopyTo(header, 16);           // biCompression = BI_RGB
        BitConverter.GetBytes((uint)pixelDataSize).CopyTo(header, 20);

        var pixels = new byte[pixelDataSize];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int i = row * rowSize + col * 3;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; // BGR order
            }
        }
        return header.Concat(pixels).ToArray();
    }
}
```

```csharp
// DibToPngConverterTests.cs
using ClipBridge.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ClipBridge.Core.Tests;

public class DibToPngConverterTests
{
    [Fact]
    public void Converts_a_2x2_dib_to_a_valid_png()
    {
        var dib = DibFixtures.Build(2, 2, 200, 100, 50);
        var png = DibToPngConverter.Convert(dib);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        using var image = Image.Load<Rgba32>(png);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        var pixel = image[0, 0];
        Assert.Equal(200, pixel.R);
        Assert.Equal(100, pixel.G);
        Assert.Equal(50, pixel.B);
    }

    [Fact]
    public void Rejects_a_payload_too_small_to_hold_a_header()
    {
        Assert.Throws<InvalidDataException>(() => DibToPngConverter.Convert(new byte[10]));
    }
}
```

```csharp
// ClipboardImageExtractorTests.cs
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClipboardImageExtractorTests
{
    [Fact]
    public void Prefers_png_over_dib_when_both_present()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var dib = DibFixtures.Build(1, 1, 10, 20, 30);
        Assert.Same(png, ClipboardImageExtractor.Resolve(png, dib));
    }

    [Fact]
    public void Falls_back_to_dib_when_no_png()
    {
        var dib = DibFixtures.Build(1, 1, 10, 20, 30);
        var result = ClipboardImageExtractor.Resolve(null, dib);
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, result![..8]);
    }

    [Fact]
    public void Returns_null_when_neither_present()
    {
        Assert.Null(ClipboardImageExtractor.Resolve(null, null));
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter "DibToPngConverterTests|ClipboardImageExtractorTests"
```

Expected: build error — `DibToPngConverter` and `ClipboardImageExtractor` do not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
// DibToPngConverter.cs
namespace ClipBridge.Core;

// Converts a raw CF_DIB clipboard payload (BITMAPINFOHEADER + optional
// color table + pixel data, no BITMAPFILEHEADER) into PNG bytes. Windows
// hands us the DIB without a file header; ImageSharp's BMP decoder requires
// one, so this synthesizes a minimal 14-byte BITMAPFILEHEADER and lets
// ImageSharp do the rest. Pure byte transform, no Win32 API involved.
public static class DibToPngConverter
{
    public static byte[] Convert(byte[] dibBytes)
    {
        if (dibBytes.Length < 40)
            throw new InvalidDataException($"DIB payload too small to hold a BITMAPINFOHEADER: {dibBytes.Length} bytes");

        uint biSize = BitConverter.ToUInt32(dibBytes, 0);
        ushort biBitCount = BitConverter.ToUInt16(dibBytes, 14);
        uint biClrUsed = BitConverter.ToUInt32(dibBytes, 32);

        int paletteEntries = biClrUsed != 0 ? (int)biClrUsed : (biBitCount <= 8 ? 1 << biBitCount : 0);
        int paletteBytes = paletteEntries * 4;

        uint offBits = (uint)(14 + biSize + paletteBytes);
        uint fileSize = (uint)(14 + dibBytes.Length);

        using var bmp = new MemoryStream();
        bmp.WriteByte((byte)'B');
        bmp.WriteByte((byte)'M');
        bmp.Write(BitConverter.GetBytes(fileSize));
        bmp.Write(BitConverter.GetBytes((ushort)0)); // reserved1
        bmp.Write(BitConverter.GetBytes((ushort)0)); // reserved2
        bmp.Write(BitConverter.GetBytes(offBits));
        bmp.Write(dibBytes);
        bmp.Position = 0;

        using var image = SixLabors.ImageSharp.Image.Load(bmp);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        return png.ToArray();
    }
}
```

```csharp
// ClipboardImageExtractor.cs
namespace ClipBridge.Core;

// Picks the lossless PNG clipboard stream when present, falls back to
// converting the DIB bitmap otherwise. Mirrors Save-ClipboardPng's
// preference order in Send-Clip.ps1, minus the Win32 extraction itself,
// which lives in ClipBridge.Win32.Win32Clipboard (Task 13).
public static class ClipboardImageExtractor
{
    public static byte[]? Resolve(byte[]? pngBytes, byte[]? dibBytes)
    {
        if (pngBytes is { Length: > 0 }) return pngBytes;
        if (dibBytes is { Length: > 0 }) return DibToPngConverter.Convert(dibBytes);
        return null;
    }
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter "DibToPngConverterTests|ClipboardImageExtractorTests"
```

Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/DibToPngConverter.cs dotnet/ClipBridge.Core/ClipboardImageExtractor.cs \
  dotnet/ClipBridge.Core.Tests/DibFixtures.cs dotnet/ClipBridge.Core.Tests/DibToPngConverterTests.cs \
  dotnet/ClipBridge.Core.Tests/ClipboardImageExtractorTests.cs
git commit -m "feat(dotnet): DIB-to-PNG fallback encoder, testable on Linux via ImageSharp"
```

---

## Task 8: `SshConfigBlockBuilder` / `SshConfigInspector` / `ClipbridgePaths` / `ClipbridgeConfigFactory` — port of `Install-Clipbridge.ps1`'s pure functions

**Files:**
- Create: `dotnet/ClipBridge.Core/SshConfigBlockBuilder.cs`
- Create: `dotnet/ClipBridge.Core/ClipbridgeConfigFactory.cs`
- Test: `dotnet/ClipBridge.Core.Tests/SshConfigBlockBuilderTests.cs`
- Test: `dotnet/ClipBridge.Core.Tests/ClipbridgeConfigFactoryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// SshConfigBlockBuilderTests.cs
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class SshConfigBlockBuilderTests
{
    private static readonly string Block = SshConfigBlockBuilder.Build(
        "clipbridge", "devsbx01", "vollmin", "/home/x/.ssh/devsbx01_id_ed25519.pub");

    [Fact]
    public void Includes_identities_only_yes()
    {
        Assert.Matches(@"IdentitiesOnly\s+yes", Block);
    }

    [Fact]
    public void Includes_forward_agent_no()
    {
        Assert.Matches(@"ForwardAgent\s+no", Block);
    }

    [Fact]
    public void Names_the_host_alias()
    {
        Assert.Matches(@"Host\s+clipbridge", Block);
    }

    [Fact]
    public void Sets_hostname_to_the_real_target()
    {
        Assert.Matches(@"HostName\s+devsbx01", Block);
    }

    [Fact]
    public void Sets_user()
    {
        Assert.Matches(@"User\s+vollmin", Block);
    }

    [Fact]
    public void Points_identity_file_at_the_given_key_path()
    {
        Assert.Contains("IdentityFile /home/x/.ssh/devsbx01_id_ed25519.pub", Block);
    }
}

public class SshConfigInspectorTests
{
    [Fact]
    public void Returns_false_on_an_empty_config()
    {
        Assert.False(SshConfigInspector.HasHostBlock("", "clipbridge"));
    }

    [Fact]
    public void Returns_false_when_the_alias_is_absent()
    {
        Assert.False(SshConfigInspector.HasHostBlock("Host github.com\n    User git\n", "clipbridge"));
    }

    [Fact]
    public void Returns_true_when_the_exact_host_line_is_present()
    {
        var cfg = "Host github.com\n    User git\n\nHost clipbridge\n    HostName devsbx01\n";
        Assert.True(SshConfigInspector.HasHostBlock(cfg, "clipbridge"));
    }

    [Fact]
    public void Round_trips_with_a_block_generated_by_the_builder()
    {
        var block = SshConfigBlockBuilder.Build("clipbridge", "devsbx01", "vollmin", "/home/x/.ssh/devsbx01_id_ed25519.pub");
        Assert.True(SshConfigInspector.HasHostBlock(block, "clipbridge"));
    }

    [Fact]
    public void Tolerates_leading_whitespace_before_host()
    {
        Assert.True(SshConfigInspector.HasHostBlock("  Host clipbridge\n", "clipbridge"));
    }

    [Fact]
    public void Does_not_false_positive_on_an_alias_that_merely_starts_the_same()
    {
        Assert.False(SshConfigInspector.HasHostBlock("Host clipbridge-laptop\n    User someone\n", "clipbridge"));
    }
}

public class ClipbridgePathsTests
{
    [Fact]
    public void Joins_ssh_and_config_paths_under_the_given_directories()
    {
        var p = ClipbridgePaths.From("/home/x/.ssh", "/home/x/.config/clipbridge");
        Assert.Equal(Path.Combine("/home/x/.ssh", "config"), p.SshConfigPath);
        Assert.Equal(Path.Combine("/home/x/.config/clipbridge", "config.json"), p.ConfigJsonPath);
    }
}
```

```csharp
// ClipbridgeConfigFactoryTests.cs
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClipbridgeConfigFactoryTests
{
    [Fact]
    public void Sets_sshhost_to_the_alias_not_the_real_hostname()
    {
        Assert.Equal("clipbridge", ClipbridgeConfigFactory.Create("clipbridge", "ssh").SshHost);
    }

    [Fact]
    public void Carries_the_transport_through()
    {
        Assert.Equal("wsl", ClipbridgeConfigFactory.Create("clipbridge", "wsl").Transport);
    }

    [Fact]
    public void Rejects_a_transport_outside_ssh_wsl()
    {
        Assert.Throws<ArgumentException>(() => ClipbridgeConfigFactory.Create("clipbridge", "carrier-pigeon"));
    }

    [Fact]
    public void Round_trips_through_json_with_the_shape_the_reader_expects()
    {
        var cfg = ClipbridgeConfigFactory.Create("clipbridge", "ssh");
        var json = ClipbridgeConfigWriter.ToJson(cfg);

        var dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), json);
            var roundTripped = ClipbridgeConfigReader.Load(dir);
            Assert.Equal("clipbridge", roundTripped.SshHost);
            Assert.Equal("ssh", roundTripped.Transport);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter "SshConfigBlockBuilderTests|SshConfigInspectorTests|ClipbridgePathsTests|ClipbridgeConfigFactoryTests"
```

Expected: build error — `SshConfigBlockBuilder`, `SshConfigInspector`, `ClipbridgePaths`, `ClipbridgeConfigFactory`, `ClipbridgeConfigWriter` do not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
// SshConfigBlockBuilder.cs
using System.Text.RegularExpressions;

namespace ClipBridge.Core;

public static class SshConfigBlockBuilder
{
    // No dedicated, restricted clipbridge key any more (see design spec,
    // "No new SSH key" - a restrict,command= key put in the shared
    // 1Password agent locked the user out of his own machine via WSL/mosh).
    // Authenticates with the user's existing devsbx01 key; IdentitiesOnly
    // pins it out of ~27 agent keys; ForwardAgent no because clipbridge
    // never authenticates onward from devsbx01.
    public static string Build(string hostAlias, string targetHost, string targetUser, string identityFile) =>
        $"""

        Host {hostAlias}
            HostName {targetHost}
            User {targetUser}
            IdentityFile {identityFile}
            IdentitiesOnly yes
            ForwardAgent no

        """;
}

public static class SshConfigInspector
{
    // Matched per-line, anchored on both ends, case-insensitive: 'Host
    // clipbridge' matches but 'Host clipbridge-laptop' must not, or a
    // second run would add a second, shadowing block next to one it wrongly
    // believes is already present.
    public static bool HasHostBlock(string existingConfig, string hostAlias)
    {
        var pattern = new Regex(@"^\s*Host\s+" + Regex.Escape(hostAlias) + @"\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return existingConfig.Split(["\r\n", "\n"], StringSplitOptions.None).Any(line => pattern.IsMatch(line));
    }
}

public sealed record ClipbridgePaths(string SshConfigPath, string ConfigJsonPath)
{
    public static ClipbridgePaths From(string sshDir, string configDir) =>
        new(Path.Combine(sshDir, "config"), Path.Combine(configDir, "config.json"));
}
```

```csharp
// ClipbridgeConfigFactory.cs
namespace ClipBridge.Core;

public static class ClipbridgeConfigFactory
{
    // sshHost is deliberately the ssh config ALIAS (e.g. 'clipbridge'), not
    // the real target hostname - the transport passes this straight to
    // ssh.exe / wsl.exe -e ssh, which resolves it through ~/.ssh/config,
    // picking up HostName/User/IdentityFile/IdentitiesOnly from the block
    // above. Passing the real hostname here would bypass that block
    // entirely and lose IdentitiesOnly.
    public static ClipbridgeConfig Create(string hostAlias, string transport)
    {
        if (transport is not ("ssh" or "wsl"))
            throw new ArgumentException($"unknown transport '{transport}' - expected ssh or wsl", nameof(transport));
        return new ClipbridgeConfig(hostAlias, transport);
    }
}

// Hand-written, not System.Text.Json's reflection-based JsonSerializer:
// this project publishes with PublishAot=true, and a reflection-based
// serializer needs a source-generated JsonSerializerContext to stay
// trim-safe. For a two-field object, hand-writing sidesteps that entirely -
// deliberate, not an oversight.
public static class ClipbridgeConfigWriter
{
    public static string ToJson(ClipbridgeConfig config) =>
        $$"""{"sshHost":"{{JsonEncode(config.SshHost)}}","transport":"{{JsonEncode(config.Transport)}}"}""";

    private static string JsonEncode(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter "SshConfigBlockBuilderTests|SshConfigInspectorTests|ClipbridgePathsTests|ClipbridgeConfigFactoryTests"
```

Expected: `Passed! - Failed: 0, Passed: 17`.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/SshConfigBlockBuilder.cs dotnet/ClipBridge.Core/ClipbridgeConfigFactory.cs \
  dotnet/ClipBridge.Core.Tests/SshConfigBlockBuilderTests.cs dotnet/ClipBridge.Core.Tests/ClipbridgeConfigFactoryTests.cs
git commit -m "feat(dotnet): port New-SshConfigBlock, Test-SshConfigHasHostBlock, Get-ClipbridgePaths, New-ClipbridgeConfigObject"
```

---

## Task 9: `TransportProbeClassifier` — port of `Get-SshProbeOutcome` / `Get-TransportFailureMessage` / `Select-Transport`

**Files:**
- Create: `dotnet/ClipBridge.Core/TransportProbeClassifier.cs`
- Test: `dotnet/ClipBridge.Core.Tests/TransportProbeClassifierTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class TransportProbeClassifierTests
{
    [Fact]
    public void Reports_exe_not_found_when_the_exe_was_not_found()
    {
        Assert.Equal(ProbeOutcome.ExeNotFound, TransportProbeClassifier.Classify(false, -1, ""));
    }

    [Fact]
    public void Reports_authenticated_on_a_clean_exit_0()
    {
        Assert.Equal(ProbeOutcome.Authenticated, TransportProbeClassifier.Classify(true, 0, ""));
    }

    [Fact]
    public void Reports_permission_denied_when_stderr_says_so()
    {
        var outcome = TransportProbeClassifier.Classify(true, 255, "user@devsbx01: Permission denied (publickey).");
        Assert.Equal(ProbeOutcome.PermissionDenied, outcome);
    }

    [Fact]
    public void Reports_timeout_on_a_connection_timeout()
    {
        var outcome = TransportProbeClassifier.Classify(true, 255, "ssh: connect to host devsbx01 port 22: Connection timed out");
        Assert.Equal(ProbeOutcome.Timeout, outcome);
    }

    [Fact]
    public void Reports_other_failure_for_anything_else()
    {
        var outcome = TransportProbeClassifier.Classify(true, 255, "ssh: Could not resolve hostname devsbx01: Name or service not known");
        Assert.Equal(ProbeOutcome.OtherFailure, outcome);
    }

    [Fact]
    public void Names_the_1password_agent_when_both_transports_are_denied()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.PermissionDenied, ProbeOutcome.PermissionDenied, "devsbx01");
        Assert.Contains("1Password", msg);
        Assert.Contains("ssh.exe and wsl.exe", msg);
    }

    [Fact]
    public void Names_the_1password_agent_and_which_transport_when_only_one_is_denied()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.PermissionDenied, ProbeOutcome.ExeNotFound, "devsbx01");
        Assert.Contains("1Password", msg);
        Assert.StartsWith("ssh.exe", msg);
    }

    [Fact]
    public void Names_wsl_exe_e_ssh_specifically_when_that_is_the_one_denied()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.ExeNotFound, ProbeOutcome.PermissionDenied, "devsbx01");
        Assert.Contains("wsl.exe -e ssh", msg);
    }

    [Fact]
    public void Reports_a_timeout_distinctly_without_blaming_1password()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.Timeout, ProbeOutcome.Timeout, "devsbx01");
        Assert.Contains("timed out", msg);
        Assert.DoesNotContain("1Password", msg);
    }

    [Fact]
    public void Reports_both_executables_missing_distinctly()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.ExeNotFound, ProbeOutcome.ExeNotFound, "devsbx01");
        Assert.Contains("PATH", msg);
        Assert.DoesNotContain("1Password", msg);
    }

    [Fact]
    public void Falls_back_to_a_generic_message_for_an_unmatched_combination()
    {
        var msg = TransportProbeClassifier.TransportFailureMessage(ProbeOutcome.OtherFailure, ProbeOutcome.OtherFailure, "devsbx01");
        Assert.Contains("OtherFailure", msg);
        Assert.Contains("devsbx01", msg);
    }

    [Fact]
    public void Picks_ssh_when_ssh_authenticated()
    {
        Assert.Equal("ssh", TransportProbeClassifier.SelectTransport(ProbeOutcome.Authenticated, ProbeOutcome.NotProbed, "devsbx01"));
    }

    [Fact]
    public void Picks_wsl_when_ssh_failed_but_wsl_authenticated()
    {
        Assert.Equal("wsl", TransportProbeClassifier.SelectTransport(ProbeOutcome.PermissionDenied, ProbeOutcome.Authenticated, "devsbx01"));
    }

    [Fact]
    public void Prefers_ssh_over_wsl_when_both_authenticate()
    {
        Assert.Equal("ssh", TransportProbeClassifier.SelectTransport(ProbeOutcome.Authenticated, ProbeOutcome.Authenticated, "devsbx01"));
    }

    [Fact]
    public void Throws_the_locked_agent_message_when_both_are_denied()
    {
        var ex = Assert.Throws<ClipbridgeConfigException>(
            () => TransportProbeClassifier.SelectTransport(ProbeOutcome.PermissionDenied, ProbeOutcome.PermissionDenied, "devsbx01"));
        Assert.Contains("1Password", ex.Message);
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter TransportProbeClassifierTests
```

Expected: build error — `TransportProbeClassifier` and `ProbeOutcome` do not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
using System.Text.RegularExpressions;

namespace ClipBridge.Core;

public enum ProbeOutcome { ExeNotFound, Authenticated, PermissionDenied, Timeout, OtherFailure, NotProbed }

public static class TransportProbeClassifier
{
    public static ProbeOutcome Classify(bool exeFound, int exitCode, string stdErr)
    {
        if (!exeFound) return ProbeOutcome.ExeNotFound;
        if (exitCode == 0) return ProbeOutcome.Authenticated;
        if (stdErr.Contains("Permission denied")) return ProbeOutcome.PermissionDenied;
        if (Regex.IsMatch(stdErr, "timed out|timeout", RegexOptions.IgnoreCase)) return ProbeOutcome.Timeout;
        return ProbeOutcome.OtherFailure;
    }

    // Ordered so the most actionable, most likely diagnosis wins.
    // "Permission denied" on this laptop's known setup (keys in the
    // 1Password agent, not on disk) is overwhelmingly a locked/stopped
    // agent, not a misconfigured server.
    public static string TransportFailureMessage(ProbeOutcome sshOutcome, ProbeOutcome wslOutcome, string targetHost)
    {
        const string agentHint =
            "This almost always means the 1Password SSH agent is locked or not running - " +
            "it offers every key it holds on unlock, so a locked or stopped agent offers " +
            "none and the server correctly reports no valid key. Unlock 1Password (or " +
            "start it) so it can serve your key, then re-run this script. If the key " +
            "genuinely is not authorized, confirm the clipbridge public key is present in ";

        if (sshOutcome == ProbeOutcome.PermissionDenied && wslOutcome == ProbeOutcome.PermissionDenied)
            return $"Both ssh.exe and wsl.exe -e ssh reached {targetHost} and were told 'Permission denied (publickey)'. {agentHint}~/.ssh/authorized_keys on {targetHost}.";

        if (sshOutcome == ProbeOutcome.PermissionDenied || wslOutcome == ProbeOutcome.PermissionDenied)
        {
            var which = sshOutcome == ProbeOutcome.PermissionDenied ? "ssh.exe" : "wsl.exe -e ssh";
            return $"{which} reached {targetHost} and was told 'Permission denied (publickey)'. {agentHint}~/.ssh/authorized_keys on {targetHost}.";
        }

        if (sshOutcome == ProbeOutcome.Timeout || wslOutcome == ProbeOutcome.Timeout)
            return $"Connection to {targetHost} timed out - the box may be unreachable, powered off, or on a different network than this laptop. This is a connectivity problem, not an authentication one. Fix connectivity to {targetHost} first, then re-run.";

        if (sshOutcome == ProbeOutcome.ExeNotFound && wslOutcome == ProbeOutcome.ExeNotFound)
            return "Neither ssh.exe nor wsl.exe was found on PATH. Install the OpenSSH client (Settings > Apps > Optional Features > OpenSSH Client) or WSL, then re-run.";

        return $"No ssh client authenticated to {targetHost}. ssh.exe: {sshOutcome}, wsl.exe -e ssh: {wslOutcome}. Fix ssh first, then re-run.";
    }

    public static string SelectTransport(ProbeOutcome sshOutcome, ProbeOutcome wslOutcome, string targetHost)
    {
        if (sshOutcome == ProbeOutcome.Authenticated) return "ssh";
        if (wslOutcome == ProbeOutcome.Authenticated) return "wsl";
        throw new ClipbridgeConfigException(TransportFailureMessage(sshOutcome, wslOutcome, targetHost));
    }
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter TransportProbeClassifierTests
```

Expected: `Passed! - Failed: 0, Passed: 15`.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/TransportProbeClassifier.cs dotnet/ClipBridge.Core.Tests/TransportProbeClassifierTests.cs
git commit -m "feat(dotnet): port Get-SshProbeOutcome, Get-TransportFailureMessage, Select-Transport"
```

---

## Task 10: Interfaces, result types, and `HotkeyDecision`

The four Win32-facing interfaces from the design spec's architecture diagram, the two process/result record types `PasteOrchestrator` needs, and the pure 3-step swallow decision the keyboard hook delegates to (Task 17) — kept in Core so it, too, is Linux-testable, even though the hook that calls it lives in Win32.

**Files:**
- Create: `dotnet/ClipBridge.Core/Interfaces.cs`
- Create: `dotnet/ClipBridge.Core/HotkeyDecision.cs`
- Test: `dotnet/ClipBridge.Core.Tests/HotkeyDecisionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class HotkeyDecisionTests
{
    // Mirrors the design spec's 3-step hook callback decision exactly:
    // 1. not Ctrl+V, or foreground process not a configured terminal -> pass through
    // 2. unforced Ctrl+V with no image available -> pass through (the common,
    //    must-stay-instant case)
    // 3. otherwise -> swallow
    [Theory]
    [InlineData(true, true, true, false, true, true)]    // plain Ctrl+V, terminal, image present -> swallow
    [InlineData(true, true, true, false, false, false)]  // plain Ctrl+V, terminal, no image -> pass through
    [InlineData(true, true, true, true, false, true)]    // forced Ctrl+Shift+V, terminal, no image -> still swallow
    [InlineData(true, true, false, false, true, false)]  // not in a terminal -> pass through regardless of image
    [InlineData(false, true, true, false, true, false)]  // Ctrl not down -> pass through
    public void Matches_the_three_step_decision_in_the_design_spec(
        bool ctrlDown, bool isVKeyDown, bool inTerminal, bool forced, bool clipboardHasImage, bool expectedSwallow)
    {
        Assert.Equal(expectedSwallow, HotkeyDecision.ShouldSwallow(ctrlDown, isVKeyDown, inTerminal, forced, clipboardHasImage));
    }

    [Fact]
    public void Terminal_check_is_an_exact_process_name_match()
    {
        var terminals = new[] { "WindowsTerminal" };
        Assert.True(HotkeyDecision.IsForegroundTerminal("WindowsTerminal", terminals));
        Assert.False(HotkeyDecision.IsForegroundTerminal("notepad", terminals));
        Assert.False(HotkeyDecision.IsForegroundTerminal(null, terminals));
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter HotkeyDecisionTests
```

Expected: build error — `HotkeyDecision` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
// Interfaces.cs
namespace ClipBridge.Core;

public interface IClipboard
{
    bool HasImageAvailable();
    byte[]? TryGetPng();
    ClipboardSnapshot Capture();
    void Restore(ClipboardSnapshot snapshot);
    void SetPathText(string path);
}

public readonly record struct ClipboardSnapshot(bool HadData, IReadOnlyDictionary<uint, byte[]> FormatsToData);

public interface IPasteSink
{
    void SendPaste();
}

public interface IForegroundWindow
{
    string? GetForegroundProcessName();
}

public interface ISshTransport
{
    SshExecResult Send(string exePath, IReadOnlyList<string> arguments, string stdinFilePath);
}

public readonly record struct SshExecResult(int ExitCode, string StdOut, string StdErr);

public interface IKeyboardHook : IDisposable
{
    event Action<bool>? PasteRequested; // payload: forced (Ctrl+Shift+V)
    void Start();
    void Rehook();
}

public enum PasteOutcome { Pasted, NoImageNoOp, Failed }

public sealed record PasteAttemptResult(PasteOutcome Outcome, string? RemotePath, string? LogMessage);
```

```csharp
// HotkeyDecision.cs
namespace ClipBridge.Core;

// The pure decision behind the keyboard hook's callback (Task 17). Kept
// separate from the Win32 P/Invoke plumbing so the highest-risk logic in
// this whole design - the swallow/pass-through call - is unit-tested on
// Linux instead of only ever reachable by actually pressing keys on
// Windows.
public static class HotkeyDecision
{
    public static bool IsForegroundTerminal(string? processName, IReadOnlyCollection<string> terminalProcessNames) =>
        processName is not null && terminalProcessNames.Contains(processName);

    // True = swallow the keystroke and hand off to the worker thread.
    // False = call CallNextHookEx immediately, letting the keystroke
    // through untouched.
    public static bool ShouldSwallow(bool ctrlDown, bool isVKeyDown, bool inTerminal, bool forced, bool clipboardHasImage) =>
        ctrlDown && isVKeyDown && inTerminal && (forced || clipboardHasImage);
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter HotkeyDecisionTests
```

Expected: `Passed! - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Core/Interfaces.cs dotnet/ClipBridge.Core/HotkeyDecision.cs \
  dotnet/ClipBridge.Core.Tests/HotkeyDecisionTests.cs
git commit -m "feat(dotnet): interfaces, result types, and the pure hotkey swallow decision"
```

---

## Task 11: `PasteOrchestrator` — the actual logic, fully unit-tested with fakes

This is the port of `Send-Clip.ps1`'s main body plus `clipbridge.ahk`'s `RunClipbridge` — the single most important invariant in the whole design lives here: **every code path except `Pasted` must call `IPasteSink.SendPaste()` exactly once.** Once the keyboard hook (Task 17) has swallowed a keystroke, the process owes the user a paste no matter what fails.

**Files:**
- Create: `dotnet/ClipBridge.Core/PasteOrchestrator.cs`
- Create: `dotnet/ClipBridge.Core.Tests/Fakes.cs`
- Test: `dotnet/ClipBridge.Core.Tests/PasteOrchestratorTests.cs`

- [ ] **Step 1: Write the fakes and the failing tests**

```csharp
// Fakes.cs
using ClipBridge.Core;

namespace ClipBridge.Core.Tests;

internal sealed class FakeClipboard : IClipboard
{
    public byte[]? PngToReturn;
    public Exception? ThrowOnGet;
    public List<string> PathTextsSet = new();
    public int RestoreCallCount;
    public int CaptureCallCount;

    public bool HasImageAvailable() => PngToReturn is not null;

    public byte[]? TryGetPng()
    {
        if (ThrowOnGet is not null) throw ThrowOnGet;
        return PngToReturn;
    }

    public ClipboardSnapshot Capture()
    {
        CaptureCallCount++;
        return new ClipboardSnapshot(true, new Dictionary<uint, byte[]>());
    }

    public void Restore(ClipboardSnapshot snapshot) => RestoreCallCount++;
    public void SetPathText(string path) => PathTextsSet.Add(path);
}

internal sealed class FakePasteSink : IPasteSink
{
    public int PasteCount;
    public void SendPaste() => PasteCount++;
}

internal sealed class FakeSshTransport : ISshTransport
{
    public SshExecResult ResultToReturn;
    public (string Exe, IReadOnlyList<string> Arguments, string StdinFile)? LastCall;

    public SshExecResult Send(string exePath, IReadOnlyList<string> arguments, string stdinFilePath)
    {
        LastCall = (exePath, arguments, stdinFilePath);
        return ResultToReturn;
    }
}
```

```csharp
// PasteOrchestratorTests.cs
using ClipBridge.Core;
using Xunit;

namespace ClipBridge.Core.Tests;

public class PasteOrchestratorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clipbridge-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly byte[] SamplePng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private PasteOrchestrator Build(FakeClipboard clip, FakePasteSink paste, FakeSshTransport ssh) =>
        new(clip, paste, ssh, () => new ClipbridgeConfig("clipbridge", "ssh"), _dir);

    [Fact]
    public void No_image_on_clipboard_pastes_without_calling_ssh()
    {
        var clip = new FakeClipboard { PngToReturn = null };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport();
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: true);

        Assert.Equal(PasteOutcome.NoImageNoOp, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
        Assert.Null(ssh.LastCall);
    }

    [Fact]
    public void Successful_transfer_sets_clipboard_pastes_and_restores()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(0, "/home/vollmin/.clipbridge/20260819-1.png\n", "") };
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Pasted, result.Outcome);
        Assert.Equal("/home/vollmin/.clipbridge/20260819-1.png", result.RemotePath);
        Assert.Equal(new[] { "/home/vollmin/.clipbridge/20260819-1.png" }, clip.PathTextsSet);
        Assert.Equal(1, paste.PasteCount);
        Assert.Equal(1, clip.RestoreCallCount);
    }

    [Theory]
    [InlineData(3, "remote rejected")]
    [InlineData(5, "remote cannot write")]
    [InlineData(255, "transport failure")]
    public void Every_ssh_failure_still_synthesizes_a_paste(int exitCode, string expectedFragment)
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(exitCode, "", "boom") };
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
        Assert.Contains(expectedFragment, result.LogMessage);
        Assert.Empty(clip.PathTextsSet); // never overwrote the clipboard on failure
    }

    [Fact]
    public void An_unusable_returned_path_still_synthesizes_a_paste()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(0, "not/absolute\n", "") };
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void A_clipboard_read_exception_still_synthesizes_a_paste()
    {
        var clip = new FakeClipboard { ThrowOnGet = new IOException("disk full") };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport();
        var orchestrator = Build(clip, paste, ssh);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
    }

    [Fact]
    public void A_broken_config_still_synthesizes_a_paste()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport();
        var orchestrator = new PasteOrchestrator(clip, paste, ssh,
            () => throw new ClipbridgeConfigException("config not found"), _dir);

        var result = orchestrator.Handle(forced: false);

        Assert.Equal(PasteOutcome.Failed, result.Outcome);
        Assert.Equal(1, paste.PasteCount);
        Assert.Null(ssh.LastCall);
    }

    [Fact]
    public void Cleans_up_the_temp_png_regardless_of_outcome()
    {
        var clip = new FakeClipboard { PngToReturn = SamplePng };
        var paste = new FakePasteSink();
        var ssh = new FakeSshTransport { ResultToReturn = new SshExecResult(4, "", "connection refused") };
        var orchestrator = Build(clip, paste, ssh);

        orchestrator.Handle(forced: false);

        Assert.NotNull(ssh.LastCall);
        Assert.False(File.Exists(ssh.LastCall!.Value.StdinFile), "temp PNG must be deleted after use, success or failure");
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter PasteOrchestratorTests
```

Expected: build error — `PasteOrchestrator` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
namespace ClipBridge.Core;

public sealed class PasteOrchestrator
{
    private readonly IClipboard _clipboard;
    private readonly IPasteSink _pasteSink;
    private readonly ISshTransport _sshTransport;
    private readonly Func<ClipbridgeConfig> _configProvider;
    private readonly string _configDir;
    private readonly Action<string> _log;

    public PasteOrchestrator(
        IClipboard clipboard,
        IPasteSink pasteSink,
        ISshTransport sshTransport,
        Func<ClipbridgeConfig> configProvider,
        string configDir,
        Action<string>? log = null)
    {
        _clipboard = clipboard;
        _pasteSink = pasteSink;
        _sshTransport = sshTransport;
        _configProvider = configProvider;
        _configDir = configDir;
        _log = log ?? (msg => ClipbridgeLogger.Append(_configDir, msg));
    }

    // Called from the worker thread AFTER the keyboard hook has already
    // swallowed the keystroke (see ClipBridge.Win32.KeyboardHook, Task 17).
    // Every return path from here on must end in a synthesized paste except
    // NoImageNoOp, which mirrors Send-Clip.ps1 exit 2 - "not treated as an
    // error, nothing is logged" - and is reachable here only via a forced
    // Ctrl+Shift+V against a text clipboard (a plain Ctrl+V never swallows
    // without an image present in the first place, per HotkeyDecision).
    public PasteAttemptResult Handle(bool forced)
    {
        byte[]? png;
        try
        {
            png = _clipboard.TryGetPng();
        }
        catch (Exception ex)
        {
            _log($"cannot read clipboard image - {ex.Message}");
            _pasteSink.SendPaste();
            return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
        }

        if (png is null)
        {
            _pasteSink.SendPaste();
            return new PasteAttemptResult(PasteOutcome.NoImageNoOp, null, null);
        }

        var tmpPng = Path.Combine(Path.GetTempPath(), $"clipbridge-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(tmpPng, png);
        }
        catch (Exception ex)
        {
            _log($"cannot write local temp file {tmpPng} - {ex.Message}");
            _pasteSink.SendPaste();
            return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
        }

        try
        {
            ClipbridgeConfig cfg;
            try
            {
                cfg = _configProvider();
            }
            catch (Exception ex)
            {
                _log($"configuration problem: {ex.Message}");
                _pasteSink.SendPaste();
                return new PasteAttemptResult(PasteOutcome.Failed, null, ex.Message);
            }

            var inv = SshArgumentBuilder.Build(cfg.Transport, cfg.SshHost);
            var result = _sshTransport.Send(inv.Exe, inv.Arguments, tmpPng);

            if (result.ExitCode != 0)
            {
                // 3 and 5 are clipbridge-recv's own codes; anything else is
                // a transport/auth failure. The process-level outcome is
                // the same either way (Failed + a synthesized paste), but
                // naming the bucket in the log keeps debugging pointed at
                // the right machine - see docs/clipbridge-architecture.md's
                // exit-code table for why that distinction was added.
                var reason = result.ExitCode switch
                {
                    3 => $"remote rejected input (clipbridge-recv exit 3): {result.StdErr}",
                    5 => $"remote cannot write (clipbridge-recv exit 5): {result.StdErr}",
                    _ => $"ssh exit {result.ExitCode} (transport failure): {result.StdErr}",
                };
                _log(reason);
                _pasteSink.SendPaste();
                return new PasteAttemptResult(PasteOutcome.Failed, null, reason);
            }

            var resolved = RemotePathResolver.Resolve(result.StdOut);
            if (resolved.Path is null)
            {
                _log(resolved.Reason!);
                _pasteSink.SendPaste();
                return new PasteAttemptResult(PasteOutcome.Failed, null, resolved.Reason);
            }

            var snapshot = _clipboard.Capture();
            _clipboard.SetPathText(resolved.Path);
            _pasteSink.SendPaste();
            _clipboard.Restore(snapshot);
            return new PasteAttemptResult(PasteOutcome.Pasted, resolved.Path, null);
        }
        finally
        {
            try { File.Delete(tmpPng); } catch { /* best effort, matches v1's -ErrorAction SilentlyContinue */ }
        }
    }
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --filter PasteOrchestratorTests
```

Expected: `Passed! - Failed: 0, Passed: 7`.

- [ ] **Step 5: Run the entire Core suite and confirm it's all green before moving to Win32**

```bash
dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj
```

Expected: every test from Tasks 2–11 passes — this is the full Linux-testable core the design spec calls the lever that must be in place before the Win32 shims start.

- [ ] **Step 6: Commit and open the PR for this branch**

```bash
git add dotnet/ClipBridge.Core/PasteOrchestrator.cs dotnet/ClipBridge.Core.Tests/Fakes.cs \
  dotnet/ClipBridge.Core.Tests/PasteOrchestratorTests.cs
git commit -m "feat(dotnet): PasteOrchestrator - every failure path still synthesizes a paste"
git push -u origin feat/dotnet-core
gh pr create --title "feat(dotnet): clipbridge v2 Core - Linux-testable orchestration logic" --body "Tasks 1-11 of docs/superpowers/plans/clipbridge-csharp-implementation.md. No Windows API surface; ClipBridge.Win32 and ClipBridge.App follow in a separate PR."
```

---

## Task 12: `NativeMethods` — every P/Invoke signature in one place

**From here on, code only compiles meaningfully on Windows and only proves anything real when tested on `windows-latest` (Task 22).** `dotnet build`/`dotnet test` still runs on devsbx01 for a fast compile-check loop, but a passing local test run against `net10.0-windows` on Linux only means "compiled and didn't crash the harness" — the P/Invokes underneath are not exercised for real until CI runs on Windows. Each Win32 task below says so explicitly.

**Files:**
- Create: `dotnet/ClipBridge.Win32/NativeMethods.cs`

No test in this task — it is pure P/Invoke declarations, nothing to assert against until something calls them (Tasks 13–18 exercise it).

- [ ] **Step 1: Write the declarations**

```csharp
using System.Runtime.InteropServices;

namespace ClipBridge.Win32;

internal static partial class NativeMethods
{
    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    // LibraryImport (source-generated marshalling) is used everywhere it
    // works cleanly. SetWindowsHookExW takes a managed delegate parameter;
    // LibraryImport's source generator has narrower, less-documented
    // support for delegate marshalling than classic DllImport, so that one
    // call (and its two companions, which share the same hook handle type)
    // stays on DllImport - both are fully AOT-compatible, this is a
    // marshalling-reliability choice, not an AOT-compatibility one. Verify
    // during Task 17 whether LibraryImport also works for it; if so this
    // note can be deleted and the three calls converted.

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Beep(uint dwFreq, uint dwDuration);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // --- kept on DllImport: see note above ---
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    // --- end DllImport block ---

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int VK_CONTROL = 0x11;
    public const int VK_SHIFT = 0x10;
    public const int VK_V = 0x56;
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // --- message loop (Task 20) ---
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int nExitCode);
}
```

- [ ] **Step 2: Confirm it compiles**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet build ClipBridge.Win32/ClipBridge.Win32.csproj
```

Expected: `Build succeeded.` — this only proves the signatures are syntactically valid C#/marshalling attributes, not that any of them work; that's Tasks 13–17.

- [ ] **Step 3: Commit**

```bash
git checkout main && git pull && git checkout -b feat/dotnet-win32
git add dotnet/ClipBridge.Win32/NativeMethods.cs
git commit -m "feat(dotnet): NativeMethods - every Win32 P/Invoke signature clipbridge needs"
```

---

## Task 13: `Win32Clipboard` — port of the Win32 half of `Save-ClipboardPng` + `A_Clipboard` save/restore

**Files:**
- Create: `dotnet/ClipBridge.Win32/Win32Clipboard.cs`
- Test: `dotnet/ClipBridge.Win32.Tests/Win32ClipboardTests.cs`

- [ ] **Step 1: Write the failing tests**

These run for real only on `windows-latest` — `OperatingSystem.IsWindows()` guards every test body so a local run on devsbx01 reports "Passed" trivially without exercising anything. Task 22 wires the real CI job.

```csharp
using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class Win32ClipboardTests
{
    [Fact]
    public void No_image_on_an_empty_clipboard_returns_null()
    {
        if (!OperatingSystem.IsWindows()) return;

        NativeMethods.OpenClipboard(IntPtr.Zero);
        NativeMethods.EmptyClipboard();
        NativeMethods.CloseClipboard();

        var clipboard = new Win32Clipboard();
        Assert.False(clipboard.HasImageAvailable());
        Assert.Null(clipboard.TryGetPng());
    }

    [Fact]
    public void Set_path_text_then_capture_and_restore_round_trips_through_real_win32_calls()
    {
        if (!OperatingSystem.IsWindows()) return;

        var clipboard = new Win32Clipboard();
        clipboard.SetPathText("/home/vollmin/.clipbridge/20260819-1.png");

        // No exception thrown is the assertion here: Capture/Restore call
        // real OpenClipboard/GetClipboardData/GlobalLock/SetClipboardData
        // and this is the first place any of that is exercised for real.
        var snapshot = clipboard.Capture();
        clipboard.Restore(snapshot);
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter Win32ClipboardTests
```

Expected on devsbx01 (Linux): build error — `Win32Clipboard` does not exist yet (the test still won't run for real once it does exist, but it must compile).

- [ ] **Step 3: Minimal implementation**

```csharp
using System.Runtime.InteropServices;
using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class Win32Clipboard : IClipboard
{
    private static readonly uint PngFormat = NativeMethods.RegisterClipboardFormat("PNG");

    public bool HasImageAvailable() =>
        NativeMethods.IsClipboardFormatAvailable(PngFormat) ||
        NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB);

    public byte[]? TryGetPng()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard failed");
        try
        {
            var png = ReadGlobal(PngFormat);
            var dib = png is null ? ReadGlobal(NativeMethods.CF_DIB) : null;
            return ClipboardImageExtractor.Resolve(png, dib);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public ClipboardSnapshot Capture()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard failed");
        Dictionary<uint, byte[]> data;
        try
        {
            data = new Dictionary<uint, byte[]>();
            var png = ReadGlobal(PngFormat);
            if (png is not null) data[PngFormat] = png;
            var dib = ReadGlobal(NativeMethods.CF_DIB);
            if (dib is not null) data[NativeMethods.CF_DIB] = dib;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
        return new ClipboardSnapshot(data.Count > 0, data);
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        if (!snapshot.HadData) return;
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard failed");
        try
        {
            NativeMethods.EmptyClipboard();
            foreach (var (format, bytes) in snapshot.FormatsToData)
            {
                WriteGlobal(format, bytes);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void SetPathText(string path)
    {
        // A trailing space, same as clipbridge.ahk's A_Clipboard := path . " ".
        var bytes = System.Text.Encoding.Unicode.GetBytes(path + " \0");
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard failed");
        try
        {
            NativeMethods.EmptyClipboard();
            WriteGlobal(NativeMethods.CF_UNICODETEXT, bytes);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // Caller must already hold the clipboard open.
    private static byte[]? ReadGlobal(uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format)) return null;
        var hGlobal = NativeMethods.GetClipboardData(format);
        if (hGlobal == IntPtr.Zero) return null;
        var ptr = NativeMethods.GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var size = (int)NativeMethods.GlobalSize(hGlobal);
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }
    }

    // Caller must already hold the clipboard open (and have called
    // EmptyClipboard once per open, not once per format).
    private static void WriteGlobal(uint format, byte[] bytes)
    {
        var hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)bytes.Length);
        var ptr = NativeMethods.GlobalLock(hGlobal);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        NativeMethods.GlobalUnlock(hGlobal);
        NativeMethods.SetClipboardData(format, hGlobal);
    }
}
```

- [ ] **Step 4: Run it locally to confirm it compiles, then in CI (Task 22) for the real signal**

```bash
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter Win32ClipboardTests
```

Expected on devsbx01: `Passed! - Failed: 0, Passed: 2` — trivially, both bodies early-return before touching Win32. This is not the verification; `windows-latest` in Task 22 is.

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Win32/Win32Clipboard.cs dotnet/ClipBridge.Win32.Tests/Win32ClipboardTests.cs
git commit -m "feat(dotnet): Win32Clipboard - raw PNG/DIB extraction, capture/restore, set-text"
```

---

## Task 14: `Win32ForegroundWindow`

**Files:**
- Create: `dotnet/ClipBridge.Win32/Win32ForegroundWindow.cs`
- Test: `dotnet/ClipBridge.Win32.Tests/Win32ForegroundWindowTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class Win32ForegroundWindowTests
{
    [Fact]
    public void Returns_a_non_empty_process_name_when_something_has_focus()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fg = new Win32ForegroundWindow();
        var name = fg.GetForegroundProcessName();

        // On a windows-latest runner some window always has focus (even if
        // it's the test host itself), so this should never be null in CI.
        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter Win32ForegroundWindowTests
```

Expected: build error — `Win32ForegroundWindow` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class Win32ForegroundWindow : IForegroundWindow
{
    public string? GetForegroundProcessName()
    {
        var hWnd = NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Process exited between GetForegroundWindow and GetProcessById.
            return null;
        }
    }
}
```

- [ ] **Step 4: Run it (compiles on Linux; real signal from `windows-latest` in Task 22)**

```bash
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter Win32ForegroundWindowTests
```

Expected on devsbx01: `Passed! - Failed: 0, Passed: 1` (trivially, via the `OperatingSystem.IsWindows()` guard).

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Win32/Win32ForegroundWindow.cs dotnet/ClipBridge.Win32.Tests/Win32ForegroundWindowTests.cs
git commit -m "feat(dotnet): Win32ForegroundWindow - GetForegroundWindow + process name"
```

---

## Task 15: `Win32PasteSink` — port of `Send("^v")`

**Files:**
- Create: `dotnet/ClipBridge.Win32/Win32PasteSink.cs`
- Test: `dotnet/ClipBridge.Win32.Tests/Win32PasteSinkTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class Win32PasteSinkTests
{
    [Fact]
    public void Send_paste_does_not_throw_and_reports_all_four_events_sent()
    {
        if (!OperatingSystem.IsWindows()) return;

        // SendInput's return value (events actually accepted by the input
        // queue) is the only signal available without a foreground window
        // to actually receive the paste - a full receive-side assertion
        // needs a real terminal and is covered by the manual test tier
        // (design spec's "Manual, on the laptop" section), not CI.
        var sink = new Win32PasteSink();
        sink.SendPaste(); // throws InvalidOperationException on a short send
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter Win32PasteSinkTests
```

Expected: build error — `Win32PasteSink` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
using System.Runtime.InteropServices;
using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class Win32PasteSink : IPasteSink
{
    public void SendPaste()
    {
        var inputs = new[]
        {
            KeyDown(NativeMethods.VK_CONTROL),
            KeyDown(NativeMethods.VK_V),
            KeyUp(NativeMethods.VK_V),
            KeyUp(NativeMethods.VK_CONTROL),
        };
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent only {sent}/{inputs.Length} events");
    }

    private static NativeMethods.INPUT KeyDown(ushort vk) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk } },
    };

    private static NativeMethods.INPUT KeyUp(ushort vk) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = NativeMethods.KEYEVENTF_KEYUP } },
    };
}
```

- [ ] **Step 4: Run it (compiles on Linux; real signal from `windows-latest` in Task 22)**

```bash
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter Win32PasteSinkTests
```

Expected on devsbx01: `Passed! - Failed: 0, Passed: 1` (trivially).

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Win32/Win32PasteSink.cs dotnet/ClipBridge.Win32.Tests/Win32PasteSinkTests.cs
git commit -m "feat(dotnet): Win32PasteSink - SendInput synthesizing Ctrl+V"
```

---

## Task 16: `SshTransport` — port of `Start-Process -RedirectStandardInput`

**Files:**
- Create: `dotnet/ClipBridge.Win32/SshTransport.cs`
- Test: `dotnet/ClipBridge.Win32.Tests/SshTransportTests.cs`

This is the one Win32-project test that gets genuinely useful CI signal without needing real `ssh.exe`/network — it spawns `powershell.exe` (always present on `windows-latest`) as a stand-in target process and verifies the stdin-from-file plumbing and exit-code/stdout capture, which v1 never had a way to test at all.

- [ ] **Step 1: Write the failing tests**

```csharp
using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class SshTransportTests
{
    [Fact]
    public void Captures_exit_code_and_stdout()
    {
        if (!OperatingSystem.IsWindows()) return;

        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            var transport = new SshTransport();
            var result = transport.Send("powershell.exe",
                new[] { "-NoProfile", "-Command", "$null = $input; Write-Output 'ok'" },
                tmp);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("ok", result.StdOut);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Sends_the_exact_bytes_from_the_stdin_file_no_string_conversion_anywhere()
    {
        if (!OperatingSystem.IsWindows()) return;

        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, payload);
        try
        {
            var transport = new SshTransport();
            // Echo stdin back as base64 on stdout so the comparison never
            // depends on a text encoding assumption anywhere in the pipe.
            var result = transport.Send("powershell.exe",
                new[]
                {
                    "-NoProfile", "-Command",
                    "$ms = New-Object System.IO.MemoryStream; " +
                    "[Console]::OpenStandardInput().CopyTo($ms); " +
                    "[Console]::Out.Write([Convert]::ToBase64String($ms.ToArray()))",
                },
                tmp);

            var roundTripped = Convert.FromBase64String(result.StdOut.Trim());
            Assert.Equal(payload, roundTripped);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter SshTransportTests
```

Expected: build error — `SshTransport` does not exist.

- [ ] **Step 3: Minimal implementation**

```csharp
using System.Diagnostics;
using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class SshTransport : ISshTransport
{
    public SshExecResult Send(string exePath, IReadOnlyList<string> arguments, string stdinFilePath)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exePath}");

        // FileStream -> process stdin stream, never a string: this
        // preserves the byte-fidelity guarantee Send-Clip.ps1 relied on
        // -RedirectStandardInput (a file path) for. .NET has no direct
        // OS-level file-to-stdin redirect equivalent to PowerShell's; a
        // FileStream.CopyTo onto StandardInput.BaseStream gives the same
        // guarantee - no string conversion at any point - which is the
        // property that actually mattered (design spec: "Binary still
        // crosses via stdin redirected from a file, never a pipe that
        // could reinterpret bytes").
        using (var fs = File.OpenRead(stdinFilePath))
        {
            fs.CopyTo(process.StandardInput.BaseStream);
        }
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new SshExecResult(process.ExitCode, stdout, stderr);
    }
}
```

- [ ] **Step 4: Run it (compiles on Linux; real signal from `windows-latest` in Task 22)**

```bash
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter SshTransportTests
```

Expected on devsbx01: `Passed! - Failed: 0, Passed: 2` (trivially).

- [ ] **Step 5: Commit**

```bash
git add dotnet/ClipBridge.Win32/SshTransport.cs dotnet/ClipBridge.Win32.Tests/SshTransportTests.cs
git commit -m "feat(dotnet): SshTransport - spawn ssh.exe, stdin from a file, byte-for-byte"
```

---

## Task 17: `KeyboardHook` + `SingleThreadDispatcher` — the highest-risk component

**Read the design spec's "Risks" section again before touching this file.** This is the one place in the whole system where getting it wrong degrades typing on the entire machine, not just clipbridge.

Hard constraints this task must satisfy, restated from the design spec:

1. `SetWindowsHookExW(WH_KEYBOARD_LL, ...)`, not `RegisterHotKey` — `RegisterHotKey` is global and cannot be scoped to a window, so it would steal Ctrl+V in every application.
2. **The callback must return within Windows' `LowLevelHooksTimeout` (5s default) or Windows silently unhooks it** — no error, no exception, clipbridge just stops working. The callback therefore does at most: two boolean updates (ctrl/shift tracked from prior events), one foreground-process-name lookup, one clipboard format-availability check, and a queue post. No file I/O, no network, no `await`, nothing that can block.
3. **The swallow decision must be made before any I/O** — `HotkeyDecision.ShouldSwallow` (Task 10) is that decision, already unit-tested on Linux; this task only wires real inputs into it.
4. **Once step 3 swallows a keystroke, every downstream path — including every failure — owes the user a paste.** `PasteOrchestrator.Handle` (Task 11) already guarantees this on its own; this task's job is only to invoke it off the hook thread.
5. **A watchdog must re-register the hook if Windows drops it.** Detecting a silent unhook isn't directly queryable from `SetWindowsHookExW`'s API surface, so the watchdog (wired in Task 20) re-arms unconditionally on a timer instead of trying to detect the drop — `UnhookWindowsHookEx` + `SetWindowsHookExW` again is cheap and idempotent from the user's perspective.
6. **The delegate passed to `SetWindowsHookExW` must be kept alive for the hook's entire lifetime.** If the GC collects it while native code still holds the function pointer, the next keystroke crashes the process. Store it in a field, never a local variable or a lambda created fresh per call.

**Files:**
- Create: `dotnet/ClipBridge.Win32/SingleThreadDispatcher.cs`
- Create: `dotnet/ClipBridge.Win32/KeyboardHook.cs`
- Test: `dotnet/ClipBridge.Win32.Tests/SingleThreadDispatcherTests.cs`

The hook's raw `HookCallback` is not independently unit-testable — it needs a real installed hook and real keystrokes, which is exactly the "genuinely uninstrumentable" tier the design spec assigns to manual laptop testing. What Tasks 10 and this task both make sure of is that everything *decidable* is unit-tested (`HotkeyDecision`, already green on Linux) and everything *mechanical* is as small and inspectable as possible. `SingleThreadDispatcher`, the one piece of this task with no Win32 dependency, is fully testable.

- [ ] **Step 1: Write the failing test for `SingleThreadDispatcher`**

```csharp
using ClipBridge.Win32;
using Xunit;

namespace ClipBridge.Win32.Tests;

public class SingleThreadDispatcherTests
{
    [Fact]
    public void Runs_posted_work_off_the_calling_thread()
    {
        using var dispatcher = new SingleThreadDispatcher();
        var callingThreadId = Environment.CurrentManagedThreadId;
        var seenThreadId = -1;
        var done = new ManualResetEventSlim();

        dispatcher.Post(() =>
        {
            seenThreadId = Environment.CurrentManagedThreadId;
            done.Set();
        });

        Assert.True(done.Wait(TimeSpan.FromSeconds(2)), "posted work never ran");
        Assert.NotEqual(callingThreadId, seenThreadId);
    }

    [Fact]
    public void An_exception_in_posted_work_does_not_kill_the_dispatcher()
    {
        using var dispatcher = new SingleThreadDispatcher();
        var secondRan = new ManualResetEventSlim();

        dispatcher.Post(() => throw new InvalidOperationException("boom"));
        dispatcher.Post(() => secondRan.Set());

        Assert.True(secondRan.Wait(TimeSpan.FromSeconds(2)), "dispatcher died after the first posted action threw");
    }
}
```

- [ ] **Step 2: Run it, verify it fails**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter SingleThreadDispatcherTests
```

Expected: build error — `SingleThreadDispatcher` does not exist. (This one runs for real on Linux too — no Win32 dependency.)

- [ ] **Step 3: Minimal implementation of `SingleThreadDispatcher`**

```csharp
using System.Collections.Concurrent;

namespace ClipBridge.Win32;

// The keyboard hook callback must return immediately (see KeyboardHook
// below), so the actual transfer runs here instead: one dedicated
// background thread with its own queue, never the hook's thread and never
// the UI/message-pump thread.
public sealed class SingleThreadDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public SingleThreadDispatcher()
    {
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "clipbridge-worker" };
        _thread.Start();
    }

    public void Post(Action action) => _queue.Add(action);

    private void RunLoop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
                // PasteOrchestrator.Handle already owns its own failure
                // paths and always pastes; a throw here would be a bug in
                // the dispatcher's caller, not something to crash the
                // process over - crashing would leave the user with no
                // keyboard hook at all, which is worse than one dropped
                // paste attempt.
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
```

- [ ] **Step 4: Run it, verify it passes**

```bash
dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --filter SingleThreadDispatcherTests
```

Expected: `Passed! - Failed: 0, Passed: 2` — for real, on Linux, no Windows needed.

- [ ] **Step 5: Write `KeyboardHook`**

No new automated test here beyond what Steps 1–4 already covered (the decision logic) and what Task 10 already covered (`HotkeyDecision`) — this step wires real `NativeMethods` calls around that already-tested decision.

```csharp
using System.Runtime.InteropServices;
using ClipBridge.Core;

namespace ClipBridge.Win32;

public sealed class KeyboardHook : IKeyboardHook
{
    private static readonly string[] TerminalProcessNames = { "WindowsTerminal" };

    private readonly IForegroundWindow _foregroundWindow;
    private readonly IClipboard _clipboard;
    private readonly Action<Action> _postToWorker;

    // Pinned for the hook's entire lifetime - see constraint 6 above. If
    // this were a local or a fresh lambda per call, the GC could collect it
    // while native code still holds the function pointer, and the next
    // keystroke would crash the process.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;

    private IntPtr _hookHandle;
    private bool _ctrlDown;
    private bool _shiftDown;

    public event Action<bool>? PasteRequested;

    public KeyboardHook(IForegroundWindow foregroundWindow, IClipboard clipboard, Action<Action> postToWorker)
    {
        _foregroundWindow = foregroundWindow;
        _clipboard = clipboard;
        _postToWorker = postToWorker;
        _proc = HookCallback;
    }

    public void Start()
    {
        _hookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException("SetWindowsHookExW failed");
    }

    // Called by Program.cs's watchdog timer (Task 20) unconditionally, not
    // in response to a detected drop - see constraint 5 above.
    public void Rehook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
        }
        Start();
    }

    // MUST return within LowLevelHooksTimeout (constraint 2). This method
    // does at most: two field writes, one process-name lookup, one
    // IsClipboardFormatAvailable call (a single Win32 call, no data copy),
    // and a queue post. No file I/O, no network, no await.
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool isKeyDown = wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN;

            if (data.vkCode == NativeMethods.VK_CONTROL) _ctrlDown = isKeyDown;
            if (data.vkCode == NativeMethods.VK_SHIFT) _shiftDown = isKeyDown;

            bool isVDown = isKeyDown && data.vkCode == NativeMethods.VK_V;
            if (isVDown)
            {
                var processName = _foregroundWindow.GetForegroundProcessName();
                bool inTerminal = HotkeyDecision.IsForegroundTerminal(processName, TerminalProcessNames);
                // Skip the Win32 clipboard call entirely outside a terminal -
                // no reason to pay it if we already know we're passing through.
                bool clipboardHasImage = inTerminal && _clipboard.HasImageAvailable();
                bool forced = _shiftDown;

                if (HotkeyDecision.ShouldSwallow(_ctrlDown, isVDown, inTerminal, forced, clipboardHasImage))
                {
                    _postToWorker(() => PasteRequested?.Invoke(forced));
                    return (IntPtr)1; // swallow
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
```

- [ ] **Step 6: Confirm it compiles**

```bash
dotnet build ClipBridge.Win32/ClipBridge.Win32.csproj
```

Expected: `Build succeeded.` Real verification — does the hook actually fire, does a real Ctrl+V in Windows Terminal actually get swallowed and handed off — is manual, on the laptop, exactly as the design spec's testing tiers say ("Manual, on the laptop, only for the genuinely uninstrumentable: does the hook fire").

- [ ] **Step 7: Commit**

```bash
git add dotnet/ClipBridge.Win32/SingleThreadDispatcher.cs dotnet/ClipBridge.Win32/KeyboardHook.cs \
  dotnet/ClipBridge.Win32.Tests/SingleThreadDispatcherTests.cs
git commit -m "feat(dotnet): KeyboardHook - WH_KEYBOARD_LL, swallow decision, watchdog re-registration"
```

---

## Task 18: `TrayIcon` — minimal tray UI (Exit, Open log, Reinstall)

Design decision #4: minimal tray UI, no settings window, `config.json` stays the configuration surface. Raw `Shell_NotifyIcon`, not `System.Windows.Forms.NotifyIcon` — consistent with the rest of the Win32 layer and avoids pulling in the WinForms message-loop/control model this design otherwise avoids. Verified manually (design spec's uninstrumentable tier), not unit tested — there is nothing here for a test to assert against without a real desktop session and a mouse.

**Files:**
- Modify: `dotnet/ClipBridge.Win32/NativeMethods.cs` — add the window/menu/shell-notify P/Invokes below
- Create: `dotnet/ClipBridge.Win32/TrayIcon.cs`

- [ ] **Step 1: Add the additional P/Invoke surface to `NativeMethods.cs`**

Append inside the `NativeMethods` partial class:

```csharp
    // --- tray icon (Task 18) ---
    public const int WM_COMMAND = 0x0111;
    public const int WM_RBUTTONUP = 0x0205;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_DELETE = 0x00000002;
    public const uint MF_STRING = 0x00000000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public static readonly IntPtr IDI_APPLICATION = (IntPtr)32512;

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    // Kept on DllImport alongside the hook calls: RegisterClassW takes a
    // struct containing a managed delegate field (WNDCLASS.lpfnWndProc),
    // same marshalling-reliability reasoning as SetWindowsHookExW above.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    public static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);
```

- [ ] **Step 2: Write `TrayIcon`**

```csharp
using System.Runtime.InteropServices;

namespace ClipBridge.Win32;

public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAYICON = 0x8000 + 1;
    private const int ID_OPEN_LOG = 1001;
    private const int ID_REINSTALL = 1002;
    private const int ID_EXIT = 1003;

    private readonly string _logPath;
    private readonly Action _onExit;
    private readonly Action _onReinstall;
    // Pinned for the same GC-collection reason as KeyboardHook's _proc.
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private IntPtr _hwnd;

    public TrayIcon(string logPath, Action onExit, Action onReinstall)
    {
        _logPath = logPath;
        _onExit = onExit;
        _onReinstall = onReinstall;
        _wndProc = WndProc;
    }

    public void Create()
    {
        var hInstance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASS
        {
            lpfnWndProc = _wndProc,
            lpszClassName = "ClipBridgeTrayWindow",
            hInstance = hInstance,
        };
        NativeMethods.RegisterClassW(ref wc);
        _hwnd = NativeMethods.CreateWindowExW(0, "ClipBridgeTrayWindow", "clipbridge",
            0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = WM_APP_TRAYICON,
            hIcon = NativeMethods.LoadIconW(IntPtr.Zero, NativeMethods.IDI_APPLICATION),
            szTip = "clipbridge",
        };
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAYICON && (int)lParam == NativeMethods.WM_RBUTTONUP)
        {
            ShowContextMenu();
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_COMMAND)
        {
            var id = (int)wParam & 0xFFFF;
            if (id == ID_OPEN_LOG)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_logPath) { UseShellExecute = true });
            else if (id == ID_REINSTALL)
                _onReinstall();
            else if (id == ID_EXIT)
                _onExit();
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, ID_OPEN_LOG, "Open log");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, ID_REINSTALL, "Reinstall");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, ID_EXIT, "Exit");
        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(_hwnd); // required, or the menu won't dismiss on an outside click
        NativeMethods.TrackPopupMenu(hMenu, NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(hMenu);
    }

    public void Dispose()
    {
        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
    }
}
```

- [ ] **Step 3: Confirm it compiles**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet build ClipBridge.Win32/ClipBridge.Win32.csproj
```

Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add dotnet/ClipBridge.Win32/NativeMethods.cs dotnet/ClipBridge.Win32/TrayIcon.cs
git commit -m "feat(dotnet): TrayIcon - raw Shell_NotifyIcon, Exit/Open log/Reinstall"
```

---

## Task 19: `InstallCommand` — port of `Install-Clipbridge.ps1`

**Files:**
- Create: `dotnet/ClipBridge.App/InstallCommand.cs`

`InstallCommand.Run` composes the already-unit-tested Core pieces from Tasks 8 and 9 (`SshConfigBlockBuilder`, `SshConfigInspector`, `ClipbridgePaths`, `ClipbridgeConfigFactory`, `ClipbridgeConfigWriter`, `TransportProbeClassifier`) around one new piece of process-spawning I/O (`ProbeTransport`) that — same as v1's `Invoke-TransportProbe` — is only exercised by actually running the installer on Windows. No new test in this task; Tasks 8 and 9 already cover everything decidable.

- [ ] **Step 1: Write `InstallCommand`**

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ClipBridge.Core;

namespace ClipBridge.App;

public static class InstallCommand
{
    public static int Run(TextWriter output)
    {
        // Two DIFFERENT names, and conflating them breaks the probe: ssh
        // matches Host patterns against the name typed on the command
        // line, not the resolved hostname. probeHost must be a name the
        // user's EXISTING ~/.ssh/config already has a Host block for.
        // targetHost becomes HostName in the generated block, which IS
        // resolved by DNS directly, so it needs the FQDN.
        const string probeHost = "devsbx01";
        const string targetHost = "devsbx01.vollminlab.com";
        const string targetUser = "vollmin";
        const string hostAlias = "clipbridge";

        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipbridge");
        var identityFile = Path.Combine(sshDir, "devsbx01_id_ed25519.pub");

        output.WriteLine($"Probing ssh.exe against {probeHost}...");
        var sshProbe = ProbeTransport("ssh.exe", Array.Empty<string>(), probeHost);
        var sshOutcome = TransportProbeClassifier.Classify(sshProbe.ExeFound, sshProbe.ExitCode, sshProbe.StdErr);

        ProbeOutcome wslOutcome;
        if (sshOutcome == ProbeOutcome.Authenticated)
        {
            wslOutcome = ProbeOutcome.NotProbed;
        }
        else
        {
            output.WriteLine($"ssh.exe did not authenticate ({sshOutcome}); probing wsl.exe -e ssh...");
            var wslProbe = ProbeTransport("wsl.exe", new[] { "-e", "ssh" }, probeHost);
            wslOutcome = TransportProbeClassifier.Classify(wslProbe.ExeFound, wslProbe.ExitCode, wslProbe.StdErr);
        }

        string transport;
        try
        {
            transport = TransportProbeClassifier.SelectTransport(sshOutcome, wslOutcome, probeHost);
        }
        catch (ClipbridgeConfigException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        output.WriteLine($"transport: {transport}");

        var paths = ClipbridgePaths.From(sshDir, configDir);
        Directory.CreateDirectory(sshDir);

        var existingConfig = File.Exists(paths.SshConfigPath) ? File.ReadAllText(paths.SshConfigPath) : "";
        if (SshConfigInspector.HasHostBlock(existingConfig, hostAlias))
        {
            output.WriteLine($"ssh config already has a '{hostAlias}' Host block - leaving it alone");
        }
        else
        {
            var block = SshConfigBlockBuilder.Build(hostAlias, targetHost, targetUser, identityFile);
            File.AppendAllText(paths.SshConfigPath, block);
            output.WriteLine($"added Host {hostAlias} to {paths.SshConfigPath}");
        }

        Directory.CreateDirectory(configDir);
        var cfg = ClipbridgeConfigFactory.Create(hostAlias, transport);
        File.WriteAllText(paths.ConfigJsonPath, ClipbridgeConfigWriter.ToJson(cfg));
        output.WriteLine($"wrote {paths.ConfigJsonPath}");

        return 0;
    }

    private static (bool ExeFound, int ExitCode, string StdErr) ProbeTransport(string exe, string[] prefix, string targetHost)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in prefix) startInfo.ArgumentList.Add(a);
        startInfo.ArgumentList.Add("-o"); startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o"); startInfo.ArgumentList.Add("ConnectTimeout=5");
        startInfo.ArgumentList.Add(targetHost);
        startInfo.ArgumentList.Add("echo clipbridge-ok");

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException();
        }
        catch (Win32Exception)
        {
            return (false, -1, "");
        }
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        // Belt-and-suspenders: a 0 exit with unexpected stdout is not a
        // trustworthy "authenticated" - downgraded to OtherFailure territory.
        var exitCode = process.ExitCode == 0 && !stdout.Contains("clipbridge-ok") ? -1 : process.ExitCode;
        return (true, exitCode, stderr);
    }
}
```

- [ ] **Step 2: Confirm it compiles**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet build ClipBridge.App/ClipBridge.App.csproj
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add dotnet/ClipBridge.App/InstallCommand.cs
git commit -m "feat(dotnet): InstallCommand - port of Install-Clipbridge.ps1, probes the alias not the FQDN"
```

---

## Task 20: `Program.cs` — composition root, message loop, watchdog, startup registration

**Files:**
- Create: `dotnet/ClipBridge.App/Program.cs`

- [ ] **Step 1: Write `Program.cs`**

```csharp
using ClipBridge.Core;
using ClipBridge.Win32;

namespace ClipBridge.App;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--install"))
        {
            return InstallCommand.Run(Console.Out);
        }

        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipbridge");
        Directory.CreateDirectory(configDir);

        RegisterStartup();

        var clipboard = new Win32Clipboard();
        var pasteSink = new Win32PasteSink();
        var sshTransport = new SshTransport();
        var foregroundWindow = new Win32ForegroundWindow();

        var orchestrator = new PasteOrchestrator(
            clipboard, pasteSink, sshTransport,
            () => ClipbridgeConfigReader.Load(configDir),
            configDir);

        using var workerThread = new SingleThreadDispatcher();
        var hook = new KeyboardHook(foregroundWindow, clipboard, workerThread.Post);
        hook.PasteRequested += forced =>
        {
            var result = orchestrator.Handle(forced);
            NotifyResult(result);
        };
        hook.Start();

        // Watchdog: re-arms unconditionally every 5 minutes rather than
        // trying to detect a silent unhook (see Task 17, constraint 5 -
        // detecting the drop isn't directly queryable, and re-hooking an
        // already-active hook is cheap and idempotent).
        using var watchdog = new Timer(_ => hook.Rehook(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        using var tray = new TrayIcon(
            Path.Combine(configDir, "clipbridge.log"),
            onExit: () => NativeMethods.PostQuitMessage(0),
            onReinstall: () => InstallCommand.Run(TextWriter.Null));
        tray.Create();

        // Raw Win32 message pump - required for the low-level hook AND the
        // tray window's WndProc to receive messages. No
        // System.Windows.Forms.Application.Run: everything here is raw
        // Win32, consistent with the rest of this project.
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        hook.Dispose();
        return 0;
    }

    // Tones mirror clipbridge.ahk's SoundBeep calls exactly, so the audible
    // feedback a user has already learned carries over unchanged: 900Hz on
    // success, the two-tone 600/400Hz on "nothing to do", 300Hz on failure.
    private static void NotifyResult(PasteAttemptResult result)
    {
        switch (result.Outcome)
        {
            case PasteOutcome.Pasted:
                NativeMethods.Beep(900, 60);
                break;
            case PasteOutcome.NoImageNoOp:
                NativeMethods.Beep(600, 80);
                NativeMethods.Beep(400, 80);
                break;
            case PasteOutcome.Failed:
                NativeMethods.Beep(300, 200);
                break;
        }
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Registry Run key, not a Startup-folder .lnk shortcut (design decision
    // #6) - one file, no shortcut to keep in sync with the exe's path.
    private static void RegisterStartup()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("could not determine exe path");
        key.SetValue("clipbridge", $"\"{exePath}\"");
    }
}
```

- [ ] **Step 2: Confirm it compiles**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet build ClipBridge.App/ClipBridge.App.csproj
```

Expected: `Build succeeded.` `Microsoft.Win32.Registry` needs no extra NuGet package on `net10.0-windows` — it's part of the Windows-specific BCL surface gated by `[SupportedOSPlatform("windows")]`.

- [ ] **Step 3: Commit**

```bash
git add dotnet/ClipBridge.App/Program.cs
git commit -m "feat(dotnet): Program.cs - composition root, raw Win32 message loop, watchdog, Run key"
```

---

## Task 21: AOT publish settings and verification

**AOT-publishing a `win-x64` binary from a Linux host is not confirmed to work** — Task 0's spike already answered this empirically. This task uses whichever path Task 0 proved (local `dotnet publish` on devsbx01, or `windows-latest` CI) and does not re-guess it.

**Files:**
- Modify: `dotnet/ClipBridge.App/ClipBridge.App.csproj`

- [ ] **Step 1: Set the AOT publish properties**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <Nullable>enable</Nullable>
    <!-- PublishAot already produces a single native executable; PublishSingleFile
         is for a non-AOT self-contained publish and the two are mutually
         exclusive - deliberately not set here. -->
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ClipBridge.Core\ClipBridge.Core.csproj" />
    <ProjectReference Include="..\ClipBridge.Win32\ClipBridge.Win32.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Attempt the publish, using whichever path Task 0 proved**

If Task 0's Step 5 succeeded locally:

```bash
cd /home/vollmin/repos/vollminlab/clipbridge/dotnet
dotnet publish ClipBridge.App/ClipBridge.App.csproj -c Release
```

If Task 0 needed `windows-latest` (the expected outcome), defer this verification to Task 22's CI job — do not attempt it again locally on devsbx01 once Task 0 already established it fails there.

- [ ] **Step 3: Confirm the published binary is self-contained and runs**

On whichever platform Step 2 ran:

```bash
ls dotnet/ClipBridge.App/bin/Release/net10.0-windows/win-x64/publish/
./dotnet/ClipBridge.App/bin/Release/net10.0-windows/win-x64/publish/ClipBridge.App.exe --install
```

Expected: one native `ClipBridge.App.exe` (no accompanying `.dll`s for the app's own code — `PublishAot` inlines the managed code into the native binary) and `--install` runs the probe/write flow from Task 19 without crashing.

- [ ] **Step 4: Commit**

```bash
git add dotnet/ClipBridge.App/ClipBridge.App.csproj
git commit -m "feat(dotnet): AOT publish settings - win-x64, self-contained, PublishAot"
git push -u origin feat/dotnet-win32
gh pr create --title "feat(dotnet): clipbridge v2 Win32 shims, hook, tray, install, AOT publish" --body "Tasks 12-21 of docs/superpowers/plans/clipbridge-csharp-implementation.md, on top of the Core PR."
```

---

## Task 22: CI — add a `dotnet` job group, wire the new required checks

**The existing job names are load-bearing.** `github-admin/terraform/main.tf`'s `github_branch_protection.clipbridge_main` hardcodes `contexts = ["shell (shellcheck, dash, busybox ash)", "pester (windows)"]` as required status checks — renaming either existing job breaks merging on `main` until `github-admin` is updated to match, and a new job's context must be *added* there or it simply won't gate anything (a required-but-nonexistent context blocks every PR forever, per that file's own comment).

**Files:**
- Modify: `.github/workflows/test.yml`
- Modify: `github-admin/terraform/main.tf` (separate repo, separate PR — branch protection changes need review there)

- [ ] **Step 1: Add the `dotnet-core` and `dotnet-win32` jobs to `test.yml`**

Add these two jobs alongside the existing `shell` and `pester` jobs (do not rename or remove either existing job):

```yaml
  dotnet-core:
    name: dotnet-core (linux)
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Test ClipBridge.Core (portable, no Windows APIs)
        working-directory: dotnet
        run: dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj --logger "console;verbosity=normal"

  dotnet-win32:
    name: dotnet-win32 (windows, AOT publish)
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Test ClipBridge.Win32 (real Win32 calls)
        working-directory: dotnet
        run: dotnet test ClipBridge.Win32.Tests/ClipBridge.Win32.Tests.csproj --logger "console;verbosity=normal"
      - name: AOT publish smoke test
        working-directory: dotnet
        run: |
          dotnet publish ClipBridge.App/ClipBridge.App.csproj -c Release
          .\ClipBridge.App\bin\Release\net10.0-windows\win-x64\publish\ClipBridge.App.exe --install
```

- [ ] **Step 2: Run it, verify the new jobs appear and are green**

```bash
git checkout main && git pull && git checkout -b feat/dotnet-ci
git add .github/workflows/test.yml
git commit -m "ci(dotnet): add dotnet-core (ubuntu) and dotnet-win32 (windows, AOT publish) jobs"
git push -u origin feat/dotnet-ci
gh pr create --title "ci(dotnet): add dotnet-core and dotnet-win32 CI jobs" --body "Tasks 12-21 depend on this for real Windows-side verification. Existing shell/pester jobs untouched."
```

Expected: the PR's checks list shows `shell (shellcheck, dash, busybox ash)`, `pester (windows)` (both still passing, untouched), plus the two new jobs, both green.

- [ ] **Step 3: Add the new contexts to `github-admin` (separate repo, separate PR)**

```bash
cd /home/vollmin/repos/vollminlab/github-admin
git checkout main && git pull && git checkout -b feat/clipbridge-dotnet-required-checks
```

Modify `terraform/main.tf`'s `github_branch_protection.clipbridge_main`:

```hcl
  required_status_checks {
    strict = true
    # Read verbatim from the API, not guessed:
    #   gh api repos/vollminlab/clipbridge/commits/main/check-runs -q '.check_runs[].name'
    # A context matching no real check blocks every PR forever, and enforce_admins
    # leaves no bypass.
    contexts = [
      "shell (shellcheck, dash, busybox ash)",
      "pester (windows)",
      "dotnet-core (linux)",
      "dotnet-win32 (windows, AOT publish)",
    ]
  }
```

- [ ] **Step 4: Verify the context names match the real check-run names exactly**

Only after the `dotnet-core`/`dotnet-win32` PR from Step 2 has actually run at least once:

```bash
gh api repos/vollminlab/clipbridge/commits/main/check-runs -q '.check_runs[].name'
```

Confirm the two new job names printed here match `contexts` in Step 3 verbatim — GitHub Actions derives the check-run name from the job's `name:` field (`dotnet-core (linux)`, `dotnet-win32 (windows, AOT publish)`), not the job id, so these must match character-for-character.

- [ ] **Step 5: Apply and commit**

```bash
cd /home/vollmin/repos/vollminlab/github-admin
terraform -chdir=terraform plan
terraform -chdir=terraform apply
git add terraform/main.tf
git commit -m "chore(clipbridge): require dotnet-core and dotnet-win32 checks on main"
git push -u origin feat/clipbridge-dotnet-required-checks
gh pr create --title "chore(clipbridge): require the new dotnet CI checks on main" --body "Follows vollminlab/clipbridge#<PR number from Step 2>. Verified context names against a real check-run list before applying."
```

---

## Task 23: Docs — `docs/clipbridge-architecture.md` and `CLAUDE.md`

**Files:**
- Modify: `docs/clipbridge-architecture.md`
- Modify: `CLAUDE.md`

Org rules apply here (from `~/.claude/CLAUDE.md`): no `../` relative links (they create ghost nodes in the Obsidian graph sync), no cross-repo wikilinks inside synced docs, never hand-add a `← [[repo-name]]` backlink (the vault sync script injects it), and any new doc filename must stay globally unique across the org's synced repos. This task only *modifies* two existing, already-unique filenames — no new doc file is created, so no vault index or `sync-docs-to-vault.sh` change is needed.

- [ ] **Step 1: Update `docs/clipbridge-architecture.md`**

Add a new top section (after the existing header, before "What this is and why it exists") documenting the v2 architecture and updating the "three components" table's `Status` column. Do not delete the v1 description until Task 24 — both exist side by side per design decision #2 ("v1 stays until v2 is proven").

```markdown
## v2 status (2026, once Tasks 1-22 are merged)

`clipbridge.exe` (`dotnet/`, win-x64, PublishAot) replaces `Send-Clip.ps1` +
`Install-Clipbridge.ps1` + `clipbridge.ahk` with one resident process. Full
rationale and architecture: `docs/superpowers/specs/clipbridge-csharp-design.md`
and `docs/superpowers/plans/clipbridge-csharp-implementation.md`. `clipbridge-recv`
is unchanged - the wire protocol (PNG on stdin, one path on stdout) never
depended on which language sent it.

v1 (`windows/`) remains installed and functional until v2 has been used
day-to-day and confirmed working; see Task 24 of the implementation plan for
the cutover criteria.
```

Update the three-component table's `Status` column once v2 actually replaces each piece (do this edit at Task 24 time, not now, so the doc always reflects what's actually live).

- [ ] **Step 2: Update `CLAUDE.md`**

Modify the repo layout block to add the `dotnet/` tree, and add a `dotnet/`-specific testing note next to the existing PowerShell one:

```markdown
dotnet/                         C# AOT rewrite (v2). See docs/superpowers/specs/clipbridge-csharp-design.md.
dotnet/ClipBridge.Core/         Portable logic, zero Windows APIs, tested on Linux.
dotnet/ClipBridge.Win32/        Thin P/Invoke shims, tested for real only on windows-latest.
dotnet/ClipBridge.App/          Composition root, tray, --install, AOT publish target.
```

```markdown
## Testing (dotnet/, v2)

**Core runs on Linux, for real, via `dotnet-sdk-10.0`:**

```bash
cd dotnet && dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj
```

**Win32 tests compile on Linux but only exercise real Win32 calls on
`windows-latest`** - every test in `ClipBridge.Win32.Tests` early-returns via
`if (!OperatingSystem.IsWindows()) return;`, so a local pass on devsbx01
proves nothing beyond "it compiles." CI's `dotnet-win32` job is the real
signal.

**AOT publish (`win-x64`) cannot be produced on devsbx01** - Task 0 of the
implementation plan establishes why. CI's `dotnet-win32` job publishes and
smoke-tests the binary on `windows-latest`.
```

- [ ] **Step 3: Commit**

```bash
git checkout main && git pull && git checkout -b docs/dotnet-v2
git add docs/clipbridge-architecture.md CLAUDE.md
git commit -m "docs: document clipbridge v2 alongside v1 in architecture doc and CLAUDE.md"
git push -u origin docs/dotnet-v2
gh pr create --title "docs: clipbridge v2 architecture and CLAUDE.md updates" --body "Documents dotnet/ alongside the still-live windows/ per design decision #2 - v1 stays until v2 is proven."
```

---

## Task 24: Cutover — delete `windows/`, only after v2 is proven in real use

**This task is explicitly last and explicitly gated.** Design decision #2: "v1 stays until v2 is proven, then `windows/` is deleted in a single commit." **Do not start this task without the user explicitly confirming clipbridge v2 has been used day-to-day and works** — this is not a decision an agent makes unilaterally from green CI alone; CI proves the code paths execute, not that the hook survives real-world foreground-window churn, real Windows Terminal sessions, and real 1Password agent unlock/lock cycles over days of actual use.

**Files:**
- Delete: `windows/Send-Clip.ps1`, `windows/Install-Clipbridge.ps1`, `windows/clipbridge.ahk`
- Delete: `windows/tests/Send-Clip.Tests.ps1`, `windows/tests/Install-Clipbridge.Tests.ps1`
- Modify: `.github/workflows/test.yml` — remove the `shell`... no, remove the `pester` job (the shell job covers `clipbridge-recv`, which is unchanged and stays)
- Modify: `docs/clipbridge-architecture.md` — remove the v1 description, keep only v2
- Modify: `CLAUDE.md` — remove the `windows/` layout entries and the Pester testing section
- Modify (separate repo): `github-admin/terraform/main.tf` — remove `"pester (windows)"` from required contexts

- [ ] **Step 1: Confirm the gate with the user**

Do not proceed past this line without an explicit "yes, cut over" from the user in this session.

- [ ] **Step 2: Delete the v1 files**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge
git checkout main && git pull && git checkout -b chore/remove-v1
git rm windows/Send-Clip.ps1 windows/Install-Clipbridge.ps1 windows/clipbridge.ahk
git rm windows/tests/Send-Clip.Tests.ps1 windows/tests/Install-Clipbridge.Tests.ps1
rmdir windows/tests 2>/dev/null || true
```

- [ ] **Step 3: Remove the `pester` job from CI**

Delete the entire `pester:` job block from `.github/workflows/test.yml`, keeping `shell` (still needed for `clipbridge-recv`) and both `dotnet-core`/`dotnet-win32` jobs.

- [ ] **Step 4: Update the two docs**

In `docs/clipbridge-architecture.md`: delete the "v2 status" interim section added in Task 23, delete every v1-specific description (the `Send-Clip.ps1`/`clipbridge.ahk` rows in the three-component table, the "Data flow" section's PowerShell-specific steps), and update the table so `clipbridge.exe` is the only Windows-side row.

In `CLAUDE.md`: delete the `windows/` repo-layout lines, delete the PowerShell-specific "Testing" section, delete Gotchas that were PowerShell-specific and don't apply to C# (Gotcha #2 — PowerShell parameter-bind-time evaluation; Gotcha #4 — Pester 6 masking `BeforeAll` exceptions; Gotcha #5 — `$IsWindows` missing in PowerShell 5.1). Keep Gotcha #1 (rephrased: Win32-only calls are quarantined in `ClipBridge.Win32`, and `ClipBridge.Core` must never reference them), #3 (rephrased for `.NET`'s `Environment.GetFolderPath`, if still relevant), #6 (`\z` vs `$`, now citing `PathValidator.cs`), and #7 (`busybox find -exec ... +`, still applies to `clipbridge-recv`, unchanged).

- [ ] **Step 5: Update `github-admin`'s required checks (separate repo, separate PR, after this PR merges)**

```bash
cd /home/vollmin/repos/vollminlab/github-admin
git checkout main && git pull && git checkout -b chore/clipbridge-remove-pester-check
```

Remove `"pester (windows)"` from `clipbridge_main`'s `contexts` list, then `terraform plan` / `terraform apply` / commit / PR, same pattern as Task 22 Step 5.

- [ ] **Step 6: Commit and PR the deletion**

```bash
cd /home/vollmin/repos/vollminlab/clipbridge
git add -A
git commit -m "chore: remove clipbridge v1 (windows/) now that v2 is proven in daily use"
git push -u origin chore/remove-v1
gh pr create --title "chore: remove clipbridge v1 now that v2 is proven" --body "Design decision #2 from docs/superpowers/specs/clipbridge-csharp-design.md: v1 stays until v2 is proven, then windows/ is deleted in a single commit. User confirmed v2 works day-to-day before this PR was opened."
```

- [ ] **Step 7: After merge, verify CI is still green on `main` with the reduced job set**

```bash
gh run list --repo vollminlab/clipbridge --branch main --limit 3
```

Expected: `shell`, `dotnet-core`, `dotnet-win32` all passing, `pester` no longer present.

