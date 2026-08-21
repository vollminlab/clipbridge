# clipbridge

> One keystroke on a Windows laptop puts a screenshot in front of Claude Code on a remote box.

Take a screenshot, click into the terminal running the Claude Code session you want, and
press `Ctrl+V`. The image is streamed to `devsbx01` over the SSH that is already there,
and its path is **pasted** into that prompt — Claude Code attaches the image on its own
from there. No `scp`, no file juggling.

Pasted, not typed, and the distinction is the whole trick: Claude Code scans *pasted* text
for image paths that exist on the local filesystem. Text arriving as keystrokes is never
scanned, so a typed path attaches nothing.

Targeting is by window focus, so it works with any number of parallel sessions. Text on
the clipboard pastes normally, and any failure falls through to an ordinary paste, so
`Ctrl+V` never breaks.

## The two halves

| | |
|---|---|
| `linux/clipbridge-recv` | POSIX `sh` on `devsbx01`. Reads a PNG on stdin, stores it, prunes, prints the path. Storage only — it has no idea which session the image is for. |
| `dotnet/` → `clipbridge.exe` | One resident `win-x64` NativeAOT binary on the laptop: keyboard hook, clipboard, ssh transport, tray icon. |

The wire protocol between them is PNG on stdin, one absolute path on stdout. It never
depended on what was sending it, which is why the Windows client was rewritten from
PowerShell + AutoHotkey to C# without touching a line of the Linux side.

## Install

`clipbridge.exe` is built by CI — NativeAOT cannot cross-compile, so a Windows runner is
the only thing that produces it. Download the `clipbridge-win-x64` artifact from the
latest [`main` run](https://github.com/vollminlab/clipbridge/actions), extract it
somewhere stable, then:

```powershell
.\ClipBridge.App.exe --install     # probes ssh, writes ~/.ssh/config + config.json, adds a Start Menu entry
```

Then launch **clipbridge** from the Start Menu. It sits in the tray; there is no window.

On the Linux side, `linux/install.sh` puts the receiver in `~/.local/bin` and prints a
command to verify it.

## Docs

- [`docs/clipbridge-architecture.md`](docs/clipbridge-architecture.md) — how it works, and why the
  awkward parts are the way they are
- [`CLAUDE.md`](CLAUDE.md) — repo conventions, testing, and the gotchas worth not rediscovering
