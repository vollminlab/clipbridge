#!/bin/sh
# Install clipbridge-recv into ~/.local/bin and print a verify command.
set -eu

SRC=$(cd "$(dirname "$0")" && pwd)/clipbridge-recv
DEST="${1:-$HOME/.local/bin/clipbridge-recv}"

[ -f "$SRC" ] || { echo "install: $SRC not found" >&2; exit 1; }

mkdir -p "$(dirname "$DEST")"
# Canonicalize after mkdir -p (the directory must exist before cd can enter
# it). A relative $1 (e.g. "subdir/clipbridge-recv") would otherwise print
# a relative path below -- this path is what Send-Clip.ps1 passes as the
# explicit remote command on the ssh command line, and a relative one is not
# guaranteed to resolve against any particular directory there, so DEST must
# always be absolute by the time it reaches that line.
DEST=$(cd "$(dirname "$DEST")" && pwd)/$(basename "$DEST")

# Write to a sibling temp file and mv into place, same pattern as
# clipbridge-recv itself: this script redeploys a receiver that concurrent
# ssh sessions invoke as an explicit remote command, and cp over an existing
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

No authorized_keys change needed -- clipbridge authenticates with the laptop's
existing devsbx01 key and names this path as the remote command explicitly,
rather than through a forced command= entry.

Verify from the laptop:
  ssh clipbridge $DEST < some.png
EOF
