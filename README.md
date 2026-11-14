<p align="center">
  <img src="icon.png" alt="GKME" width="128"/>
</p>

<h1 align="center">GKME（电脑端）</h1>

<p align="center">
  <strong>G</strong>amepad <strong>K</strong>eyboard <strong>M</strong>ouse <strong>E</strong>mulator<br/>
  also. <strong>G</strong>eneral <strong>K</strong>ey <strong>M</strong>apping <strong>E</strong>ngine
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPL v3"/></a>
  <a href="https://github.com/4zyz4/GKME-Windows/releases"><img src="https://img.shields.io/badge/download-Releases-blue?logo=github" alt="Download Releases"/></a>
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux-lightgrey.svg" alt="Platforms"/>
</p>

把 Android 手机变成电脑上的虚拟游戏手柄 —— 也可以是虚拟键盘和鼠标。电脑端通过 WiFi 局域网、USB 直连或网页手柄接收手机发来的按键、摇杆、陀螺仪与触摸数据，再以虚拟 USB 设备的形式交给系统，游戏和软件看到的就是一台真实的 DualShock 4、DualSense、Xbox 或 Switch 手柄。

- 💬 软件 QQ 群：**639317971**
- 📱 手机端：[GKME](https://github.com/4zyz4/GKME)
- 🌐 网页手柄：[GKME-Web](https://github.com/4zyz4/GKME-Web)
- ⚙️ 虚拟设备引擎：[GKMD](https://github.com/4zyz4/GKMD)

## 目录

- [支持的平台](#支持的平台)
- [功能特性](#功能特性)
- [工作原理](#工作原理)
- [快速上手](#快速上手)
- [下载与安装](#下载与安装)
- [从源码构建](#从源码构建)
- [系统要求](#系统要求)
- [项目结构](#项目结构)
- [日志与排错](#日志与排错)
- [开源协议](#开源协议)
- [鸣谢](#鸣谢)

---

## 支持的平台

| 平台 | 虚拟主机控制器 | 说明 |
|------|----------------|------|
| Windows 10 / 11（x64 / ARM64） | [usbip-win2](https://github.com/vadimgrn/usbip-win2) | 通过应用清单申请管理员权限；缺少驱动时内嵌官方安装包并按需安装 |
| Linux（主线内核 4.x+，x64 / ARM64） | 内核自带 `vhci_hcd`（USB/IP） | 挂载虚拟设备写入 root 所有的 sysfs 属性，因此需要 root 权限 |

虚拟设备引擎 [GKMD](../GKMD) 以源码方式编译进同一个 NativeAOT 映像，Windows 与 Linux 共用同一套设备实现，只是换成各自的主机控制器后端。

---

## 功能特性

### 🔌 连接方式

| 功能 | 说明 |
|------|------|
| **WiFi 局域网** | 自动发现或手动输入 IP，UDP + Protobuf 通信，断线自动重连 |
| **USB（ADB）** | 通过 `adb forward` 直连，无需局域网、延迟更低；优先使用已在运行的 adb，其次 `PATH` / Android SDK，最后回退到随程序附带的 `platform-tools` |
| **网页手柄** | 内置 HTTP + WebSocket 服务（默认端口 `37285`），托管 [GKME-Web](https://github.com/4zyz4/GKME-Web)，手机浏览器打开地址或扫码即可当手柄用，无需安装客户端 |
| **手动连接** | 直接输入手机 IP 连接；广播与握手携带设备标识（MAC / adb serial），便于区分多台设备 |
| **多端点合并** | 同一台手机经多个网卡被扫到时合并为一张设备卡片，展开卡片即可逐个 IP 连接 |

### 🎮 虚拟手柄

| 功能 | 说明 |
|------|------|
| **多种手柄型号** | DualShock 4、DualSense、Xbox 360、Xbox One、Xbox Series X\|S、Switch Pro、双 Joy-Con |
| **双 Joy-Con** | 以左右两只独立的 USB HID 设备（`057E:2006` / `057E:2007`）呈现，可被识别为真正的两只 Joy-Con |
| **按设备记住型号** | 每张设备卡片可单独选择手柄型号，选择结果按设备 ID 保存在 `device-modes.json` 中，重连或自动恢复时沿用 |
| **多设备并行** | 可同时连接多台手机或多个浏览器，各自对应一个独立的虚拟手柄 |
| **完整输入** | ABXY、摇杆、十字键、线性扳机、肩键、L3/R3、分享键、DS4 触摸板、陀螺仪与加速度计、电量与充电状态 |
| **虚拟键盘鼠标** | 会话期间同时创建 USB HID 键盘与鼠标，接收手机下发的按键、鼠标移动、滚轮与扫描码 |
| **Xbox GIP** | Xbox One / Series 以厂商类 GIP 设备呈现，Windows 由 `xboxgip.sys` + `xinputhid.sys` 原生识别为 XInput 设备 |

### ⚡ 反馈、音频与状态

| 功能 | 说明 |
|------|------|
| **震动** | 把系统下发的强弱马达、左右扳机震动回传给手机，由手机或实体手柄的马达执行 |
| **LED / 玩家指示灯** | 转发手柄灯色与玩家编号，外观颜色可实时跟随 |
| **自适应扳机** | 将 DualSense 自适应扳机效果原样转发给手机 |
| **HD 震动** | 把 Switch Pro / Joy-Con 的 HD 震动频段与幅度参数（`HdRumble`）下发，由手机本地合成 |
| **音频** | DualSense 扬声器 / 音圈音频以 48 kHz PCM 下发（`AudioFrame`），DS4 为 32 kHz 耳机音频折叠为单声道；支持测试音 |
| **电量上报** | 手机电量与充电状态写入虚拟手柄报文，Steam 等软件可直接读取 |

### 🖥️ 桌面体验

| 功能 | 说明 |
|------|------|
| **即插即用** | 无需安装 .NET 运行时，单个自包含 NativeAOT 可执行文件 |
| **多语言界面** | 跟随系统 / English / 简体中文 / 繁體中文（香港）/ 繁體中文（台灣）/ Русский / Français |
| **托盘常驻** | 关闭窗口隐藏到托盘而不是退出；Linux 下先检查 StatusNotifier 宿主，没有宿主时直接退出，避免窗口再也找不回来 |
| **单实例** | 检测到发现端口已被占用时提示「GKME 已在运行」，避免两个实例互相抢设备 |
| **驱动按需安装** | Windows 缺少或需要升级 USB/IP 驱动时弹出提示并调用内嵌安装包；Linux 直接或经 `pkexec` 执行 `modprobe vhci_hcd` 加载内核模块 |
| **关于页** | 连接方式、开源项目、GPLv3 协议全文、作者与赞助入口，以及当前日志路径 |
| **诊断日志** | 启动横幅、传输状态、连接尝试、创建失败堆栈与安装进度，全部写入固定路径的日志文件 |

---

## 工作原理

1. 电脑端启动后监听 UDP `37284`，广播携带 `GKME|` 前缀与设备标识，手机回应后出现在设备列表里；USB 模式则通过 `adb devices` 发现手机并建立 `adb forward`；
2. 连接时按接入方式建立会话：WiFi 走 UDP，USB 走 adb 转发端口，网页手柄走同源 WebSocket `/ws`；
3. 手机持续上传 `GamepadInput`（按键、摇杆、扳机、触摸、鼠标、键盘、姿态、电量），电脑端按所选型号把输入打包成对应的 USB HID 报文；
4. 报文经 USB/IP 交给 `usbip-win2` 虚拟主机控制器（Windows）或内核 `vhci_hcd`（Linux），设备被枚举成一棵真实的 USB 设备树；
5. 系统下发的输出报文（震动、灯色、扳机效果、音频）由电脑端解析成 `Vibration`、`LedState`、`TriggerEffects`、`HdRumble`、`AudioFrame`、`TestTone` 等消息回传给手机执行。

协议定义在 [`proto/gamepad_state.proto`](proto/gamepad_state.proto)，与手机端、网页端共用；传输层只在 WiFi / USB / WebSocket 之间替换分帧方式。

---

## 快速上手

### Windows

1. 安装并运行 GKME（会请求管理员权限）；
2. 手机安装 [GKME](https://github.com/4zyz4/GKME) 并启动服务，与电脑连入同一局域网；
3. 电脑端会自动出现设备卡片，选择手柄型号后点击「连接」；
4. 首次创建虚拟手柄时若提示缺少 USB/IP 传输，按提示点「安装」，必要时重启电脑；
5. 打开游戏，手柄即可被识别。

### Linux

```bash
sudo modprobe vhci_hcd        # 每次开机后挂载一次内核 USB/IP 主机控制器
chmod +x GKME-*.AppImage
./GKME-*.AppImage
```

AppImage 会在普通用户启动时通过 `pkexec` 重新执行自己以获得 root 权限，因此桌面上会弹出授权窗口，授权后程序自带所需权限，托盘图标仍注册在用户会话总线上。设置 `GKME_NO_ELEVATE=1` 可跳过提权，仅用于查看界面（此时无法创建虚拟手柄）。

### USB 模式（ADB）

用数据线连接手机与电脑，并在手机端选择 USB 模式。首次使用需要在手机上开启 USB 调试：

1. 打开「设置 → 关于手机」，连续点击「版本号」7 次开启开发者选项；
2. 回到「设置 → 系统 → 开发者选项」，打开「USB 调试」；
3. 用数据线连接电脑，并在手机上允许该电脑进行 USB 调试。

电脑端会自动使用 `platform-tools` 中附带的 adb，无需另外安装 Android SDK。

### 网页手柄

电脑端启动后，主界面会显示「网页手柄」卡片，可选择网卡并给出形如 `http://192.168.x.x:37285/` 的地址与二维码。手机与电脑在同一局域网时，用手机浏览器打开该地址（或扫码）即可把浏览器变成手柄，页面通过 `/ws` 上报输入并接收震动、灯色等反馈，无需安装任何 App。

---

## 下载与安装

前往 [GitHub Releases](https://github.com/4zyz4/GKME-Windows/releases) 下载最新版本：

| 平台 | 产物 |
|------|------|
| Windows x64 | `GKME_Setup_<版本>_x64.exe` |
| Windows ARM64 | `GKME_Setup_<版本>_arm64.exe` |
| Linux x64 | `GKME-v<版本>-linux-x64.AppImage` |
| Linux ARM64 | `GKME-v<版本>-linux-arm64.AppImage` |

Windows 安装包会创建开始菜单项、桌面快捷方式与卸载程序，并把随包分发的第三方协议文本放在安装目录的 `licenses\` 下。AppImage 无需安装，赋予可执行权限后直接运行。

---

## 从源码构建

### 目录布局

项目把 [GKMD](../GKMD) 的源码直接编译进产物，并可选地把 [GKME-Web](../GKME-Web) 打包进 `web\`，因此三个仓库需要放在同一父目录下：

```
DEV/
├── GKME-Windows/   # 本仓库
├── GKMD/           # 必需：虚拟设备引擎
└── GKME-Web/       # 可选：网页手柄前端，存在时自动打包
```

### 前置条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows：Visual Studio 2022 的 **使用 C++ 的桌面开发**（NativeAOT 需要 MSVC；构建 ARM64 还需 **MSVC ARM64/ARM64EC 生成工具**）
- Linux：`clang`、`zlib1g-dev`（ARM64 交叉构建另需 `aarch64-linux-gnu-objcopy`）
- 构建 Linux 产物必须在 Linux 上执行，NativeAOT 不支持跨平台编译

### 直接发布

同一个工程文件按 RID 派生目标框架与全部平台相关输入，一条命令对应一个平台：

```powershell
# Windows（在 Windows 上执行）
dotnet publish -c Release -r win-x64
dotnet publish -c Release -r win-arm64
```

```bash
# Linux（在 Linux 上执行）
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r linux-arm64
```

省略 `-r` 时使用当前机器的 RID，产物位于 `bin\Release\<tfm>\<rid>\publish`。

### 构建脚本（推荐）

```powershell
Install\Build-Installer.ps1                        # Windows x64 安装包（需 Inno Setup 7）
Install\Build-Installer.ps1 -Architecture arm64    # Windows ARM64 安装包
Install\Build-Linux.ps1                            # 在 WSL 中构建 AppImage
Install\Build-Linux.ps1 -Target Publish            # 只要发布目录，不打包 AppImage
Install\Build-All.ps1                              # 依次构建四个平台并汇总产物
Install\Build-All.ps1 -Platforms win-x64,linux-x64
```

`Build-Linux.ps1` 会把本仓库及其同级的 `GKMD`（存在 `GKME-Web` 时一并）同步到 WSL 的 `~/gkme-build` 后再构建——从 `/mnt/c` 构建既慢又会丢失可执行位；可用 `-Distro`、`-InstallDeps`、`-Clean` 调整。`Build-All.ps1` 把各平台产物收集到 `Install\Output\<rid>\`，某个平台失败不会中断其余平台，但脚本最终会以非零码退出并在摘要中列出失败原因。Linux 上也可直接调用 shell 脚本：

```bash
./Install/Build-Linux-AppImage.sh linux-x64      # → bin/Release/GKME-v<版本>-linux-x64.AppImage
./Install/Build-Linux-AppImage.sh linux-arm64
```

AppImage 由官方 MIT 的 type-2 runtime（内置于 `ThirdParty/AppImage`）加 SquashFS 载荷组装而成，使用 `mksquashfs` 打包，不需要 `appimagetool`，构建过程可离线完成。

### 随包 adb（`platform-tools`）

USB 模式会回退到应用旁边附带的 adb，因此不需要 Android SDK。Google 只按操作系统、不按架构分发 platform-tools，所以这里按系统分目录：

- `platform-tools\windows\`：`adb.exe` 与 `AdbWinApi.dll`、`AdbWinUsbApi.dll`。官方产物是 32 位 x86，可在 x64 与 ARM64 上通过模拟运行，无需单独的 ARM64 目录；
- `platform-tools\linux\`：官方 x86_64 版 `adb`。Linux ARM64 上只有在支持 x86_64 模拟时才能直接使用，否则会改用 `PATH` 中本机的 adb；
- `platform-tools\NOTICE.txt`：两者的 Apache 2.0 声明。

更新时，把 <https://developer.android.com/tools/releases/platform-tools> 的 `platform-tools-latest-windows.zip` / `platform-tools-latest-linux.zip` 解压覆盖到对应目录即可；目录只保留 adb 与其两个 Windows DLL，`platform-tools/linux/adb` 需保持可执行位。

---

## 系统要求

| 项目 | 要求 |
|------|------|
| Windows | Windows 10 / 11，x64 或 ARM64；运行需管理员权限 |
| Linux | 主线内核（4.x+）且提供 `vhci_hcd`；运行需 root 权限 |
| 运行时 | 无需安装 .NET（NativeAOT 自包含单文件） |
| 手机端 | Android 8.0（API 26）及以上；USB 模式需开启 USB 调试 |
| 网页手柄 | 支持 WebSocket 与 ES Modules 的现代移动浏览器，与电脑在同一局域网 |
| 构建 | .NET 10 SDK + C/C++ 工具链；Windows 安装包需 Inno Setup 7 |

### Linux 运行时说明

- 每次开机后需加载一次内核 USB/IP 主机控制器：`sudo modprobe vhci_hcd`。应用在缺少模块时也会尝试直接或经 `pkexec` 加载；
- 无 FUSE 或 AppImage runtime 无法挂载时，使用 `APPIMAGE_EXTRACT_AND_RUN=1`；不再默认提供 FUSE 2 的发行版需安装 `libfuse2`；
- 关闭窗口在 Linux 上依赖能承载 StatusNotifierItem 的桌面（KDE Plasma、Xfce、LXQt、带 AppIndicator 扩展的 GNOME 等）。应用会先检查 `org.kde.StatusNotifierWatcher`，没有宿主时直接退出而不是隐藏窗口；
- 中日韩文字优先使用系统 CJK 字体；进程看不到任何 CJK 字体时（例如以 root 运行、字体装在用户目录下），会注册内置的文泉驿微米黑作为回退字体，避免显示成方块。

---

## 项目结构

```
GKME-Windows/
├── App.axaml(.cs)              # 应用入口、主题与全局异常处理
├── Program.cs                  # 进程入口：启动横幅、全局异常与 CJK 字体回退
├── GKME.csproj                 # 单工程，按 RID 构建四个平台
├── app.manifest                # 申请管理员权限（构建时按版本生成）
├── linker.xml                  # NativeAOT 裁剪配置
├── Core/
│   ├── NetworkService.cs       # 设备发现、会话编排、UDP / ADB 传输
│   ├── GamepadSession.cs       # 单个会话：输入 → 虚拟手柄 + 下行反馈
│   ├── Personas/               # DS4 / DualSense / Xbox / Switch 报文与人设
│   ├── Transport/              # UDP、USB（adb forward）、WebSocket 传输
│   ├── Downlink/               # 震动、LED、扳机、HD 震动、音频下行
│   └── Usb/                    # adb 管理与 USB 连接
├── Ui/                         # 主窗口、设备卡片、关于 / 协议 / 赞助对话框
├── Web/                        # 内嵌 HTTP + WebSocket 静态托管与浏览器桥接
├── proto/gamepad_state.proto   # 与手机端、网页端共用的协议定义
├── Localization/               # 六种语言的界面文本与系统语言识别
├── Assets/                     # 图标、GPLv3 全文、CJK 回退字体
├── Install/                    # Windows 安装包与 Linux AppImage 构建脚本
├── platform-tools/             # 随包分发的 adb（windows / linux）
└── ThirdParty/AppImage/        # AppImage type-2 runtime
```

---

## 日志与排错

电脑端会写一份与安装/挂载目录无关的诊断日志（AppImage 载荷只读，旧版写在应用目录下的日志根本写不进去）：

| 运行方式 | 日志路径 |
|----------|----------|
| Windows | `%LOCALAPPDATA%\GKME\logs\gkme.log` |
| Linux，以 root 运行（AppImage + pkexec） | `/var/log/gkme/gkme.log` |
| Linux，以普通用户运行 | `~/.local/state/GKME/logs/gkme.log`（`$XDG_STATE_HOME`） |

日志包含启动横幅（构建、系统、架构、提权状态、路径）、USB/IP 传输状态、每次连接尝试、创建虚拟手柄失败时的完整堆栈与安装进度，同样的内容也会输出到 stderr。关于页会显示当前日志的准确路径。

内核侧的 USB/IP 问题查看内核环形缓冲区：

```bash
sudo dmesg | grep -iE 'vhci|usbip'
cat /sys/devices/platform/vhci_hcd.0/status   # 端口 / 状态 / 设备表
```

若 `/sys/devices/platform/vhci_hcd*` 不存在，说明模块未加载：

```bash
sudo modprobe vhci_hcd
```

### 已知限制

- WSLg 可以运行并操作界面，但 WSL 内核通常不带 `vhci_hcd`，虚拟手柄需要到真实 Linux 环境验证（较新的 WSL2 内核执行 `sudo modprobe vhci_hcd` 后也可完整测试）；
- Windows ARM64 与 Linux ARM64 产物属于跨架构构建，尚未在真机上完整验证。

---

## 开源协议

本项目基于 [GNU General Public License v3.0](LICENSE) 发布，完整协议文本见 [`LICENSE`](LICENSE)。

Copyright (C) 2026 4zyz4

```
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
```

---

## 鸣谢

### 参考与衍生项目

| 项目 | 用途 |
|------|------|
| [**HIDMaestro**](https://github.com/hifihedgehog/HIDMaestro) | 虚拟手柄驱动框架；[GKMD](../GKMD) 即由其 1.5.1 衍生 |
| [**usbip-win2**](https://github.com/vadimgrn/usbip-win2) | Windows 端虚拟 HID 设备桥接驱动（USB/IP 主机控制器） |
| [**Moonlight**](https://github.com/moonlight-stream/moonlight-android) | DualSense 触摸板识别算法参考 |
| [**Axixi2233/moonlight-android**](https://github.com/Axixi2233/moonlight-android) | 实体手柄 USB 驱动来源 |
| [**qiin2333/moonlight-vplus**](https://github.com/qiin2333/moonlight-vplus) | DS 蓝牙驱动来源 |

### 使用的开源库

| 库 | 用途 |
|------|------|
| [Avalonia](https://avaloniaui.net/) | 跨平台桌面 UI 框架 |
| [Google.Protobuf](https://github.com/protocolbuffers/protobuf) | 与手机端、网页端共用的协议序列化 |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | 检查 Linux 会话总线上的托盘宿主 |
| [**GKMD**](https://github.com/4zyz4/GKMD) | 虚拟 USB 手柄引擎（MIT） |
| [SDL3 / HIDAPI](https://libsdl.org/) | 手机端实体手柄输入与 HID 访问 |

### 随包分发的第三方资源

| 资源 | 协议 | 说明 |
|------|------|------|
| WenQuanYi Micro Hei | Apache-2.0 | Linux 缺少系统 CJK 字体时的回退字体，见 `Assets/Fonts/LICENSE-WQY-MicroHei.txt` |
| AppImage type-2 runtime | MIT | 置于 Linux AppImage 载荷之前，见 `ThirdParty/AppImage/LICENSE` |
| Android platform-tools（adb） | Apache-2.0 | USB 模式的随包 adb，见 `platform-tools/NOTICE.txt` |
| usbip-win2 安装包 | BSD-2-Clause | Windows 虚拟主机控制器驱动，安装后见 `licenses\USBIP-WIN2-LICENSE.txt` |
