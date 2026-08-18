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
