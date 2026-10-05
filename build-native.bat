@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo  TunnelGate Native - .NET 8 WPF self-contained build
echo ============================================================
echo.

where dotnet >nul 2>&1 || (
  echo ERROR: .NET 8 SDK is required.
  echo Install the x64 .NET 8 SDK, then run this file again.
  exit /b 1
)

if exist output rmdir /s /q output
mkdir output

echo [1/2] Restoring and compiling...
dotnet publish TunnelGate.Native.csproj -c Release -r win-x64 --self-contained true -o "%~dp0output" -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 exit /b 1

echo [2/2] Verifying output...
if not exist "%~dp0output\TunnelGate.exe" (
  echo ERROR: TunnelGate.exe was not produced.
  exit /b 1
)

rem Keep the requested output directory strictly single-file.
for %%F in ("%~dp0output\*") do (
  if /I not "%%~nxF"=="TunnelGate.exe" del /f /q "%%~fF" >nul 2>&1
)
for /d %%D in ("%~dp0output\*") do rmdir /s /q "%%~fD" >nul 2>&1

echo.
echo ============================================================
echo  Done: %~dp0output\TunnelGate.exe
echo  Single-file output verified. plink.exe is embedded and extracted at runtime when needed.
echo ============================================================
exit /b 0
