@echo off
title VRCNext - Release Build
cd /d "%~dp0"

echo.
echo  =========================================
echo   VRCNext - Release Build + Installer
echo  =========================================
echo.

:: ── Load secrets ──────────────────────────────────────────────────────────────
if exist "%~dp0secrets.bat" (
    call "%~dp0secrets.bat"
) else (
    echo  [!] secrets.bat not found - GitHub upload will be skipped.
    echo.
)

:: ── Version ───────────────────────────────────────────────────────────────────
set /p "VERSION=  Enter version (e.g. 2026.1.0): "
if "%VERSION%"=="" (
    echo  [ERROR] No version entered.
    pause & exit /b 1
)
echo.

:: ── Draft or Public ───────────────────────────────────────────────────────────
set "PUBLISH_FLAG="
set /p "PUB_CHOICE=  Publish as public release? (y = public, N = draft): "
if /i "%PUB_CHOICE%"=="y" (
    set "PUBLISH_FLAG=--publish"
    echo  Release type: PUBLIC
) else (
    echo  Release type: DRAFT
)
echo.

:: ── Changelog ─────────────────────────────────────────────────────────────────
if exist "%~dp0RELEASE_NOTES.md" (
    echo  Changelog: RELEASE_NOTES.md found.
) else (
    echo  [!] RELEASE_NOTES.md not found - release will have no description.
)
echo.

:: ── ISCC ──────────────────────────────────────────────────────────────────────
set "ISCC="
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"  set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if exist "C:\Program Files\Inno Setup 6\ISCC.exe"         set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"  set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if "%ISCC%"=="" (
    echo  [ERROR] Inno Setup 6 not found. Download: https://jrsoftware.org/isdl.php
    pause & exit /b 1
)

:: ── vpk ───────────────────────────────────────────────────────────────────────
set "VPK=%USERPROFILE%\.dotnet\tools\vpk.exe"
if not exist "%VPK%" (
    echo  [!] vpk not found, installing...
    dotnet tool install -g vpk
    if not exist "%VPK%" (
        echo  [ERROR] vpk install failed. Run: dotnet tool install -g vpk
        pause & exit /b 1
    )
)

echo  ISCC: %ISCC%
echo  vpk:  %VPK%
echo.

set "PUBLISH_DIR=bin\Release\net9.0\win-x64\publish"

:: ── 0. Patch version in index.html ────────────────────────────────────────────
powershell -NoProfile -Command ^
    "$f = '%~dp0wwwroot\index.html';" ^
    "$c = [System.IO.File]::ReadAllText($f);" ^
    "$c = $c -replace 'v\d[\d.]+[a-z0-9]*', 'v%VERSION%';" ^
    "[System.IO.File]::WriteAllText($f, $c, [System.Text.Encoding]::UTF8);" ^
    "Write-Host ' [OK] index.html version -> v%VERSION%'"
echo.

:: ── 1. dotnet publish ─────────────────────────────────────────────────────────
echo  [1/4] Building...
echo.
dotnet publish -c Release -r win-x64 --self-contained false
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  [ERROR] dotnet publish failed.
    pause & exit /b 1
)
echo.
echo  [OK] Build done.
echo.

:: ── 2. vpk pack ───────────────────────────────────────────────────────────────
echo  [2/4] Creating Velopack package...
echo.
if not exist "releases" mkdir releases
"%VPK%" pack --packId VRCNext --packVersion %VERSION% --packDir "%PUBLISH_DIR%" --mainExe VRCNext.exe --outputDir releases
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  [ERROR] vpk pack failed.
    pause & exit /b 1
)
echo.
echo  [OK] Velopack package ready.
echo.

:: ── 3. InnoSetup ──────────────────────────────────────────────────────────────
echo  [3/4] Compiling installer...
echo.
if not exist "installer" mkdir installer
"%ISCC%" /DMyAppVersion="%VERSION%" installer.iss
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  [ERROR] InnoSetup failed.
    pause & exit /b 1
)
echo.
echo  [OK] Installer ready.
echo.

:: ── 4. GitHub upload ──────────────────────────────────────────────────────────
echo  [4/4] Uploading to GitHub...
echo.
if "%GITHUB_TOKEN%"=="" (
    echo  [!] GITHUB_TOKEN not set - skipping upload.
    echo      Run manually: "%VPK%" upload github --repoUrl https://github.com/shinyflvre/VRCNext --token YOUR_TOKEN --tag v%VERSION% --outputDir releases
    echo.
    goto :done
)
"%VPK%" upload github --repoUrl https://github.com/shinyflvre/VRCNext --token %GITHUB_TOKEN% --tag v%VERSION% --outputDir releases %PUBLISH_FLAG%
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  [ERROR] GitHub upload failed.
    pause & exit /b 1
)

:: Set release notes + upload InnoSetup installer via GitHub API
powershell -NoProfile -Command ^
    "$headers = @{ Authorization = 'Bearer %GITHUB_TOKEN%'; 'Content-Type' = 'application/json' };" ^
    "$rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/shinyflvre/VRCNext/releases/tags/v%VERSION%' -Headers $headers;" ^
    "if (Test-Path '%~dp0RELEASE_NOTES.md') {" ^
    "  $notes = [System.IO.File]::ReadAllText('%~dp0RELEASE_NOTES.md');" ^
    "  $body = @{ body = $notes } | ConvertTo-Json;" ^
    "  Invoke-RestMethod -Method Patch -Uri $rel.url -Headers $headers -Body $body | Out-Null;" ^
    "  Write-Host ' [OK] Release notes set.';" ^
    "};" ^
    "$installer = '%~dp0installer\VRCNext_Setup_%VERSION%_x64.exe';" ^
    "if (Test-Path $installer) {" ^
    "  $uploadUrl = $rel.upload_url -replace '\{.*\}', '';" ^
    "  $upHeaders = @{ Authorization = 'Bearer %GITHUB_TOKEN%'; 'Content-Type' = 'application/octet-stream' };" ^
    "  Invoke-RestMethod -Method Post -Uri \"${uploadUrl}?name=VRCNext_Setup_%VERSION%_x64.exe\" -Headers $upHeaders -InFile $installer | Out-Null;" ^
    "  Write-Host ' [OK] Installer uploaded.';" ^
    "}"

echo.
echo  [OK] Uploaded to GitHub Releases.
echo.

:done
echo  =========================================
echo   [OK] Release v%VERSION% done!
echo  =========================================
echo.
echo  Installer:  %~dp0installer\
echo  Velopack:   %~dp0releases\
echo.
explorer "%~dp0installer"
pause
