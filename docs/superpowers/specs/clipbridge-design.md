# clipbridge — design

**Date:** 2026-08-18
**Status:** Approved, not yet implemented
**Author:** Scott Vollmin (with Claude)

## Problem

Screenshots are taken on a Windows laptop. Claude Code runs on `devsbx01`
(192.168.152.3), reached over mosh inside tmux. There is no path for an image to get
from the Windows clipboard into a Claude Code prompt short of saving a file and copying
it across with `scp`, which is enough friction that screenshots don't get shared at all.

The goal is a single keystroke on the laptop that puts a screenshot in front of Claude.

## Constraints

These are measured facts about the environment, not assumptions.

| Fact | Consequence |
|---|---|
| The session is **mosh**, not ssh (`192.168.120.217 via mosh`) | No port forwarding. Any design that tunnels a socket back to the laptop is impossible. |
| `devsbx01` has no `DISPLAY` and no Wayland socket | X11 forwarding + `xclip` is not available, even though `xclip` is installed. |
| Three concurrent tmux sessions, **all running `claude`** | Choosing the destination pane is real work, not a formality. |
| `tmux list-clients -F '#{client_activity}'` is populated and accurate | The pane the user most recently typed in is discoverable. |
| The Windows clipboard is readable only by a process on Windows | Some component must run on the laptop. This is not negotiable by architecture. |
| `sshd` already listens on `devsbx01:22`; the laptop already authenticates to it | No new inbound port is needed on either machine. |

## Non-goals

- Copied files from Explorer, copied text, or any clipboard format other than an image.
- Transfers in the other direction (`devsbx01` → laptop).
- Any machine other than the Windows laptop and `devsbx01`.

## Architecture

Three components. The Windows half is split so that the hotkey binding stays trivial and
all the logic lives somewhere testable.

```
Screenshot  →  Windows clipboard
                    │
             Ctrl+Shift+V
                    │
            clipbridge.ahk          (tray, ~30 lines: bind key, spawn, beep on failure)
                    │
             Send-Clip.ps1          (grab bitmap → PNG → stream over ssh)
                    │
                   ssh              (existing sshd, restricted key)
                    │
            clipbridge-recv         (devsbx01: validate → save → prune → inject)
                    │
          tmux send-keys -l         (path typed into the active pane, no Enter)
                    │
        "/home/vollmin/.clipbridge/20260818-024715.png ▊"
```

### 1. `clipbridge.ahk` — Windows, resident

AutoHotkey v2 script in the tray. Binds `Ctrl+Shift+V` (configurable). Its entire job is
to launch `Send-Clip.ps1` hidden and translate its exit code into audible feedback:

| Exit code | Feedback |
|---|---|
| 0 | Short confirmation beep |
| 2 (no image on clipboard) | Distinct two-tone "nothing to do" beep |
| any other | Error beep + tray balloon naming the log file |

It deliberately contains no clipboard, file, or network logic. Anything it did would be
untestable, since AHK cannot be exercised in CI.

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
Start-Process -FilePath $exe -ArgumentList $sshArgs `
    -RedirectStandardInput $tmp -RedirectStandardOutput $out -RedirectStandardError $err `
    -NoNewWindow -Wait -PassThru
```

`Start-Process -RedirectStandardInput` takes a **file path** and hands the raw bytes to
the child process. This is deliberate: PowerShell's `|` operator converts objects to text
and corrupts binary, which is the classic way this kind of tool breaks.

**Transport is auto-detected at install time, not hardcoded.** `Install-Clipbridge.ps1`
probes both `ssh.exe` (native Windows OpenSSH) and `wsl.exe -e ssh` against the target and
pins whichever authenticates into `%LOCALAPPDATA%\clipbridge\config.json`. The laptop's
`authorized_keys` entries on `devsbx01` are all `#ssh.id @vollmin` (1Password SSH agent),
which does not reveal which client is in use — detection is correct here, guessing is not.

```json
{
  "sshHost": "clipbridge",
  "transport": "ssh"
}
```

The hotkey itself is set at the top of `clipbridge.ahk`, not in this file — AutoHotkey v2
has no JSON parser, and a config only one of the two components can read is a trap.

