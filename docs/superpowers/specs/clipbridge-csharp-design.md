# clipbridge v2 — a single AOT-compiled binary

**Date:** 2026-08-19
**Status:** Draft, awaiting review
**Supersedes (once built):** `windows/Send-Clip.ps1`, `windows/Install-Clipbridge.ps1`, `windows/clipbridge.ahk`
**Unchanged:** `linux/clipbridge-recv`

## Why replace something that works

v1 works. Screenshot, `Ctrl+V`, image in the prompt. It has 64 PowerShell tests and 28 shell
assertions, and CI runs them on two PowerShell runtimes. This is not a rewrite for its own sake,
and it should not start until v1 has been used for a while in anger.

Three reasons, in order of weight.

**The hotkey costs 3.02 seconds, and essentially all of it is process startup.** Measured from
AutoHotkey's own trace:

```
091: code := RunWait('powershell.exe -STA -NoProfile ... -File "…\Send-Clip.ps1"') (3.02)
```

The ssh round trip is about half a second; the clipboard restore adds 200 ms. Everything else is
`powershell.exe` starting, per keypress. A resident process removes that entirely — the same work
becomes the ssh round trip alone. This is the reason a user would notice.

**`clipbridge.ahk` cannot be tested, and it produced the last two bugs.** It is 136 lines, it has
no test framework available anywhere, and CI cannot exercise it. Both remaining v1 defects lived
there: typing the path instead of pasting it, and the global `Ctrl+Shift+V` that would have stolen
paste-as-plain-text in every browser and editor. Everything testable was tested; the untestable
part is where the bugs were. A C# binary folds that component into code with xUnit around it.

**Three processes and two languages for one keypress.** AutoHotkey → `powershell.exe` → `ssh.exe`,
with a file and an exit-code table as the interface between the first two. In one process,
`last-path.txt` disappears, the inter-process exit-code taxonomy disappears, and the stale-path
deletion that guards it disappears. The design gets smaller, which is the sign a consolidation is
warranted rather than merely appealing.

## Requirements that survive the rewrite

These were expensive to learn. They are not open for rediscovery.

**PASTE the path — never type it.** Claude Code scans *pasted* text for image paths that exist on
the local filesystem and attaches the image. Text arriving as keystrokes is never scanned. Measured
2026-08-19: three `SendText` attempts placed a correct path in the prompt and attached nothing; the
identical path pasted attached instantly. v2 must put the path on the clipboard and synthesize a
real paste.

**No new SSH key, and nothing added to the 1Password agent.** A `restrict,command=` key was tried
in v1 and locked the user out of his own machine: the agent offers every key it holds to any client
that does not pin an identity, WSL's ssh config pins nothing, and mosh runs inside WSL — so mosh
authenticated with the restricted key and inherited `no-pty`. `command=` binds to a *key*, not to a
destination. v2 authenticates with the existing `devsbx01_id_ed25519` and names the remote command
on the ssh command line, exactly as v1 does now.

**No failure may leave `Ctrl+V` dead.** Text on the clipboard pastes normally. Every error path
ends in an ordinary paste. A paste key that silently does nothing when the far side is unreachable
is worse than no tool at all.

**The path is typed into a prompt unquoted**, so it must be a single line, absolute, printable
ASCII, no whitespace: `^/[\x21-\x7E]+\z`. Note `\z`, not `$` — .NET's `$` also matches before a
trailing newline, and a newline reaching the prompt is typed as Enter, submitting the message
before the user writes anything. The equivalent trap exists in C#: `Regex` `$` behaves the same way.

**`Host` patterns match the name typed on the command line**, not the resolved hostname. Probe the
alias (`devsbx01`); write the FQDN as `HostName`. Conflating them produced `Permission denied` on a
box that authenticates fine.

## Architecture

One process, resident in the tray. The Windows-only surface is quarantined behind interfaces so
that everything else is testable on Linux — the same discipline that made v1's PowerShell half
solid, but enforced by the type system rather than by a mocking convention.

