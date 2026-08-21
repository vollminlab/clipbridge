# CLAUDE.md — clipbridge

Screenshot from a Windows laptop clipboard into a Claude Code prompt on `devsbx01`, one
keystroke, no `scp`. Full design and rationale: `docs/clipbridge-architecture.md`.

## Repo layout

```
linux/clipbridge-recv           POSIX sh, devsbx01. Validates, stores, prunes. Storage only.
linux/clipbridge-recv_test.sh   Shell tests — no cluster, no tmux, no network required.
linux/install.sh                Installs the receiver, prints a verify command.
verify/                         One-off measurement probes behind the design doc's Verified findings.
docs/clipbridge-architecture.md Architecture record.
docs/superpowers/specs/         Design spec.
docs/superpowers/plans/         Implementation plan (task-by-task).

dotnet/                         clipbridge.exe - the Windows client. Replaced windows/ on 2026-08-21.
dotnet/ClipBridge.Core/         Every decision. Zero Windows APIs, so it tests on Linux for real.
dotnet/ClipBridge.Win32/        Thin P/Invoke shims. Only genuinely exercised on windows-latest.
dotnet/ClipBridge.App/          Composition root, message loop, tray, --install, AOT publish target.
```

## Testing

**Shell tests must pass under both `dash` and `busybox ash` — always run both, not just one:**

```bash
dash linux/clipbridge-recv_test.sh
busybox ash linux/clipbridge-recv_test.sh
shellcheck -s sh linux/clipbridge-recv linux/clipbridge-recv_test.sh linux/install.sh
```

## Testing (dotnet/)

**Core runs on Linux, for real** — 143 tests, no Windows needed:

```bash
cd dotnet && dotnet test ClipBridge.Core.Tests/ClipBridge.Core.Tests.csproj
dotnet build -c Release                                                   # must be 0 warnings
dotnet build ClipBridge.Core/ClipBridge.Core.csproj -c Release -p:IsAotCompatible=true
```

Run the Release build, not just Debug: ImageSharp's licence gate (gotcha #15) is a hard error
only in Release, so a Debug-only check lets an accidental unpin through and breaks publish.

**Win32 tests compile on Linux but only execute on `windows-latest`.** They are marked
`[WindowsFact]`, a `FactAttribute` subclass that sets `Skip` off-Windows, so a Linux run reports
`Skipped: 8` and **cannot report a false `Passed`**. Do not replace it with an
`if (!OperatingSystem.IsWindows()) return;` early return inside the test body — that reports
`Passed` while executing nothing, which is gotcha #5 all over again.

**The `dotnet-win32` CI job is the only place any Win32 code runs at all.** It is not a
formality: it caught `CreateWindowExW` marshalling ANSI into a `W` entry point (gotcha #12)
that code review had missed. It also AOT-publishes and then asserts the binary is **still
running** after 15s — a resident app that exits early has failed its startup path, and every
startup call throws on failure, so staying alive proves `RegisterClassW`, `CreateWindowExW`,
`SetWindowsHookExW` and `Shell_NotifyIconW` all succeeded.

**AOT publish cannot be produced on devsbx01** — `Cross-OS native compilation is not supported`,
measured. Plain (non-AOT) cross-publish to `win-x64` works, so the gap is precisely the native
linker. CI does the AOT publish.

## The one rule that makes this safe to leave installed

**No failure path may leave `Ctrl+V` dead.** The keyboard hook *swallows* the keystroke before
handing off to the worker, so from that moment the process owes the user a paste. Every path
through `PasteOrchestrator.Handle` ends in exactly one `IPasteSink.SendPaste()` - never zero,
never twice - including paths that throw unexpectedly, and including a logger that itself
throws. A paste key that silently does nothing when the far side is unreachable is worse than
no tool at all.

This is not a style preference; it is the property the whole tool depends on to be trustworthy
enough to bind over the real paste key. It has a dedicated regression suite
(`PasteOrchestratorInvariantProbeTests`) that injects a throw at each collaborator and asserts
the paste still happens. Removing the catch-all backstop fails exactly four of them.

## Never commit a `.png`

`.gitignore` blocks `*.png` deliberately (with a carve-out for `docs/**/*.png`). Screenshots
routinely contain credentials — a repo that structurally cannot accept a `.png` cannot leak one.
Test fixtures are generated at runtime with `printf`, never committed:

```sh
printf '\211PNG\r\n\032\n' > "$tmp"; printf 'padding-bytes' >> "$tmp"
```

is a complete, valid-enough PNG signature for every test in this repo — the receiver only
checks the 8-byte magic.

## Gotchas

Traps that cost real time building this. None of them are discoverable from reading the code
cold — write them down here or they get rediscovered the hard way.

> **Gotchas 1-5 describe `windows/`, the PowerShell + AutoHotkey v1, deleted 2026-08-21.** They
> are kept because the traps generalise and because #5 is the direct precedent for how the
> dotnet Win32 tests skip. The code they refer to is gone; the lessons are not.

**1. `System.Windows.Forms` does not exist on Linux.** Any Windows-only API referenced at file
scope makes `Send-Clip.ps1` fail to dot-source, which kills every test in the file before any of
them run. Keep Windows-only calls inside `Get-ClipboardDataObject` — the one seam the Pester
tests mock — and nowhere else.

**2. PowerShell binds every parameter default before the script body runs**, including when the
parameter is supplied explicitly on the command line and including when an early
`if ($DotSourceOnly) { return }` would otherwise skip past it. A default expression that touches
`$env:LOCALAPPDATA` throws at bind time on Linux, before a single test executes — regardless of
whether anything in the body would ever have used it.

**3. `$env:LOCALAPPDATA` and `$env:TEMP` are both null on Linux.** Use
`[System.IO.Path]::GetTempPath()` for temp files, and a guarded fallback for config —
`$(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'clipbridge' } else { Join-Path $HOME '.clipbridge' })`.
The fallback must be **absolute**. A relative one doesn't throw — it silently creates
`clipbridge/` under whatever the process's working directory happens to be (`System32` for a
scheduled task) while you check `%LOCALAPPDATA%` and find nothing.

