# Optional bundled Plink

TunnelGate can optionally embed `plink.exe` from this directory at build time.

- Expected path: `tools/plink.exe`
- Recommended source: the official PuTTY download host (`the.earth.li` / PuTTY project)
- If the file is not present, the application still builds normally and downloads the official PuTTY Plink executable on first use.

Do not commit an unverified third-party copy of `plink.exe`.
