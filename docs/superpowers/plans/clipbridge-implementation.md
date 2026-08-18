# clipbridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One keypress on a Windows laptop puts a screenshot on `devsbx01` and its path into the focused Claude Code prompt.

**Architecture:** Three parts. `clipbridge-recv` (POSIX sh on devsbx01) takes a PNG on stdin, stores it, prints its path — storage only, no targeting. `Send-Clip.ps1` (Windows) pulls the image off the clipboard and streams it over the existing sshd via `Start-Process -RedirectStandardInput`. `clipbridge.ahk` binds `Ctrl+V` in the terminal, runs the grabber, and types the returned path into the focused window. Targeting is by window focus because focus is knowable on the laptop and unknowable on devsbx01.

**Tech Stack:** POSIX `sh` (must pass under `dash` and `busybox ash`), Windows PowerShell 5.1, AutoHotkey v2, OpenSSH (`ssh.exe`), Pester, shellcheck, GitHub Actions.

**Where the PowerShell tests actually run.** devsbx01 has `pwsh` 7.6.5 and Pester 6.1.0, so the
PowerShell half is developed test-first *on Linux*, exactly like the shell half — no waiting on
CI, no merging on faith. Measured 2026-08-18: Pester 6 accepts the Pester-5 syntax used here
(`BeforeAll`, `Should -Be`, `Should -Throw -ExpectedMessage`, `Mock`) unchanged.

**The constraint that falls out of it:** `Add-Type -AssemblyName System.Windows.Forms` **fails on
Linux** — the assembly is Windows-only. So no Windows-only API may be touched at *load* time, or
dot-sourcing the script in a test throws before any test runs. Every Windows-only call must sit
inside a function that tests mock away. That is why `Get-ClipboardDataObject` exists as a
one-line wrapper: it is the single seam where the platform-specific surface is quarantined.

**Design spec:** `docs/superpowers/specs/clipbridge-design.md`. All four verification items are measured green — do not re-litigate the transport or targeting decisions, they were tested.

---

## File structure

| File | Responsibility |
|---|---|
| `linux/clipbridge-recv` | Read PNG on stdin → validate → store 0600 → prune → print path. Nothing else. |
| `linux/clipbridge-recv_test.sh` | Shell tests, no cluster or tmux needed. Fixtures generated at runtime. |
| `linux/install.sh` | Copy the receiver to `~/.local/bin`, chmod, print the `authorized_keys` line to add. |
| `windows/Send-Clip.ps1` | Clipboard → PNG → ssh → `last-path.txt`. All Windows-side logic lives here. |
| `windows/Install-Clipbridge.ps1` | Detect which ssh client authenticates, write `config.json` and the `~/.ssh/config` Host block. |
| `windows/clipbridge.ahk` | Hotkeys only: run the grabber, read the path, `SendText`, beep. No clipboard/file/network logic. |
| `windows/tests/Send-Clip.Tests.ps1` | Pester tests for config resolution and the no-image path. |
| `.github/workflows/test.yml` | shellcheck + shell tests on ubuntu, Pester on windows. |
| `docs/clipbridge-architecture.md` | How the three parts fit; the "why focus, not tmux" record. |
| `docs/clipbridge-installation.md` | Install runbook for a rebuilt laptop. |
| `CLAUDE.md` | Repo conventions for future sessions. |

**Git workflow:** one branch per task group, PR per branch, never push to `main`. Branch names: `feat/receiver`, `feat/windows-grabber`, `feat/ahk-hotkey`, `feat/ci`, `docs/runbooks`.

---

## Task 1: Receiver — input validation

**Files:**
- Create: `linux/clipbridge-recv`
- Create: `linux/clipbridge-recv_test.sh`

Start branch: `git checkout main && git pull && git checkout -b feat/receiver`

- [ ] **Step 1: Write the failing test**

Create `linux/clipbridge-recv_test.sh`:

```sh
#!/bin/sh
# Tests for clipbridge-recv. No cluster, no tmux, no network.
# Run under both shells:  dash linux/clipbridge-recv_test.sh
#                         busybox ash linux/clipbridge-recv_test.sh
set -u

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
RECV="$SCRIPT_DIR/clipbridge-recv"
FAILED=0

pass() { echo "  PASS  $1"; }
fail() { echo "  FAIL  $1"; FAILED=$((FAILED + 1)); }

# The receiver validates only the 8-byte PNG signature, so a fixture needs a
# correct signature and nothing more. Calling this "a valid PNG" would overstate it.
make_png_sig() { printf '\211PNG\r\n\032\n' > "$1"; printf 'padding-bytes' >> "$1"; }

new_sandbox() {
    SANDBOX=$(mktemp -d)
    export CLIPBRIDGE_DIR="$SANDBOX/clip"
}
cleanup_sandbox() { rm -rf "$SANDBOX"; }

# --- valid signature is accepted -------------------------------------------
new_sandbox
make_png_sig "$SANDBOX/in.png"
out=$("$RECV" < "$SANDBOX/in.png" 2>"$SANDBOX/err"); rc=$?
if [ "$rc" -eq 0 ]; then pass "valid signature exits 0"; else fail "valid signature exited $rc: $(cat "$SANDBOX/err")"; fi
if [ -f "$out" ]; then pass "printed path exists on disk"; else fail "printed path does not exist: '$out'"; fi
case "$out" in
    /*) pass "printed path is absolute" ;;
    *)  fail "printed path is not absolute: '$out'" ;;
esac
mode=$(ls -l "$out" | cut -c1-10)
if [ "$mode" = "-rw-------" ]; then pass "stored file is 0600"; else fail "stored file mode is $mode, want -rw-------"; fi
cleanup_sandbox

# --- non-PNG is rejected ----------------------------------------------------
new_sandbox
printf 'this is not a png at all' > "$SANDBOX/in.bin"
out=$("$RECV" < "$SANDBOX/in.bin" 2>"$SANDBOX/err"); rc=$?
if [ "$rc" -eq 3 ]; then pass "non-PNG exits 3"; else fail "non-PNG exited $rc, want 3"; fi
if grep -q "not a PNG" "$SANDBOX/err"; then pass "non-PNG explains itself on stderr"; else fail "non-PNG gave no reason: $(cat "$SANDBOX/err")"; fi
if [ -z "$(ls -A "$CLIPBRIDGE_DIR" 2>/dev/null)" ]; then pass "non-PNG leaves no file behind"; else fail "non-PNG left files in $CLIPBRIDGE_DIR"; fi
cleanup_sandbox

# --- empty stdin is rejected ------------------------------------------------
new_sandbox
out=$("$RECV" < /dev/null 2>"$SANDBOX/err"); rc=$?
if [ "$rc" -eq 3 ]; then pass "empty stdin exits 3"; else fail "empty stdin exited $rc, want 3"; fi
if grep -q "empty input" "$SANDBOX/err"; then pass "empty stdin explains itself"; else fail "empty stdin gave no reason"; fi
cleanup_sandbox

echo
if [ "$FAILED" -eq 0 ]; then echo "all tests passed"; exit 0; else echo "$FAILED test(s) failed"; exit 1; fi
```

