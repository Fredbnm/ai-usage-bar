#!/usr/bin/env bash
# Regenerates Support/dmg-background.tiff (1x + 2x in one file, so Retina
# screens get the sharp one) from scripts/make-dmg-background.swift.
set -euo pipefail
cd "$(dirname "$0")/.."

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

swift scripts/make-dmg-background.swift "$WORK/background.png" "$WORK/background@2x.png"
tiffutil -cathidpicheck "$WORK/background.png" "$WORK/background@2x.png" -out Support/dmg-background.tiff
echo "✓ Support/dmg-background.tiff"
