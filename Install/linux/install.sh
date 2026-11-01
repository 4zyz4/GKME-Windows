#!/usr/bin/env bash
# Installs an unpacked GKME Linux package system-wide.
#
#   sudo ./install.sh
#
# Virtual controllers are created through the kernel's vhci_hcd USB/IP host
# controller, whose sysfs attributes only root may write, so GKME has to run
# elevated. The installed `gkme` launcher asks for that authorization with
# pkexec and keeps the desktop session's display environment.
set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
    echo "install.sh must run as root: sudo ./install.sh" >&2
    exit 1
fi

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${PREFIX:-/opt/gkme}"

echo "Installing GKME to $PREFIX"
install -d "$PREFIX"
# The package root is exactly the installed tree (executable, native
# libraries, web/, licenses/, platform-tools/, launcher and icon); only the
# installer artefacts are dropped.
cp -a "$HERE"/. "$PREFIX"/
rm -f "$PREFIX/install.sh" "$PREFIX/gkme.desktop"
chmod +x "$PREFIX/GKME" "$PREFIX/gkme"

install -d /usr/share/applications
sed "s|@PREFIX@|$PREFIX|g" "$HERE/gkme.desktop" > /usr/share/applications/gkme.desktop

if [[ -f "$HERE/icon.png" ]]; then
    install -d /usr/share/icons/hicolor/256x256/apps
    install -m 644 "$HERE/icon.png" /usr/share/icons/hicolor/256x256/apps/gkme.png
fi

install -d /usr/local/bin
ln -sf "$PREFIX/gkme" /usr/local/bin/gkme

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -f -t /usr/share/icons/hicolor || true

echo "Installed. Start GKME from the application menu or run: sudo $PREFIX/GKME"
echo "USB mode additionally needs adb (Android SDK platform-tools) on PATH or in $PREFIX/platform-tools."
