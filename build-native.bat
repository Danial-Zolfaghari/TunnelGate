@echo off
setlocal EnableExtensions
cd /d "%~dp0"
where dotnet >nul 2>&1 || (echo ERROR: .NET 8 SDK is required.& exit /b 1)
if exist output rmdir /s /q output
mkdir output
dotnet publish TunnelGate.Native.csproj -c Release -r win-x64 --self-contained true -o "%~dp0output" -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 exit /b 1
if not exist "%~dp0output\TunnelGate.exe" (echo ERROR: TunnelGate.exe was not produced.& exit /b 1)
for %%F in ("%~dp0output\*") do if /I not "%%~nxF"=="TunnelGate.exe" del /f /q "%%~fF" >nul 2>&1
for /d %%D in ("%~dp0output\*") do rmdir /s /q "%%~fD" >nul 2>&1
echo Done: %~dp0output\TunnelGate.exe
exit /b 0
