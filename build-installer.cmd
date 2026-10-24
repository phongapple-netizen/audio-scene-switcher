@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem 版本号取自 AudioPanel.csproj 的 <Version>
for /f "tokens=3 delims=<>" %%v in ('findstr /c:"<Version>" AudioPanel.csproj') do set "VERSION=%%v"
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (echo 找不到 Inno Setup 6，请先安装：winget install JRSoftware.InnoSetup & pause & exit /b 1)
set DOTNET_NOLOGO=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
echo === publish %VERSION% ===
dotnet publish AudioPanel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "artifacts\publish-%VERSION%" -nologo -v q
if errorlevel 1 (echo PUBLISH FAILED & pause & exit /b 1)
echo === installer ===
"%ISCC%" /Q /DAppVersion=%VERSION% installer\AudioPanel.iss
if errorlevel 1 (echo INSTALLER FAILED & pause & exit /b 1)
echo DONE: artifacts\installer\AudioPanel-Setup-%VERSION%.exe
pause