```
clipbridge.exe  (win-x64, PublishAot, self-contained, no runtime install)
│
├── IKeyboardHook      ── WH_KEYBOARD_LL, decides per-event whether we own this Ctrl+V
├── IClipboard         ── Win32 OpenClipboard/GetClipboardData/SetClipboardData
├── IPasteSink         ── SendInput(Ctrl+V)
├── IForegroundWindow  ── GetForegroundWindow + process name, for terminal scoping
├── ISshTransport      ── spawns ssh.exe with stdin redirected from a file
│
└── PasteOrchestrator  ── the actual logic. Depends only on the interfaces above.
                          Fully unit-tested on Linux with fakes.
```

Everything with a `Win32` implementation is a thin shim containing no branching. All decisions —
is this an image, is the path usable, what do we do on failure, what gets logged — live in
`PasteOrchestrator`, which never touches a Windows API.

### The hotkey needs a low-level hook, not `RegisterHotKey`

`RegisterHotKey` is global and cannot be scoped to a window, so it would steal `Ctrl+V` in every
application. AHK scopes `#HotIf WinActive(...)` by using a low-level keyboard hook and deciding per
event, and v2 must do the same: `SetWindowsHookEx(WH_KEYBOARD_LL)`, then on each `Ctrl+V` check
`GetForegroundWindow()`'s process against the configured terminal list.

**The hook callback must return immediately.** Windows enforces `LowLevelHooksTimeout` (5 s by
default) and silently unhooks a callback that overruns. The transfer takes ~0.5 s, which is far too
long to do inline. So the callback does only this:

1. Not `Ctrl+V`, or foreground process not a configured terminal → `CallNextHookEx`, done.
2. `IsClipboardFormatAvailable` says no image → `CallNextHookEx`, so a text paste proceeds
   untouched and instantly. This is the common case and must stay free.
3. Otherwise → **swallow the keystroke** (return 1), post the work to a worker thread, return.

The worker then does the transfer and synthesizes the paste itself. Because step 3 swallowed the
original keystroke, *every* subsequent path must synthesize a paste — including failures. That is
how "no failure leaves `Ctrl+V` dead" is honoured once the key has been intercepted.

### Clipboard access is raw Win32, not `System.Windows.Forms`

`System.Windows.Forms` is not AOT-friendly and is precisely the dependency v1 had to quarantine to
stay testable. v2 uses `RegisterClipboardFormat("PNG")` to find the lossless PNG stream — measured
present on this user's clipboard — and falls back to `CF_DIB`.

**The DIB fallback needs a PNG encoder.** Recommendation: **ImageSharp** (managed, MIT, AOT-compatible).
The alternative is hand-rolling an encoder over `System.IO.Compression.ZLibStream`, which is
genuinely feasible in ~150 lines but is exactly the sort of code that is subtly wrong for years.
The fallback is rarely exercised — this user's clipboard publishes a real PNG stream — which argues
*against* hand-rolling it, because rarely-run code that is subtly wrong is the worst kind.

For the first time this branch becomes testable: `windows-latest` CI can run the Win32 code, so the
DIB path can have a real test instead of the currently-skipped one.

### Transport stays `ssh.exe`

v2 keeps shelling out rather than adopting an SSH library. `ssh.exe` already works, honours the
user's `~/.ssh/config`, and — critically — talks to the 1Password agent with an authorization policy
that has been *measured*: one prompt per cache lapse, not one per connection. An SSH library would
mean re-solving agent integration for no benefit. Binary still crosses via stdin redirected from a
file, never a pipe that could reinterpret bytes.

## What disappears

| v1 | v2 |
|---|---|
| `last-path.txt` handoff file | in-memory |
| Inter-process exit codes 0/2/3/4/5/6/7/8 | exceptions and a result type; ssh's own exit code still read |
| Stale-path deletion before each run | nothing to go stale |
| `Install-Clipbridge.ps1` (288 lines) | `clipbridge.exe --install` |
| `clipbridge.ahk` (136 lines, untestable) | tested C# |
| AutoHotkey v2 install | none |
| ~3.0 s per paste | ~0.5 s |

