# clipbridge — design

**Date:** 2026-08-18
**Status:** Approved. All verification items measured green on 2026-08-18.
**Author:** Scott Vollmin (with Claude)

## Problem

Screenshots are taken on a Windows laptop. Claude Code runs on `devsbx01`
(192.168.152.3), reached over mosh inside tmux. There is no path for an image to get from
the Windows clipboard into a Claude Code prompt short of saving a file and copying it
across with `scp`, which is enough friction that screenshots don't get shared at all.

The goal is a single keystroke on the laptop that puts a screenshot in front of Claude,
**in whichever Claude Code session is currently on screen** — there are several running in
parallel at any time, across different tmux sessions and windows.

## Constraints

Measured on 2026-08-18, not assumed.

| Fact | Consequence |
|---|---|
| The session is **mosh**, not ssh (`192.168.120.217 via mosh`) | No port forwarding. Any design that tunnels a socket back to the laptop is impossible. |
| `devsbx01` has no `DISPLAY` and no Wayland socket | X11 forwarding + `xclip` unavailable, even though `xclip` is installed. |
| Several concurrent Claude Code sessions, across different tmux sessions **and** windows | Target selection must resolve to the one on screen, and nothing on the remote side knows which that is. |
| The Windows clipboard is readable only by a process on Windows | Some component must run on the laptop. Not negotiable by architecture. |
| `sshd` already listens on `devsbx01:22`; the laptop already authenticates to it | No new inbound port needed on either machine. |

## Non-goals

- Copied files from Explorer, copied text, or any clipboard format other than an image.
- Transfers in the other direction (`devsbx01` → laptop).
- Any machine other than the Windows laptop and `devsbx01`.

## Verified findings

These were established experimentally before the design was settled. They are recorded
because two of them killed an earlier version of it.

**`tmux send-keys -l` into a live Claude Code TUI works.** A detached session running
`claude` received a literal path into its prompt box with no `Enter`, no `@` autocomplete
overlay, and no mangling. Server-side injection was mechanically sound.

**`tmux` works with `TMUX`, `TMUX_PANE` and `TERM` stripped and no controlling tty**, which
is the environment a forced-command ssh session provides. Not a blocker.

**`#{client_activity}` is input-only.** Sampled twice across 20 idle seconds with a Claude
session actively rendering output the whole time: the value did not move. Terminal output
and redraws do not bump it, so it genuinely means "last time this client was typed at."

**And yet the "last typed pane" heuristic is wrong here.** Observed live: the selector
resolved to `hashlink` (116s) while the session actually being worked in was
`EyeDropClone` (187s). The mechanism was correct; the premise was not. "Last pane typed
in" only equals "pane you are looking at" if one session is worked at a time — but Claude
turns take minutes, so tabbing away and typing elsewhere while one thinks is the normal
workflow, not an edge case. The failure is silent in the worst way: the success beep
fires, nothing appears on screen, and the tool looks broken.

**This is why injection moved to the Windows side.** Focus is unambiguous on the laptop
and unknowable from `devsbx01`, so the injection belongs where the answer already exists.

## Architecture

Three components. The remote half does storage only; all targeting lives on the laptop,
where focus is known.

```
Screenshot  →  Windows clipboard
                    │
             Ctrl+Shift+V
                    │
            clipbridge.ahk        (tray: bind key, run grabber, type result, beep)
                    │
             Send-Clip.ps1        (clipboard → PNG → stream over ssh → write path file)
                    │
                   ssh            (existing sshd, restricted key)
                    │
            clipbridge-recv       (devsbx01: validate → save → prune → print path)
                    │
              path returned
                    │
            AHK SendText          (typed into the FOCUSED window, as if by keyboard)
                    │
        "/home/vollmin/.clipbridge/20260818-024715.png ▊"
```

Keystrokes flow Windows Terminal → mosh → tmux → Claude Code's prompt exactly as they
would from the keyboard. Nothing in the chain needs to know it is a tmux pane, or a pane
at all.

### 1. `clipbridge.ahk` — Windows, resident

AutoHotkey v2 script in the tray. Hotkeys are set at the top of the script, not in a
config file — AHK v2 has no JSON parser, and a config only one component can read is a
trap.

