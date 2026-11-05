#!/usr/bin/env bash
set -e

APP_NAME="VRCNext"
ORIGINAL_CREATOR="shinyflvre"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Dynamically detect repository from git config (supports forks)
REPOSITORY=""
if [ -d "$SCRIPT_DIR/.git" ]; then
    REPOSITORY="$(cd "$SCRIPT_DIR" && git config --get remote.origin.url | sed -E 's|.*[:/]([^/]+)/([^/]+?)(\.git)?$|\1/\2|')"
fi

# Fallback to original creator if not in a git repository
if [ -z "$REPOSITORY" ]; then
    REPOSITORY="$ORIGINAL_CREATOR/VRCNext"
fi

if [ "$(uname -s)" != "Linux" ]; then
    echo "Error: This installer supports Linux only."
    exit 1
fi

# Prefer a local AppImage (beside this script or in publish/linux/); otherwise fetch the latest release.
APPIMAGE_PATH=""
for candidate in "$SCRIPT_DIR"/VRCNext*.AppImage "$SCRIPT_DIR/publish/linux/"VRCNext*.AppImage; do
    if [ -f "$candidate" ]; then
        APPIMAGE_PATH="$candidate"
        break
    fi
done

if [ -z "$APPIMAGE_PATH" ]; then
    if ! command -v curl >/dev/null 2>&1; then
        echo "Error: curl is required to download the latest release."
        exit 1
    fi
    RELEASE_JSON="$(curl -fsSL "https://api.github.com/repos/$REPOSITORY/releases/latest")"
    ASSET_URL="$(printf '%s\n' "$RELEASE_JSON" | grep -Eo '"browser_download_url": *"[^"]+\.AppImage"' | head -n 1 | sed -E 's/.*"(https:[^"]+)"/\1/' || true)"
    if [ -z "$ASSET_URL" ]; then
        echo "Error: No AppImage asset found in the latest $REPOSITORY release."
        exit 1
    fi
    APPIMAGE_PATH="$(mktemp "${TMPDIR:-/tmp}/vrcnext-XXXXXX.AppImage")"
    trap 'rm -f "$APPIMAGE_PATH"' EXIT
    echo "Downloading latest VRCNext AppImage..."
    curl -fL "$ASSET_URL" -o "$APPIMAGE_PATH"
    chmod +x "$APPIMAGE_PATH"
fi

# Detect package manager and install dependencies
install_deps() {
    local PKGS_APT="libwebkit2gtk-4.1-0 libgtk-3-0 libglib2.0-0 libcanberra-gtk3-0 zenity gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-libav"
    local PKGS_ARCH="webkit2gtk-4.1 gtk3 glib2 libcanberra zenity gst-plugins-base gst-plugins-base-libs gst-plugins-good gst-libav"
    local PKGS_DNF="webkit2gtk4.1 gtk3 glib2 libcanberra-gtk3 zenity gstreamer1-plugins-base-tools gstreamer1-plugins-base gstreamer1-plugins-good gstreamer1-plugin-libav"
    local PKGS_ZYPPER="libwebkit2gtk-4_1-0 gtk3 glib2 libcanberra-gtk3-0 zenity gstreamer-plugins-base gstreamer-plugins-good gstreamer-plugins-libav"

    if command -v apt-get &>/dev/null; then
        echo "[1/4] Installing dependencies via apt..."
        sudo apt-get update
        sudo apt-get install -y $PKGS_APT
    elif command -v pacman &>/dev/null; then
        echo "[1/4] Installing dependencies via pacman..."
        sudo pacman -Sy --noconfirm $PKGS_ARCH
    elif command -v dnf &>/dev/null; then
        echo "[1/4] Installing dependencies via dnf..."
        sudo dnf install -y $PKGS_DNF
    elif command -v zypper &>/dev/null; then
        echo "[1/4] Installing dependencies via zypper..."
        sudo zypper install -y $PKGS_ZYPPER
    else
        echo "Warning: Could not detect package manager. Please install manually:"
        echo "  libwebkit2gtk-4.1, libgtk-3, libglib2.0, zenity, GStreamer tools (gst-play-1.0), GStreamer base/good plugins"
    fi
}

install_deps

# Install AppImage with Shelly
echo "[2/4] Installing $APP_NAME via Shelly..."
if command -v shelly &>/dev/null; then
    shelly -Ii "$APPIMAGE_PATH"
else
    echo "Error: Shelly not found. Please install Shelly first."
    echo "Install Shelly using the instructions at: https://github.com/barknq11/shelly-apt"
    exit 1
fi



echo "[3/3] Done. Shelly installed $APP_NAME."
echo "Launch it from your application menu."
echo "To uninstall: shelly -Ri VRCNext"
