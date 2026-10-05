# TunnelGate

TunnelGate is a native Windows tunnel manager built with **.NET 8 / WPF**. It provides a desktop interface for managing SSH reverse tunnels and local SOCKS proxies, with encrypted local configuration, per-tunnel lifecycle management, reconnect logic, network tools, and a self-contained Windows installer.

## Features

- SSH **reverse tunnels** (`plink -R`)
- Local **SOCKS proxy** tunnels (`plink -D`)
- Multiple categories, profiles, and tunnel definitions
- Independent per-tunnel workers and reconnect behavior
- Optional scheduled tunnel restarts
- SSH keepalive and compression settings
- Optional SSH host-key fingerprint pinning
- Password or private-key authentication
- Local encrypted vault using **PBKDF2-SHA256 + AES-256-GCM**
- Windows DPAPI-backed session restore
- Encrypted configuration backup/import
- DNS changer and network adapter inspection tools
- Windows startup integration
- Runtime logging and connection status UI
- Self-contained single-file Windows build
- Self-contained installer project

## Requirements

### To run

- Windows 10/11 x64
- Network access to the target SSH server

The published application is self-contained and does not require a separate .NET runtime installation.

### To build

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- PowerShell 5.1+ or PowerShell 7+

## Plink / PuTTY dependency

TunnelGate uses **Plink**, the command-line SSH client from the PuTTY project.

The build supports two modes:

1. If `tools/plink.exe` exists, it is embedded into `TunnelGate.exe` and extracted on first use.
2. If it is not bundled, TunnelGate still builds normally and downloads the official PuTTY Plink executable from the PuTTY project's official download host on first use.

This prevents the repository from depending on an unverified third-party binary while keeping the application functional from a clean source checkout.

## Build

### Application only

```powershell
./build-native.ps1
```

Output:

```text
output/TunnelGate.exe
```

### Application + installer

```powershell
./build-installer.ps1
```

Outputs:

```text
output/TunnelGate.exe
installer-output/TunnelGate-Setup.exe
```

You can also run:

```bat
build-all.bat
```

## Project structure

```text
TunnelGate/
├─ Assets/                  # Application icon and image assets
├─ Core/                    # Runtime, tunneling, vault, networking, startup
├─ Installer/               # Self-contained WPF installer
├─ Models/                  # Application models
├─ UI/                      # Main WPF windows and dialogs
├─ tools/                   # Optional bundled plink.exe
├─ TunnelGate.Native.csproj
├─ build-native.ps1
└─ build-installer.ps1
```

## Security notes

- Vault data is stored locally under the current Windows user's Local AppData directory.
- Stored vault data is encrypted using PBKDF2-SHA256-derived keys and AES-256-GCM.
- Session restoration uses Windows DPAPI.
- Commands written to the runtime log mask password arguments.
- Do not commit real tunnel credentials, private keys, exported vaults, logs, or local runtime data.
- For a public/non-loopback reverse bind, the target SSH server may require an appropriate `GatewayPorts` setting.

## Data location

Runtime data is stored in:

```text
%LOCALAPPDATA%\TunnelGate
```

This includes the encrypted vault, lockout/session state, logs, and the runtime Plink executable when needed.

## CI

GitHub Actions builds the Windows application and installer on pushes and pull requests to `main`. This catches missing files and compilation failures in clean environments.

## Third-party software

TunnelGate can download and use Plink from the PuTTY project. PuTTY/Plink is a separate open-source project and is not authored by TunnelGate.

## Author

**Danial Zolfaghari**
