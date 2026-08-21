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
rem Windows can hold the file lock for a moment after the process dies, and
rem publishing into a still-locked exe fails. Wait until it is really gone,
rem and say so by name if it is not - otherwise the failure shows up as a wall
rem of MSBuild errors that do not mention the running exe at all.
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

echo [3/3] Packaging...
rem Every step here is checked. Without -Force, Compress-Archive refuses to
rem overwrite an existing zip, and the old zip then passes an "if exist" check -
rem so a failed run used to print Done and hand over yesterday's build.
if exist "dist" rmdir /s /q "dist"
if exist "dist" (
    echo ERROR: could not clear dist. Close anything using that folder.
    pause
    exit /b 1
)
mkdir "dist\TaskbarMeter"
if not exist "dist\TaskbarMeter" (
    echo ERROR: could not create dist\TaskbarMeter.
    pause
    exit /b 1
)
copy /Y "bin\Release\net8.0-windows\win-x64\publish\TaskbarMeter.exe" "dist\TaskbarMeter\" >nul
if not exist "dist\TaskbarMeter\TaskbarMeter.exe" (
    echo ERROR: published exe not found. Did the publish path change?
    pause
    exit /b 1
)
rem Wildcard copy so no Japanese file name has to appear in this script
copy /Y "dist-template\*" "dist\TaskbarMeter\" >nul
if errorlevel 1 (
    echo ERROR: could not copy dist-template.
    pause
    exit /b 1
)
rem Re-save the bundled text as UTF-8 with BOM and CRLF. It is the first
rem Japanese file the recipient opens, and Notepad on older setups mangles
rem BOM-less LF text into one long line.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem 'dist\TaskbarMeter\*.txt' | ForEach-Object { $t = [IO.File]::ReadAllText($_.FullName) -replace \"`r`n\", \"`n\" -replace \"`n\", \"`r`n\"; [IO.File]::WriteAllText($_.FullName, $t, (New-Object Text.UTF8Encoding $true)) }"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'dist\TaskbarMeter\*' -DestinationPath 'dist\TaskbarMeter.zip' -CompressionLevel Optimal -Force"
if errorlevel 1 (
    echo.
    echo PACKAGING FAILED.
    pause
    exit /b 1
)
if not exist "dist\TaskbarMeter.zip" (
    echo.
    echo PACKAGING FAILED.
    pause
    exit /b 1
)

rem Stamp today's date into the release notes. The template holds the Japanese
rem text so that no Japanese literal has to appear in this script.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d = Get-Date -Format 'yyyy-MM-dd'; $t = [IO.File]::ReadAllText('release-notes.template.md'); [IO.File]::WriteAllText('dist\release-notes.md', $t.Replace('{DATE}', $d), (New-Object Text.UTF8Encoding $false))"

echo.
echo Done.
rem Around 63 MB is the expected size. A tiny zip means the exe is missing.
for %%A in ("dist\TaskbarMeter.zip") do echo   dist\TaskbarMeter.zip  (%%~zA bytes)
echo   dist\release-notes.md
echo.
echo The asset name has no version in it on purpose, so this link always
echo points at the newest build:
echo   https://github.com/ShionAkaiwa/TaskbarMeter/releases/latest/download/TaskbarMeter.zip
echo.
echo Publish it with (bump the version):
echo   gh release create v1.2 dist\TaskbarMeter.zip --title "TaskbarMeter v1.2" --notes-file dist\release-notes.md
echo.
pause
