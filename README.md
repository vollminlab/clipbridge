# clipbridge

> One keystroke on a Windows laptop puts a screenshot in front of Claude Code on a remote box.

Take a screenshot on the laptop, click into the terminal running the Claude Code session
you want, and press `Ctrl+Shift+V`. The image is streamed to `devsbx01` over the SSH that
is already there, and its path is typed into that prompt for you — no `scp`, no file
juggling. Targeting is by window focus, so it works with any number of parallel sessions.

**Status: design approved, pending pre-build verification.**
See [`docs/superpowers/specs/clipbridge-design.md`](docs/superpowers/specs/clipbridge-design.md).
