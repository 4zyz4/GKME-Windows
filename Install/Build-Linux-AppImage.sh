#!/usr/bin/env bash
# Builds the Linux AppImage for one architecture.
#
#   ./Install/Build-Linux-AppImage.sh            # host architecture
#   ./Install/Build-Linux-AppImage.sh linux-arm64
#
# Must run on Linux: NativeAOT cannot cross-compile from Windows. On an x64
# machine the ARM64 build additionally needs aarch64 binutils
# (aarch64-linux-gnu-objcopy, override with OBJCOPY=...).
#
# No appimagetool is needed: an AppImage is the official AppImage runtime
# followed by a SquashFS image. The runtimes are vendored under
# ThirdParty/AppImage (MIT), so the build works offline; override the runtime
# with APPIMAGE_RUNTIME=/path/to/runtime-<arch>.
#
# Output: bin/Release/GKME-v<version>-<rid>.AppImage
set -euo pipefail

if [[ $# -ge 1 ]]; then
    RID="$1"
else
    case "$(uname -m)" in
        aarch64|arm64) RID="linux-arm64" ;;
        *)             RID="linux-x64" ;;
    esac
fi
CONFIG="${2:-Release}"

case "$RID" in
    linux-x64)   APPIMAGE_ARCH="x86_64" ;;
    linux-arm64) APPIMAGE_ARCH="aarch64" ;;
    *) echo "usage: $0 [linux-x64|linux-arm64] [Release|Debug]" >&2; exit 2 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/GKME.csproj"
PUBLISH_DIR="$REPO_ROOT/bin/$CONFIG/net10.0/$RID/publish"
RUNTIME="${APPIMAGE_RUNTIME:-$REPO_ROOT/ThirdParty/AppImage/runtime-$APPIMAGE_ARCH}"
BUILD_DIR="$REPO_ROOT/obj/appimage"
APPDIR="$BUILD_DIR/GKME.AppDir"
PAYLOAD="$BUILD_DIR/GKME-$RID.squashfs"

for tool in dotnet mksquashfs; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "missing required tool: $tool (install squashfs-tools)" >&2
        exit 1
    }
done
[[ -x "$RUNTIME" ]] || { echo "AppImage runtime not found or not executable: $RUNTIME" >&2; exit 1; }

VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$CSPROJ" | head -n1)"
VERSION="${VERSION:-0.0.0}"
SHORT_VERSION="$(echo "$VERSION" | cut -d. -f1-3)"
OUTPUT="$REPO_ROOT/bin/$CONFIG/GKME-v$SHORT_VERSION-$RID.AppImage"

PUBLISH_ARGS=(publish "$CSPROJ" -c "$CONFIG" -r "$RID" -o "$PUBLISH_DIR")
if [[ "$RID" == "linux-arm64" && "$(uname -m)" != "aarch64" ]]; then
    PUBLISH_ARGS+=(-p:ObjCopyName="${OBJCOPY:-aarch64-linux-gnu-objcopy}")
fi

echo "=== Publishing GKME $SHORT_VERSION for $RID ==="
dotnet "${PUBLISH_ARGS[@]}"

# Strip repository metadata a stale checkout may have copied into web/.
WEB_PUBLISH="$PUBLISH_DIR/web"
if [[ -d "$WEB_PUBLISH" ]]; then
    rm -rf "$WEB_PUBLISH/.git" "$WEB_PUBLISH/.github" "$WEB_PUBLISH/.vscode" \
           "$WEB_PUBLISH/.idea" "$WEB_PUBLISH/.gitignore" "$WEB_PUBLISH/.gitattributes"
fi

echo "=== Assembling AppDir ==="
rm -rf "$APPDIR"
install -d "$APPDIR/usr/bin" \
           "$APPDIR/usr/share/applications" \
           "$APPDIR/usr/share/icons/hicolor/256x256/apps"

# Executable and every runtime file it needs next to it; skip symbols and
# logs a previous local run may have left in the publish folder.
find "$PUBLISH_DIR" -maxdepth 1 -type f \
    ! -name '*.dbg' ! -name '*.pdb' ! -name '*.log' ! -name 'icon.ico' ! -name 'Thumbs.db' \
    -exec cp -a {} "$APPDIR/usr/bin/" \;

for dir in web licenses; do
    if [[ -d "$PUBLISH_DIR/$dir" ]]; then cp -a "$PUBLISH_DIR/$dir" "$APPDIR/usr/bin/$dir"; fi
done

# platform-tools holds one subfolder per OS; the AppImage ships only the Linux
# adb (the Windows build would be dead weight). The executable bit is restored
# here because a checkout made on Windows cannot record it.
LINUX_ADB_DIR="$PUBLISH_DIR/platform-tools/linux"
if [[ -f "$LINUX_ADB_DIR/adb" ]]; then
    install -d "$APPDIR/usr/bin/platform-tools/linux"
    cp -a "$LINUX_ADB_DIR/." "$APPDIR/usr/bin/platform-tools/linux/"
    chmod 755 "$APPDIR/usr/bin/platform-tools/linux/adb"
    if [[ -f "$PUBLISH_DIR/platform-tools/NOTICE.txt" ]]; then
        install -m 644 "$PUBLISH_DIR/platform-tools/NOTICE.txt" "$APPDIR/usr/bin/platform-tools/NOTICE.txt"
    fi
fi

cp "$REPO_ROOT/ThirdParty/AppImage/LICENSE" "$APPDIR/usr/bin/licenses/APPIMAGE-RUNTIME-LICENSE.txt"

install -m 755 "$REPO_ROOT/Install/linux/AppRun" "$APPDIR/AppRun"
install -m 644 "$REPO_ROOT/Install/linux/gkme.desktop" "$APPDIR/gkme.desktop"
install -m 644 "$REPO_ROOT/Install/linux/gkme.desktop" "$APPDIR/usr/share/applications/gkme.desktop"
install -m 644 "$PUBLISH_DIR/icon.png" "$APPDIR/gkme.png"
install -m 644 "$PUBLISH_DIR/icon.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/gkme.png"
ln -sf gkme.png "$APPDIR/.DirIcon"

# The build's umask may be 077; AppImage tools and the FUSE mount expect
# world-readable files and traversable directories.
chmod -R a+rX "$APPDIR"

echo "=== Compressing SquashFS ==="
rm -f "$PAYLOAD"
mksquashfs "$APPDIR" "$PAYLOAD" \
    -comp zstd -Xcompression-level 19 \
    -root-owned -noappend -no-progress >/dev/null

echo "=== Concatenating runtime and payload ==="
install -d "$(dirname "$OUTPUT")"
cat "$RUNTIME" "$PAYLOAD" > "$OUTPUT.tmp"
chmod +x "$OUTPUT.tmp"
# Atomic replace: a running AppImage must not block a rebuild with ETXTBSY.
mv -f "$OUTPUT.tmp" "$OUTPUT"

echo "=== Done ==="
echo "AppImage: $OUTPUT ($(du -h "$OUTPUT" | cut -f1))"
