# Builds the Linux version of GKME by running the build inside WSL.
#
# NativeAOT cannot cross-compile, so the Linux executable must come from a
# Linux .NET SDK. This entry point starts WSL, hands the work to
# Install/Build-Linux-InWsl.sh (which copies the checkout out of /mnt/c into
# the Linux filesystem, publishes, optionally packs an AppImage, and copies the
# artifact back), and reports the result.
#
# Examples:
#   Install\Build-Linux.ps1
#   Install\Build-Linux.ps1 -Target Publish -Rid linux-x64
#   Install\Build-Linux.ps1 -InstallDeps -Clean
#   Install\Build-Linux.ps1 -Distro Ubuntu -Configuration Debug

[CmdletBinding()]
param(
    # AppImage: dotnet publish + Install/Build-Linux-AppImage.sh (the release
    # artifact). Publish: plain publish output folder only.
    [ValidateSet('AppImage', 'Publish')]
    [string]$Target = 'AppImage',

    # auto follows the WSL architecture (linux-x64 on an x64 machine).
    [ValidateSet('auto', 'linux-x64', 'linux-arm64')]
    [string]$Rid = 'auto',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # WSL distribution to use; empty means the default distribution.
    [string]$Distro,

    # Build tree inside Linux. A path on the Linux filesystem is much faster
    # than /mnt/c and keeps the executable bit. Default: ~/gkme-build.
    [string]$BuildRoot,

    # Where to copy the artifact back to. Default: bin\<Configuration>.
    # Build-All.ps1 points this at Install\Output\<rid>.
    [string]$OutputDir,

    # Install the missing prerequisites with apt-get (clang, zlib1g-dev,
    # squashfs-tools, and the .NET 10 SDK when absent).
    [switch]$InstallDeps,

    # Delete the previous build tree before syncing (a full NativeAOT rebuild).
    [switch]$Clean,

    # Do not copy the artifact back to bin\<Configuration>.
    [switch]$NoCopyBack
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw '找不到 wsl.exe，请先安装并初始化 WSL2：wsl --install'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceParent = Split-Path -Parent $repoRoot

if (-not (Test-Path (Join-Path $sourceParent 'GKMD'))) {
    throw "未找到 GKMD 源码：$sourceParent\GKMD。GKME.csproj 直接编译同级目录的 GKMD，请保持目录结构不变。"
}

$wslPrefix = @()
if ($Distro) { $wslPrefix += @('-d', $Distro) }

# Verify WSL before doing anything else, so the error is not buried in a long
# command line later.
& wsl.exe @wslPrefix -e true
if ($LASTEXITCODE -ne 0) {
    throw "WSL 不可用（发行版：$(if ($Distro) { $Distro } else { '默认' })）。先运行 wsl --install 或用 -Distro 指定已安装的发行版。"
}

$inWslWin = Join-Path $PSScriptRoot 'Build-Linux-InWsl.sh'
if (-not (Test-Path $inWslWin)) {
    throw "缺少 WSL 构建脚本：$inWslWin"
}

$inWslLinuxOut = & wsl.exe @wslPrefix -e wslpath -u $inWslWin
$wslPathExit = $LASTEXITCODE
if ($wslPathExit -ne 0 -or -not $inWslLinuxOut) {
    throw "无法把 $inWslWin 转换为 WSL 路径。"
}
$inWslLinux = (@($inWslLinuxOut)[0]).Trim()

$winOut = Join-Path (Join-Path $repoRoot 'bin') $Configuration
if ($OutputDir) {
    if (-not [IO.Path]::IsPathRooted($OutputDir)) {
        $OutputDir = Join-Path $repoRoot $OutputDir
    }
    $winOut = [IO.Path]::GetFullPath($OutputDir)
}

$wslBuildArgs = @(
    "--src=$sourceParent"
    "--rid=$Rid"
    "--config=$Configuration"
    "--target=$Target"
    "--install-deps=$([int]$InstallDeps.IsPresent)"
    "--clean=$([int]$Clean.IsPresent)"
    "--copy-back=$([int](-not $NoCopyBack.IsPresent))"
    "--out=$winOut"
)
if ($BuildRoot) { $wslBuildArgs += "--build-root=$BuildRoot" }

Write-Host "==> Windows 侧：$sourceParent"
Write-Host "==> WSL 内目标：$Target $Rid ($Configuration)"
if ($InstallDeps) { Write-Host '==> 缺少的依赖会自动通过 apt-get 安装' }

& wsl.exe @wslPrefix -e bash $inWslLinux @wslBuildArgs
if ($LASTEXITCODE -ne 0) {
    throw "Linux 构建失败（退出码 $LASTEXITCODE）。"
}

if (-not $NoCopyBack) {
    Write-Host "==> 产物已复制到 $winOut"
}
Write-Host '==> 完成'
