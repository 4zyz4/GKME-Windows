#!/usr/bin/env bash
# Builds the Linux package for one architecture.
#
#   ./Install/Build-Linux-Package.sh            # the host architecture
#   ./Install/Build-Linux-Package.sh linux-arm64
#
# Must run on Linux: NativeAOT cannot cross-compile from Windows. On an x64
# machine the ARM64 build additionally needs aarch64 binutils
# (aarch64-linux-gnu-objcopy, override with OBJCOPY=...).
#
# Output: bin/Release/GKME-v<version>-<rid>.tar.gz
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
    linux-x64|linux-arm64) ;;
    *) echo "usage: $0 [linux-x64|linux-arm64] [Release|Debug]" >&2; exit 2 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/GKME.csproj"
PUBLISH_DIR="$REPO_ROOT/bin/$CONFIG/net10.0/$RID/publish"

VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$CSPROJ" | head -n1)"
VERSION="${VERSION:-0.0.0}"
SHORT_VERSION="$(echo "$VERSION" | cut -d. -f1-3)"
STAGE="$REPO_ROOT/obj/release-package/GKME-v$SHORT_VERSION-$RID"
OUTPUT="$REPO_ROOT/bin/$CONFIG/GKME-v$SHORT_VERSION-$RID.tar.gz"

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

echo "=== Staging $STAGE ==="
rm -rf "$STAGE"
mkdir -p "$STAGE"

# NativeAOT plus Avalonia's native libraries (libSkiaSharp.so,
# libHarfBuzzSharp.so) are separate files on Linux; ship every published file
# except debug symbols.
find "$PUBLISH_DIR" -maxdepth 1 -type f ! -name '*.dbg' ! -name '*.pdb' \
    -exec cp -a {} "$STAGE/" \;
chmod +x "$STAGE/GKME"

for dir in web licenses; do
    if [[ -d "$PUBLISH_DIR/$dir" ]]; then cp -a "$PUBLISH_DIR/$dir" "$STAGE/$dir"; fi
done

# The repository's platform-tools folder holds the Windows adb. Only bundle it
# when it actually contains the Linux adb, which the app looks up as `adb`.
if [[ -x "$PUBLISH_DIR/platform-tools/adb" ]]; then
    cp -a "$PUBLISH_DIR/platform-tools" "$STAGE/platform-tools"
elif [[ -d "$PUBLISH_DIR/platform-tools" ]]; then
    echo "Skipping platform-tools (no Linux adb inside)"
fi

cp "$REPO_ROOT/Install/linux/install.sh" "$STAGE/install.sh"
cp "$REPO_ROOT/Install/linux/gkme" "$STAGE/gkme"
cp "$REPO_ROOT/Install/linux/gkme.desktop" "$STAGE/gkme.desktop"
chmod +x "$STAGE/install.sh" "$STAGE/gkme"

mkdir -p "$(dirname "$OUTPUT")"
tar -C "$STAGE/.." -czf "$OUTPUT" "$(basename "$STAGE")"

echo "=== Done ==="
echo "Package: $OUTPUT"