**4. Pester 6 masks exceptions thrown in `BeforeAll`.** It reports them as a bogus
`a 'break' or 'continue' statement with a label that does not match any enclosing loop` error,
which has nothing to do with the actual problem. When you see that specific message, don't
debug loop labels — go run the `BeforeAll` block's contents directly outside Pester to get the
real exception.

**5. `$IsWindows` does not exist in Windows PowerShell 5.1**, which is what `shell: powershell`
runs on `windows-latest` in GitHub Actions. There, referencing `$IsWindows` silently evaluates
to `$null`, so `-Skip:(-not $IsWindows)` is always true and the test skips forever — including
in real Windows CI, which is the one place it was supposed to run. Use `$env:OS -ne 'Windows_NT'`
instead; it's set correctly under both 5.1 and pwsh Core.

**6. .NET's `$` regex anchor also matches immediately before a trailing newline.** A path
validator meant to reject a trailing newline needs `\z`, not `$` — `'/path/x.png\n'` passes a
`$`-anchored check. This matters here specifically because the path gets typed as literal
keystrokes: a trailing newline types as Enter and submits the prompt before anyone gets to
write their question after it.

**7. `busybox find` does not reliably support `-exec ... +`.** Use `-exec ... \;`. This bit the
receiver's pruning step, which needs to run correctly under `busybox ash` as well as `dash`.

### C# / Win32 (v2)

Every one of these was found in code that compiled cleanly and looked right, and every one is
invisible from Linux. Six of them would have left `Ctrl+V` dead or broken.

**8. `sizeof(INPUT)` is set by the union's LARGEST member, not the one you use.** Declaring
`InputUnion` with only `KEYBDINPUT` gives 32 bytes; Windows requires 40 (`MOUSEINPUT` is the
largest). `SendInput` rejects any `cbSize` that is not exactly `sizeof(INPUT)` — it returns 0
and synthesizes nothing, so **every paste silently does nothing**. Declare all three union
members. Always check `SendInput`'s return value against the count you passed.

**9. A low-level keyboard hook never reports `VK_CONTROL` or `VK_SHIFT`.** `KBDLLHOOKSTRUCT.vkCode`
carries the distinguished `VK_LCONTROL`/`VK_RCONTROL`/`VK_LSHIFT`/`VK_RSHIFT` codes. Tracking
modifiers by comparing against the generic codes means the state never becomes true and **the
hotkey never fires at all** — it installs cleanly and does nothing. Query
`GetAsyncKeyState(VK_CONTROL)` at the moment you need it instead: it aggregates left and right,
costs one call, and cannot desynchronise the way event-tracking does when a modifier is already
held as the hook installs.

