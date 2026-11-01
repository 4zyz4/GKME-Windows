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

### Windows installer

```powershell
Install\Build-Installer.ps1                 # x64 → GKME_Setup_<version>.exe
Install\Build-Installer.ps1 -Architecture arm64  # → GKME_Setup_<version>_arm64.exe
```

Requires Inno Setup 7 and, for ARM64, the Visual Studio **MSVC ARM64/ARM64EC
build tools** component. The ARM64 package embeds the ARM64 usbip-win2
installer; the bundled `platform-tools\adb.exe` is the x64 build and runs under
Windows' x64 emulation.

### Linux package

```bash
./Install/Build-Linux-Package.sh linux-x64      # → bin/Release/GKME-v<version>-linux-x64.tar.gz
./Install/Build-Linux-Package.sh linux-arm64
```

The tarball contains the executable, `web/`, `licenses/`, `icon.png`, an
`install.sh`, a `gkme` launcher and a desktop entry. `sudo ./install.sh`
installs to `/opt/gkme` and registers the application menu entry.

## Linux runtime notes

- Load the in-kernel USB/IP host controller once per boot:
  `sudo modprobe vhci_hcd`. The app also offers a prompt that runs this through
  `pkexec`.
- Attaching a virtual controller needs root. The installed `gkme` launcher
  elevates through `pkexec` and forwards the session's `DISPLAY`,
  `WAYLAND_DISPLAY`, `XAUTHORITY`, `XDG_RUNTIME_DIR` and locale variables.
  Without pkexec, run `sudo /opt/gkme/GKME` (start it from the launcher when
  the UI language matters, since a bare `sudo` may not keep `LANG`).
- USB mode needs `adb` on `PATH`, in `$ANDROID_HOME/platform-tools`, or in
  `platform-tools/` next to the executable. The Windows `adb.exe` copied into
  the repository is not used on Linux.
- Closing the window exits on Linux; on Windows it hides to the tray. The
  Linux tray icon is created when the desktop provides a StatusNotifier host,
  but a hidden window must never depend on one.
- Chinese/Japanese/Korean text uses the system CJK font when one is visible to
  the process. When none is visible — typically when the app runs as root and
  the fonts were installed under the user's home, as in WSLg — GKME registers
  the bundled WenQuanYi Micro Hei as the CJK fallback instead of drawing boxes
  (licence: `licenses/WQY-MICROHEI-LICENSE.txt`).

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