**`Ctrl+V`, scoped to the terminal window**, is the primary binding, because the target
behavior is "pasting a screenshot works," not "a special key exists":

```
#HotIf WinActive("ahk_exe WindowsTerminal.exe")
^v:: {
    if (!ClipboardHasImage())
        return Send("^v")          ; ordinary text paste, untouched
    if (!RunClipbridge())
        return Send("^v")          ; ANY failure falls through to a normal paste
}
#HotIf
```

`Ctrl+Shift+V` is bound unscoped as an explicit "force" — send the image and give me the
path even if text is also on the clipboard.

**A failure must never leave `Ctrl+V` dead.** A paste key that silently does nothing when
the box is unreachable is worse than no tool at all, so every non-zero path ends in a
normal paste plus the usual beep and log line.

On a clipbridge run:

1. `RunWait` `Send-Clip.ps1` hidden, capture its exit code.
2. On exit 0, read the returned path from `%LOCALAPPDATA%\clipbridge\last-path.txt` and
   `SendText` it plus a trailing space into the active window.
3. Otherwise, beep according to the exit code and do not type anything.

| Exit code | Feedback |
|---|---|
| 0 | Path typed into the focused window; short confirmation beep |
| 2 (no image on clipboard) | Distinct two-tone "nothing to do" beep, nothing typed |
| any other | Error beep + tray balloon naming the log file, nothing typed |

The path is handed over through a file rather than stdout because AHK v2 cannot capture a
child process's stdout without `WScript.Shell.Exec`, and a file is simpler to assert on in
a test.

Filenames are deliberately free of spaces (see Store, below), so the typed path never
needs quoting.

### 2. `Send-Clip.ps1` — Windows

Runs under `powershell.exe -STA` (Windows PowerShell 5.1, always present; STA is required
for clipboard access).

**Clipboard extraction, preferring the lossless path:**

```powershell
$dobj = [System.Windows.Forms.Clipboard]::GetDataObject()
if ($dobj.GetDataPresent("PNG")) {
    # Snipping Tool and browsers publish a real PNG stream — use it verbatim,
    # preserving alpha and avoiding a re-encode.
    $stream = $dobj.GetData("PNG")
} else {
    $img = [System.Windows.Forms.Clipboard]::GetImage()   # falls back to a DIB
    if ($null -eq $img) { exit 2 }
    $img.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
}
```

The `PNG` format check matters: `Clipboard::GetImage()` goes through a device-independent
bitmap, which flattens transparency to black. Screenshots are opaque so it rarely shows,
but the fallback should be the fallback.

**Transport:** the PNG is streamed to `devsbx01` on stdin.

```powershell
$p = Start-Process -FilePath $exe -ArgumentList $sshArgs `
    -RedirectStandardInput $tmp -RedirectStandardOutput $out -RedirectStandardError $err `
    -NoNewWindow -Wait -PassThru
```

`Start-Process -RedirectStandardInput` takes a **file path** and hands the raw bytes to the
child. This is deliberate: PowerShell's `|` converts objects to text and corrupts binary,
which is the classic way this kind of tool breaks.

On success the single line of stdout (the remote path) is written to
`%LOCALAPPDATA%\clipbridge\last-path.txt` for AHK to pick up. The temp PNG is deleted.

**Transport is auto-detected at install time, not hardcoded.** `Install-Clipbridge.ps1`
probes both `ssh.exe` (native Windows OpenSSH) and `wsl.exe -e ssh` against the target and
pins whichever authenticates into `%LOCALAPPDATA%\clipbridge\config.json`:

```json
{
  "sshHost": "clipbridge",
  "transport": "ssh"
}
```

The laptop's `authorized_keys` entries on `devsbx01` are all `#ssh.id @vollmin`
(1Password SSH agent), which does not reveal which client is in use — detection is correct
here, guessing is not.

The installer writes a `Host clipbridge` block into the laptop's `~/.ssh/config`, the same
pattern `VMDeployTools` already uses, so `Send-Clip.ps1` never carries a hostname, user,
port, or key path.

### 3. `clipbridge-recv` — devsbx01, `~/.local/bin/clipbridge-recv`

POSIX `sh`, roughly 15 lines. Chosen over Python because it must be testable with stubbed
binaries under both `dash` and `busybox ash`, matching the established pattern of
`velero-pvb-healer/app/heal_test.sh`, and because it adds no runtime dependency.

