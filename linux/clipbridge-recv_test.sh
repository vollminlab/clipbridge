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
