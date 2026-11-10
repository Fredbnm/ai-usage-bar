#!/usr/bin/env bash
# Builds "Claude Usage.app" and the .dmg installer into macos/dist/.
#
#   scripts/build.sh                     universal binary (Apple Silicon + Intel), ad-hoc signed
#   VERSION=1.2.0 scripts/build.sh       override the version in ./VERSION
#   SIGN_IDENTITY="Developer ID Application: …" scripts/build.sh
#                                        sign with a real certificate (needed for notarization)
set -euo pipefail
cd "$(dirname "$0")/.."

APP_NAME="Claude Usage"
EXECUTABLE="ClaudeUsageBar"
VERSION="${VERSION:-$(tr -d '[:space:]' < VERSION)}"
DIST="dist"
APP="$DIST/$APP_NAME.app"
DMG="$DIST/ClaudeUsage-$VERSION.dmg"
ARCH_FLAGS=(--arch arm64 --arch x86_64)

echo "▸ Compilando versão $VERSION (arm64 + x86_64)…"
swift build -c release "${ARCH_FLAGS[@]}"
BIN_DIR="$(swift build -c release "${ARCH_FLAGS[@]}" --show-bin-path)"

echo "▸ Montando $APP_NAME.app…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$EXECUTABLE" "$APP/Contents/MacOS/$EXECUTABLE"
sed -e "s/__VERSION__/$VERSION/g" Support/Info.plist > "$APP/Contents/Info.plist"
[[ -f Support/AppIcon.icns ]] || scripts/make-icon.sh
cp Support/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

IDENTITY="${SIGN_IDENTITY:--}"
echo "▸ Assinando (${IDENTITY})…"
if [[ "$IDENTITY" == "-" ]]; then
  codesign --force --sign - "$APP"
else
  codesign --force --options runtime --timestamp --sign "$IDENTITY" "$APP"
fi

echo "▸ Gerando o instalador .dmg…"
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
rm -f "$DMG"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGING" -ov -format UDZO "$DMG" >/dev/null

echo "✓ App:        macos/$APP"
echo "✓ Instalador: macos/$DMG"