It reads the image on stdin and does three things — targeting is no longer its problem, so
it has no tmux dependency at all.

**Validate.** Write stdin to a temp file first, then check the magic bytes; this avoids
consuming the stream before the real write.

```sh
cat > "$tmp"
[ -s "$tmp" ] || { echo "clipbridge: empty input" >&2; exit 3; }
magic=$(od -An -tx1 -N8 "$tmp" | tr -d ' \n')
[ "$magic" = "89504e470d0a1a0a" ] || { echo "clipbridge: not a PNG (magic=$magic)" >&2; exit 3; }
```

**Store.** `~/.clipbridge/YYYYmmdd-HHMMSS.png`, mode 0600, directory 0700. No spaces in
the name, so the path AHK types never needs quoting. On a same-second collision append
`-2`, `-3`, … rather than overwriting.

**Prune.** Both bounds are applied on every run, and a file is deleted if it fails
*either*: it is outside the newest 50, or it is older than 7 days. Two rules, because a
burst of screenshots should not be retained for a week and a single screenshot should not
live forever.

Then it prints the absolute path on stdout and exits 0. That single line is the entire
contract with the Windows side.

## Data flow

1. Screenshot lands on the Windows clipboard.
2. You click into the terminal showing the Claude session you want (you were going to type
   your question there anyway) and press `Ctrl+V`.
3. AHK spawns `Send-Clip.ps1` hidden and waits.
4. PowerShell extracts a PNG to `%TEMP%`.
5. `ssh clipbridge` streams those bytes; the restricted key forces `clipbridge-recv`.
6. Receiver validates, writes `~/.clipbridge/<ts>.png`, prunes, prints the path.
7. PowerShell writes that path to `last-path.txt` and exits 0.
8. AHK types the path plus a space into the focused window.
9. You type your question after it and press Enter.

Round trip is one LAN ssh connection plus a PowerShell start, expected well under a
second and dominated by process spawn rather than transfer.

## Error handling

No failure is silent, and no failure is reported without its reason.

| Condition | Exit | Behavior |
|---|---|---|
| No image on clipboard | 2 | Distinct beep, nothing typed. Explicitly not a no-op — an unresponsive hotkey is indistinguishable from a broken one. |
| SSH unreachable / auth failure | 4 | ssh's own stderr appended to `%LOCALAPPDATA%\clipbridge\clipbridge.log`; error beep; nothing typed. |
| Non-PNG or empty stdin | 3 | Receiver writes the reason to stderr; ssh relays it into the same log. |
| Receiver cannot write `~/.clipbridge` | 5 | Reason logged; error beep; nothing typed. Also covers a failed `cat > $tmp` write and a failed final `chmod`/`mv` — any local write failure on the receiver side. |
| Cannot write the local temp PNG | 7 | Raised before ssh is ever invoked, so the failure is provably local. Scoped by call site rather than exception type: `UnauthorizedAccessException` does not derive from `IOException`, so a type filter would miss permission denials. |
| Configuration problem | 8 | Missing or malformed `config.json`, unknown transport, blank `sshHost`. Split out from 4 because a first run before `Install-Clipbridge.ps1` was reporting "ssh failed" for a purely local problem, sending debugging at the network. |
| Receiver printed no path / unparseable | 6 | Logged; error beep; nothing typed. The image may exist on the far side; the log records the raw stdout so it can be recovered. |
| Hotkey pressed with a non-terminal focused | — | `Ctrl+V` is scoped to the terminal, so this only applies to the unscoped `Ctrl+Shift+V` force binding. The path is typed into whatever has focus: visible, harmless, and recoverable — the file still exists and `last-path.txt` still holds its path. |
| Clipboard holds text, not an image | — | Ordinary paste. `Ctrl+V` behaves exactly as it did before clipbridge existed. |

Nothing is ever typed on a non-zero exit, and **every non-zero exit falls through to a
normal paste**. A tool that types a stale or partial path into a prompt is worse than one
that does nothing and says why — and a paste key that silently does nothing when the far
side is unreachable is worse than both.

The log is append-only with a timestamp per entry, capped by the same 7-day rule as the
images.

