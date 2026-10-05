# Security Policy

## Reporting a vulnerability

Please do not publish credentials, private keys, exported TunnelGate vaults, or sensitive server details in a public issue.

When reporting a security issue, provide the minimum information required to reproduce the problem and redact secrets from logs and screenshots.

## Sensitive local files

The following data should never be committed to the repository:

- SSH private keys
- Tunnel passwords
- Exported vault backups
- `%LOCALAPPDATA%\TunnelGate` runtime data
- Runtime logs containing environment-specific host information
