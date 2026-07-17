@echo off
echo ===================================================
echo Building Screen Recorder v2 Installer...
echo ===================================================

:: Clean directories
if exist publish rmdir /s /q publish
if exist installer rmdir /s /q installer

echo [1/4] Setting up Visual Studio Build Environment...
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
if %errorlevel% neq 0 (
    echo [ERROR] Failed to load Visual Studio dev environment!
    exit /b %errorlevel%
)

echo [2/4] Compiling native Encoder.dll with static CRT (/MT)...
cl /LD /MT /O2 /EHsc Encoder.cpp /link /OUT:Encoder.dll mfplat.lib mfuuid.lib mfreadwrite.lib user32.lib gdi32.lib ole32.lib oleaut32.lib
if %errorlevel% neq 0 (
    echo [ERROR] Failed to compile C++ Encoder.cpp!
    exit /b %errorlevel%
)

echo [3/4] Publishing self-contained WPF Application...
dotnet publish -c Release -r win-x64 --self-contained true -o publish
if %errorlevel% neq 0 (
    echo [ERROR] Failed to publish WPF Application!
    exit /b %errorlevel%
)

:: Copy native DLL to publish output folder
echo Copying native Encoder.dll to publish directory...
copy Encoder.dll publish\
if %errorlevel% neq 0 (
    echo [ERROR] Failed to copy Encoder.dll to publish folder!
    exit /b %errorlevel%
)

echo [4/4] Compiling Inno Setup Installer...
"C:\Users\user\AppData\Local\Programs\Inno Setup 6\ISCC.exe" setup.iss
if %errorlevel% neq 0 (
    echo [ERROR] Failed to compile setup installer!
    exit /b %errorlevel%
)

echo ===================================================
echo Installer Built Successfully!
echo ===================================================
echo Location: installer\ScreenRecorder-v2.1.0-Setup.exe
echo ===================================================
