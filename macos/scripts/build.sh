#!/usr/bin/env bash
# Builds "AI Usage Bar.app" and the .dmg installer into macos/dist/.
#
#   scripts/build.sh                     universal binary (Apple Silicon + Intel), ad-hoc signed
#   VERSION=1.2.0 scripts/build.sh       override the version in ./VERSION
#   SIGN_IDENTITY="Developer ID Application: …" scripts/build.sh
#                                        sign with a real certificate (needed for notarization)
set -euo pipefail
cd "$(dirname "$0")/.."

APP_NAME="AI Usage Bar"
EXECUTABLE="AIUsageBar"
VERSION="${VERSION:-$(tr -d '[:space:]' < VERSION)}"
DIST="dist"
APP="$DIST/$APP_NAME.app"
DMG="$DIST/AIUsageBar-$VERSION.dmg"
ARCH_FLAGS=(--arch arm64 --arch x86_64)

echo "▸ Building version $VERSION (arm64 + x86_64)…"
swift build -c release "${ARCH_FLAGS[@]}"
BIN_DIR="$(swift build -c release "${ARCH_FLAGS[@]}" --show-bin-path)"

echo "▸ Assembling $APP_NAME.app…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$EXECUTABLE" "$APP/Contents/MacOS/$EXECUTABLE"
sed -e "s/__VERSION__/$VERSION/g" Support/Info.plist > "$APP/Contents/Info.plist"
[[ -f Support/AppIcon.icns ]] || scripts/make-icon.sh
cp Support/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

IDENTITY="${SIGN_IDENTITY:--}"
echo "▸ Signing (${IDENTITY})…"
if [[ "$IDENTITY" == "-" ]]; then
  codesign --force --sign - "$APP"
else
  codesign --force --options runtime --timestamp --sign "$IDENTITY" "$APP"
fi

echo "▸ Creating the .dmg installer…"
rm -f "$DMG"
# dmgbuild lays out the window (background, icon positions) without scripting
# Finder, so it works the same on CI. It lives in its own virtualenv, made once.
DMGBUILD_VERSION="1.6.7"
DMGBUILD_ENV="${TMPDIR:-/tmp}/aiusagebar-dmgbuild-$DMGBUILD_VERSION"
if [[ ! -x "$DMGBUILD_ENV/bin/dmgbuild" ]]; then
  rm -rf "$DMGBUILD_ENV"
  if ! { python3 -m venv "$DMGBUILD_ENV" && "$DMGBUILD_ENV/bin/pip" install --quiet --disable-pip-version-check "dmgbuild==$DMGBUILD_VERSION"; }; then
    rm -rf "$DMGBUILD_ENV"
  fi
fi
if [[ -x "$DMGBUILD_ENV/bin/dmgbuild" ]]; then
  "$DMGBUILD_ENV/bin/dmgbuild" -s scripts/dmg-settings.py \
    -D app="$APP" -D background=Support/dmg-background.tiff -D volume_icon=Support/AppIcon.icns \
    "$APP_NAME" "$DMG" >/dev/null
elif [[ "${CI:-}" == "true" ]]; then
  echo "✗ Could not install dmgbuild (needs Python 3.10+); a release needs the styled window." >&2
  exit 1
else
  # Offline or an old Python: the same contents, without the styled window.
  echo "! dmgbuild unavailable (needs Python 3.10+ and pip); making a plain .dmg"
  STAGING="$(mktemp -d)"
  trap 'rm -rf "$STAGING"' EXIT
  cp -R "$APP" "$STAGING/"
  ln -s /Applications "$STAGING/Applications"
  hdiutil create -volname "$APP_NAME" -srcfolder "$STAGING" -ov -format UDZO "$DMG" >/dev/null
fi

echo "✓ App:        macos/$APP"
echo "✓ Installer:  macos/$DMG"