**10. Your own `SendInput` re-enters your own hook.** Injected input is delivered to low-level
hooks — that is what `LLKHF_INJECTED` is for. A tool that both hooks `Ctrl+V` and synthesizes
`Ctrl+V` will re-trigger itself; here that was an **infinite paste loop** on every failure path,
because failures deliberately leave the image on the clipboard. Tag your own events with a magic
`dwExtraInfo` and skip them in the callback. Prefer the marker over testing `LLKHF_INJECTED`,
which would also ignore on-screen keyboards, remote desktop and accessibility tools.

Note 9 and 10 mask each other: with 9 unfixed nothing fires, so 10 is invisible — and fixing 9
alone, the obvious first move once you find the tool does nothing, turns every failure into a
runaway loop.

**11. A low-level hook is serviced on the thread that installed it, and that thread must keep
pumping messages.** So re-installing the hook from a `System.Threading.Timer` (a thread-pool
thread with no message loop) silently stops it delivering — a watchdog that kills the hook it
exists to protect, five minutes after every startup. Marshal such work onto the pump thread with
`PostMessage`. For the same reason, never run anything slow on the pump: blocking it starves the
hook callback, and Windows silently unhooks a callback that overruns `LowLevelHooksTimeout` (5s).

**12. `DllImport` defaults to `CharSet.Ansi`.** A `string` parameter on a `*W` entry point is
then marshalled as ANSI and arrives as mojibake. `CreateWindowExW` failed this way with
`ERROR_CANNOT_FIND_WND_CLASS` (1407) while `RegisterClassW` succeeded, because `WNDCLASS`'s
field carried an explicit `[MarshalAs(UnmanagedType.LPWStr)]` — so the class registered under
the right name and was looked up under a mangled one. Set `CharSet = CharSet.Unicode` on every
`*W` `DllImport`. (`LibraryImport` uses `StringMarshalling` and ignores `CharSet` entirely.)

**13. `Shell_NotifyIcon` validates `cbSize` against its known struct versions and returns FALSE
if it matches none.** A truncated `NOTIFYICONDATA` measuring 296 bytes matched nothing
(V1=168, V2=952, V3=968, current=976), so **the tray icon simply never appeared** — silently,
because the return value was ignored. Size the struct to a documented version and check the
return.

**14. Draining stdout then stderr sequentially deadlocks.** If the child fills the stderr pipe
buffer while you are blocked reading stdout, neither side moves — and `ssh.exe` writes freely to
stderr. Start `ReadToEndAsync()` on both **before** writing stdin. v1 did not have this failure
mode because PowerShell's `Start-Process -RedirectStandardInput <file>` is an OS-level redirect
with no pipe at all. Also **bound the wait**: `PasteOrchestrator` catches exceptions and still
pastes, but it cannot catch a thread parked forever, so an unbounded wait is a dead `Ctrl+V`
that no backstop can reach.

**15. ImageSharp 4.x fails the build without a licence key — as an ERROR in Release, a warning
in Debug.** So an unpinned bump looks fine locally and breaks `dotnet publish` in CI. Pinned at
`3.1.12`. This is not a licensing problem: clipbridge is a public MIT repo, which qualifies for
the Six Labors Split Licence's Apache-2.0 arm. (The design spec's description of ImageSharp as
"MIT" is wrong — it has been the Split Licence since v2.)

**16. A custom `DateTime` format string takes `:` from the current culture.** Under `fi-FI`,
`"yyyy-MM-ddTHH:mm:ss"` renders `2026-08-19T04.30.00`. Pass `CultureInfo.InvariantCulture`
explicitly wherever the rendered text is load-bearing. The subtle part: the *tests* built stamps
the same culture-sensitive way, so implementation and tests varied together and agreed by
accident — the suite stayed green under every locale while the on-disk format changed. Pinning
only the implementation is what exposed it.

### The pattern behind 8-14

They cluster where the C# port **substituted a different mechanism** for one of v1's rather than
porting its behaviour: an OS-level stdin redirect became a pipe, an explicit `Sleep(200)` between
paste and clipboard-restore vanished, AHK's modifier handling became a manual `vkCode` compare.
Each substitution silently dropped a property v1 had structurally, and `clipbridge.ahk` — the
component that could never be tested — is where most of them were lost. **When porting, diff
against the original's behaviour, not against your reading of the new code.**
