#!/bin/sh
# Tests for install.sh. No cluster, no tmux, no network.
# Run under both shells:  dash linux/install_test.sh
#                         busybox ash linux/install_test.sh
#
# All destructive testing happens under mktemp -d scratch paths. This suite
# must never touch ~/.local/bin/clipbridge-recv or ~/.clipbridge -- both are
# live and in use.
set -u

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
SRC="$SCRIPT_DIR/clipbridge-recv"
# Overridable so the negative check below (verifying this suite actually
# catches the relative-path regression) can point at a different install.sh
# without touching how the suite is normally invoked.
INSTALL="${CLIPBRIDGE_INSTALL_SCRIPT:-$SCRIPT_DIR/install.sh}"
FAILED=0

pass() { echo "  PASS  $1"; }
fail() { echo "  FAIL  $1"; FAILED=$((FAILED + 1)); }

new_sandbox() { SANDBOX=$(mktemp -d); }
cleanup_sandbox() { rm -rf "$SANDBOX"; }

# --- a relative destination still prints an absolute path in the verify line --
new_sandbox
mkdir -p "$SANDBOX/relwork"
out=$(cd "$SANDBOX/relwork" && sh "$INSTALL" "subdir/clipbridge-recv" 2>"$SANDBOX/err")
line=$(echo "$out" | grep '^  ssh clipbridge ')
path=$(echo "$line" | awk '{print $3}')
case "$path" in
    /*) pass "relative destination arg still prints an absolute path in the verify command" ;;
    *)  fail "printed verify path is not absolute: '$path'" ;;
esac
cleanup_sandbox

# --- installed file is byte-identical to source and mode 0755 --------------
new_sandbox
dest="$SANDBOX/bin/clipbridge-recv"
sh "$INSTALL" "$dest" > "$SANDBOX/out" 2>"$SANDBOX/err"
if cmp -s "$SRC" "$dest"; then pass "installed file is byte-identical to source"; else fail "installed file differs from source"; fi
# shellcheck disable=SC2012 # $dest is a path this test just created (mktemp
# sandbox, fully controlled name -- not a glob result), so there's no
# funky-filename risk to parse around. Extracting the mode string this way
# is the portable choice: GNU stat's `-c` and BSD/busybox stat's `-f` take
# different format strings, and busybox stat supports neither reliably, so
# there is no single stat invocation that works under both dash and busybox
# ash here. `ls -l` is universal across all three.
mode=$(ls -l "$dest" | cut -c1-10)
if [ "$mode" = "-rwxr-xr-x" ]; then pass "installed file is mode 0755"; else fail "installed file mode is $mode, want -rwxr-xr-x"; fi
cleanup_sandbox

# --- running the installer twice is idempotent ------------------------------
new_sandbox
dest="$SANDBOX/bin/clipbridge-recv"
sh "$INSTALL" "$dest" > "$SANDBOX/out1" 2>"$SANDBOX/err1"; rc1=$?
sum1=$(cksum "$dest")
sh "$INSTALL" "$dest" > "$SANDBOX/out2" 2>"$SANDBOX/err2"; rc2=$?
sum2=$(cksum "$dest")
if [ "$rc1" -eq 0 ] && [ "$rc2" -eq 0 ]; then pass "installer succeeds on both runs"; else fail "installer exited rc1=$rc1 rc2=$rc2, want 0 and 0"; fi
if [ "$sum1" = "$sum2" ]; then pass "destination is identical after a second install"; else fail "destination changed between installs"; fi
cleanup_sandbox

# --- no stray temp file is left behind after a successful install ----------
new_sandbox
dest="$SANDBOX/bin/clipbridge-recv"
sh "$INSTALL" "$dest" > "$SANDBOX/out" 2>"$SANDBOX/err"
stray=$(find "$SANDBOX/bin" -name '*.tmp.*')
if [ -z "$stray" ]; then pass "no stray .tmp.* file left behind"; else fail "stray temp file(s) left behind: $stray"; fi
cleanup_sandbox

echo
if [ "$FAILED" -eq 0 ]; then echo "all tests passed"; exit 0; else echo "$FAILED test(s) failed"; exit 1; fi
