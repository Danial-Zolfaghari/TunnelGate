# TunnelGate Requirements

## Platform support

| Platform | Support |
|---|---|
| Windows 10/11 x64 | Full |
| Linux | Not supported by the current WPF/Windows integration |
| macOS | Not supported by the current WPF/Windows integration |

## Runtime

- Windows 10/11 x64
- Network access to the configured SSH server
- PuTTY/Plink is embedded when supplied from `tools/plink.exe`; otherwise TunnelGate downloads the official Plink binary on first use

The published executable is self-contained and does not require a separately installed .NET runtime.

## Build

- .NET 8 SDK
- PowerShell 5.1+ or PowerShell 7+
- Internet access for NuGet restore
