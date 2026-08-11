@echo off
rem Build and package the distributable zip.
rem Keep this file ASCII only - Japanese characters have broken it before.
cd /d "%~dp0"

if not exist "TaskbarMeter.csproj" (
    echo ERROR: TaskbarMeter.csproj not found in this folder.
    pause
    exit /b 1
)

echo [1/3] Stopping running instance...
taskkill /IM TaskbarMeter.exe /F >nul 2>&1

echo [2/3] Building...
call dotnet publish -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED. See the errors above.
    pause
    exit /b 1
)

echo [3/3] Packaging...
if exist "dist" rmdir /s /q "dist"
mkdir "dist\TaskbarMeter"
copy /Y "bin\Release\net8.0-windows\win-x64\publish\TaskbarMeter.exe" "dist\TaskbarMeter\" >nul
rem Wildcard copy so no Japanese file name has to appear in this script
copy /Y "dist-template\*" "dist\TaskbarMeter\" >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'dist\TaskbarMeter\*' -DestinationPath 'dist\TaskbarMeter.zip' -CompressionLevel Optimal"
if not exist "dist\TaskbarMeter.zip" (
    echo.
    echo PACKAGING FAILED.
    pause
    exit /b 1
)

echo.
echo Done.  dist\TaskbarMeter.zip
echo.
echo The asset name has no version in it on purpose, so this link always
echo points at the newest build:
echo   https://github.com/ShionAkaiwa/TaskbarMeter/releases/latest/download/TaskbarMeter.zip
echo.
echo Publish it with:
echo   gh release create v1.2 dist\TaskbarMeter.zip --title "TaskbarMeter v1.2" --notes "what changed"
echo.
pause