- [ ] **Step 2: Run it to verify it fails**

Run: `chmod +x linux/clipbridge-recv_test.sh && dash linux/clipbridge-recv_test.sh`
Expected: FAIL — every case errors because `linux/clipbridge-recv` does not exist.

- [ ] **Step 3: Write the minimal implementation**

Create `linux/clipbridge-recv`:

```sh
#!/bin/sh
# clipbridge-recv - read a PNG on stdin, store it, print its absolute path.
#
# Storage only. Targeting is the laptop's job: focus is knowable there and
# unknowable here. See docs/superpowers/specs/clipbridge-design.md.
#
# Exit codes: 0 ok | 3 bad input | 5 cannot write
set -u

CLIP_DIR="${CLIPBRIDGE_DIR:-$HOME/.clipbridge}"

die() { echo "clipbridge: $1" >&2; exit "$2"; }

mkdir -p "$CLIP_DIR" 2>/dev/null || die "cannot create $CLIP_DIR" 5
chmod 700 "$CLIP_DIR" 2>/dev/null || die "cannot chmod $CLIP_DIR" 5
[ -w "$CLIP_DIR" ] || die "cannot write $CLIP_DIR" 5

tmp=$(mktemp "$CLIP_DIR/.incoming.XXXXXX") || die "cannot create temp file in $CLIP_DIR" 5
trap 'rm -f "$tmp"' EXIT INT TERM

# Write stdin out first, then inspect the file. Peeking at the stream would
# consume the bytes we still need.
cat > "$tmp"

[ -s "$tmp" ] || die "empty input" 3

magic=$(od -An -tx1 -N8 "$tmp" | tr -d ' \n')
[ "$magic" = "89504e470d0a1a0a" ] || die "not a PNG (magic=$magic)" 3

path="$CLIP_DIR/$(date +%Y%m%d-%H%M%S).png"

chmod 600 "$tmp" || die "cannot chmod incoming file" 5
mv "$tmp" "$path" || die "cannot move incoming file into place" 5
trap - EXIT INT TERM

echo "$path"
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `chmod +x linux/clipbridge-recv && dash linux/clipbridge-recv_test.sh && busybox ash linux/clipbridge-recv_test.sh`
Expected: `all tests passed` from both shells.

- [ ] **Step 5: Run shellcheck**

Run: `shellcheck -s sh linux/clipbridge-recv linux/clipbridge-recv_test.sh`
Expected: no output. If it flags `SC2064` on the trap, the quoting above is already correct (single quotes defer expansion) — do not "fix" it into double quotes.

- [ ] **Step 6: Commit**

```bash
git add linux/clipbridge-recv linux/clipbridge-recv_test.sh
git commit -m "feat: receiver validates and stores an incoming PNG"
```

---

## Task 2: Receiver — same-second collision handling

**Files:**
- Modify: `linux/clipbridge-recv`
- Modify: `linux/clipbridge-recv_test.sh`

- [ ] **Step 1: Write the failing test**

Append to `linux/clipbridge-recv_test.sh`, immediately before the final `echo` / summary block:

```sh
# --- same-second collision gets a suffix, does not overwrite -----------------
new_sandbox
make_png_sig "$SANDBOX/in.png"
# Freeze the clock so both writes land in the same second.
STUBS="$SANDBOX/stubs"; mkdir -p "$STUBS"
printf '#!/bin/sh\necho 20260818-041500\n' > "$STUBS/date"
chmod +x "$STUBS/date"
OLD_PATH="$PATH"; PATH="$STUBS:$PATH"; export PATH

first=$("$RECV" < "$SANDBOX/in.png")
printf 'DIFFERENT-CONTENT' >> "$SANDBOX/in.png"
second=$("$RECV" < "$SANDBOX/in.png")

PATH="$OLD_PATH"; export PATH

if [ "$first" != "$second" ]; then pass "collision produces a distinct path"; else fail "collision reused the same path: $first"; fi
case "$second" in
    *-2.png) pass "collision suffix is -2" ;;
    *)       fail "collision suffix wrong: $second" ;;
