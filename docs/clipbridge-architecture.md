# clipbridge — architecture

**Date:** 2026-08-18
**Status:** Design approved and fully verified. `clipbridge-recv` and `Send-Clip.ps1` are
implemented (on `feat/receiver` and `feat/windows-grabber`, not yet merged). `clipbridge.ahk`
is specified below but does not exist yet — see the design spec's Task list for what remains.

## What this is and why it exists

Screenshots get taken on a Windows laptop. The Claude Code session they're needed in runs on
`devsbx01`, reached over `mosh` inside `tmux`. There is no path from the Windows clipboard into
that prompt short of saving the image to a file and `scp`-ing it across — enough friction that
screenshots mostly just didn't get shared. clipbridge closes that gap: one keystroke on the
laptop, and the screenshot's path lands in the prompt of whichever Claude Code session is
currently on screen.

## The three components

Each owns exactly one concern. Nothing does two jobs.

| Component | Runs on | Owns | Status |
|---|---|---|---|
| `clipbridge-recv` | `devsbx01` | Validate, store, prune. Storage only. | Implemented |
| `Send-Clip.ps1` | Windows laptop | Clipboard extraction, transport to `devsbx01`. | Implemented |
| `clipbridge.ahk` | Windows laptop | Hotkey binding, invoking the grabber, typing the result into the focused window. | **Not yet built** — specified in the design doc, not implemented |

`clipbridge-recv` (POSIX `sh`, `~/.local/bin/clipbridge-recv` on `devsbx01`) reads a PNG on
stdin, writes it to `~/.clipbridge/<timestamp>.png`, prunes old files, and prints the absolute
path on stdout. It has no idea which Claude Code session anything is going to, and doesn't need
to — targeting isn't its problem.

`Send-Clip.ps1` (Windows PowerShell 5.1, `-STA`) pulls the image off the clipboard, prefers the
lossless `PNG` clipboard format over the DIB fallback, streams the bytes to `devsbx01` over
`ssh.exe`, and writes the single returned path to `%LOCALAPPDATA%\clipbridge\last-path.txt`.

`clipbridge.ahk` is where `Ctrl+V` gets scoped to the terminal window, `Send-Clip.ps1` gets
run hidden, and the returned path gets typed into whatever has focus. It is described here as
designed, not as built, because it isn't built. Describing it otherwise would be documenting
the plan instead of the code.

## Data flow

```
Screenshot lands on the Windows clipboard
        │
Click into the terminal with the target Claude Code session, press Ctrl+V
        │
clipbridge.ahk runs Send-Clip.ps1 hidden, waits for it to exit
        │
Send-Clip.ps1 extracts a PNG to %TEMP%, streams it over ssh.exe to devsbx01
        │
clipbridge-recv validates the PNG magic, writes ~/.clipbridge/<ts>.png, prunes, prints the path
        │
Send-Clip.ps1 writes that path to last-path.txt, exits 0
        │
clipbridge.ahk reads last-path.txt, SendText's the path + a trailing space into the focused window
        │
Claude Code scans the pasted text, finds a path that exists on the local filesystem,
attaches the image, strips the path from the message
```

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

Get these from the code, not the design doc — the implementation added more granularity than
the original spec table.

### `clipbridge-recv` (`linux/clipbridge-recv`)

| Exit | Condition |
|---|---|
| `0` | PNG validated, stored, path printed on stdout |
| `3` | Bad input — empty stdin, or the first 8 bytes aren't the PNG magic (`89 50 4E 47 0D 0A 1A 0A`) |
| `5` | Cannot write — `mkdir -p ~/.clipbridge` failed, `chmod 700` failed, the directory isn't writable, `mktemp` failed, the `cat > $tmp` write failed, or the final `chmod 600` / `mv` into place failed |

### `Send-Clip.ps1` (`windows/Send-Clip.ps1`)