The installer writes a `Host clipbridge` block into the laptop's `~/.ssh/config`, the same
pattern `VMDeployTools` already uses. `Send-Clip.ps1` therefore never carries a hostname,
user, port, or key path.

### 3. `clipbridge-recv` — devsbx01, `~/.local/bin/clipbridge-recv`

POSIX `sh`. Chosen over Python because it must be testable with stubbed binaries under both
`dash` and `busybox ash`, matching the established pattern of
`velero-pvb-healer/app/heal_test.sh`, and because it adds no runtime dependency.

Reads the image on stdin and does four things:

**Validate.** Write stdin to a temp file first, then check the magic bytes — this avoids
consuming the stream before the real write:

```sh
cat > "$tmp"
magic=$(od -An -tx1 -N8 "$tmp" | tr -d ' \n')
[ "$magic" = "89504e470d0a1a0a" ] || { echo "clipbridge: not a PNG (magic=$magic)" >&2; exit 3; }
[ -s "$tmp" ] || { echo "clipbridge: empty input" >&2; exit 3; }
```

**Store.** `~/.clipbridge/YYYYmmdd-HHMMSS.png`, mode 0600, directory 0700. On a
same-second collision, append `-2`, `-3`, … rather than overwriting.

**Prune.** Both bounds are applied on every run, and a file is deleted if it fails
*either*: it is outside the newest 50, or it is older than 7 days. Two rules because a
burst of screenshots should not be retained for a week, and a single screenshot should not
live forever.

**Inject.**

```sh
sess=$(tmux list-clients -F '#{client_activity} #{client_session}' 2>/dev/null \
       | sort -rn | head -1 | cut -d' ' -f2)
if [ -z "$sess" ]; then
    echo "$path"                      # no client attached — saved, not injected
    echo "clipbridge: no tmux client attached, not injecting" >&2
    exit 0
fi
pane=$(tmux list-panes -s -t "$sess" -F '#{pane_id} #{window_active}#{pane_active}' \
       | awk '$2=="11"{print $1; exit}')
tmux send-keys -t "$pane" -l "$path "
echo "$path"
```

Three deliberate choices here:

- **`-l` (literal) and no `Enter`.** The path is inserted for you to write a question
  around. Auto-submitting a bare path would waste a turn every single time.
- **A bare absolute path, not `@path`.** Claude Code's `@` opens an autocomplete overlay,
  and driving an interactive picker with synthetic keystrokes is fragile.
- **`list-panes -s`** scopes to the whole session; without `-s`, `-t <session>` resolves to
  a window and silently returns the wrong set.

The newest `client_activity` wins because that value is the last time *input was received*
from that client — it identifies the pane being typed in, not the pane producing output.
Claude Code writes to all three panes constantly, so an output-based signal such as
`window_activity` would be noise.

## Data flow

1. Screenshot lands on the Windows clipboard.
2. `Ctrl+Shift+V`.
3. AHK spawns `Send-Clip.ps1` hidden.
4. PowerShell extracts a PNG to `%TEMP%`.
5. `ssh clipbridge` streams those bytes; the restricted key forces `clipbridge-recv`.
6. Receiver validates, writes `~/.clipbridge/<ts>.png`, prunes.
7. Receiver resolves the last-typed pane and types the path into it.
8. Path appears at the Claude Code prompt with the cursor after it; you type your question
   and press Enter.

Round trip is one LAN ssh connection plus a PowerShell start — expected to be well under a
second, dominated by process spawn rather than transfer.

## Error handling

No failure is silent, and no failure is reported without its reason.

