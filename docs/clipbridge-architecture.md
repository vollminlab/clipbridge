# clipbridge — architecture

**Date:** 2026-08-21
**Status:** v2 is the tool. v1 (`windows/`, PowerShell + AutoHotkey) was deleted on 2026-08-21
after v2 was verified in day-to-day use.

## The Windows client

`clipbridge.exe` (`dotnet/`, `win-x64`, `PublishAot`) is one resident process holding a
low-level keyboard hook, a tray icon and a worker thread. It replaced three components in two
languages — `Send-Clip.ps1`, `Install-Clipbridge.ps1` and `clipbridge.ahk` — cutting the
per-paste cost from ~3.0s (almost entirely `powershell.exe` startup, paid on every keypress) to
the ssh round trip alone. Rationale: `docs/superpowers/specs/clipbridge-csharp-design.md`;
task-by-task build: `docs/superpowers/plans/clipbridge-csharp-implementation.md`.

`clipbridge-recv` was **never touched by the rewrite**. The wire protocol — PNG on stdin, one
absolute path on stdout — never depended on what was sending it, so the entire Linux side came
through the language change unchanged.

Most of the logic lives in `ClipBridge.Core`, which references no Windows API and is therefore
tested on Linux for real. `ClipBridge.Win32` holds thin P/Invoke shims that only execute on
Windows. What CI proves and what it does not:

| Proven on `windows-latest` | Only provable on the laptop |
|---|---|
| Clipboard PNG/DIB extraction, capture/restore, set-text | Does a physical `Ctrl+V` in Windows Terminal get swallowed |
| `SendInput` accepts all four Ctrl+V events | Does the pasted path actually attach the image in a prompt |
| Foreground-window process lookup | Does the hook survive real foreground-window churn |
| ssh transport: byte fidelity, large stderr, timeout | Does it survive 1Password agent lock/unlock cycles |
| AOT publish, and the binary starting and staying resident | Does the tray menu behave |

The startup assertion is stronger than it looks: `RegisterClassW`, `CreateWindowExW`,
`SetWindowsHookExW` and `Shell_NotifyIconW` each throw on failure, so "still resident after 15s"
means all four succeeded on a real Windows host.

The cutover was gated on that right-hand column, not on green CI. It happened on 2026-08-21
after v2 pasted a screenshot end to end, passed a text-paste and a browser `Ctrl+Shift+V` check,
failed cleanly with the network down, and survived 31 minutes of uptime — the last one being the
watchdog re-hook, whose failure mode is "worked for a while, then quietly stopped".

### Installing v2 on the laptop

NativeAOT cannot cross-compile, so the binary is only ever produced by the `dotnet-win32` CI
job. It is uploaded as the `clipbridge-win-x64` artifact on every run — download it from the
run's Artifacts section on GitHub.

