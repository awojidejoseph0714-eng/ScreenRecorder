@echo off
setlocal enabledelayedexpansion
echo ===================================================
echo Screen Recorder v2.1 Local Release Publisher
echo ===================================================

:: Check if git CLI is installed
where git >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] Git CLI is not installed or not in PATH!
    exit /b 1
)

:: Check if GitHub CLI is installed
where gh >nul 2>nul
if %errorlevel% neq 0 (
    echo [WARNING] GitHub CLI (gh) is not installed. 
    echo We will push the Git tag, and GitHub Actions will publish the release.
    set HAS_GH=0
) else (
    set HAS_GH=1
)

:: Verify that the installer exists
if not exist installer\ScreenRecorder-v2.1.0-Setup.exe (
    echo [ERROR] Could not find installer\ScreenRecorder-v2.1.0-Setup.exe!
    echo Please run build_installer.bat first to build the installer before publishing.
    exit /b 1
)

:: Prompt for version tag
set /p TAG="Enter release tag name (e.g. v2.1.0): "
if "%TAG%"=="" (
    echo [ERROR] Tag name cannot be empty!
    exit /b 1
)

:: Validate tag format starts with v
echo %TAG% | findstr /R "^v[0-9]" >nul
if %errorlevel% neq 0 (
    echo [WARNING] Release tags usually start with 'v' followed by a number (e.g., v2.1.0).
    set /p CONFIRM="Are you sure you want to use tag '%TAG%'? (y/n): "
    if /I "!CONFIRM!" neq "y" exit /b 1
)

echo.
echo Tagging commit with %TAG%...
git tag -a %TAG% -m "Release %TAG%"
if %errorlevel% neq 0 (
    echo [ERROR] Failed to tag commit! Tag might already exist locally.
    exit /b %errorlevel%
)

echo.
echo Pushing tag to GitHub origin...
git push origin %TAG%
if %errorlevel% neq 0 (
    echo [ERROR] Failed to push tag to GitHub!
    exit /b %errorlevel%
)

if "%HAS_GH%"=="1" (
    echo.
    echo Creating release and uploading installer using gh CLI...
    gh release create %TAG% installer\ScreenRecorder-v2.1.0-Setup.exe --title "Release %TAG%" --notes "Screen Recorder %TAG% Release"
    if %errorlevel% neq 0 (
        echo [WARNING] Local release creation via gh CLI failed. 
        echo The tag has been pushed, so GitHub Actions should automatically build and publish.
    ) else (
        echo ===================================================
        echo Local Release Created Successfully!
        echo ===================================================
    endlocal
    exit /b 0
)

echo ===================================================
echo Tag Pushed Successfully!
echo Cloud GitHub Actions pipeline will compile and release.
echo ===================================================
endlocal