## Testing

**On Linux, via `dotnet-sdk-10.0` from apt** — available on devsbx01, confirmed. `PasteOrchestrator`
and every validator, config reader, and ssh-argument builder is tested here with fakes, exactly as
the PowerShell half was. This is the lever that made v1's tested half trustworthy and it must be in
place before implementation starts, not after.

**But the AOT publish itself probably cannot run on Linux, and this document originally implied it
could.** NativeAOT needs the *target* platform's native linker, and cross-OS publishing
(Linux → `win-x64`) has not historically been supported — cross-*architecture* on the same OS is a
different thing. So the split is:

| | where |
|---|---|
| `ClipBridge.Core` unit tests (the majority of the code) | Linux, fast local loop |
| AOT publish and the Win32 shims | `windows-latest` CI, and the laptop |

Task 0 of the implementation plan tests this for real rather than trusting either answer, and the
publish task branches on the result. If cross-publish does work, it is a bonus; the plan does not
depend on it.

**On `windows-latest` CI**, the Win32 shims get their first real coverage: clipboard round-trips,
the DIB fallback, foreground-window detection. v1 could never do this.

**Manual, on the laptop**, only for the genuinely uninstrumentable: does the hook fire, does the
paste land in a mosh session, does the image attach.

## Decisions made without you — please overturn any of these

1. **Same repo, new `dotnet/` directory.** It is the same tool; a second repo doubles the CI, docs
   and onboarding for one deliverable.
2. **v1 stays until v2 is proven**, then `windows/` is deleted in a single commit. Running both
   simultaneously is fine — v1 only acts when its AHK script is loaded.
3. **ImageSharp for the DIB fallback**, over hand-rolling a PNG encoder.
4. **Minimal tray UI**: Exit, Open log, Reinstall. No settings window; `config.json` stays the
   configuration surface.
5. **`Ctrl+Shift+V` force binding is kept**, terminal-scoped, as in v1.
6. **Registry `Run` key for startup**, rather than a Startup-folder shortcut — one file, no `.lnk`.
7. **.NET 10** (`net10.0-windows`), `PublishAot=true`, `win-x64`, self-contained single file.

## What I need from you

**One command, so the portable half can be developed test-first:**

```
sudo apt install -y dotnet-sdk-10.0
```

Without it I can write C# but not compile or test it, and this project has already demonstrated
what happens when code is written against an untested assumption. If you would rather not add an
SDK to that box, say so — the fallback is CI-only testing, which works but replaces a two-second
loop with a two-minute one.

## Risks

**The low-level hook is the highest-risk component.** It runs in the input path of every keystroke
on the machine. A slow or crashing callback degrades typing system-wide, and Windows silently
unhooks it on timeout — which would present as "clipbridge just stopped working" with no error.
Mitigations: the callback does nothing but a format check and a queue post; a watchdog re-registers
the hook if Windows drops it; and the swallow decision is made before any I/O.

**Swallowing `Ctrl+V` raises the cost of a bug.** In v1, AHK failing meant the paste fell through.
In v2, once the hook returns 1, the keystroke is gone and the process owes the user a paste. Every
failure path must end in a synthesized paste, and that is a testable property of
`PasteOrchestrator` rather than a convention.

**AOT plus P/Invoke plus a third-party imaging library** is the least-travelled combination here.
Worth an early spike: a hello-world AOT build that opens the clipboard and encodes a DIB, before
any real code is written.

## Not doing

- No SSH library — `ssh.exe` stays.
- No changes to `clipbridge-recv`. The protocol is PNG on stdin, one path on stdout, and it does
  not care what is on the other end.
- No settings GUI.
- No cross-platform support. This is a Windows laptop talking to one Linux box.