1. **Extract to a stable path**, not `Downloads`. `%LOCALAPPDATA%\clipbridge\bin\` works.
   On first normal launch the app records *its own current path* in the `Run` key, so moving
   the exe afterwards leaves a startup entry pointing at nothing.
2. **`clipbridge.exe --install`** — probes `ssh.exe`, falls back to `wsl.exe -e ssh`, then
   writes the `Host clipbridge` block into `~/.ssh/config` and `config.json` into
   `%LOCALAPPDATA%\clipbridge\`. Needs the 1Password agent unlocked and an existing
   `devsbx01` Host block to probe against. Exits non-zero with a diagnostic if neither
   transport authenticates — on this laptop's setup that almost always means the agent is
   locked, not that the key is wrong.
3. **Check the generated block** in `~/.ssh/config`. `IdentityFile` is written as
   `~/.ssh/devsbx01_id_ed25519.pub` — the *public* key path, which is how the 1Password agent
   selects which key to offer. If the real key is named differently, correct it there;
   `IdentitiesOnly yes` means a wrong path fails the whole connection.
4. **`clipbridge.exe`** with no arguments — installs the keyboard hook, registers startup, and
   puts an icon in the tray. It stays resident; there is no window.

## What this is and why it exists

Screenshots get taken on a Windows laptop. The Claude Code session they're needed in runs on
`devsbx01`, reached over `mosh` inside `tmux`. There is no path from the Windows clipboard into
that prompt short of saving the image to a file and `scp`-ing it across — enough friction that
screenshots mostly just didn't get shared. clipbridge closes that gap: one keystroke on the
laptop, and the screenshot's path lands in the prompt of whichever Claude Code session is
currently on screen.

## The two components

Each owns exactly one concern. Nothing does two jobs.

| Component | Runs on | Owns |
|---|---|---|
| `clipbridge-recv` | `devsbx01` | Validate, store, prune. Storage only. |
| `clipbridge.exe` | Windows laptop | Hotkey, clipboard extraction, transport, pasting the result. |

`clipbridge-recv` (POSIX `sh`, `~/.local/bin/clipbridge-recv` on `devsbx01`) reads a PNG on
stdin, writes it to `~/.clipbridge/<timestamp>.png`, prunes old files, and prints the absolute
path on stdout. It has no idea which Claude Code session anything is going to, and doesn't need
to — targeting isn't its problem.

`clipbridge.exe` scopes `Ctrl+V` to the terminal, pulls the image off the clipboard (preferring
the lossless `PNG` clipboard format over the DIB fallback), streams the bytes to `devsbx01` over
`ssh.exe`, and **pastes** the returned path into whatever has focus.

Pasted, not typed. Claude Code scans *pasted* text for image paths that exist on the local
filesystem and attaches the image; text arriving as keystrokes is never scanned. Measured
2026-08-19 — three `SendText` attempts placed a correct path in the prompt and attached nothing;
the identical path pasted attached instantly.

The paste is synthesized because the hotkey was **swallowed**: the keyboard hook returns 1 for a
`Ctrl+V` it intends to handle, so the original keystroke never reaches the terminal. From that
moment the process owes the user a paste, and every path through the orchestrator — including
every failure — ends in exactly one `SendInput`. On a failure the clipboard is deliberately left
untouched, so what gets pasted is whatever the user already had.

## Data flow

```
Screenshot lands on the Windows clipboard
        │
Click into the terminal with the target Claude Code session, press Ctrl+V
        │
The low-level keyboard hook sees Ctrl+V, confirms the foreground process is a
configured terminal and that an image is on the clipboard, and SWALLOWS the key
        │
The hook posts to a worker thread and returns immediately - it must, or Windows
silently unhooks a callback that overruns LowLevelHooksTimeout
        │
Worker: extract PNG to %TEMP%, stream it over ssh.exe to devsbx01
        │
clipbridge-recv validates the PNG magic, writes ~/.clipbridge/<ts>.png, prunes, prints the path
        │
Worker: capture the clipboard, put the path on it as text, synthesize Ctrl+V,
wait ~200ms for the target to consume it, restore the original clipboard
        │
Claude Code scans the pasted text, finds a path that exists on the local filesystem,
attaches the image, strips the path from the message
```

That ~200ms wait is not padding. `SendInput` only queues input, so restoring the clipboard
immediately would put the user's original contents back before the target had read the path —
and the target would paste the wrong thing, intermittently.

The last step is what makes the whole design work, and it wasn't assumed — it was observed.
`paste-cache` recorded the pasted text at `04:04:34.675` with the Linux path stripped mid-line,
and `image-cache/<session>/1.png` was written 10 ms later, at `04:04:34.685`, with a hash
identical to the file that had just been transferred. A Windows path present in the same paste
survived completely untouched, because it doesn't resolve to anything on that filesystem. Claude
Code auto-attaches any locally-existing image path it finds in prompt text; the receiver only
has to get the file onto the right disk and get its path typed in. Nothing downstream of that
needs to know clipbridge exists.

## Why targeting is by window focus, not tmux

This is the decision the rest of the design hangs off, and it reversed an earlier version of
the tool that had already been built partway.

The original design injected server-side, via `tmux send-keys -l` into the pane that had most
recently been typed into. Both halves of that mechanism were verified working: a detached
`tmux` session running `claude` received a literal path with no `Enter`, no `@`-autocomplete
overlay, and no mangling, and `#{client_activity}` was confirmed to be a genuine input-only
signal — sampled twice across 20 idle seconds with a Claude session actively rendering output
the entire time, and the value did not move once. Server-side injection was mechanically sound.

