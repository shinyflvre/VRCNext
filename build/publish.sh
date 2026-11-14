#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
CACHE="$HOME/.cache/vrcnext-build"
OUT_DIR="$REPO/publish"
PUBLISH_DIR="$OUT_DIR/release"

VERSION="$(grep -oP 'Version = "\K[^"]+' "$REPO/main/AppInfo.cs")"
[ -n "$VERSION" ] || { echo "[ERROR] Could not read version from main/AppInfo.cs"; exit 1; }

echo "==============================================="
echo "VRCNext Build & Publish v$VERSION"
echo "==============================================="

# Setup environment
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
mkdir -p "$CACHE" "$PUBLISH_DIR"

# ================================================================ Find dotnet
DOTNET="${DOTNET_PATH:-/home/hollyn/.dotnet/dotnet}"
if [ ! -x "$DOTNET" ]; then
    DOTNET="$(command -v dotnet || echo '')"
fi
if [ -z "$DOTNET" ] || [ ! -x "$DOTNET" ]; then
    echo "[ERROR] dotnet not found. Please ensure .NET SDK 9 is installed."
    exit 1
fi
echo "[INFO] Using dotnet: $DOTNET ($("$DOTNET" --version))"

# ================================================================ Windows Build
build_windows() {
    echo ""
    echo ">>> Building Windows Publish (win-x64)..."
    
    local STAGE="$CACHE/stage-windows"
    rm -rf "$STAGE"
    
    "$DOTNET" publish "$REPO/VRCNext.csproj" \
        -c Release -r win-x64 --self-contained true \
        -o "$STAGE" \
        -p:VRCNextVrcndbKey="${VRCNEXT_VRCNDB_KEY:-}" \
        -p:VRCNextHypeRateApiKey="${VRCNEXT_HYPERATE_API_KEY:-}"
    
    [ -f "$STAGE/VRCNext.exe" ] || { echo "[ERROR] Windows publish missing VRCNext.exe"; exit 1; }
    [ -d "$STAGE/frontend" ] || { echo "[ERROR] Windows publish missing frontend/"; exit 1; }
    
    echo "[✓] Windows build complete"
    echo "    Output: $STAGE"
    
    # Package Windows release
    local WIN_ZIP="$PUBLISH_DIR/VRCNext-$VERSION-windows-x64.zip"
    echo "[INFO] Creating Windows archive: $WIN_ZIP"
    (cd "$STAGE" && zip -r -q "$WIN_ZIP" . || (echo "[ERROR] zip failed"; exit 1))
    echo "[✓] Windows archive created ($(du -h "$WIN_ZIP" | cut -f1))"
}

# ================================================================ Linux AppImage Build
build_appimage() {
    echo ""
    echo ">>> Building Linux AppImage (linux-x64)..."
    
    local STAGE="$CACHE/stage-appimage"
    
    rm -rf "$STAGE"
    
    # Publish
    echo "[INFO] Publishing linux-x64 self-contained..."
    "$DOTNET" publish "$REPO/VRCNext.csproj" \
        -c Release -r linux-x64 --self-contained true \
        -o "$STAGE" \
        -p:VRCNextVrcndbKey="${VRCNEXT_VRCNDB_KEY:-}" \
        -p:VRCNextHypeRateApiKey="${VRCNEXT_HYPERATE_API_KEY:-}"
    
    [ -f "$STAGE/VRCNext" ] || { echo "[ERROR] Linux publish missing VRCNext binary"; exit 1; }
    [ -d "$STAGE/frontend" ] || { echo "[ERROR] Linux publish missing frontend/"; exit 1; }
    
    # Verify required libraries
    for lib in Photino.Native.so libnfd.so libe_sqlite3.so libSkiaSharp.so; do
        [ -f "$STAGE/$lib" ] || { echo "[ERROR] Linux publish missing $lib"; exit 1; }
    done
    
    echo "[INFO] Cleaning Windows-only artifacts..."
    for f in libvosk.so Vosk.dll NAudio.dll NAudio.Vorbis.dll NVorbis.dll DiscordRPC.dll \
             System.Speech.dll openvr_api.dll libSkiaSharp.dll e_sqlite3.dll nfd.dll \
             Vortice.Direct3D11.dll Vortice.DXGI.dll Vortice.Direct3D.dll \
             System.Diagnostics.PerformanceCounter.dll; do
        [ -e "$STAGE/$f" ] && rm -f "$STAGE/$f"
    done
    for d in voice tray frameshot; do
        [ -d "$STAGE/$d" ] && rm -rf "$STAGE/$d"
    done
    find "$STAGE" -maxdepth 1 -name "*.exe" -delete
    
    chmod +x "$STAGE/VRCNext"
    
    echo "[✓] Linux build complete"
    
    # Create tarball
    local TAR_FILE="$PUBLISH_DIR/VRCNext-$VERSION-linux-x64.tar.gz"
    echo "[INFO] Creating Linux tarball: $TAR_FILE"
    tar -czf "$TAR_FILE" -C "$STAGE" .
    echo "[✓] Tarball created ($(du -h "$TAR_FILE" | cut -f1))"
}

# ================================================================ Main
main() {
    case "${1:-all}" in
        windows)
            build_windows
            ;;
        linux|appimage)
            build_appimage
            ;;
        all)
            build_windows
            build_appimage
            ;;
        *)
            echo "Usage: $0 {windows|appimage|all}"
            exit 1
            ;;
    esac
    
    echo ""
    echo "==============================================="
    echo "[✓] Build complete!"
    echo "    Output directory: $PUBLISH_DIR"
    echo "    Files:"
    ls -lh "$PUBLISH_DIR"/ 2>/dev/null || echo "    (no files yet)"
    echo "==============================================="
}

main "$@"

