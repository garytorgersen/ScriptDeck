# ---------------------------------------------------------------------------
# Build-Installer.ps1
#
# Produces a redistributable ZIP for ScriptDeck containing:
#   - The Release/x64 build of ScriptDeck.exe + all dependencies
#   - Install.cmd (user-profile install, checks .NET 4.8 prereq)
#   - Uninstall.cmd (removes app, preserves user data)
#   - Documentation/ folder with the user guide + technical manual
#     in both Markdown and Word formats
#   - README.txt
#
# Output:
#   dist/staging/         intermediate folder (re-created each run)
#   dist/ScriptDeck-vN.N.N.zip
#
# Usage:
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tools/Build-Installer.ps1
#       [-Version 0.1.0] [-Configuration Release]
#
# The Version parameter defaults to whatever's in ScriptDeck.csproj's
# <Version> element, so cutting a new release is just "bump the csproj
# version, push, run this script."
# ---------------------------------------------------------------------------

[CmdletBinding()]
param(
    [string]$Version       = $null,
    [string]$Configuration = 'Release',
    [string]$Platform      = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolve repo root from the script's location -- works whether the
# user runs from repo root or from anywhere else.
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$csproj   = Join-Path $repoRoot 'src\ScriptDeck\ScriptDeck.csproj'
if (-not (Test-Path $csproj)) {
    throw "Couldn't find ScriptDeck.csproj at $csproj. Run this from a clean checkout."
}

# Auto-discover version from the csproj if not supplied. Robust against
# whitespace + the <Version>X.Y.Z</Version> element being on any line.
if (-not $Version) {
    $xml = [xml](Get-Content $csproj -Raw)
    $Version = $xml.Project.PropertyGroup.Version
    if (-not $Version) {
        throw "No <Version> in ScriptDeck.csproj and -Version not supplied."
    }
    Write-Host "Version (auto-detected): $Version"
} else {
    Write-Host "Version: $Version"
}

# ---- 1. Build Release/x64 ----
Write-Host ""
Write-Host "Building $Configuration/$Platform..."
& dotnet build $csproj -c $Configuration -p:Platform=$Platform --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

$binDir = Join-Path $repoRoot "src\ScriptDeck\bin\$Platform\$Configuration\net48"
if (-not (Test-Path $binDir)) {
    throw "Expected build output at $binDir but it doesn't exist."
}

# ---- 2. Stage ----
$distDir    = Join-Path $repoRoot 'dist'
$stagingDir = Join-Path $distDir  'staging'
$appDir     = Join-Path $stagingDir 'App'
$docsDir    = Join-Path $stagingDir 'Documentation'

Write-Host ""
Write-Host "Staging files into $stagingDir..."
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $appDir,$docsDir | Out-Null

# Copy app binaries + workspace samples; drop the .pdb (debug symbols
# aren't shipped to end users -- they add ~1MB and aren't useful
# without source).
Copy-Item -Path "$binDir\*" -Destination $appDir -Recurse -Force -Exclude '*.pdb'

# Copy docs.
foreach ($doc in 'USER_GUIDE.md','USER_GUIDE.docx','USER_MANUAL.md','USER_MANUAL.docx') {
    $src = Join-Path $repoRoot $doc
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $docsDir -Force
    } else {
        Write-Warning "Documentation file not found: $doc (skipping)"
    }
}

# Copy installer scripts + README from THIS script's own template
# location. They live under dist/staging in version control as
# templates that this script re-stages each build.
foreach ($f in 'Install.cmd','Uninstall.cmd','README.txt') {
    # The "canonical" copies of these files were created by hand in
    # a previous run and live in the prior dist/staging. We can't
    # source from there (the directory is recreated above), so we
    # ALSO check the repo's templates folder. For now we just bail
    # with a helpful message if they're missing -- the user is
    # expected to keep these template files alive somewhere.
    $template = Join-Path $repoRoot "tools\installer-templates\$f"
    if (-not (Test-Path $template)) {
        throw "Missing installer template: $template`nCreate this folder and copy in the Install.cmd / Uninstall.cmd / README.txt you want shipped."
    }
    Copy-Item -Path $template -Destination $stagingDir -Force
}

# ---- 3. Zip ----
$zipPath = Join-Path $distDir "ScriptDeck-v$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host ""
Write-Host "Creating $zipPath..."
Compress-Archive -Path "$stagingDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

# Report.
$zipInfo = Get-Item $zipPath
$hash    = (Get-FileHash $zipPath -Algorithm SHA256).Hash
Write-Host ""
Write-Host "----------------------------------------------------------"
Write-Host "  Done."
Write-Host "  Output:  $zipPath"
Write-Host "  Size:    $([Math]::Round($zipInfo.Length / 1MB, 2)) MB"
Write-Host "  SHA256:  $hash"
Write-Host "----------------------------------------------------------"
