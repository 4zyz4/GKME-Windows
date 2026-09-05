param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputDir = "$PSScriptRoot\Output",
    [string]$InnoSetupPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Resolve-Path "$PSScriptRoot\.."
$PublishDir = "$RepoRoot\bin\$Configuration\net10.0-windows\win-x64\publish"

# ---------- step 1: publish self-contained ----------
Write-Host "=== Publishing self-contained app ==="
dotnet publish "$RepoRoot\GKME.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o "$PublishDir"

if (-not $?) { throw "dotnet publish failed" }

# ---------- step 1.5: read version from project ----------
Write-Host "=== Reading version from GKME.csproj ==="
$csprojContent = Get-Content "$RepoRoot\GKME.csproj" -Raw
$versionMatch = [regex]::Match($csprojContent, '<VersionString>([^<]+)</VersionString>')
if (-not $versionMatch.Success) {
    throw "VersionString not found in GKME.csproj"
}
$version = $versionMatch.Groups[1].Value
Write-Host "Version: $version"

# ---------- step 1.6: replace version placeholders in setup.iss ----------
Write-Host "=== Generating setup.iss ==="
$issContent = [System.IO.File]::ReadAllText("$PSScriptRoot\setup.iss", [System.Text.Encoding]::UTF8)
$issContent = $issContent -replace 'VERSION_PLACEHOLDER', $version
[System.IO.File]::WriteAllText("$PSScriptRoot\setup.iss", $issContent, [System.Text.UTF8Encoding]::new($false))

# ---------- step 2: compile InnoSetup installer ----------
Write-Host "=== Compiling InnoSetup installer ==="
if (-not (Test-Path $InnoSetupPath)) {
    throw "InnoSetup not found at $InnoSetupPath. Please install InnoSetup 6 from https://jrsoftware.org/isdl.php"
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

& $InnoSetupPath /Q /O"$OutputDir" "$PSScriptRoot\setup.iss"
if (-not $?) { throw "InnoSetup compilation failed" }

Write-Host "=== Done ==="
$result = Get-ChildItem -Path $OutputDir -Filter "*.exe" | Select-Object -First 1
Write-Host "Installer created: $($result.FullName)"