| Condition | Exit | Behavior |
|---|---|---|
| No image on clipboard | 2 | Distinct beep. Explicitly not a no-op — an unresponsive hotkey is indistinguishable from a broken one. |
| SSH unreachable / auth failure | 4 | Reason (ssh's own stderr) appended to `%LOCALAPPDATA%\clipbridge\clipbridge.log`; error beep + balloon. |
| Non-PNG or empty stdin | 3 | Receiver writes the reason to stderr; ssh relays it into the same log. |
| Receiver cannot write `~/.clipbridge` | 5 | Reason logged; error beep. |
| No tmux client attached | 0 | **File is still saved** and its path printed. Degraded, not failed — the image is recoverable by hand. |
| `send-keys` fails (pane died mid-flight) | 0 | Same: file kept, warning to stderr. |

The log is append-only with a timestamp per entry and is capped by the same 7-day rule as
the images.

## Testing

**`clipbridge-recv`** — `linux/clipbridge-recv_test.sh`, pure shell, no cluster or tmux
server required. A stub `tmux` earlier on `PATH` records its arguments to a file; a fixture
PNG and a fixture non-PNG are fed on stdin. Cases:

- valid PNG → file created, mode 0600, path printed, `send-keys` called with the right pane
- non-PNG input → exit 3, no file created
- empty stdin → exit 3
- no tmux clients → exit 0, file created, no `send-keys`
- same-second collision → second file gets a `-2` suffix, first is untouched
- 51 existing files → oldest pruned, newest 50 retained
- file older than 7 days → deleted even when under the count cap

Must pass under `dash` **and** `busybox ash`.

**`Send-Clip.ps1`** — Pester tests under `windows/tests/`, mirroring `VMDeployTools/tests`.
Cases: transport resolution from config, missing/invalid config, the no-image exit-2 path,
and that the ssh invocation is built with `-RedirectStandardInput` pointed at the temp file
(guarding against a future refactor reintroducing a text-mangling pipe).

**CI** — `.github/workflows/test.yml`: an `ubuntu-latest` job running `shellcheck` and the
shell tests under both shells, plus a `windows-latest` job running Pester. Making these
required checks is a `github-admin` (OpenTofu) change and is explicitly **out of scope for
this repo's PRs** — the escape hatch `vars.CI_RUNNER` must exist before any check is made
required.

## Security

- **No new inbound port** on the laptop or `devsbx01`. The design rides the existing sshd.
- **A dedicated ed25519 key**, generated into 1Password (Homelab vault, item
  `Clipbridge SSH Key`, tagged `Homelab`), pinned on `devsbx01` as:

  ```
  restrict,command="/home/vollmin/.local/bin/clipbridge-recv" ssh-ed25519 AAAA...
  ```

  `restrict` implies `no-pty`, `no-port-forwarding`, `no-agent-forwarding` and
  `no-X11-forwarding`, so they are not listed separately. The `command=` restriction is
  the point: this credential cannot open a shell. A paste
  hotkey should not carry the same authority as an interactive login, and the marginal cost
  is one `authorized_keys` line.
- Images are written 0600 into a 0700 directory and pruned. Screenshots routinely contain
  tokens, dashboards, and account data.
- No credential is ever written to the repo. The private key lives in the 1Password SSH
  agent (or, if the agent is not in use, at `%USERPROFILE%\.ssh\clipbridge_ed25519`); only
  the public key is placed on `devsbx01`, by hand, as part of install.

## Rejected alternatives

**Syncthing shared folder.** Syncthing is already running on `devsbx01` (`:22000`) and
reaches the Windows side. It would move the file with no new code at all — but it provides
no pane injection, so the path still has to be typed, and sync latency makes the moment of
arrival unpredictable. It solves the transport, which was the easy half.

**Remote port forward + pull.** `ssh -R` a socket back to a clipboard service on the
laptop, then run `clipimg` on `devsbx01`. Clean, and impossible: the session is mosh, which
has no forwarding. It also still requires a listener process on Windows, so it does not
avoid the Windows component — it only adds a firewall rule and a DHCP dependency.

**X11 forwarding + `xclip`.** No `DISPLAY` on `devsbx01`, and it would drag an X
dependency into a headless box to move one PNG.

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
8. Verify by running `./scripts/sync-docs-to-vault.sh && ./scripts/enforce-graph-colors.sh`.

Branch protection for the new repo is an OpenTofu change in `github-admin`, not a UI
change, and is a separate PR there.

## Open verification item

`tmux send-keys -l` writes bytes to the pane's pty exactly as a keyboard would, so Claude
Code's prompt should accept them like typed input. This is the one behavior in the design
that has not been exercised on this box. It must be confirmed against a live Claude Code
pane **before** the rest of the implementation is built out — if the TUI's input handling
rejects synthetic keystrokes, the injection step falls back to the stable-path variant
(write the file, print the path, type it yourself) and the rest of the design is unaffected.
