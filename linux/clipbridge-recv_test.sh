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

# --- a write that fails partway must not be reported as success ------------
# Stub `cat` on PATH ahead of the real one. The script runs `cat > "$tmp"`,
# so the stub must emit the PNG magic to ITS OWN stdout (which the script has
# already redirected into $tmp) and then exit non-zero, simulating a write
# that dies partway through (disk full, RO remount, quota).
new_sandbox
make_png_sig "$SANDBOX/in.png"
STUBS="$SANDBOX/stubs"; mkdir -p "$STUBS"
cat > "$STUBS/cat" << 'STUBEOF'
#!/bin/sh
printf '\211PNG\r\n\032\n'
exit 1
STUBEOF
chmod +x "$STUBS/cat"
OLD_PATH="$PATH"; PATH="$STUBS:$PATH"; export PATH
out=$("$RECV" < "$SANDBOX/in.png" 2>"$SANDBOX/err"); rc=$?
PATH="$OLD_PATH"; export PATH
if [ "$rc" -eq 5 ]; then pass "a failed write exits 5"; else fail "a failed write exited $rc, want 5"; fi
if [ -z "$(ls -A "$CLIPBRIDGE_DIR" 2>/dev/null)" ]; then pass "a failed write leaves no file behind"; else fail "a failed write left files in $CLIPBRIDGE_DIR"; fi
cleanup_sandbox

# --- unwritable target directory --------------------------------------------
# NOTE: locking $CLIPBRIDGE_DIR itself does not work here -- the script does
# an unconditional `chmod 700 "$CLIP_DIR"` as its own hardening step, and
# chmod only requires ownership (not existing write permission), so an
# owner-locked target directory silently self-heals before mktemp ever runs.
# Verified empirically: chmod 500 on $CLIPBRIDGE_DIR + a run still exits 0.
# Locking the PARENT instead makes `mkdir -p "$CLIP_DIR"` itself fail, which
# is a real, unrecoverable "cannot write" condition.
new_sandbox
mkdir -p "$SANDBOX/locked"
chmod 500 "$SANDBOX/locked"
export CLIPBRIDGE_DIR="$SANDBOX/locked/clip"
make_png_sig "$SANDBOX/in.png"
out=$("$RECV" < "$SANDBOX/in.png" 2>"$SANDBOX/err"); rc=$?
chmod 700 "$SANDBOX/locked"
if [ "$rc" -eq 5 ]; then pass "unwritable parent directory exits 5"; else fail "unwritable parent directory exited $rc, want 5"; fi
if [ -s "$SANDBOX/err" ]; then pass "unwritable parent directory explains itself"; else fail "unwritable parent directory failed silently"; fi
cleanup_sandbox

echo
if [ "$FAILED" -eq 0 ]; then echo "all tests passed"; exit 0; else echo "$FAILED test(s) failed"; exit 1; fi
