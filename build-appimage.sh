#!/usr/bin/env bash
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD="$HOME/vrcnext-appimage-build"
PUB="$BUILD/publish"
APPDIR="$BUILD/AppDir"
TOOLS="$BUILD/tools"
OUT="$REPO/VRCNext-x86_64.AppImage"

echo ">> Installing dependencies (sudo)"
if command -v pacman >/dev/null 2>&1; then
  sudo pacman -Sy --needed --noconfirm \
    webkit2gtk-4.1 gtk3 glib2 gst-plugins-base gst-plugins-good gst-libav \
    zenity patchelf desktop-file-utils librsvg fuse2 wget || true
elif command -v apt-get >/dev/null 2>&1; then
  sudo apt-get update
  sudo apt-get install -y \
    libwebkit2gtk-4.1-0 libwebkit2gtk-4.1-dev libgtk-3-0 libgtk-3-dev libglib2.0-0 \
    gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-libav \
    zenity patchelf desktop-file-utils librsvg2-dev wget file fuse libfuse2 || true
elif command -v dnf >/dev/null 2>&1; then
  sudo dnf install -y \
    webkit2gtk4.1 gtk3 glib2 gstreamer1-plugins-base gstreamer1-plugins-good gstreamer1-plugin-libav \
    zenity patchelf desktop-file-utils librsvg2 fuse fuse-libs wget || true
elif command -v zypper >/dev/null 2>&1; then
  sudo zypper install -y \
    libwebkit2gtk-4_1-0 gtk3 glib2 gstreamer-plugins-base gstreamer-plugins-good gstreamer-plugins-libav \
    zenity patchelf desktop-file-utils librsvg fuse wget || true
else
  echo "WARN: unknown package manager, install webkit2gtk-4.1/gtk3/patchelf/desktop-file-utils/fuse manually"
fi

rm -rf "$BUILD"
mkdir -p "$PUB"

echo ">> Preparing publish output"
if [ -f "$REPO/publish-linux/VRCNext" ]; then
  cp -r "$REPO/publish-linux/." "$PUB/"
elif command -v dotnet >/dev/null 2>&1; then
  dotnet publish "$REPO/VRCNext.csproj" -c Release -r linux-x64 --self-contained true \
    -p:VRCNextWhKey="${VRCNextWhKey:-}" -p:VRCNextVrcnPlusAdminToken="${VRCNextVrcnPlusAdminToken:-}" \
    -o "$PUB"
else
  echo "ERROR: no publish-linux/ folder found and no dotnet in WSL."
  echo "Run this on Windows (PowerShell) first, in the repo folder:"
  echo "  dotnet publish VRCNext.csproj -c Release -r linux-x64 --self-contained true -p:VRCNextWhKey= -p:VRCNextVrcnPlusAdminToken= -o publish-linux"
  exit 1
fi
chmod +x "$PUB/VRCNext"

echo ">> Assembling AppDir"
mkdir -p "$APPDIR/usr/bin"
cp -r "$PUB/." "$APPDIR/usr/bin/"
rm -f "$APPDIR/usr/bin/libcoreclrtraceptprovider.so"

mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp "$APPDIR/usr/bin/frontend/logo.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/vrcnext.png"
cp "$APPDIR/usr/bin/frontend/logo.png" "$APPDIR/vrcnext.png"

mkdir -p "$APPDIR/usr/share/applications"
cat > "$APPDIR/usr/share/applications/vrcnext.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=VRCNext
Comment=VRChat launcher and management
Exec=VRCNext
Icon=vrcnext
Terminal=false
Categories=Game;Utility;
StartupWMClass=VRCNext
EOF
cp "$APPDIR/usr/share/applications/vrcnext.desktop" "$APPDIR/vrcnext.desktop"

WK="$(find /usr/lib /usr/libexec /usr/lib/x86_64-linux-gnu -type d -name 'webkit2gtk-4.1' 2>/dev/null | head -1 || true)"
if [ -n "$WK" ]; then
  mkdir -p "$APPDIR/usr/libexec/webkit2gtk-4.1"
  cp -a "$WK/." "$APPDIR/usr/libexec/webkit2gtk-4.1/"
fi

mkdir -p "$APPDIR/apprun-hooks"
cat > "$APPDIR/apprun-hooks/zz-vrcnext-env.sh" <<'EOF'
export WEBKIT_EXEC_PATH="${APPDIR}/usr/libexec/webkit2gtk-4.1"
EOF

echo ">> Downloading linuxdeploy + gtk plugin"
mkdir -p "$TOOLS"
wget -qO "$TOOLS/linuxdeploy-x86_64.AppImage" \
  https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage
wget -qO "$TOOLS/linuxdeploy-plugin-gtk.sh" \
  https://github.com/linuxdeploy/linuxdeploy-plugin-gtk/raw/master/linuxdeploy-plugin-gtk.sh
chmod +x "$TOOLS/linuxdeploy-x86_64.AppImage" "$TOOLS/linuxdeploy-plugin-gtk.sh"

echo ">> Building AppImage"
export APPIMAGE_EXTRACT_AND_RUN=1
export NO_STRIP=1
export PATH="$TOOLS:$PATH"
export LD_LIBRARY_PATH="$APPDIR/usr/bin:${LD_LIBRARY_PATH:-}"
export DEPLOY_GTK_VERSION=3
cd "$BUILD"
"$TOOLS/linuxdeploy-x86_64.AppImage" --appimage-extract-and-run \
  --appdir "$APPDIR" \
  --executable "$APPDIR/usr/bin/VRCNext" \
  --library "$APPDIR/usr/bin/Photino.Native.so" \
  --library "$APPDIR/usr/bin/libnfd.so" \
  --desktop-file "$APPDIR/vrcnext.desktop" \
  --icon-file "$APPDIR/vrcnext.png" \
  --plugin gtk \
  --output appimage

mv -f "$BUILD"/VRCNext*.AppImage "$OUT"
chmod +x "$OUT"
echo ">> Done: $OUT"
