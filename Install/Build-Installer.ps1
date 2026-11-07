param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [string]$OutputDir = "$PSScriptRoot\Output",
    [string]$InnoSetupPath = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Resolve-Path "$PSScriptRoot\.."
$Rid = "win-$Architecture"
$PublishDir = "$RepoRoot\bin\$Configuration\net10.0-windows\$Rid\publish"

# Both installers carry their architecture in the file name so they can sit
# in the same release without ambiguity.
$OutputSuffix = "_$Architecture"

# ---------- step 1: publish self-contained NativeAOT ----------
# NativeAOT cannot cross-compile: the Windows ARM64 build needs the MSVC
# ARM64/ARM64EC build tools installed, and every RID must be built on Windows.
Write-Host "=== Publishing self-contained app ($Rid) ==="
dotnet publish "$RepoRoot\GKME.csproj" `
    -c $Configuration `
    -r $Rid `
    -p:UsbipPayloadArch=$Architecture `
    --self-contained true `
    -o "$PublishDir"

if (-not $?) { throw "dotnet publish failed" }

# ---------- step 1.2: strip stray VCS metadata from publish\web ----------
# Older GKME.csproj versions copied the whole GKME-Web repository (including
# .git) into web\, and `PreserveNewest` never deletes leftovers. Remove them
# explicitly so a stale checkout can never end up in the installer again.
$WebPublishDir = Join-Path $PublishDir 'web'
if (Test-Path -LiteralPath $WebPublishDir) {
    Write-Host "=== Stripping repository metadata from publish\web ==="
    foreach ($Junk in @('.git', '.github', '.vscode', '.idea', '.gitignore', '.gitattributes')) {
        $JunkPath = Join-Path $WebPublishDir $Junk
        if (Test-Path -LiteralPath $JunkPath) {
            Remove-Item -LiteralPath $JunkPath -Recurse -Force
            Write-Host "Removed $Junk"
        }
    }
}

# ---------- step 1.5: locate Inno Setup 7 ----------
# Either edition builds the installer; the 64-bit one is preferred.
Write-Host "=== Locating Inno Setup 7 ==="
if (-not $InnoSetupPath) {
    $Candidates = @(
        "C:\Program Files\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
    )
    $InnoSetupPath = $Candidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}
if (-not $InnoSetupPath -or -not (Test-Path -LiteralPath $InnoSetupPath)) {
    throw "Inno Setup 7 not found. Install it from https://jrsoftware.org/isdl.php or pass -InnoSetupPath."
}
Write-Host "Using: $InnoSetupPath"

# ---------- step 2: compile Inno Setup installer ----------
# setup.iss reads the version from GKME.exe in the publish folder, so the publish
# path just has to be passed along -- no version placeholder or generated .iss.
Write-Host "=== Compiling Inno Setup installer ($Architecture) ==="
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

& $InnoSetupPath /Q /O"$OutputDir" "/DBuildDir=$PublishDir" "/DSetupArch=$Architecture" "/DOutputSuffix=$OutputSuffix" "$PSScriptRoot\setup.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit code $LASTEXITCODE)" }

Write-Host "=== Done ==="
$result = Get-ChildItem -Path $OutputDir -Filter "*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
Write-Host "Installer created: $($result.FullName)"
