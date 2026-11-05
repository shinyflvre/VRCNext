$ErrorActionPreference = "Stop"

$APP_NAME = "VRCNext"
$REPOSITORY = "hollyntt/VRCNext"

$SCRIPT_DIR = Split-Path -Parent $MyInvocation.MyCommand.Definition

if ($PSVersionTable.OS -notlike "*Windows*") {
    Write-Error "This installer supports Windows only."
    exit 1
}

# Prefer a zip bundled beside this script; otherwise fetch the latest Windows release asset.
$ZIP_PATH = ""
$CANDIDATES = Get-ChildItem -Path "$SCRIPT_DIR" -Filter "VRCNext*.zip"
if ($CANDIDATES.Count -gt 0) {
    $ZIP_PATH = $CANDIDATES[0].FullName
}

if ([string]::IsNullOrEmpty($ZIP_PATH)) {
    if (-not (Get-Command curl -ErrorAction SilentlyContinue)) {
        Write-Error "curl is required to download the latest release."
        exit 1
    }
    $RELEASE_JSON = curl -fsSL "https://api.github.com/repos/$REPOSITORY/releases/latest"
    $ASSET_URL = ($RELEASE_JSON | ConvertFrom-Json).assets | Where-Object { $_.name -like "*win-x64.zip" } | Select-Object -First 1 -ExpandProperty browser_download_url
    if ([string]::IsNullOrEmpty($ASSET_URL)) {
        Write-Error "No Windows zip asset found in the latest $REPOSITORY release."
        exit 1
    }
    $ZIP_PATH = Join-Path -Path $env:TEMP -ChildPath "vrcnext-$(Get-Random).zip"
    trap { Remove-Item -Path $ZIP_PATH -ErrorAction SilentlyContinue } EXIT
    Write-Host "Downloading latest VRCNext Windows release..."
    curl -fL $ASSET_URL -o $ZIP_PATH
}

# Install dependencies
Write-Host "[1/3] Installing dependencies..."
if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
    Install-PackageProvider -Name NuGet -Force -ErrorAction Stop
}

# Install AppImage with Shelly
Write-Host "[2/3] Installing $APP_NAME via Shelly..."
if (Get-Command shelly -ErrorAction SilentlyContinue) {
    shelly -Ii $ZIP_PATH
} else {
    Write-Error "Shelly not found. Please install Shelly first."
    Write-Host "Install Shelly using the instructions at: https://github.com/barknq11/shelly-apt"
    exit 1
}

Write-Host "[3/3] Done. Shelly installed $APP_NAME."
Write-Host "Launch it from your application menu."
Write-Host "To uninstall: shelly -Ri VRCNext"
