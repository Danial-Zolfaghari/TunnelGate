@echo off
setlocal EnableExtensions
cd /d "%~dp0"
where dotnet >nul 2>&1 || (echo ERROR: .NET 8 SDK is required.& exit /b 1)
call "%~dp0build-native.bat"
if errorlevel 1 exit /b 1
if exist "%~dp0Installer\Payload" rmdir /s /q "%~dp0Installer\Payload"
mkdir "%~dp0Installer\Payload"
copy /y "%~dp0output\TunnelGate.exe" "%~dp0Installer\Payload\TunnelGate.exe" >nul
if exist "%~dp0installer-build" rmdir /s /q "%~dp0installer-build"
if exist "%~dp0installer-output" rmdir /s /q "%~dp0installer-output"
mkdir "%~dp0installer-build"
mkdir "%~dp0installer-output"
dotnet publish "%~dp0Installer\TunnelGate.Setup.csproj" -c Release -r win-x64 --self-contained true -o "%~dp0installer-build" -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 exit /b 1
if not exist "%~dp0installer-build\TunnelGate.Setup.exe" (echo ERROR: setup executable was not produced.& exit /b 1)
copy /y "%~dp0installer-build\TunnelGate.Setup.exe" "%~dp0installer-output\TunnelGate-Setup.exe" >nul
del /f /q "%~dp0Installer\Payload\TunnelGate.exe" >nul 2>&1
exit /b 0
