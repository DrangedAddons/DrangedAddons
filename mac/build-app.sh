#!/bin/bash
# Builds DrangedAddons.app for one Mac architecture and zips it (run on a Mac; the workflows do).
#   mac/build-app.sh osx-arm64 0.43.0   ->  artifacts/DrangedAddons-mac-apple-silicon.zip
#   mac/build-app.sh osx-x64 0.43.0     ->  artifacts/DrangedAddons-mac-intel.zip
set -euo pipefail
RID="$1"
VERSION="$2"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
OUT="$ROOT/artifacts"
WORK="$OUT/$RID"
APP="$WORK/DrangedAddons.app"
case "$RID" in
  osx-arm64) NAME="apple-silicon" ;;
  osx-x64) NAME="intel" ;;
  *) echo "Unknown runtime $RID" >&2; exit 1 ;;
esac

rm -rf "$WORK"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
dotnet publish "$HERE" -c Release -r "$RID" --self-contained -p:Version="$VERSION" -o "$APP/Contents/MacOS"

# The app icon: the DrangedAddons logo at every size macOS asks for.
ICONSET="$WORK/DrangedAddons.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$ROOT/src/logo.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$ROOT/src/logo.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/DrangedAddons.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>DrangedAddons</string>
  <key>CFBundleDisplayName</key><string>DrangedAddons</string>
  <key>CFBundleIdentifier</key><string>com.drangedaddons.manager</string>
  <key>CFBundleExecutable</key><string>DrangedAddons</string>
  <key>CFBundleIconFile</key><string>DrangedAddons.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
PLIST

# Not signed with an Apple Developer ID: an ad-hoc signature is the minimum Apple Silicon needs to run it at all.
codesign --force --deep --sign - "$APP"

ditto -c -k --keepParent "$APP" "$OUT/DrangedAddons-mac-$NAME.zip"
echo "Built $OUT/DrangedAddons-mac-$NAME.zip"
