@echo off
cd /d "%~dp0"

if not exist "TaskbarMeter.csproj" (
    echo ERROR: TaskbarMeter.csproj not found in this folder.
    echo Current folder: %~dp0
    echo Put this .cmd file next to TaskbarMeter.csproj.
    pause
    exit /b 1
)

echo [1/3] Stopping running instance...
taskkill /IM TaskbarMeter.exe /F >nul 2>&1
rem Windows can hold the file lock for a moment after the process dies, and
rem publishing into a still-locked exe fails. ping is used as a sleep because
rem timeout refuses to run when stdin is redirected.
ping -n 3 127.0.0.1 >nul

echo [2/3] Building...
call dotnet publish -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED. See the errors above.
    pause
    exit /b 1
)

echo [3/3] Starting...
start "" "%~dp0bin\Release\net8.0-windows\win-x64\publish\TaskbarMeter.exe"

echo.
echo Done. Check the tray icons at the bottom-right of the taskbar.
pause
