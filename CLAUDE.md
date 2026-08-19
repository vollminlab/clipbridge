# CLAUDE.md — clipbridge

Screenshot from a Windows laptop clipboard into a Claude Code prompt on `devsbx01`, one
keystroke, no `scp`. Full design and rationale: `docs/clipbridge-architecture.md`.

## Repo layout

```
linux/clipbridge-recv           POSIX sh, devsbx01. Validates, stores, prunes. Storage only.
linux/clipbridge-recv_test.sh   Shell tests — no cluster, no tmux, no network required.
linux/install.sh                Installs the receiver, prints a verify command.
windows/Send-Clip.ps1           Clipboard extraction + transport. Runs under powershell.exe -STA.
windows/tests/Send-Clip.Tests.ps1   Pester tests — run on Linux via pwsh, not just in CI.
windows/clipbridge.ahk          NOT YET BUILT. Hotkey binding + injection. See design doc Task list.
verify/                         One-off measurement probes behind the design doc's Verified findings.
docs/clipbridge-architecture.md Architecture record.
docs/superpowers/specs/         Design spec.
docs/superpowers/plans/         Implementation plan (task-by-task).
```

`clipbridge.ahk` holds no clipboard, file, or network logic, and that's deliberate, not an
oversight to fix later. AHK cannot be exercised in CI — there's no way to assert on what it did.
So all logic that can be wrong lives in `clipbridge-recv` and `Send-Clip.ps1`, where it's
testable, and the AHK script's only job is `RunWait`, read a file, `SendText`. Don't move logic
into the AHK script to "simplify" the Windows side — that moves it out of test coverage.

## Testing

**Shell tests must pass under both `dash` and `busybox ash` — always run both, not just one:**

```bash
dash linux/clipbridge-recv_test.sh
busybox ash linux/clipbridge-recv_test.sh
shellcheck -s sh linux/clipbridge-recv linux/clipbridge-recv_test.sh linux/install.sh
```

**PowerShell tests run on Linux**, via `pwsh` (Pester 6, confirmed to accept Pester 5 syntax —
`BeforeAll`, `Should -Be`, `Should -Throw -ExpectedMessage`, `Mock` — unchanged):

```bash
pwsh -NoProfile -Command "Invoke-Pester ./windows/tests/Send-Clip.Tests.ps1 -Output Detailed"
```

No waiting on CI for the PowerShell half — develop it test-first on Linux exactly like the
shell half.

## The one rule that makes this safe to leave installed

**No failure path may leave `Ctrl+V` dead.** Every error in `clipbridge.ahk` falls through to
an ordinary paste — a paste key that silently does nothing when the far side is unreachable is
worse than no tool at all. If you're touching the AHK script, every new failure branch needs
its own beep/log and still has to end in `Send("^v")`. This is not a style preference; it's the
property the whole tool depends on to be trustworthy enough to bind over the real paste key.

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