| Exit | Condition |
|---|---|
| `0` | Path written to `last-path.txt` |
| `2` | No image on the clipboard — not treated as an error, nothing is logged |
| `3` | ssh exited 3 — the receiver rejected the input (propagated verbatim) |
| `4` | ssh exited with anything other than 0, 3, or 5 (transport/auth failure), or an unhandled exception anywhere in the script |
| `5` | ssh exited 5 — the receiver couldn't write its storage directory (propagated verbatim) |
| `6` | ssh exited 0 but stdout wasn't exactly one non-blank, well-formed absolute path |
| `7` | Local failure extracting/writing the clipboard PNG to `%TEMP%`, before ssh was ever invoked |
| `8` | Configuration problem — `config.json` missing, malformed JSON, unknown `transport`, or blank `sshHost`, before ssh was ever invoked |

Exit codes `3` and `5` are the receiver's own codes, checked explicitly and passed straight
through rather than folded into the generic `4`. That distinction exists so a failure on the
remote side is never misreported as a transport failure — the two are diagnosed by looking in
completely different places (the receiver's stderr relayed into `clipbridge.log` vs. `ssh`
connectivity). Exit codes `7` and `8` don't appear in the original design spec at all: local
clipboard/temp-file trouble and configuration trouble both used to fall into the same "ssh
failed" bucket as a genuine transport failure. Splitting them out means the very first run
before `Install-Clipbridge.ps1` has ever executed logs "configuration problem: clipbridge config
not found" instead of "ssh exit 4" — which would have sent debugging toward the network instead
of toward the missing install step. This is a real place where the implementation is more
precise than the design doc, not a deviation from it.

## Security properties

- **No new inbound port** on either machine. Both directions ride ssh that already exists —
  `sshd` already listens on `devsbx01:22`, and the laptop already authenticates to it.
- **A dedicated, restricted key.** The `authorized_keys` entry on `devsbx01` reads:

  ```
  restrict,command="/home/vollmin/.local/bin/clipbridge-recv" ssh-ed25519 AAAA...
  ```

  `restrict` implies `no-pty`, `no-port-forwarding`, `no-agent-forwarding`, and
  `no-X11-forwarding`, so they aren't listed separately. `command=` is the load-bearing part:
  this credential cannot open a shell, no matter what command the client tries to send. A paste
  hotkey has no business carrying the same authority as an interactive login, and the cost is
  one `authorized_keys` line.
- **Storage is locked down and self-healing.** Images are written 0600 into a 0700 directory,
  and `clipbridge-recv` re-applies `chmod 700` on the directory *every run*, unconditionally —
  it doesn't trust a prior run or a stray `chmod -R` to have left the permission alone. The kill
  switch for this tool is the forced-command entry in `authorized_keys`, not filesystem
  permissions on its own storage directory. Both bounds — 50 files, 7 days — are enforced on
  every invocation, because screenshots routinely contain tokens, dashboards, and account data,
  and a burst of them shouldn't sit around for a week just because the count cap hasn't been
  hit yet.
- **The path is typed unquoted.** `SendText` puts the path directly into whatever has focus,
  with no shell and no quoting around it — which is exactly why `Test-RemotePath` in
  `Send-Clip.ps1` is strict: one line, absolute, and only printable ASCII
  (`^/[\x21-\x7E]+\z`). That excludes space, so a filename never needs quoting to type safely;
  it excludes non-ASCII, because `last-path.txt` is written with `-Encoding ASCII`, which
  doesn't throw on a non-ASCII byte — it silently substitutes `?`, which would corrupt the path
  with no error and no log line if the check didn't catch it first; and it uses `\z` rather than
  `$` as the end anchor, because .NET's `$` also matches immediately before a single trailing
  newline — a path with one trailing `\n` would otherwise pass, and `SendText` types a newline
  as Enter, submitting the prompt before anyone got to type their question after it.
- **No credential ever touches the repo.** The private key lives in the 1Password SSH agent (or,
  failing that, `%USERPROFILE%\.ssh\clipbridge_ed25519`); only the public key goes onto
  `devsbx01`, by hand, at install time.