What killed it was the premise, not the mechanism. "The pane most recently typed into" is not
the same thing as "the pane currently on screen" once more than one Claude Code session runs in
parallel — and Claude turns take minutes, so tabbing away to work in a different session while
one thinks is the ordinary workflow, not an edge case. This was observed live, not inferred:
the selector resolved to `hashlink` (`client_activity` 116s stale) while the session actually
being worked in at that moment was `EyeDropClone` (187s stale). The heuristic picked the wrong
session while measuring exactly what it was supposed to measure. Worse, the failure is silent
in the least helpful way possible — the success beep fires, nothing shows up on screen, and the
tool just looks broken.

The fix was not a better heuristic. It was recognizing that focus is knowable on the laptop and
unknowable on `devsbx01`, so injection belongs wherever the answer already exists — which is
never the remote side. That's the whole reason `clipbridge-recv` has no tmux dependency at all,
and why targeting doesn't appear anywhere in its ~60 lines: the receiver's job shrank to
storage the moment the targeting problem moved off the box it used to think it had to solve on.

Record this for the next design that's tempted toward a clever remote-side heuristic: a signal
can measure perfectly and still answer the wrong question.

## Verification results

All measured on 2026-08-18, before any implementation was written, through the production
mechanism rather than an equivalent one.

| # | Item | Result |
|---|---|---|
| 1 | `SendText` survives Windows Terminal → mosh → tmux → Claude Code | **Pass.** The full 45-character production-shaped path arrived complete and in order, in default Input send mode — Event mode with key delay was not needed even as a fallback. |
| 2 | Clipboard formats the screenshot tool publishes | **A real `PNG` stream is present** (alongside `System.Drawing.Bitmap`, `Bitmap`, `CanUploadToCloudClipboard`, `CanIncludeInClipboardHistory`), so the lossless branch is the live path, not a fallback. `file` on the arrived bytes confirmed `8-bit/color RGBA` — alpha survived transport intact. |
| 3 | Which ssh client authenticates | **`ssh.exe` only.** `wsl.exe -e ssh` returns `Permission denied (publickey)` — the 1Password SSH agent serves the Windows client; WSL has no key of its own. A guess had even odds of picking the transport that cannot authenticate at all. |
| 4 | PNG byte integrity over `ssh` stdin via `Start-Process -RedirectStandardInput` | **Pass.** 10200 bytes, identical sha256 on both ends. |

Transport is pinned to `ssh.exe` at install time by probing both clients and recording the
result — not hardcoded, because the answer is machine-specific, but not re-detected on every
run either, because it's known once installed.

## Exit codes

Get these from the code, not the design doc.

### `clipbridge-recv` (`linux/clipbridge-recv`)

| Exit | Condition |
|---|---|
| `0` | PNG validated, stored, path printed on stdout |
| `3` | Bad input — empty stdin, or the first 8 bytes aren't the PNG magic (`89 50 4E 47 0D 0A 1A 0A`) |
| `5` | Cannot write — `mkdir -p ~/.clipbridge` failed, `chmod 700` failed, the directory isn't writable, `mktemp` failed, the `cat > $tmp` write failed, or the final `chmod 600` / `mv` into place failed |

### `clipbridge.exe`

v2 has no exit-code taxonomy, because there is no longer a process boundary to carry one. v1
needed exits `2`-`8` so `clipbridge.ahk` could tell a receiver rejection from a transport
failure from a config problem; in one process that is a return value and a log line.

What replaced it:

| Outcome | User sees | Log |
|---|---|---|
| Pasted | the path in the prompt, 900Hz beep | nothing |
| No image on the clipboard | ordinary paste, 600/400Hz two-tone | nothing - not an error |
| Failed | ordinary paste, 300Hz beep | one line naming the cause |

