# GKME (PC host)

The desktop side of **GKME**: it discovers an Android phone over WiFi, USB
(adb) or the browser, receives its controller input and presents it to the
system as a real USB gamepad — plus an optional USB keyboard and mouse.

Supported hosts:

| Host | Backend |
|------|---------|
| Windows x64 / ARM64 | [usbip-win2](https://github.com/vadimgrn/usbip-win2) virtual host controller |
| Linux x64 / ARM64 | mainline kernel `vhci_hcd` (USB/IP) |

The virtual-device engine is [GKMD](../GKMD), compiled into the same NativeAOT
image. On Windows the app requests administrator rights through its manifest;
on Linux every vhci attach writes root-owned sysfs attributes, so GKME has to
run as root.

## Build

The project builds one self-contained NativeAOT executable per platform. The
target framework and every platform-specific input are derived from the runtime
identifier, so the same project file builds all four.

```powershell
# Windows (run on Windows)
dotnet publish -c Release -r win-x64
dotnet publish -c Release -r win-arm64
```

```bash
# Linux (run on Linux; NativeAOT cannot cross-compile)
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r linux-arm64   # x64 host also needs aarch64-linux-gnu-objcopy
```

Without `-r`, the machine's own RID is used.

### Linux builds from Windows (WSL)

NativeAOT cannot cross-compile, so building the Linux executable from Windows
starts WSL and builds there:

```powershell
Install\Build-Linux.ps1                          # AppImage for the WSL architecture
Install\Build-Linux.ps1 -Target Publish          # plain publish output only
Install\Build-Linux.ps1 -Rid linux-arm64         # on x64 needs aarch64 binutils
Install\Build-Linux.ps1 -InstallDeps -Clean      # apt install prerequisites, full rebuild
Install\Build-Linux.ps1 -Distro Ubuntu -NoCopyBack
```

The script copies this project and its sibling `GKMD` (plus `GKME-Web` when
present) into `~/gkme-build` inside WSL. Building from `/mnt/c` is slow and
loses the executable bit, so the sources are synced to the Linux filesystem
first; `bin/`, `obj/` and repository metadata are skipped, and `rsync` keeps
the copy in sync when it is available. The artifact is copied back to
`bin\Release` (`GKME-v<version>-<rid>.AppImage`, or `publish-<rid>/` for
`-Target Publish`).

### All platforms

```powershell
Install\Build-All.ps1                              # every supported platform
Install\Build-All.ps1 -Platforms win-x64,linux-x64
Install\Build-All.ps1 -OutputDir D:\GKME-release
Install\Build-All.ps1 -KeepOutput -InstallDeps -Clean
```

Each platform is built in turn — Windows installers through
`Build-Installer.ps1`, Linux AppImages through WSL — and the artifacts are
collected into one subfolder per platform under `Install\Output`:

```
Install\Output\
  win-x64\GKME_Setup_<version>.exe
  win-arm64\GKME_Setup_<version>_arm64.exe
  linux-x64\GKME-v<version>-linux-x64.AppImage
  linux-arm64\GKME-v<version>-linux-arm64.AppImage
```

A platform that fails (Inno Setup 7, WSL or an ARM64 toolchain missing) is
reported in the summary and the script exits non-zero, but the remaining
platforms are still built. The per-platform folders are recreated unless
`-KeepOutput` is passed; `-InstallDeps` and `-Clean` are forwarded to the Linux
builds.

### Windows installer

```powershell
Install\Build-Installer.ps1                 # x64 → GKME_Setup_<version>.exe
Install\Build-Installer.ps1 -Architecture arm64  # → GKME_Setup_<version>_arm64.exe
```

Requires Inno Setup 7 and, for ARM64, the Visual Studio **MSVC ARM64/ARM64EC
build tools** component. The ARM64 package embeds the ARM64 usbip-win2
installer; adb is taken from `platform-tools\windows\`, whose official build is
32-bit x86 — the same file runs on x64 and on ARM64 through Windows' x86
emulation, so no separate ARM64 adb is needed.

### Bundled adb (`platform-tools`)

USB mode falls back to the adb shipped next to the app, so no Android SDK is
required. The Google platform-tools packages are split per OS, not per
architecture, so there is one folder per OS:

- `platform-tools\windows\` — `adb.exe` plus `AdbWinApi.dll` and
  `AdbWinUsbApi.dll`. The official build is 32-bit x86 and runs on x64 and on
  ARM64 through emulation, so there is no separate ARM64 folder. Shipped by
  both Windows installers.
- `platform-tools\linux\` — `adb` (official x86_64 build). Shipped by both
  AppImages.
- `platform-tools\NOTICE.txt` — Apache 2.0 notices for both.

To refresh them, extract `platform-tools-latest-windows.zip` /
`platform-tools-latest-linux.zip` from
<https://developer.android.com/tools/releases/platform-tools> over the two
folders. Only adb and its two Windows DLLs are kept — the folders are trimmed
on purpose. `platform-tools/linux/adb` must stay executable.

### Linux AppImage

```bash
./Install/Build-Linux-AppImage.sh linux-x64      # → bin/Release/GKME-v<version>-linux-x64.AppImage
./Install/Build-Linux-AppImage.sh linux-arm64
```

The AppImage contains the executable, its native libraries, `web/`,
`licenses/`, a desktop entry and an `AppRun` that requests administrator
rights through `pkexec` when started as a normal user (no install step and no
separate launcher). It is assembled from the official MIT AppImage runtime
(vendored under `ThirdParty/AppImage`) plus a SquashFS payload with
`mksquashfs`; `appimagetool` is not required and the build works offline.

Run it with `chmod +x GKME-*.AppImage && ./GKME-*.AppImage`, or double-click it
in the file manager.

## Linux runtime notes

- Load the in-kernel USB/IP host controller once per boot:
  `sudo modprobe vhci_hcd`. The app also offers a prompt that runs this through
  `pkexec`.
- Attaching a virtual controller needs root. `AppRun` re-runs the AppImage
  through `pkexec` (forwarding `DISPLAY`, `WAYLAND_DISPLAY`, `XAUTHORITY`,
  `XDG_RUNTIME_DIR`, `PULSE_SERVER` and the locale variables), so the desktop
  asks for authentication and the app comes up with the rights it needs. If
  polkit is unavailable and a graphical `SUDO_ASKPASS` helper is configured,
  `sudo -A` is used instead; otherwise the AppImage explains how to launch it
  with `sudo`.
- Set `GKME_NO_ELEVATE=1` to skip elevation for UI-only testing (virtual
  controllers will not be available). Without FUSE (or when the AppImage
  runtime cannot mount), use `APPIMAGE_EXTRACT_AND_RUN=1`; install `libfuse2`
  on distributions that no longer ship FUSE 2 by default.
- USB mode needs `adb` on `PATH`, in `$ANDROID_HOME/platform-tools`, or in
  `platform-tools/linux/` next to the executable. The AppImage bundles the
  official Linux adb, so no external install is required; on Linux ARM64 that
  copy is the x86_64 build and only works where x86_64 emulation is available,
  otherwise a native `adb` on `PATH` is used instead.
- Closing the window exits on Linux; on Windows it hides to the tray. The
  Linux tray icon is created when the desktop provides a StatusNotifier host,
  but a hidden window must never depend on one.
- Chinese/Japanese/Korean text uses the system CJK font when one is visible to
  the process. When none is visible — typically when the app runs as root and
  the fonts were installed under the user's home, as in WSLg — GKME registers
  the bundled WenQuanYi Micro Hei as the CJK fallback instead of drawing boxes
  (licence: `licenses/WQY-MICROHEI-LICENSE.txt`).

## Logs and troubleshooting

GKME writes a diagnostic log that is independent of the install/mount
directory (an AppImage payload is read-only, so the old
`<app dir>/gkme_error.log` could never be written there):

| Where GKME runs | Log file |
|-----------------|----------|
| Linux, as root (AppImage + pkexec) | `/var/log/gkme/gkme.log` |
| Linux, as a normal user | `~/.local/state/GKME/logs/gkme.log` (`$XDG_STATE_HOME`) |
| Windows | `%LOCALAPPDATA%\GKME\logs\gkme.log` |

The log contains a startup banner (build, OS, architecture, elevation, paths),
the USB/IP transport state, every connect attempt, controller-creation
failures with full stack traces, and installer progress. The same lines go to
stderr. The About dialog shows the exact log path.

Kernel-side USB/IP failures are in the kernel ring buffer:

```bash
sudo dmesg | grep -iE 'vhci|usbip'
cat /sys/devices/platform/vhci_hcd.0/status   # port/state/device table
```

If `/sys/devices/platform/vhci_hcd*` does not exist, the module is not loaded:

```bash
sudo modprobe vhci_hcd
```

## Limitations

- WSLg can run and drive the UI, but the WSL kernel usually ships without
  `vhci_hcd`, so virtual controller creation has to be verified on a real Linux
  installation. (A `sudo modprobe vhci_hcd` in a recent WSL2 kernel makes the
  whole flow testable inside WSL.)
- Windows arm64 and Linux arm64 builds are cross-architecture artifacts; they
  were not exercised on physical hardware as part of this repository's CI.

## Third-party assets

- **WenQuanYi Micro Hei** (Apache-2.0), embedded as a CJK glyph fallback for
  Linux hosts without a system CJK font. See
  `Assets/Fonts/LICENSE-WQY-MicroHei.txt`.
- **AppImage type-2 runtime** (MIT), vendored under `ThirdParty/AppImage` and
  prepended to the Linux AppImage payload. See `ThirdParty/AppImage/LICENSE`.
