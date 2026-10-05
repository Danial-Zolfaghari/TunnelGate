@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo  TunnelGate - Application + Setup Builder
echo ============================================================
echo.

where dotnet >nul 2>&1 || (
  echo ERROR: .NET 8 SDK is required.
  exit /b 1
)

echo [1/4] Building TunnelGate.exe...
call "%~dp0build-native.bat"
if errorlevel 1 exit /b 1

echo [2/4] Preparing installer payload...
if exist "%~dp0Installer\Payload" rmdir /s /q "%~dp0Installer\Payload"
mkdir "%~dp0Installer\Payload"
copy /y "%~dp0output\TunnelGate.exe" "%~dp0Installer\Payload\TunnelGate.exe" >nul
if errorlevel 1 (
  echo ERROR: Could not prepare installer payload.
  exit /b 1
)

if exist "%~dp0installer-build" rmdir /s /q "%~dp0installer-build"
if exist "%~dp0installer-output" rmdir /s /q "%~dp0installer-output"
mkdir "%~dp0installer-build"
mkdir "%~dp0installer-output"

echo [3/4] Building self-contained setup executable...
dotnet publish "%~dp0Installer\TunnelGate.Setup.csproj" -c Release -r win-x64 --self-contained true -o "%~dp0installer-build" -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 exit /b 1

if not exist "%~dp0installer-build\TunnelGate.Setup.exe" (
  echo ERROR: TunnelGate.Setup.exe was not produced.
  exit /b 1
)

echo [4/4] Finalizing installer...
copy /y "%~dp0installer-build\TunnelGate.Setup.exe" "%~dp0installer-output\TunnelGate-Setup.exe" >nul
if errorlevel 1 exit /b 1

for %%F in ("%~dp0installer-output\*") do (
  if /I not "%%~nxF"=="TunnelGate-Setup.exe" del /f /q "%%~fF" >nul 2>&1
)

rem The payload copy is only a build-time intermediate; keep the source tree clean.
del /f /q "%~dp0Installer\Payload\TunnelGate.exe" >nul 2>&1

echo.
echo ============================================================
echo  Done:
echo    App:   %~dp0output\TunnelGate.exe
echo    Setup: %~dp0installer-output\TunnelGate-Setup.exe
echo ============================================================
exit /b 0
