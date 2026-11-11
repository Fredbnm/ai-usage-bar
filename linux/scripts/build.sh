#!/bin/bash
# Builds the Linux release into linux/dist:
#   AIUsageBar-Linux-x86_64.AppImage  the app as an AppImage (updates itself)
#   AIUsageBar-Linux-x64.tar.gz       the portable build: one self-contained executable
# Needs the .NET 10 SDK and curl (appimagetool is downloaded on first use; APPIMAGETOOL points at
# another copy). VERSION overrides linux/VERSION.
set -euo pipefail
cd "$(dirname "$0")/.."

version="${VERSION:-$(tr -d '[:space:]' < VERSION)}"
project=src/AIUsageBar/AIUsageBar.csproj
common=(-c Release -r linux-x64 --self-contained true -p:DebugType=none -p:Version="$version")
rm -rf publish dist
mkdir -p publish dist

echo "== Portable executable ($version)"
dotnet publish "$project" "${common[@]}" -o publish/single \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
mkdir -p publish/archive/ai-usage-bar
cp publish/single/AIUsageBar packaging/README.txt publish/archive/ai-usage-bar/
tar -czf dist/AIUsageBar-Linux-x64.tar.gz -C publish/archive --owner=0 --group=0 ai-usage-bar

echo "== AppImage"
# A folder rather than a single file: the AppImage is compressed already, and nothing is extracted at startup.
appdir=publish/AppDir
dotnet publish "$project" "${common[@]}" -o "$appdir/usr/bin"
install -Dm755 packaging/AppRun "$appdir/AppRun"
install -Dm644 packaging/ai-usage-bar.desktop "$appdir/ai-usage-bar.desktop"
install -Dm644 packaging/ai-usage-bar.desktop "$appdir/usr/share/applications/ai-usage-bar.desktop"
install -Dm644 src/AIUsageBar/Assets/AppIcon.png "$appdir/ai-usage-bar.png"
install -Dm644 src/AIUsageBar/Assets/AppIcon.png "$appdir/usr/share/icons/hicolor/256x256/apps/ai-usage-bar.png"
ln -sf ai-usage-bar.png "$appdir/.DirIcon"

tool="${APPIMAGETOOL:-publish/appimagetool}"
if [[ ! -x "$tool" ]]; then
  curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$tool"
fi
# Extract-and-run: build machines often have no FUSE.
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 VERSION="$version" "$tool" --no-appstream "$appdir" dist/AIUsageBar-Linux-x86_64.AppImage

ls -l dist