esac
if [ -f "$first" ] && [ -f "$second" ]; then pass "both files survive a collision"; else fail "a collision destroyed one of the files"; fi
count=$(ls -1 "$CLIPBRIDGE_DIR"/*.png | wc -l)
if [ "$count" -eq 2 ]; then pass "collision leaves exactly 2 files"; else fail "collision left $count files, want 2"; fi
cleanup_sandbox
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dash linux/clipbridge-recv_test.sh`
Expected: FAIL — `collision reused the same path`, and the second write silently overwrote the first.

- [ ] **Step 3: Write the implementation**

In `linux/clipbridge-recv`, replace this line:

```sh
path="$CLIP_DIR/$(date +%Y%m%d-%H%M%S).png"
```

with:

```sh
base=$(date +%Y%m%d-%H%M%S)
path="$CLIP_DIR/$base.png"
n=2
while [ -e "$path" ]; do
    path="$CLIP_DIR/$base-$n.png"
    n=$((n + 1))
done
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dash linux/clipbridge-recv_test.sh && busybox ash linux/clipbridge-recv_test.sh`
Expected: `all tests passed` from both.

- [ ] **Step 5: Commit**

```bash
git add linux/clipbridge-recv linux/clipbridge-recv_test.sh
git commit -m "feat: suffix same-second filenames instead of overwriting"
```

---

## Task 3: Receiver — pruning

**Files:**
- Modify: `linux/clipbridge-recv`
- Modify: `linux/clipbridge-recv_test.sh`

Pruning applies **both** bounds every run: a file is deleted if it is outside the newest `KEEP_COUNT` **or** older than `KEEP_DAYS`.

- [ ] **Step 1: Write the failing test**

Append to `linux/clipbridge-recv_test.sh` before the summary block:

```sh
# --- prune by count ---------------------------------------------------------
new_sandbox
mkdir -p "$CLIPBRIDGE_DIR"
i=1
while [ "$i" -le 5 ]; do
    printf 'old' > "$CLIPBRIDGE_DIR/2026010$i-000000.png"
    touch -t "20260101000$i" "$CLIPBRIDGE_DIR/2026010$i-000000.png"
    i=$((i + 1))
done
make_png_sig "$SANDBOX/in.png"
CLIPBRIDGE_KEEP_COUNT=3 "$RECV" < "$SANDBOX/in.png" > /dev/null
count=$(ls -1 "$CLIPBRIDGE_DIR"/*.png | wc -l)
if [ "$count" -eq 3 ]; then pass "prune keeps exactly KEEP_COUNT files"; else fail "prune left $count files, want 3"; fi
if [ ! -f "$CLIPBRIDGE_DIR/20260101-000000.png" ]; then pass "prune deleted the oldest"; else fail "prune kept the oldest"; fi
cleanup_sandbox

# --- prune by age, even when under the count cap ----------------------------
new_sandbox
mkdir -p "$CLIPBRIDGE_DIR"
printf 'ancient' > "$CLIPBRIDGE_DIR/20200101-000000.png"
touch -t "202001010000" "$CLIPBRIDGE_DIR/20200101-000000.png"
make_png_sig "$SANDBOX/in.png"
CLIPBRIDGE_KEEP_COUNT=50 CLIPBRIDGE_KEEP_DAYS=7 "$RECV" < "$SANDBOX/in.png" > /dev/null
if [ ! -f "$CLIPBRIDGE_DIR/20200101-000000.png" ]; then pass "prune deletes by age under the count cap"; else fail "age-based prune did not fire"; fi
count=$(ls -1 "$CLIPBRIDGE_DIR"/*.png | wc -l)
if [ "$count" -eq 1 ]; then pass "the new file survives age pruning"; else fail "age prune left $count files, want 1"; fi
cleanup_sandbox
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dash linux/clipbridge-recv_test.sh`
Expected: FAIL — `prune left 6 files, want 3`, and the ancient file is still present.

- [ ] **Step 3: Write the implementation**

In `linux/clipbridge-recv`, add these two lines next to `CLIP_DIR` at the top:

```sh
KEEP_COUNT="${CLIPBRIDGE_KEEP_COUNT:-50}"
KEEP_DAYS="${CLIPBRIDGE_KEEP_DAYS:-7}"
```

and insert this block after `trap - EXIT INT TERM` but before `echo "$path"`:

```sh
# Both bounds, every run. A burst should not be retained for a week, and a
# single screenshot should not live forever.
ls -1t "$CLIP_DIR"/*.png 2>/dev/null | tail -n "+$((KEEP_COUNT + 1))" | while IFS= read -r old; do
    rm -f "$old"
done
# -exec ... \; rather than + : busybox find does not reliably support +.
find "$CLIP_DIR" -maxdepth 1 -name '*.png' -mtime "+$KEEP_DAYS" -exec rm -f {} \; 2>/dev/null
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dash linux/clipbridge-recv_test.sh && busybox ash linux/clipbridge-recv_test.sh`
Expected: `all tests passed` from both.

- [ ] **Step 5: Run shellcheck**

Run: `shellcheck -s sh linux/clipbridge-recv linux/clipbridge-recv_test.sh`
Expected: no output.

- [ ] **Step 6: Commit**

```bash
git add linux/clipbridge-recv linux/clipbridge-recv_test.sh
git commit -m "feat: prune stored images by both count and age"
```

---

## Task 4: Receiver — installer

**Files:**
- Create: `linux/install.sh`

> **Plan correction (2026-08-18).** This task originally opened with an
> unwritable-directory test that did `chmod 500 "$CLIPBRIDGE_DIR"` and expected exit 5,
> annotated "Expected: PASS — the `[ -w ]` guard already covers it." **That was wrong.**
> The script's own unconditional `chmod 700 "$CLIP_DIR"` repairs the lockdown before
> `mktemp` runs, because `chmod(2)` requires only ownership, not prior write permission —
> so the case exits 0, not 5. Exit-5 coverage was added during Task 1 review instead, by
> locking the *parent* directory so `mkdir -p` itself fails. Do not reintroduce the
> original test.

- [ ] **Step 1: Write the installer**

Create `linux/install.sh`:

```sh
#!/bin/sh
# Install clipbridge-recv into ~/.local/bin and print the authorized_keys line.
set -eu

SRC=$(cd "$(dirname "$0")" && pwd)/clipbridge-recv
DEST="${1:-$HOME/.local/bin/clipbridge-recv}"

[ -f "$SRC" ] || { echo "install: $SRC not found" >&2; exit 1; }

mkdir -p "$(dirname "$DEST")"
cp "$SRC" "$DEST"
chmod 755 "$DEST"
echo "installed $DEST"

cat <<EOF

Add this line to ~/.ssh/authorized_keys, substituting the clipbridge public key.
'restrict' implies no-pty, no-port-forwarding, no-agent-forwarding and
no-X11-forwarding; 'command=' is what stops this credential opening a shell.

restrict,command="$DEST" ssh-ed25519 AAAA... clipbridge

Then verify from the laptop:
  ssh clipbridge < some.png
EOF
```

- [ ] **Step 2: Install and smoke-test it live**

```bash
sh linux/install.sh
printf '\211PNG\r\n\032\n' > /tmp/sig.png && printf 'x' >> /tmp/sig.png
~/.local/bin/clipbridge-recv < /tmp/sig.png
ls -l ~/.clipbridge/
```
Expected: a path is printed, and `ls -l` shows one `-rw-------` file at that path.

- [ ] **Step 3: Commit and open the PR**

```bash
git add linux/install.sh
git commit -m "feat: add receiver installer"
git push -u origin feat/receiver
gh pr create --title "feat: clipbridge receiver" --body "POSIX sh receiver: validate a PNG on stdin, store it 0600, prune by count and age, print the path. Tests pass under dash and busybox ash. No tmux dependency — targeting is the laptop's job."
```

---

## Task 5: Restricted SSH key

**Files:** none in the repo. This task creates a credential and an `authorized_keys` entry.

No key material may be committed. Only the public key leaves 1Password.

- [ ] **Step 1: Generate the key into 1Password**

```bash
op item create --category "SSH Key" --title "Clipbridge SSH Key" --vault Homelab --tags Homelab \
  "notesPlain=Referenced by clipbridge. Restricted with command=clipbridge-recv on devsbx01. Do not rename fields."
```
Expected: the item is created and 1Password generates an ed25519 keypair.

- [ ] **Step 2: Read back the public key**

```bash
op item get "Clipbridge SSH Key" --vault Homelab --format json | python3 -c \
  "import json,sys; d=json.load(sys.stdin); print(next(f['value'] for f in d['fields'] if f.get('label')=='public key'))"
```
Expected: one `ssh-ed25519 AAAA... ` line. If the field label differs, list all labels with `[print(f.get('label')) for f in d['fields']]` and use the right one.

- [ ] **Step 3: Add the restricted entry to authorized_keys**

```bash
PUB="<paste the public key from step 2>"
printf 'restrict,command="%s/.local/bin/clipbridge-recv" %s clipbridge\n' "$HOME" "$PUB" >> ~/.ssh/authorized_keys
chmod 600 ~/.ssh/authorized_keys
tail -1 ~/.ssh/authorized_keys
```
Expected: the line ends with the comment `clipbridge` and begins with `restrict,command=`.

- [ ] **Step 4: Verify the key cannot open a shell**

From the laptop, after the ssh config block exists (Task 9), run:
```
ssh clipbridge
```
Expected: it does **not** give a shell. It runs `clipbridge-recv`, which blocks reading stdin; `Ctrl+C` out. Then confirm `ssh clipbridge whoami` also refuses to run `whoami` — the forced command wins. If either gives a shell or runs `whoami`, the `command=` restriction is not in effect and must be fixed before going further.

---

## Task 6: `Send-Clip.ps1` — config and transport resolution

**Files:**
- Create: `windows/Send-Clip.ps1`
- Create: `windows/tests/Send-Clip.Tests.ps1`

Start branch: `git checkout main && git pull && git checkout -b feat/windows-grabber`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/Send-Clip.Tests.ps1`:

```powershell
BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..\Send-Clip.ps1'
    . $script:ScriptPath -DotSourceOnly
}

Describe 'Get-ClipbridgeConfig' {
    BeforeEach {
        $script:CfgDir = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:CfgDir | Out-Null
    }
    AfterEach { Remove-Item -Recurse -Force $script:CfgDir -ErrorAction SilentlyContinue }

    It 'reads sshHost and transport from config.json' {
        '{ "sshHost": "clipbridge", "transport": "ssh" }' |
            Set-Content (Join-Path $script:CfgDir 'config.json')
        $cfg = Get-ClipbridgeConfig -ConfigDir $script:CfgDir
        $cfg.sshHost   | Should -Be 'clipbridge'
        $cfg.transport | Should -Be 'ssh'
    }

    It 'throws a named error when config.json is missing' {
        { Get-ClipbridgeConfig -ConfigDir $script:CfgDir } |
            Should -Throw -ExpectedMessage '*not found*'
    }

    It 'throws when transport is not ssh or wsl' {
        '{ "sshHost": "clipbridge", "transport": "carrier-pigeon" }' |
            Set-Content (Join-Path $script:CfgDir 'config.json')
        { Get-ClipbridgeConfig -ConfigDir $script:CfgDir } |
            Should -Throw -ExpectedMessage '*carrier-pigeon*'
    }
}

Describe 'Get-SshInvocation' {
    It 'uses ssh.exe with no prefix for the ssh transport' {
        $inv = Get-SshInvocation -Transport 'ssh' -SshHost 'clipbridge'
        $inv.Exe          | Should -Be 'ssh.exe'
        $inv.Arguments[0] | Should -Be 'clipbridge'
    }
    It 'uses wsl.exe with an -e ssh prefix for the wsl transport' {
        $inv = Get-SshInvocation -Transport 'wsl' -SshHost 'clipbridge'
        $inv.Exe          | Should -Be 'wsl.exe'
        $inv.Arguments[0] | Should -Be '-e'
        $inv.Arguments[1] | Should -Be 'ssh'
        $inv.Arguments[2] | Should -Be 'clipbridge'
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `pwsh -NoProfile -Command "Invoke-Pester ./windows/tests/Send-Clip.Tests.ps1 -Output Detailed"`
Expected: FAIL — `Send-Clip.ps1` does not exist.

- [ ] **Step 3: Write the minimal implementation**

Create `windows/Send-Clip.ps1`:

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
    Put the clipboard image on devsbx01 and record where it landed.
.DESCRIPTION
    Extracts a PNG from the clipboard, streams it to clipbridge-recv over ssh,
    and writes the returned remote path to last-path.txt for clipbridge.ahk.
    Exit codes: 0 ok | 2 no image | 3 remote rejected input | 4 ssh failed
                5 remote cannot write | 6 no usable path returned
#>
[CmdletBinding()]
param(
    [string] $ConfigDir     = (Join-Path $env:LOCALAPPDATA 'clipbridge'),
    [switch] $DotSourceOnly
)

function Get-ClipbridgeConfig {
    param([Parameter(Mandatory)][string] $ConfigDir)

    $path = Join-Path $ConfigDir 'config.json'
    if (-not (Test-Path $path)) {
        throw "clipbridge config not found at $path - run Install-Clipbridge.ps1"
    }
    $cfg = Get-Content $path -Raw | ConvertFrom-Json
    if ($cfg.transport -notin @('ssh', 'wsl')) {
        throw "clipbridge config has an unknown transport '$($cfg.transport)' - expected ssh or wsl"
    }
    if ([string]::IsNullOrWhiteSpace($cfg.sshHost)) {
        throw "clipbridge config has no sshHost"
    }
    return $cfg
}

function Get-SshInvocation {
    param(
        [Parameter(Mandatory)][ValidateSet('ssh', 'wsl')][string] $Transport,
        [Parameter(Mandatory)][string] $SshHost
    )
    if ($Transport -eq 'wsl') {
        return [pscustomobject]@{ Exe = 'wsl.exe'; Arguments = @('-e', 'ssh', $SshHost) }
    }
    return [pscustomobject]@{ Exe = 'ssh.exe'; Arguments = @($SshHost) }
}

if ($DotSourceOnly) { return }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `pwsh -NoProfile -Command "Invoke-Pester ./windows/tests/Send-Clip.Tests.ps1 -Output Detailed"`
Expected: 5 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add windows/Send-Clip.ps1 windows/tests/Send-Clip.Tests.ps1
git commit -m "feat: config and transport resolution for the Windows grabber"
```

---

## Task 7: `Send-Clip.ps1` — clipboard extraction

**Files:**
- Modify: `windows/Send-Clip.ps1`
- Modify: `windows/tests/Send-Clip.Tests.ps1`

Measured on 2026-08-18: the clipboard advertises a real `PNG` stream, so the lossless branch is the live path. The DIB fallback still exists for tools that publish only a bitmap.

- [ ] **Step 1: Write the failing test**

Append to `windows/tests/Send-Clip.Tests.ps1`:

```powershell
Describe 'Save-ClipboardPng' {
    It 'returns $null when the clipboard holds no image' {
        Mock -CommandName Get-ClipboardDataObject -MockWith { $null }
        Save-ClipboardPng -Path (Join-Path $env:TEMP 'never-written.png') | Should -BeNullOrEmpty
    }
    It 'prefers the PNG stream over the bitmap when both are present' {
        $bytes = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x41,0x42)
        $script:ms = New-Object System.IO.MemoryStream(,$bytes)
        Mock -CommandName Get-ClipboardDataObject -MockWith {
            $o = New-Object psobject
            $o | Add-Member ScriptMethod GetDataPresent { param($f) $f -eq 'PNG' } -PassThru |
                 Add-Member ScriptMethod GetData        { param($f) $script:ms }  -PassThru
        }
        $out = Join-Path $env:TEMP 'clipbridge-test-stream.png'
        Save-ClipboardPng -Path $out | Should -Be $out
        (Get-Item $out).Length | Should -Be 10
        [System.IO.File]::ReadAllBytes($out)[0..7] |
            Should -Be @(0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)
        Remove-Item $out -Force
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `pwsh -NoProfile -Command "Invoke-Pester ./windows/tests/Send-Clip.Tests.ps1 -Output Detailed"`
Expected: FAIL — `Save-ClipboardPng` and `Get-ClipboardDataObject` are not defined.

- [ ] **Step 3: Write the implementation**

Add to `windows/Send-Clip.ps1`, above the `if ($DotSourceOnly)` line.

**The `Add-Type` calls go INSIDE the function, not at file scope.** `System.Windows.Forms` does
not exist on Linux, and these tests run under `pwsh` on devsbx01 — loading it at file scope makes
dot-sourcing throw before any test executes. Keeping it inside the one function tests always mock
means the whole file stays dot-sourceable on Linux, and the assemblies load only on the machine
that has them.

```powershell
# The single seam where Windows-only APIs are quarantined. Tests mock this, which
# is also what keeps the file dot-sourceable on Linux (System.Windows.Forms is
# Windows-only and would throw at load time if this were at file scope).
function Get-ClipboardDataObject {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    return [System.Windows.Forms.Clipboard]::GetDataObject()
}

function Save-ClipboardPng {
    param([Parameter(Mandatory)][string] $Path)

    $dobj = Get-ClipboardDataObject
    if ($null -eq $dobj) { return $null }

    if ($dobj.GetDataPresent('PNG')) {
        # A real PNG stream: use it verbatim. GetImage() would route through a
        # device-independent bitmap and flatten transparency to black.
        $stream = $dobj.GetData('PNG')
        $fs = [System.IO.File]::Create($Path)
        try { $stream.Position = 0; $stream.CopyTo($fs) } finally { $fs.Dispose() }
        return $Path
    }

    # Windows-only, and never reached in tests: the no-image case returns above on a
    # null data object, and the PNG case takes the branch above. PowerShell resolves
    # types at runtime, so naming them here is harmless on Linux as long as this line
    # does not execute.
    $img = [System.Windows.Forms.Clipboard]::GetImage()
    if ($null -eq $img) { return $null }
    $img.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    return $Path
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `pwsh -NoProfile -Command "Invoke-Pester ./windows/tests/Send-Clip.Tests.ps1 -Output Detailed"`
Expected: 7 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add windows/Send-Clip.ps1 windows/tests/Send-Clip.Tests.ps1
git commit -m "feat: extract a PNG from the clipboard, preferring the lossless stream"
```

---

## Task 8: `Send-Clip.ps1` — transfer and the main entry point

**Files:**
- Modify: `windows/Send-Clip.ps1`
- Modify: `windows/tests/Send-Clip.Tests.ps1`

- [ ] **Step 1: Write the failing test**

Append to `windows/tests/Send-Clip.Tests.ps1`:

```powershell
Describe 'Write-ClipbridgeLog' {
    It 'appends a timestamped line' {
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid())
        New-Item -ItemType Directory -Path $dir | Out-Null
        Write-ClipbridgeLog -ConfigDir $dir -Message 'ssh exploded'
        $line = Get-Content (Join-Path $dir 'clipbridge.log') -Tail 1
        $line | Should -Match '^\d{4}-\d{2}-\d{2}T'
        $line | Should -Match 'ssh exploded'
        Remove-Item -Recurse -Force $dir
    }
}

Describe 'Test-RemotePath' {
    It 'accepts a single absolute POSIX path' {
        Test-RemotePath "/home/vollmin/.clipbridge/20260818-041500.png" | Should -BeTrue
    }
    It 'rejects empty output' { Test-RemotePath '' | Should -BeFalse }
    It 'rejects a relative path' { Test-RemotePath 'clipbridge/x.png' | Should -BeFalse }
    It 'rejects a path with a space, which would break unquoted typing' {
        Test-RemotePath '/home/vollmin/my screenshots/x.png' | Should -BeFalse
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `pwsh -NoProfile -Command "Invoke-Pester ./windows/tests/Send-Clip.Tests.ps1 -Output Detailed"`
Expected: FAIL — `Write-ClipbridgeLog` and `Test-RemotePath` are not defined.

- [ ] **Step 3: Write the implementation**

Add to `windows/Send-Clip.ps1` above the `if ($DotSourceOnly)` line:

```powershell
function Write-ClipbridgeLog {
    param(
        [Parameter(Mandatory)][string] $ConfigDir,
        [Parameter(Mandatory)][string] $Message
    )
    if (-not (Test-Path $ConfigDir)) { New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null }
    $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
    Add-Content -Path (Join-Path $ConfigDir 'clipbridge.log') -Value "$stamp  $Message"
}

function Test-RemotePath {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    # One line, absolute, no whitespace: the path is typed unquoted into a prompt,
    # so anything else is not safe to hand to SendText.
    return ($Path -match '^/[^\s]+$')
}
```

and replace the `if ($DotSourceOnly) { return }` line with:

```powershell
if ($DotSourceOnly) { return }

# --------------------------- main -----------------------------------------
$tmpPng = Join-Path $env:TEMP ('clipbridge-{0}.png' -f ([guid]::NewGuid().ToString('N')))
try {
    if (-not (Save-ClipboardPng -Path $tmpPng)) { exit 2 }   # no image: not an error

    $cfg = Get-ClipbridgeConfig -ConfigDir $ConfigDir
    $inv = Get-SshInvocation -Transport $cfg.transport -SshHost $cfg.sshHost

    $out = Join-Path $env:TEMP 'clipbridge-xfer.out'
    $err = Join-Path $env:TEMP 'clipbridge-xfer.err'

    # -RedirectStandardInput takes a FILE PATH, so the bytes never pass through a
    # PowerShell pipe. A pipe would stringify them and corrupt the image.
    $p = Start-Process -FilePath $inv.Exe -ArgumentList $inv.Arguments `
                       -RedirectStandardInput $tmpPng `
                       -RedirectStandardOutput $out -RedirectStandardError $err `
                       -NoNewWindow -Wait -PassThru

    $stdout = (Get-Content $out -Raw -ErrorAction SilentlyContinue)
    $stderr = (Get-Content $err -Raw -ErrorAction SilentlyContinue)

    if ($p.ExitCode -ne 0) {
        Write-ClipbridgeLog -ConfigDir $ConfigDir -Message "ssh exit $($p.ExitCode): $stderr"
        # 3 and 5 come from clipbridge-recv; anything else is a transport failure.
        if ($p.ExitCode -in @(3, 5)) { exit $p.ExitCode }
        exit 4
    }

    $remote = ($stdout -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 1)
    if (-not (Test-RemotePath $remote)) {
        Write-ClipbridgeLog -ConfigDir $ConfigDir -Message "unusable path from receiver: '$stdout'"
        exit 6
    }

    if (-not (Test-Path $ConfigDir)) { New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null }
    Set-Content -Path (Join-Path $ConfigDir 'last-path.txt') -Value $remote -NoNewline -Encoding ASCII
    exit 0
}
catch {
    Write-ClipbridgeLog -ConfigDir $ConfigDir -Message "unhandled: $($_.Exception.Message)"
    exit 4
}
finally {
    Remove-Item $tmpPng -Force -ErrorAction SilentlyContinue
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `pwsh -NoProfile -Command "Invoke-Pester ./windows/tests/Send-Clip.Tests.ps1 -Output Detailed"`
Expected: 12 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add windows/Send-Clip.ps1 windows/tests/Send-Clip.Tests.ps1
git commit -m "feat: stream the PNG over ssh and record the returned path"
```

---

## Task 9: `Install-Clipbridge.ps1`

**Files:**
- Create: `windows/Install-Clipbridge.ps1`

Transport is detected, not assumed. Measured on this laptop: `ssh.exe` authenticates and `wsl.exe -e ssh` returns `Permission denied (publickey)` — so the detection is load-bearing on a rebuild, not decoration.

- [ ] **Step 1: Write the installer**

Create `windows/Install-Clipbridge.ps1`:

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
    Detect a working ssh transport, write clipbridge's config and ssh Host block.
#>
[CmdletBinding()]
param(
    [string] $TargetHost = 'devsbx01',
    [string] $TargetUser = 'vollmin',
    [string] $HostAlias  = 'clipbridge',
    [string] $ConfigDir  = (Join-Path $env:LOCALAPPDATA 'clipbridge'),
    [string] $IdentityFile
)

$ErrorActionPreference = 'Stop'

function Test-Transport {
    param([string] $Exe, [string[]] $Prefix, [string] $TargetHost)

    if (-not (Get-Command $Exe -ErrorAction SilentlyContinue)) { return $false }
    $out = Join-Path $env:TEMP "clipbridge-detect.out"
    $err = Join-Path $env:TEMP "clipbridge-detect.err"
    $sshArgs = $Prefix + @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=5', $TargetHost, 'echo clipbridge-ok')
    $p = Start-Process -FilePath $Exe -ArgumentList $sshArgs -NoNewWindow -Wait -PassThru `
                       -RedirectStandardOutput $out -RedirectStandardError $err
    return ($p.ExitCode -eq 0 -and (Get-Content $out -Raw -ErrorAction SilentlyContinue) -match 'clipbridge-ok')
}

$transport = $null
if     (Test-Transport -Exe 'ssh.exe' -Prefix @()            -TargetHost $TargetHost) { $transport = 'ssh' }
elseif (Test-Transport -Exe 'wsl.exe' -Prefix @('-e','ssh')  -TargetHost $TargetHost) { $transport = 'wsl' }
else { throw "No ssh client authenticated to $TargetHost. Fix ssh first, then re-run." }

Write-Host "transport: $transport" -ForegroundColor Green

# The Host block keeps hostname, user, port and key out of Send-Clip.ps1 entirely.
$sshConfig = Join-Path $env:USERPROFILE '.ssh\config'
$block = @"

Host $HostAlias
    HostName $TargetHost
    User $TargetUser
$(if ($IdentityFile) { "    IdentityFile $IdentityFile`n    IdentitiesOnly yes" })
"@

if ((Test-Path $sshConfig) -and (Select-String -Path $sshConfig -Pattern "^Host $HostAlias\s*$" -Quiet)) {
    Write-Host "ssh config already has a '$HostAlias' Host block - leaving it alone" -ForegroundColor Yellow
} else {
    New-Item -ItemType Directory -Path (Split-Path $sshConfig) -Force | Out-Null
    Add-Content -Path $sshConfig -Value $block
    Write-Host "added Host $HostAlias to $sshConfig" -ForegroundColor Green
}

New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
[pscustomobject]@{ sshHost = $HostAlias; transport = $transport } |
    ConvertTo-Json | Set-Content (Join-Path $ConfigDir 'config.json') -Encoding ASCII

Write-Host "wrote $(Join-Path $ConfigDir 'config.json')" -ForegroundColor Green
Write-Host "`nNext: put windows\clipbridge.ahk in shell:startup so it loads at login." -ForegroundColor Cyan
```

- [ ] **Step 2: Run it**

Run: `powershell.exe -ExecutionPolicy Bypass -File .\windows\Install-Clipbridge.ps1`
Expected: `transport: ssh`, a Host block added, and `config.json` written. Confirm with `Get-Content $env:LOCALAPPDATA\clipbridge\config.json`.

- [ ] **Step 3: Verify end to end without AHK**

```powershell
# Take a screenshot first.
powershell.exe -STA -ExecutionPolicy Bypass -File .\windows\Send-Clip.ps1
echo "exit=$LASTEXITCODE"
Get-Content $env:LOCALAPPDATA\clipbridge\last-path.txt
```
Expected: `exit=0` and an absolute `/home/vollmin/.clipbridge/...png` path. Confirm on devsbx01 with `ls -l ~/.clipbridge/`.

- [ ] **Step 4: Commit and open the PR**

```bash
git add windows/Install-Clipbridge.ps1
git commit -m "feat: detect the ssh transport and write clipbridge config"
git push -u origin feat/windows-grabber
gh pr create --title "feat: Windows clipboard grabber" --body "Extracts a PNG from the clipboard, streams it to clipbridge-recv over the detected ssh transport, records the returned path. Transport is detected because wsl ssh has no key on this laptop while ssh.exe does — a guess had even odds of being unusable."
```

---

## Task 10: `clipbridge.ahk`

**Files:**
- Create: `windows/clipbridge.ahk`

Start branch: `git checkout main && git pull && git checkout -b feat/ahk-hotkey`

- [ ] **Step 1: Write the hotkey script**

Create `windows/clipbridge.ahk`:

```ahk
#Requires AutoHotkey v2.0
#SingleInstance Force

; clipbridge - paste a screenshot into a remote Claude Code session.
;
; Ctrl+V (terminal only): image on clipboard -> send it and type the path.
;                         anything else, or any failure -> ordinary paste.
; Ctrl+Shift+V (global):  force a send even when text is also on the clipboard.
;
; This script holds no clipboard, file, or network logic. That lives in
; Send-Clip.ps1, which is testable; AHK cannot be exercised in CI.

CONFIG_DIR  := EnvGet("LOCALAPPDATA") . "\clipbridge"
SEND_CLIP   := A_ScriptDir . "\Send-Clip.ps1"
TERMINALS   := "ahk_exe WindowsTerminal.exe"

#HotIf WinActive(TERMINALS)
^v:: {
    if (!ClipboardHasImage() || !RunClipbridge())
        Send "^v"           ; text paste, or a failure falling through
}
#HotIf

^+v:: {
    if (!RunClipbridge())
        Send "^v"
}

ClipboardHasImage() {
    ; DllCall avoids touching the clipboard contents themselves.
    ; 2 = CF_BITMAP, 8 = CF_DIB
    return DllCall("IsClipboardFormatAvailable", "UInt", 2)
        || DllCall("IsClipboardFormatAvailable", "UInt", 8)
}

; Returns true only if the path was actually typed.
RunClipbridge() {
    lastPath := CONFIG_DIR . "\last-path.txt"
    try FileDelete(lastPath)          ; never type a stale path

    code := RunWait('powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File "' SEND_CLIP '"', , "Hide")

    if (code = 2) {                    ; no image: expected, not a failure
        SoundBeep(600, 80), SoundBeep(400, 80)
        return false
    }
    if (code != 0) {
        SoundBeep(300, 200)
        TrayTip("clipbridge failed (exit " code ")", CONFIG_DIR . "\clipbridge.log")
        return false
    }

    path := ""
    try path := Trim(FileRead(lastPath))
    if (path = "") {
        SoundBeep(300, 200)
        TrayTip("clipbridge returned no path", CONFIG_DIR . "\clipbridge.log")
        return false
    }

    SendText(path . " ")
    SoundBeep(900, 60)
    return true
}
```

- [ ] **Step 2: Load it and verify the tray icon**

Double-click `windows\clipbridge.ahk`.
Expected: a green **H** appears in the system tray. If it does not, the file is opening under AutoHotkey v1 — right-click → *Run with AutoHotkey v2*.

- [ ] **Step 3: Test the text path first, because it must not regress**

Copy any text, click into a terminal, press `Ctrl+V`.
Expected: the text pastes exactly as it always did. No beep, no delay. **If this regresses, stop** — a broken `Ctrl+V` is worse than no tool.

- [ ] **Step 4: Test the image path**

Take a screenshot, click into a Claude Code prompt, press `Ctrl+V`.
Expected: a short high beep and `/home/vollmin/.clipbridge/<timestamp>.png ` typed at the prompt. Type a question after it and press Enter; Claude should describe the screenshot.

- [ ] **Step 5: Test the failure path**

```powershell
Rename-Item $env:LOCALAPPDATA\clipbridge\config.json config.json.bak
```
Take a screenshot, press `Ctrl+V` in the terminal.
Expected: low error beep, a tray tip naming the log, **and a normal paste happens anyway**. Check `Get-Content $env:LOCALAPPDATA\clipbridge\clipbridge.log -Tail 3` for a line naming the missing config. Then restore: `Rename-Item ...config.json.bak config.json`.

- [ ] **Step 6: Install for login and commit**

```powershell
$wsh = New-Object -ComObject WScript.Shell
$lnk = $wsh.CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\clipbridge.lnk")
$lnk.TargetPath = (Resolve-Path .\windows\clipbridge.ahk).Path
$lnk.Save()
```

```bash
git add windows/clipbridge.ahk
git commit -m "feat: bind Ctrl+V in the terminal to clipbridge"
git push -u origin feat/ahk-hotkey
gh pr create --title "feat: AHK hotkey" --body "Ctrl+V scoped to Windows Terminal: image goes to devsbx01 and its path is typed into the focused prompt; text pastes normally; every failure falls through to an ordinary paste so Ctrl+V can never end up dead."
```

---

## Task 11: CI

**Files:**
- Create: `.github/workflows/test.yml`

Start branch: `git checkout main && git pull && git checkout -b feat/ci`

- [ ] **Step 1: Write the workflow**

Create `.github/workflows/test.yml`:

```yaml
name: Tests

on:
  pull_request:
  push:
    branches: [main]

jobs:
  shell:
    name: Shell
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: shellcheck
        run: shellcheck -s sh linux/clipbridge-recv linux/clipbridge-recv_test.sh linux/install.sh

      - name: tests under dash
        run: dash linux/clipbridge-recv_test.sh

      - name: tests under busybox ash
        run: |
          sudo apt-get update -qq && sudo apt-get install -y -qq busybox-static
          busybox ash linux/clipbridge-recv_test.sh

  pester:
    name: Pester
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - name: Run Pester
        shell: powershell
        run: |
          Install-Module Pester -Force -SkipPublisherCheck -Scope CurrentUser
          $r = Invoke-Pester .\windows\tests -PassThru
          if ($r.FailedCount -gt 0) { exit 1 }
```

- [ ] **Step 2: Verify the shell job locally first**

Run: `shellcheck -s sh linux/*.sh linux/clipbridge-recv && dash linux/clipbridge-recv_test.sh && busybox ash linux/clipbridge-recv_test.sh`
Expected: clean shellcheck, `all tests passed` twice.

- [ ] **Step 3: Commit, push, and confirm both jobs go green**

```bash
git add .github/workflows/test.yml
git commit -m "ci: shellcheck and shell tests on ubuntu, Pester on windows"
git push -u origin feat/ci
gh pr create --title "ci: add test workflow" --body "shellcheck plus the receiver tests under dash and busybox ash, and Pester for the PowerShell half."
gh pr checks --watch
```
Expected: `Shell` and `Pester` both pass. **Do not proceed to Task 13 until both have been green at least once** — that is the precondition for making them required.

---

## Task 12: Documentation

**Files:**
- Create: `docs/clipbridge-architecture.md`
- Create: `docs/clipbridge-installation.md`
- Create: `CLAUDE.md`

Start branch: `git checkout main && git pull && git checkout -b docs/runbooks`

Filenames are globally unique on purpose — `architecture.md` is already claimed twice in the vault's rename map, and unique names avoid needing a third entry.

- [ ] **Step 1: Write `docs/clipbridge-architecture.md`**

Cover, with no `../` links and no cross-repo wikilinks (both break the Obsidian graph):
- the three components and what each owns
- the data flow, ending at "Claude Code auto-attaches a locally-existing image path found in the prompt"
- **why targeting is by window focus, not tmux** — record that `tmux send-keys` was verified working and rejected anyway because "last pane typed in" was observed resolving to the wrong session during ordinary parallel work
- the four verification results and their measured values
- exit codes and what each means

- [ ] **Step 2: Write `docs/clipbridge-installation.md`**

A runbook that works on a freshly rebuilt laptop:
1. `sh linux/install.sh` on devsbx01
2. create the 1Password key, add the `restrict,command=` line to `authorized_keys`
3. `Install-Clipbridge.ps1` on Windows
4. install AutoHotkey v2, put `clipbridge.ahk` in `shell:startup`
5. verify: screenshot → `Ctrl+V` in a terminal → path appears
6. troubleshooting: no tray icon (AHK v1 vs v2), exit 4 (check `clipbridge.log`), exit 2 (clipboard has no image), text paste broken (unload the script and report it — that is the one unacceptable failure)

- [ ] **Step 3: Write `CLAUDE.md`**

Short. Repo layout, the rule that `clipbridge.ahk` holds no logic because it cannot be tested in CI, the requirement that shell tests pass under both `dash` and `busybox ash`, and the standing rule that no failure path may leave `Ctrl+V` dead.

- [ ] **Step 4: Commit and open the PR**

```bash
git add docs/clipbridge-architecture.md docs/clipbridge-installation.md CLAUDE.md
git commit -m "docs: architecture, installation runbook, repo conventions"
git push -u origin docs/runbooks
gh pr create --title "docs: architecture and installation" --body "Architecture record including why targeting is by window focus, an installation runbook for a rebuilt laptop, and repo conventions."
```

---

## Task 13: Org onboarding and branch protection

**Files:** in `homelab-obsidian-vault` and `github-admin`, not this repo.

- [ ] **Step 1: Add clipbridge to the vault sync**

In `homelab-obsidian-vault`, on a branch: add `clipbridge` to `repos=()` in `scripts/sync-docs-to-vault.sh`. No `renames` entry is needed — the doc filenames are already unique.

- [ ] **Step 2: Create the vault index file**

Create `repos/clipbridge/clipbridge.md` from the template in the global rules: an H1, `← [[Home]]`, a one-line description, a `## Docs` list linking `[[repos/clipbridge/docs/clipbridge-architecture|Architecture]]` and the installation doc, and a `## Key facts` section in plain text with **no cross-repo wikilinks**.

- [ ] **Step 3: Wire it into the graph**

Add `[[clipbridge]]` to `Home.md`, add a graph color in `scripts/enforce-graph-colors.sh`, and increment that script's threshold.

- [ ] **Step 4: Verify the vault**

```bash
cd ~/repos/vollminlab/homelab-obsidian-vault
./scripts/sync-docs-to-vault.sh && ./scripts/enforce-graph-colors.sh
ls repos/clipbridge/docs/
```
Expected: both docs present with `← [[clipbridge]]` backlinks injected, and no "Unlisted Docs" section added to the index.

- [ ] **Step 5: Read the real check context names**

Do not guess these strings. A `contexts` entry that matches no real check blocks every PR
on the repo forever, and `enforce_admins = true` means there is no bypass.

```bash
gh api repos/vollminlab/clipbridge/commits/main/check-runs -q '.check_runs[].name'
```
Expected: two names. Use them verbatim in the next step — they are most likely `Shell` and
`Pester`, but the workflow name can prefix them, and only the API knows.

- [ ] **Step 6: Add branch protection**

Only now, with both checks green on a real PR and their exact names in hand. In `github-admin`, on a branch, append to `terraform/main.tf`:

```hcl
resource "github_branch_protection" "clipbridge_main" {
  repository_id = github_repository.clipbridge.node_id
  pattern       = "main"

  required_status_checks {
    strict   = true
    contexts = ["Shell", "Pester"]   # replace with the exact names from step 5
  }

  required_pull_request_reviews {
    dismiss_stale_reviews           = true
    required_approving_review_count = 0
  }

  enforce_admins                  = true
  require_conversation_resolution = true
}
```

Also remove the paragraph in the `clipbridge` resource comment explaining why protection was deferred — it is no longer true.

- [ ] **Step 7: Open both PRs**

Open a PR on each repo. **Confirm the `Terraform Plan` check shows `1 to add, 0 to change, 0 to destroy`** before asking for a merge — a plan that proposes changes to other repositories means something else drifted and must be investigated first.

---

## Definition of done

- [ ] Screenshot → `Ctrl+V` in a terminal → path appears in the Claude Code prompt → Claude describes the image
- [ ] Text on the clipboard still pastes normally in the terminal
- [ ] With devsbx01 unreachable, `Ctrl+V` still performs an ordinary paste and the reason is in `clipbridge.log`
- [ ] `ssh clipbridge whoami` does not run `whoami` — the forced command holds
- [ ] `Shell` and `Pester` green on main
- [ ] Both docs render in the vault with injected backlinks
- [ ] Branch protection active on `clipbridge`
