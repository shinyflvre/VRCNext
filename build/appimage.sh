#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
CACHE="$HOME/.cache/vrcnext-build"
STAGE="$CACHE/stage"
APPDIR="$CACHE/AppDir"
OUT_DIR="$REPO/publish/linux"

VERSION="$(grep -oP 'Version = "\K[^"]+' "$REPO/main/AppInfo.cs")"
[ -n "$VERSION" ] || { echo "[ERROR] Could not read version from main/AppInfo.cs"; exit 1; }

echo "==> VRCNext $VERSION Linux AppImage build"
mkdir -p "$CACHE" "$OUT_DIR"

# ---------------------------------------------------------------- dotnet SDK 9
find_dotnet() {
    for c in "$(command -v dotnet 2>/dev/null || true)" "$HOME/.dotnet/dotnet"; do
        [ -x "$c" ] || continue
        if "$c" --list-sdks 2>/dev/null | grep -q '^9\.'; then
            echo "$c"
            return 0
        fi
    done
    return 1
}

DOTNET="$(find_dotnet || true)"
if [ -z "$DOTNET" ]; then
    echo "==> Installing .NET SDK 9 to ~/.dotnet (one-time)..."
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "$CACHE/dotnet-install.sh"
    bash "$CACHE/dotnet-install.sh" --channel 9.0 --install-dir "$HOME/.dotnet"
    DOTNET="$HOME/.dotnet/dotnet"
fi
echo "==> Using dotnet: $DOTNET ($("$DOTNET" --version))"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# ----------------------------------------------------------------- source sync
SRC="$CACHE/src"
echo "==> Syncing sources to native build dir..."
mkdir -p "$SRC"
RSYNC_EXCLUDES=(--exclude .git --exclude .vs --exclude bin --exclude obj
    --exclude bin-linux --exclude obj-linux --exclude publish --exclude publish-linux
    --exclude releases --exclude installer --exclude Website --exclude "API Refs"
    --exclude REFS --exclude kikitan-translator --exclude voice --exclude BuildSecrets.cs)
if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete "${RSYNC_EXCLUDES[@]}" "$REPO/" "$SRC/"
else
    rm -rf "$SRC"
    mkdir -p "$SRC"
    (cd "$REPO" && tar -cf - \
        --exclude=./.git --exclude=./.vs --exclude=./bin --exclude=./obj \
        --exclude=./bin-linux --exclude=./obj-linux --exclude=./publish --exclude=./publish-linux \
        --exclude=./releases --exclude=./installer --exclude=./Website --exclude="./API Refs" \
        --exclude=./REFS --exclude=./kikitan-translator --exclude=./voice --exclude=./BuildSecrets.cs \
        .) | (cd "$SRC" && tar -xf -)
fi

# -------------------------------------------------------------------- publish
echo "==> Publishing linux-x64 (self-contained)..."
rm -rf "$STAGE"
"$DOTNET" publish "$SRC/VRCNext.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -o "$STAGE" \
    -p:VRCNextWhKey="${VRCNEXT_WH_KEY:-}" \
    -p:VRCNextVrcndbKey="${VRCNEXT_VRCNDB_KEY:-}" \
    -p:VRCNextVrcnPlusAdminToken="${VRCNEXT_VRCN_PLUS_ADMIN_TOKEN:-}" \
    -p:VRCNextHypeRateApiKey="${VRCNEXT_HYPERATE_API_KEY:-}"

[ -f "$STAGE/VRCNext" ] || { echo "[ERROR] Publish output missing VRCNext binary"; exit 1; }
[ -d "$STAGE/frontend" ] || { echo "[ERROR] Publish output missing frontend/"; exit 1; }
[ -f "$STAGE/Photino.Native.so" ] || { echo "[ERROR] Publish output missing Photino.Native.so"; exit 1; }
[ -f "$STAGE/libnfd.so" ] || { echo "[ERROR] Publish output missing libnfd.so (file dialogs)"; exit 1; }
[ -f "$STAGE/libe_sqlite3.so" ] || { echo "[ERROR] Publish output missing libe_sqlite3.so (SQLite)"; exit 1; }
[ -f "$STAGE/libSkiaSharp.so" ] || { echo "[ERROR] Publish output missing libSkiaSharp.so"; exit 1; }

echo "==> Verifying no Windows-only payload is shipped..."
LEFTOVERS=""
for f in libvosk.so Vosk.dll NAudio.dll NAudio.Vorbis.dll NVorbis.dll DiscordRPC.dll \
         System.Speech.dll openvr_api.dll libSkiaSharp.dll e_sqlite3.dll nfd.dll \
         Vortice.Direct3D11.dll Vortice.DXGI.dll Vortice.Direct3D.dll \
         System.Diagnostics.PerformanceCounter.dll; do
    if [ -e "$STAGE/$f" ]; then
        rm -f "$STAGE/$f"
        LEFTOVERS="$LEFTOVERS $f"
    fi
done
for d in voice tray frameshot; do
    if [ -d "$STAGE/$d" ]; then
        rm -rf "$STAGE/$d"
        LEFTOVERS="$LEFTOVERS $d/"
    fi
done
find "$STAGE" -maxdepth 1 -name "*.exe" -delete
[ -z "$LEFTOVERS" ] || echo "    removed:$LEFTOVERS"

chmod +x "$STAGE/VRCNext"
[ -f "$STAGE/createdump" ] && chmod +x "$STAGE/createdump"

# --------------------------------------------------------------------- AppDir
echo "==> Building AppDir..."
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin/vrcnext" \
         "$APPDIR/usr/lib" \
         "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/512x512/apps"

cp -a "$STAGE/." "$APPDIR/usr/bin/vrcnext/"

for lib in /usr/lib/x86_64-linux-gnu/libnotify.so.4*; do
    [ -e "$lib" ] && cp -a "$lib" "$APPDIR/usr/lib/"
done
[ -e "$APPDIR/usr/lib/libnotify.so.4" ] || echo "[WARN] libnotify.so.4 not found on build system — not bundled"

cat > "$APPDIR/vrcnext.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=VRCNext
Comment=VRChat companion app
Exec=VRCNext %u
Icon=vrcnext
Terminal=false
Categories=Game;Utility;Network;
StartupWMClass=VRCNext
MimeType=x-scheme-handler/vrcn;
X-AppImage-Version=$VERSION
EOF
cp "$APPDIR/vrcnext.desktop" "$APPDIR/usr/share/applications/vrcnext.desktop"

cp "$REPO/frontend/logo.png" "$APPDIR/vrcnext.png"
cp "$REPO/frontend/logo.png" "$APPDIR/usr/share/icons/hicolor/512x512/apps/vrcnext.png"
cp "$REPO/frontend/logo.png" "$APPDIR/.DirIcon"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/bash
HERE="$(dirname "$(readlink -f "$0")")"
APP="$HERE/usr/bin/vrcnext/VRCNext"
export LD_LIBRARY_PATH="$HERE/usr/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

MISSING="$(ldd "$HERE/usr/bin/vrcnext/Photino.Native.so" 2>&1 | grep -E 'not found' | sed 's|'"$HERE"'/usr/bin/vrcnext/||g' | sort -u)"
if [ -n "$MISSING" ]; then
    MSG="VRCNext cannot start on this system:

$MISSING

If a library is missing, install the WebKitGTK package of your distribution, e.g.:
  Arch/CachyOS:  sudo pacman -S webkit2gtk-4.1 gst-plugins-base gst-plugins-good gst-libav
  Debian/Ubuntu: sudo apt install libwebkit2gtk-4.1-0 gstreamer1.0-plugins-good gstreamer1.0-libav
  Fedora:        sudo dnf install webkit2gtk4.1 gstreamer1-plugins-good

If a GLIBC version is reported as missing, your distribution is too old for this build (glibc 2.38 or newer is required)."
    echo "$MSG" >&2
    if command -v zenity >/dev/null 2>&1; then
        zenity --error --title="VRCNext" --width=520 --text="$MSG" || true
    elif command -v kdialog >/dev/null 2>&1; then
        kdialog --error "$MSG" || true
    elif command -v notify-send >/dev/null 2>&1; then
        notify-send "VRCNext" "Missing system libraries: $MISSING" || true
    fi
    exit 1
fi

exec "$APP" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# --------------------------------------------------------------- appimagetool
APPIMAGETOOL="$CACHE/appimagetool"
if [ ! -x "$APPIMAGETOOL" ]; then
    echo "==> Downloading appimagetool (one-time)..."
    curl -sSL -o "$APPIMAGETOOL" \
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "$APPIMAGETOOL"
fi

OUT_FILE="$OUT_DIR/VRCNext-$VERSION-x86_64.AppImage"
echo "==> Packing AppImage..."
rm -f "$OUT_FILE"
ARCH=x86_64 "$APPIMAGETOOL" --appimage-extract-and-run -n "$APPDIR" "$OUT_FILE" >/dev/null

[ -f "$OUT_FILE" ] || { echo "[ERROR] appimagetool did not produce an output file"; exit 1; }
chmod +x "$OUT_FILE"

echo "==> Done: $OUT_FILE ($(du -h "$OUT_FILE" | cut -f1))"
