$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 8 SDK is required.' }
& "$PSScriptRoot\build-native.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
$payload = Join-Path $PSScriptRoot 'Installer\Payload'
$build = Join-Path $PSScriptRoot 'installer-build'
$out = Join-Path $PSScriptRoot 'installer-output'
Remove-Item $payload -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item $payload -ItemType Directory | Out-Null
New-Item $build -ItemType Directory | Out-Null
New-Item $out -ItemType Directory | Out-Null
Copy-Item "$PSScriptRoot\output\TunnelGate.exe" "$payload\TunnelGate.exe"
dotnet publish "$PSScriptRoot\Installer\TunnelGate.Setup.csproj" -c Release -r win-x64 --self-contained true -o $build -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
$setup = Join-Path $build 'TunnelGate.Setup.exe'
if (-not (Test-Path $setup)) { throw 'TunnelGate.Setup.exe was not produced.' }
Copy-Item $setup "$out\TunnelGate-Setup.exe" -Force
Remove-Item "$payload\TunnelGate.exe" -Force -ErrorAction SilentlyContinue
Write-Host "App:   $PSScriptRoot\output\TunnelGate.exe" -ForegroundColor Green
Write-Host "Setup: $out\TunnelGate-Setup.exe" -ForegroundColor Green
