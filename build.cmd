@echo off
rem Keep this file ASCII only - Japanese characters have broken it before.
cd /d "%~dp0"

if not exist "TaskbarMeter.csproj" (
    echo ERROR: TaskbarMeter.csproj not found in this folder.
    echo Current folder: %~dp0
    echo Put this .cmd file next to TaskbarMeter.csproj.
    pause
    exit /b 1
)

echo [1/3] Stopping running instance...
rem Windows can hold the file lock for a moment after the process dies, and
rem publishing into a still-locked exe fails. Wait until it is really gone,
rem and say so by name if it is not - otherwise the failure shows up as a wall
rem of MSBuild errors that never mention the running exe.
rem ping is used as a sleep because timeout refuses to run when stdin is redirected.
for /l %%i in (1,1,10) do (
    tasklist /FI "IMAGENAME eq TaskbarMeter.exe" | find /I "TaskbarMeter.exe" >nul || goto :stopped
    taskkill /IM TaskbarMeter.exe /F >nul 2>&1
    ping -n 2 127.0.0.1 >nul
)
echo ERROR: TaskbarMeter.exe is still running. Close it and run this again.
pause
exit /b 1
:stopped

echo [2/3] Building...
call dotnet publish -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED. See the errors above.
    pause
    exit /b 1
)

echo [3/3] Starting...
if not exist "%~dp0bin\Release\net8.0-windows\win-x64\publish\TaskbarMeter.exe" (
    echo ERROR: published exe not found. Did the publish path change?
    pause
    exit /b 1
)
start "" "%~dp0bin\Release\net8.0-windows\win-x64\publish\TaskbarMeter.exe"

echo.
echo Done. Check the tray icons at the bottom-right of the taskbar.
pause
