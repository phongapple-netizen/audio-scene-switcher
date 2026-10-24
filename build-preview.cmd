@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
set DOTNET_NOLOGO=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
echo === build ===
"%DOTNET%" build AudioPanel.csproj -c Debug -o artifacts\redesign-build -nologo -v q > artifacts\redesign-build.log 2>&1
if errorlevel 1 (type artifacts\redesign-build.log & echo BUILD FAILED & pause & exit /b 1)
echo === render ===
if exist "%USERPROFILE%\.dotnet\shared\Microsoft.WindowsDesktop.App" set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
set AUDIO_PANEL_SCREENSHOT=%~dp0artifacts\ui-redesign.png
start "" /wait artifacts\redesign-build\AudioPanel.exe --render-smoke
echo exit code %errorlevel%
echo DONE & pause
