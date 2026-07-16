@echo off
echo ===================================================
echo Building C++ Encoder DLL...
echo ===================================================

:: Load Visual Studio developer environment
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"

:: Compile Encoder.cpp into Encoder.dll
cl /LD /MT /O2 /EHsc Encoder.cpp /link /OUT:Encoder.dll mfplat.lib mfuuid.lib mfreadwrite.lib user32.lib gdi32.lib ole32.lib oleaut32.lib

if %errorlevel% neq 0 (
    echo [ERROR] Failed to compile C++ Encoder.cpp!
    exit /b %errorlevel%
)

echo ===================================================
echo Building C# Screen Recorder Application...
echo ===================================================

:: Build C# application
dotnet build -c Release

if %errorlevel% neq 0 (
    echo [ERROR] Failed to build C# application!
    exit /b %errorlevel%
)

echo ===================================================
echo Build Successful!
echo ===================================================
echo Run "bin\Release\net10.0-windows\ScreenRecorder.exe" to start the app.
echo ===================================================
