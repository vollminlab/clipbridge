# clipbridge

> One keystroke on a Windows laptop puts a screenshot in front of Claude Code on a remote box.

Take a screenshot, click into the terminal running the Claude Code session you want, and
press `Ctrl+V`. The image is streamed to `devsbx01` over the SSH that is already there,
and its path is typed into that prompt — Claude Code attaches the image on its own from
there. No `scp`, no file juggling.

Targeting is by window focus, so it works with any number of parallel sessions. Text on
the clipboard pastes normally, and any failure falls through to an ordinary paste, so
`Ctrl+V` never breaks.

**Status: design approved and fully verified; implementation not yet written.**
See [`docs/superpowers/specs/clipbridge-design.md`](docs/superpowers/specs/clipbridge-design.md).
