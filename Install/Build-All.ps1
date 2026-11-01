# Builds every supported platform and collects the release artifacts into
# Install\Output\<rid>\ (one subfolder per platform).
#
#   Install\Build-All.ps1
#   Install\Build-All.ps1 -Platforms win-x64,linux-x64
#   Install\Build-All.ps1 -OutputDir D:\GKME-release -InstallDeps -Clean
#
# Windows installers are produced by Build-Installer.ps1 (Inno Setup 7), the
# Linux AppImages by Build-Linux.ps1 (WSL, because NativeAOT cannot
# cross-compile from Windows). A failing platform does not stop the others; the
# summary at the end lists what succeeded and what did not.

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # all, or any of win-x64, win-arm64, linux-x64, linux-arm64.
    [string[]]$Platforms = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64'),

    # Root of the artifact tree; one subfolder per platform is created inside.
    # Relative paths are resolved against the repository root. Default:
    # Install\Output (the same folder Build-Installer.ps1 uses by default).
    [string]$OutputDir,

    # Keep existing files in Install\Output\<rid>\ instead of rebuilding that
    # folder.
    [switch]$KeepOutput,

    # Forwarded to the Linux build: install missing prerequisites with apt-get
    # (.NET 10 SDK, clang, zlib1g-dev, squashfs-tools).
    [switch]$InstallDeps,

    # Forwarded to the Linux build: delete the WSL build tree for a full
    # NativeAOT rebuild.
    [switch]$Clean,

    # WSL distribution for the Linux builds; empty means the default one.
    [string]$Distro,

    # Inno Setup 7 compiler for the Windows installers.
    [string]$InnoSetupPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$allPlatforms = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')

$selected = New-Object System.Collections.Generic.List[string]
foreach ($entry in $Platforms) {
    foreach ($name in ($entry -split ',')) {
        $name = $name.Trim()
        if (-not $name) { continue }
        if ($name -eq 'all') {
            foreach ($platform in $allPlatforms) {
                if (-not $selected.Contains($platform)) { $selected.Add($platform) }
            }
            continue
        }
        if ($allPlatforms -notcontains $name) {
            throw "未知平台：$name（可用：all、$($allPlatforms -join '、')）"
        }
        if (-not $selected.Contains($name)) { $selected.Add($name) }
    }
}
if ($selected.Count -eq 0) {
    throw '没有选择任何平台。'
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $PSScriptRoot 'Output'
}
elseif (-not [IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $repoRoot $OutputDir
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

Write-Host "==> 构建平台：$($selected -join '、')"
Write-Host "==> 输出目录：$OutputDir"
Write-Host "==> 配置：$Configuration"

$results = New-Object System.Collections.Generic.List[object]

foreach ($platform in $selected) {
    $platformDir = Join-Path $OutputDir $platform
    if (-not $KeepOutput -and (Test-Path -LiteralPath $platformDir)) {
        Remove-Item -LiteralPath $platformDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $platformDir -Force | Out-Null

    Write-Host ''
    Write-Host "===== [$platform] =====" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()

    try {
        if ($platform -like 'win-*') {
            $architecture = $platform.Substring('win-'.Length)
            & "$PSScriptRoot\Build-Installer.ps1" `
                -Configuration $Configuration `
                -Architecture $architecture `
                -OutputDir $platformDir `
                -InnoSetupPath $InnoSetupPath
        }
        else {
            & "$PSScriptRoot\Build-Linux.ps1" `
                -Target AppImage `
                -Rid $platform `
                -Configuration $Configuration `
                -OutputDir $platformDir `
                -InstallDeps:$InstallDeps `
                -Clean:$Clean `
                -Distro $Distro
        }

        # Installers are .exe, AppImages are .AppImage; pick whatever the build
        # just wrote, newest first, so a stale artifact cannot be reported.
        $artifact = Get-ChildItem -LiteralPath $platformDir -File -Recurse |
            Where-Object { $_.Extension -in '.exe', '.AppImage' } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $artifact) {
            throw "构建脚本结束但 $platformDir 中没有产物（*.exe / *.AppImage）。"
        }

        $results.Add([pscustomobject]@{
            Platform = $platform
            Status   = '成功'
            Size     = '{0:N1} MB' -f ($artifact.Length / 1MB)
            Artifact = $artifact.FullName
            Seconds  = [int]$watch.Elapsed.TotalSeconds
        })
    }
    catch {
        $results.Add([pscustomobject]@{
            Platform = $platform
            Status   = '失败'
            Size     = ''
            Artifact = $_.Exception.Message
            Seconds  = [int]$watch.Elapsed.TotalSeconds
        })
        Write-Warning "$platform 构建失败：$($_.Exception.Message)"
    }
}

Write-Host ''
Write-Host '===== 构建结果 =====' -ForegroundColor Cyan
$results | Format-Table Platform, Status, Size, Seconds -AutoSize | Out-String -Width 120 | Write-Host

$failed = @($results | Where-Object { $_.Status -eq '失败' })
if ($failed.Count -gt 0) {
    Write-Host '失败详情：' -ForegroundColor Yellow
    foreach ($item in $failed) {
        Write-Host "  $($item.Platform): $($item.Artifact)" -ForegroundColor Yellow
    }
    throw "$($failed.Count) 个平台构建失败。"
}

Write-Host "全部成功，产物位于 $OutputDir" -ForegroundColor Green
