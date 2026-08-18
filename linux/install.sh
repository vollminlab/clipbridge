#!/bin/sh
# Install clipbridge-recv into ~/.local/bin and print the authorized_keys line.
set -eu

SRC=$(cd "$(dirname "$0")" && pwd)/clipbridge-recv
DEST="${1:-$HOME/.local/bin/clipbridge-recv}"

[ -f "$SRC" ] || { echo "install: $SRC not found" >&2; exit 1; }

mkdir -p "$(dirname "$DEST")"
# Canonicalize after mkdir -p (the directory must exist before cd can enter
# it). A relative $1 (e.g. "subdir/clipbridge-recv") would otherwise print
# a relative path below -- a relative command= in authorized_keys is not
# guaranteed to resolve against any particular directory at ssh login time,
# so DEST must always be absolute by the time it reaches that line.
DEST=$(cd "$(dirname "$DEST")" && pwd)/$(basename "$DEST")

# Write to a sibling temp file and mv into place, same pattern as
# clipbridge-recv itself: this script redeploys a receiver that concurrent
# ssh sessions invoke as a forced command, and cp over an existing
# destination is open(O_TRUNC)+write(), not atomic -- a new session
# arriving mid-write could see a partial or empty binary. mv within the
# same directory is a single rename, so no session ever observes a
# half-written file.
tmp="$DEST.tmp.$$"
trap 'rm -f "$tmp"' EXIT INT TERM
cp "$SRC" "$tmp"
chmod 755 "$tmp"
mv "$tmp" "$DEST"
trap - EXIT INT TERM

echo "installed $DEST"

cat <<EOF

Add this line to ~/.ssh/authorized_keys, substituting the clipbridge public key.
'restrict' implies no-pty, no-port-forwarding, no-agent-forwarding and
no-X11-forwarding; 'command=' is what stops this credential opening a shell.

restrict,command="$DEST" ssh-ed25519 AAAA... clipbridge

Then verify from the laptop:
  ssh clipbridge < some.png
EOF
