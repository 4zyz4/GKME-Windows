## Description

<!-- Describe what this PR changes and why. Keep one PR focused on one thing. -->

## Related issue

<!-- Link the issue(s) this PR addresses, e.g. Closes #123 or Refs #123. -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Performance improvement
- [ ] Refactor (no behavior change)
- [ ] Documentation / translation
- [ ] Build / toolchain / installer
- [ ] Other (describe below)

## How has this been tested?

<!--
Describe your testing. State the platform, OS, connection method and the virtual controller models you verified.
If you could not test a platform or transport path, say so explicitly.
-->

- **Platform / RID:** <!-- win-x64 / win-arm64 / linux-x64 / linux-arm64 -->
- **OS:** <!-- e.g. Windows 11 24H2, Fedora 41 kernel 6.11 -->
- **Connection method:** <!-- WiFi LAN / USB (ADB) / Web controller -->
- **Virtual controller:** <!-- DS4 / DualSense / Xbox 360 / Xbox One / Series / Switch Pro / Dual Joy-Con / keyboard+mouse -->
- **Verified scenarios:**
- **Build:** <!-- e.g. `dotnet publish -c Release -r win-x64` succeeded, or `Install\Build-Installer.ps1` -->

## Screenshots / screen recording

<!-- Required for UI changes. Mention the affected languages (en / zh-CN / zh-HK / zh-TW / ru / fr) and high-DPI scaling where relevant. -->

## Compatibility

<!--
Explain any impact on:
- the protobuf wire protocol shared with the Android client and the web controller (`proto/gamepad_state.proto`)
  (changes must land in GKME and GKME-Web at the same time)
- persisted data (`%LOCALAPPDATA%\GKME\settings.json`, `device-modes.json`)
- virtual device reports and HID descriptors (`Core/Personas/`, GKMD)
- the GKMD sources compiled from `..\GKMD` (submit those changes to the GKMD repository)
- installer / AppImage layout and the bundled adb (`platform-tools/`)
Write "None" if not applicable.
-->

## Checklist

- [ ] The change is focused and does not include unrelated formatting or renames.
- [ ] I built and actually ran the change on at least one platform (NativeAOT publish succeeds).
- [ ] UI text changes update all six languages in `Localization/Translations.cs`.
- [ ] I have updated the documentation (`README.md`) and code comments where behavior changed.
- [ ] I have not included build outputs (`bin/`, `obj/`, `Install/Output/`, `*.exe`, AppImage), `.vs/`, logs, or local settings files.
- [ ] I have not changed `<Version>` in `GKME.csproj` (releases are handled by the maintainer).
- [ ] Changes to `..\GKMD` are submitted in the [GKMD](https://github.com/4zyz4/GKMD) repository instead of patching the local copy.