## Testing

**`clipbridge-recv`** — `linux/clipbridge-recv_test.sh`, pure shell, no tmux server or
cluster required. A fixture PNG and a fixture non-PNG are fed on stdin. Cases:

- valid PNG → file created, mode 0600, absolute path printed on stdout, exit 0
- non-PNG input → exit 3, no file created, reason on stderr
- empty stdin → exit 3, no file created
- same-second collision → second file gets a `-2` suffix, first untouched
- 51 existing files → oldest pruned, newest 50 retained
- file older than 7 days → deleted even when under the count cap
- unwritable target directory → exit 5, reason on stderr

Must pass under `dash` **and** `busybox ash`.

Fixtures are **generated at runtime**, not committed — a minimal valid PNG is a few dozen
bytes and can be emitted with `printf`. That keeps `*.png` globally ignored in
`.gitignore`, which is a safety property worth having: screenshots routinely contain
credentials, and a repo that cannot accept a `.png` cannot leak one.

**`Send-Clip.ps1`** — Pester tests under `windows/tests/`, mirroring `VMDeployTools/tests`.
Cases: transport resolution from config, missing/invalid config, the no-image exit-2 path,
that `last-path.txt` is written only on success, and that the ssh invocation is built with
`-RedirectStandardInput` pointed at the temp file (guarding against a future refactor
reintroducing a text-mangling pipe).

**`clipbridge.ahk`** is not unit-testable — AHK cannot be exercised in CI. This is the
reason it holds no clipboard, file, or network logic: its only untested behavior is
"`RunWait`, read a file, `SendText`."

**CI** — `.github/workflows/test.yml`: an `ubuntu-latest` job running `shellcheck` and the
shell tests under both shells, plus a `windows-latest` job running Pester. Making these
required checks is a `github-admin` (OpenTofu) change and is explicitly **out of scope for
this repo's PRs** — the `vars.CI_RUNNER` escape hatch must exist before any check is made
required.

## Verification results

All four items were measured on 2026-08-18 before any implementation. Each was run through
the production mechanism rather than an equivalent one.

| # | Item | Result |
|---|---|---|
| 1 | `SendText` survives Windows Terminal → mosh → tmux → Claude Code | **Pass.** The full 45-character path arrived complete and in order in Input mode. Event mode was not needed. |
| 2 | Which clipboard formats the screenshot tool publishes | **`PNG` stream present.** Formats: `System.Drawing.Bitmap`, `Bitmap`, `PNG`, `CanUploadToCloudClipboard`, `CanIncludeInClipboardHistory`. The lossless branch is the live path; `file` confirmed `8-bit/color RGBA` on the far side, so alpha survived. |
| 3 | Which ssh client authenticates | **`ssh.exe` only.** `wsl.exe -e ssh` returns `Permission denied (publickey)` — the 1Password agent serves the Windows client, and WSL has no key. A guess had even odds of picking a transport that cannot authenticate. |
| 4 | PNG byte integrity over `ssh` stdin | **Pass.** 10200 bytes, identical sha256 end to end via `Start-Process -RedirectStandardInput`. |

**A fifth fact was established by accident, and it is the one that closes the design.**
Claude Code scans pasted and typed text for file paths that exist on the local filesystem,
attaches any image it finds, and removes the path from the message. Observed directly:
`paste-cache` recorded the pasted text at `04:04:34.675` with the Linux path stripped
mid-line, and `image-cache/<session>/1.png` was written 10 ms later at `04:04:34.685` with
a hash identical to the transferred file. A Windows path in the same paste survived
untouched, because it does not exist on this filesystem.

So the consumption half of clipbridge needs no cooperation from anything: put the file on
the box, put its path in the prompt, and the image arrives. This was previously an
inference; it is now an observation.

Transport is therefore **pinned to `ssh.exe`** at install time by detection, exactly as
designed — the installer's probe is kept because the answer is machine-specific, not
because it is unknown here.

## Security

