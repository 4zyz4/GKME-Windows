#!/usr/bin/env bash
# Builds GKME for Linux from inside WSL (or on any Linux host).
#
#   bash Build-Linux-InWsl.sh --src=/mnt/c/.../DEV --target=AppImage
#
# Normally invoked by Build-Linux.ps1, which passes Windows paths; every option
# is --name=value so no positional quoting games are needed.
#
# Steps: sync the checkout (GKME-Windows + sibling GKMD + optional GKME-Web)
# into the Linux filesystem, restore permissions, publish with the Linux .NET
# SDK (NativeAOT cannot cross-compile), optionally wrap it into an AppImage,
# then copy the artifact back to the Windows bin folder.
set -euo pipefail

die() { echo "error: $*" >&2; exit 1; }
log() { echo "==> $*"; }

usage() {
    cat <<'EOF'
usage: Build-Linux-InWsl.sh --src=<dir> [options]

  --src=<dir>           directory containing GKME-Windows (and GKMD / GKME-Web)
  --rid=<rid>           linux-x64 (default: detected), linux-arm64, or auto
  --config=<cfg>        Release (default) or Debug
  --target=<task>       AppImage (default) or Publish
  --build-root=<dir>    Linux build tree (default: $HOME/gkme-build)
  --out=<dir>           Windows folder to copy artifacts to (optional)
  --copy-back=<0|1>     copy artifacts to --out (default: 1)
  --install-deps=<0|1>  apt-get install the missing build prerequisites
  --clean=<0|1>         delete the previous build tree before syncing
  -h, --help            this text
EOF
}

SRC_WIN=""
RID=""
CONFIG="Release"
TARGET="AppImage"
BUILD_ROOT=""
INSTALL_DEPS=0
COPY_BACK=1
CLEAN=0
WIN_OUT=""

for arg in "$@"; do
    case "$arg" in
        --src=*)          SRC_WIN="${arg#*=}" ;;
        --rid=*)          RID="${arg#*=}" ;;
        --config=*)       CONFIG="${arg#*=}" ;;
        --target=*)       TARGET="${arg#*=}" ;;
        --build-root=*)   BUILD_ROOT="${arg#*=}" ;;
        --install-deps=*) INSTALL_DEPS="${arg#*=}" ;;
        --copy-back=*)    COPY_BACK="${arg#*=}" ;;
        --clean=*)        CLEAN="${arg#*=}" ;;
        --out=*)          WIN_OUT="${arg#*=}" ;;
        -h|--help)        usage; exit 0 ;;
        *) die "unknown argument: $arg (see --help)" ;;
    esac
done

[[ -n "$SRC_WIN" ]] || { usage; die "--src is required"; }
[[ "$(uname -s)" == "Linux" ]] || die "this script must run on Linux (use Build-Linux.ps1 from Windows)"

# Accept both Windows (C:\...) and Linux paths.
SRC_PARENT="$(wslpath -u "$SRC_WIN" 2>/dev/null || printf '%s' "$SRC_WIN")"
[[ -d "$SRC_PARENT/GKME-Windows" ]] || die "not found: $SRC_PARENT/GKME-Windows"
[[ -d "$SRC_PARENT/GKMD" ]] || die "not found: $SRC_PARENT/GKMD (it must be a sibling of GKME-Windows)"

case "$RID" in
    ''|auto)
        case "$(uname -m)" in
            aarch64|arm64) RID="linux-arm64" ;;
            *)             RID="linux-x64" ;;
        esac
        ;;
    linux-x64|linux-arm64) ;;
    *) die "unsupported rid: $RID" ;;
esac

case "$TARGET" in
    AppImage|Publish) ;;
    *) die "unsupported target: $TARGET" ;;
esac

case "$CONFIG" in
    Release|Debug) ;;
    *) die "unsupported configuration: $CONFIG" ;;
esac

case "$BUILD_ROOT" in
    '') BUILD_ROOT="$HOME/gkme-build" ;;
    *)  BUILD_ROOT="$(wslpath -u "$BUILD_ROOT" 2>/dev/null || printf '%s' "$BUILD_ROOT")" ;;
esac
REPO="$BUILD_ROOT/GKME-Windows"

has_dotnet_sdk() {
    command -v dotnet >/dev/null 2>&1 || return 1
    dotnet --list-sdks 2>/dev/null |
        awk -F. '{ if ($1 + 0 > seen) seen = $1 + 0 } END { exit (seen >= 10 ? 0 : 1) }'
}

install_prereqs() {
    log "Installing build prerequisites (apt-get may ask for your password)"
    sudo apt-get update
    sudo apt-get install -y --no-install-recommends \
        clang zlib1g-dev squashfs-tools curl ca-certificates
    if ! has_dotnet_sdk; then
        log "Installing the .NET 10 SDK into $HOME/.dotnet"
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --channel 10.0
    fi
}

if [[ "$INSTALL_DEPS" == 1 ]]; then
    install_prereqs
fi

export PATH="$HOME/.dotnet:$PATH"

