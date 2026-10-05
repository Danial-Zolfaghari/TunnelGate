$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 8 SDK is required.' }
Remove-Item "$PSScriptRoot\output" -Recurse -Force -ErrorAction SilentlyContinue
New-Item "$PSScriptRoot\output" -ItemType Directory | Out-Null
dotnet publish "$PSScriptRoot\TunnelGate.Native.csproj" -c Release -r win-x64 --self-contained true -o "$PSScriptRoot\output" -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
if (-not (Test-Path "$PSScriptRoot\output\TunnelGate.exe")) { throw 'TunnelGate.exe was not produced.' }
Get-ChildItem "$PSScriptRoot\output" -Force | Where-Object { $_.Name -ne 'TunnelGate.exe' } | Remove-Item -Recurse -Force
Write-Host "Built: $PSScriptRoot\output\TunnelGate.exe" -ForegroundColor Green