- **No new inbound port** on the laptop or `devsbx01`. The design rides the existing sshd.
- **A dedicated ed25519 key**, generated into 1Password (Homelab vault, item
  `Clipbridge SSH Key`, tagged `Homelab`), pinned on `devsbx01` as:

  ```
  restrict,command="/home/vollmin/.local/bin/clipbridge-recv" ssh-ed25519 AAAA...
  ```

  `restrict` implies `no-pty`, `no-port-forwarding`, `no-agent-forwarding` and
  `no-X11-forwarding`, so those are not listed separately. The `command=` restriction is
  the point: this credential cannot open a shell. A paste hotkey should not carry the same
  authority as an interactive login, and the marginal cost is one `authorized_keys` line.
- Images are written 0600 into a 0700 directory and pruned. Screenshots routinely contain
  tokens, dashboards, and account data.
- No credential is ever written to the repo. The private key lives in the 1Password SSH
  agent (or, if the agent is not in use, at `%USERPROFILE%\.ssh\clipbridge_ed25519`); only
  the public key is placed on `devsbx01`, by hand, during install.

## Rejected alternatives

**Server-side `tmux send-keys` injection.** The original design. Verified working
mechanically — a literal path lands cleanly in a Claude Code prompt, and `client_activity`
is a genuine input-only signal. Rejected because the *premise* fails: no signal available
on `devsbx01` identifies which session is on screen, and the best available proxy was
observed picking the wrong one during ordinary parallel work. Moving injection to the
laptop makes the question disappear rather than answering it better.

**Terminal-title targeting.** Turn on tmux `set-titles` with `#S`, have AHK read the
focused window's title and pass the session name to the receiver. Deterministic in
principle, but it needs a change to the tmux environment, depends on the OSC title
surviving the mosh hop and on Windows Terminal not suppressing application titles, and
still leaves the receiver resolving windows and panes. Strictly more moving parts than
typing into the window that already has focus.

**Syncthing shared folder.** Already running on `devsbx01` (`:22000`) and reaching the
Windows side; it would move the file with no new code. But it provides no injection, so
the path still has to be typed by hand, and sync latency makes arrival unpredictable. It
solves the easy half.

**Remote port forward + pull.** `ssh -R` a socket back to a clipboard service on the
laptop. Impossible: the session is mosh, which has no forwarding. It also still requires a
listener process on Windows, so it does not avoid the Windows component — it only adds a
firewall rule and a DHCP dependency.

**X11 forwarding + `xclip`.** No `DISPLAY` on `devsbx01`, and it drags an X dependency
into a headless box to move one PNG.

**Terminal-native image paste.** No terminal in this path (Windows Terminal → WSL → mosh →
tmux) transmits image data on input. Terminal image protocols such as Kitty's and iTerm2's
are output-only.

## Deliverables

```
clipbridge/
├── README.md
├── LICENSE
├── CLAUDE.md
├── .gitignore
├── .github/workflows/test.yml
├── docs/
│   ├── clipbridge-architecture.md
│   ├── clipbridge-installation.md
│   └── superpowers/specs/clipbridge-design.md
├── windows/
│   ├── clipbridge.ahk
│   ├── Send-Clip.ps1
│   ├── Install-Clipbridge.ps1
│   └── tests/Send-Clip.Tests.ps1
└── linux/
    ├── clipbridge-recv
    ├── clipbridge-recv_test.sh
    └── install.sh
```

Doc filenames are globally unique by construction (`clipbridge-architecture.md`, not
`architecture.md`), so no entry is needed in the vault's `renames` map — `architecture.md`
is already claimed twice.

## Org onboarding checklist

Required by the global rules for any new repo in the org:

1. `clipbridge/docs/` exists with at least one markdown doc — covered by the deliverables.
2. Add `clipbridge` to `repos=()` in `homelab-obsidian-vault/scripts/sync-docs-to-vault.sh`.
3. No `renames` entries needed (filenames are unique).
4. Create `homelab-obsidian-vault/repos/clipbridge/clipbridge.md` from the vault index
   template.
5. Add `[[clipbridge]]` to `homelab-obsidian-vault/Home.md`.
6. Add a graph color in `scripts/enforce-graph-colors.sh` and increment the threshold.
7. Add the runtime integration (laptop ↔ devsbx01) to `Home.md` if applicable.
8. Verify with `./scripts/sync-docs-to-vault.sh && ./scripts/enforce-graph-colors.sh`.

Branch protection for the new repo is an OpenTofu change in `github-admin`, not a UI
change, and is a separate PR there.