has_dotnet_sdk || die "no .NET 10 SDK found; rerun with --install-deps=1 or install it from https://dot.net"
command -v clang >/dev/null 2>&1 || die "clang not found; rerun with --install-deps=1"
if [[ "$TARGET" == "AppImage" ]]; then
    command -v mksquashfs >/dev/null 2>&1 ||
        die "mksquashfs not found (squashfs-tools); rerun with --install-deps=1"
fi

OBJCOPY="${OBJCOPY:-aarch64-linux-gnu-objcopy}"
if [[ "$RID" == "linux-arm64" && "$(uname -m)" != "aarch64" ]]; then
    command -v "$OBJCOPY" >/dev/null 2>&1 ||
        die "linux-arm64 on an x64 host needs $OBJCOPY (binutils-aarch64-linux-gnu)"
fi

log "Host: $(. /etc/os-release 2>/dev/null && echo "${PRETTY_NAME:-unknown}") $(uname -m)"
log "Build: $TARGET $RID ($CONFIG) -> $REPO"

ITEMS=(GKME-Windows GKMD)
if [[ -d "$SRC_PARENT/GKME-Web" ]]; then
    ITEMS+=(GKME-Web)
fi
EXCLUDES=(
    --exclude=bin --exclude=obj --exclude=.git --exclude=.vs
    --exclude=.idea --exclude=.vscode --exclude=node_modules --exclude='*.user'
)

if [[ "$CLEAN" == 1 ]]; then
    log "Removing the previous build tree"
    rm -rf "$BUILD_ROOT/GKME-Windows" "$BUILD_ROOT/GKMD" "$BUILD_ROOT/GKME-Web"
fi
mkdir -p "$BUILD_ROOT"

log "Copying sources: ${ITEMS[*]}"
if command -v rsync >/dev/null 2>&1; then
    # --delete keeps the copy honest (a removed source file must not linger and
    # still be compiled); bin/ and obj/ are excluded, so the incremental caches
    # of the previous build survive.
    for item in "${ITEMS[@]}"; do
        rsync -a --delete "${EXCLUDES[@]}" "$SRC_PARENT/$item/" "$BUILD_ROOT/$item/"
    done
else
    tar -C "$SRC_PARENT" -cf - "${EXCLUDES[@]}" "${ITEMS[@]}" |
        tar -C "$BUILD_ROOT" -xf -
fi

# /mnt/c does not carry POSIX permissions; a checkout copied from Windows can
# arrive with 0644 scripts, an executable AppImage runtime or a non-executable
# adb. Restore what the build and the payload need.
find "$REPO" -name '*.sh' -exec chmod +x {} + 2>/dev/null || true
chmod +x "$REPO"/ThirdParty/AppImage/runtime-* 2>/dev/null || true
chmod +x "$REPO/Install/linux/AppRun" 2>/dev/null || true
chmod 755 "$REPO/platform-tools/linux/adb" 2>/dev/null || true

if [[ "$TARGET" == "AppImage" ]]; then
    log "Publishing and packing the AppImage"
    bash "$REPO/Install/Build-Linux-AppImage.sh" "$RID" "$CONFIG"
    ARTIFACT="$(find "$REPO/bin/$CONFIG" -maxdepth 1 -name "GKME-v*-$RID.AppImage" -print -quit)"
    [[ -n "$ARTIFACT" ]] || die "AppImage was not produced under $REPO/bin/$CONFIG"
else
    PUBLISH_DIR="$REPO/bin/$CONFIG/net10.0/$RID/publish"
    PUBLISH_ARGS=(publish "$REPO/GKME.csproj" -c "$CONFIG" -r "$RID" -o "$PUBLISH_DIR")
    if [[ "$RID" == "linux-arm64" && "$(uname -m)" != "aarch64" ]]; then
        PUBLISH_ARGS+=(-p:ObjCopyName="$OBJCOPY")
    fi
    log "Publishing the executable"
    dotnet "${PUBLISH_ARGS[@]}"
    ARTIFACT="$PUBLISH_DIR"
fi

if [[ "$COPY_BACK" == 1 && -n "$WIN_OUT" ]]; then
    DEST="$(wslpath -u "$WIN_OUT" 2>/dev/null || printf '%s' "$WIN_OUT")"
    mkdir -p "$DEST"
    if [[ "$TARGET" == "AppImage" ]]; then
        cp -f "$ARTIFACT" "$DEST/"
        log "Copied $(basename "$ARTIFACT") to $WIN_OUT"
    else
        DEST="$DEST/publish-$RID"
        rm -rf "$DEST"
        mkdir -p "$DEST"
        cp -a "$ARTIFACT/." "$DEST/"
        log "Copied the publish output to $WIN_OUT\\publish-$RID"
    fi
fi

if [[ -d "$ARTIFACT" ]]; then
    log "Done: $ARTIFACT"
else
    log "Done: $ARTIFACT ($(du -h "$ARTIFACT" | cut -f1))"
fi
log "Run it on Linux with: chmod +x <file> && ./<file>   (virtual controllers need root, see README)"
