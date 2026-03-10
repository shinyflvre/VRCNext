#!/usr/bin/env bash
set -e

APP_NAME="VRCNext"
INSTALL_DIR="/opt/vrcnext"
DESKTOP_FILE="$HOME/.local/share/applications/vrcnext.desktop"
BINARY_NAME="VRCNext"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY_PATH="$SCRIPT_DIR/$BINARY_NAME"

if [ ! -f "$BINARY_PATH" ]; then
    echo "Error: $BINARY_NAME not found in $SCRIPT_DIR"
    echo "Run this script from the same directory as the VRCNext binary."
    exit 1
fi

# Detect package manager and install dependencies
install_deps() {
    local PKGS_APT="libwebkit2gtk-4.1-0 libgtk-3-0 libglib2.0-0 zenity gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-libav"
    local PKGS_ARCH="webkit2gtk-4.1 gtk3 glib2 zenity gst-plugins-base gst-plugins-good gst-libav"
    local PKGS_DNF="webkit2gtk4.1 gtk3 glib2 zenity gstreamer1-plugins-base gstreamer1-plugins-good gstreamer1-plugin-libav"
    local PKGS_ZYPPER="libwebkit2gtk-4_1-0 gtk3 glib2 zenity gstreamer-plugins-base gstreamer-plugins-good gstreamer-plugins-libav"

    if command -v apt-get &>/dev/null; then
        echo "[1/4] Installing dependencies via apt..."
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
        echo "  libwebkit2gtk-4.1, libgtk-3, libglib2.0, zenity"
    fi
}

install_deps

# Install binary and required assets
echo "[2/4] Installing $APP_NAME to $INSTALL_DIR..."
chmod +x "$BINARY_PATH"
sudo mkdir -p "$INSTALL_DIR"
sudo cp "$BINARY_PATH" "$INSTALL_DIR/$BINARY_NAME"
sudo chmod +x "$INSTALL_DIR/$BINARY_NAME"

# Copy wwwroot (required — app will not start without it)
if [ -d "$SCRIPT_DIR/wwwroot" ]; then
    sudo cp -r "$SCRIPT_DIR/wwwroot" "$INSTALL_DIR/"
fi

# Copy voice folder (Voice Fight samples)
if [ -d "$SCRIPT_DIR/voice" ]; then
    sudo cp -r "$SCRIPT_DIR/voice" "$INSTALL_DIR/"
fi

# Copy icon if present
if [ -f "$SCRIPT_DIR/wwwroot/logo.png" ]; then
    sudo cp "$SCRIPT_DIR/wwwroot/logo.png" "$INSTALL_DIR/icon.png"
    ICON_PATH="$INSTALL_DIR/icon.png"
else
    ICON_PATH="application-x-executable"
fi

# Create .desktop entry
echo "[3/4] Registering application..."
mkdir -p "$(dirname "$DESKTOP_FILE")"
cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=VRCNext
Comment=VRChat companion app
Exec=$INSTALL_DIR/$BINARY_NAME
Icon=$ICON_PATH
Terminal=false
Categories=Game;Utility;
StartupWMClass=VRCNext
EOF
chmod +x "$DESKTOP_FILE"

# Update desktop database if available
if command -v update-desktop-database &>/dev/null; then
    update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
fi

echo "[4/4] Done."
echo ""
echo "VRCNext installed to $INSTALL_DIR/$BINARY_NAME"
echo "You can launch it from your application menu or run:"
echo "  $INSTALL_DIR/$BINARY_NAME"
echo ""
echo "To uninstall:"
echo "  sudo rm -rf $INSTALL_DIR && rm -f $DESKTOP_FILE"