The receiver's own exits `3` and `5` are still read and named distinctly in the log rather than
folded into a generic transport failure, for the reason the taxonomy existed in the first place:
a failure on the remote side and a failure reaching it are diagnosed by looking in completely
different places. `clipbridge.log` lives beside `config.json` in `%LOCALAPPDATA%\clipbridge\`,
is capped at 7 days, and is stamped in UTC with a trailing `Z`.

Every one of those outcomes ends in exactly one synthesized paste. That is the invariant the
whole tool rests on - see "The two components" above.

## Security properties

- **No new inbound port** on either machine. Both directions ride ssh that already exists —
  `sshd` already listens on `devsbx01:22`, and the laptop already authenticates to it.
- **No dedicated key.** An earlier version of this design generated a dedicated ed25519 key
  restricted with `restrict,command="/home/vollmin/.local/bin/clipbridge-recv"` in
  `authorized_keys`, so the credential could never open a shell. It was removed 2026-08-19,
  because it broke the user's own access to `devsbx01`: the key went into the shared 1Password
  SSH agent, which offers every key it holds to any client that doesn't pin identities. Windows'
  ssh config pins identities globally, so `ssh.exe` was unaffected — but `mosh` runs inside WSL,
  whose ssh config has no such pinning, so WSL offered the restricted key, `sshd` accepted it,
  and the forced command's implicit `no-pty` killed `mosh`, locking the user out of their own
  box until the key was pulled from `authorized_keys`. The lesson: a `command=` restriction
  attaches to a *key*, not a destination, so adding it to a shared agent changes behavior for
  every client on that machine that doesn't pin identities — and those clients can't be
  reliably enumerated in advance. The marginal security was also thin to begin with: the same
  agent already holds a key that opens a full shell on this exact host, so a shell-less
  credential next to it wasn't buying much. clipbridge now authenticates with the user's
  ordinary `devsbx01` key and names the remote command explicitly on the ssh command line
  (`SshArgumentBuilder.DefaultRemoteCommand`, `/home/vollmin/.local/bin/clipbridge-recv`)
  instead of restricting via `authorized_keys`. `~/.ssh/config`'s `Host clipbridge` block still
  sets `IdentitiesOnly yes` — now to keep ssh from working through the agent's ~27 other keys —
  and `ForwardAgent no`, since clipbridge never authenticates onward from `devsbx01` and a
  forwarded agent there would be needless exposure.
- **Storage is locked down and self-healing.** Images are written 0600 into a 0700 directory,
  and `clipbridge-recv` re-applies `chmod 700` on the directory *every run*, unconditionally —
  it doesn't trust a prior run or a stray `chmod -R` to have left the permission alone. The kill
  switch for this tool is the presence of `clipbridge-recv` on `devsbx01` — reinstalling to a
  no-op or removing it from `~/.local/bin` disables the receiver — not filesystem permissions on
  its own storage directory. Both bounds — 50 files, 7 days — are enforced on every invocation,
  because screenshots routinely contain tokens, dashboards, and account data, and a burst of
  them shouldn't sit around for a week just because the count cap hasn't been hit yet.
- **The path is pasted unquoted.** It goes straight into whatever has focus, with no shell and
  no quoting around it — which is exactly why `PathValidator` is strict: one line, absolute, and
  only printable ASCII (`^/[\x21-\x7E]+\z`). That excludes space, so a filename never needs
  quoting; it excludes control characters and non-ASCII; and it uses `\z` rather than `$` as the
  end anchor, because .NET's `$` also matches immediately before a single trailing newline. A
  path with one trailing `\n` would otherwise pass — and a newline reaching the prompt is Enter,
  submitting the message before the user has written their question.
- **No credential ever touches the repo.** `devsbx01`'s ssh key already lived on the laptop
  before clipbridge existed and is managed outside this repo entirely; clipbridge writes no key
  material anywhere, on either machine.
