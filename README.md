# ALAZ OPSDECK

ALAZ OPSDECK is a local operations console built around a 7-inch ESP32-S3 touch panel and a Windows host application. It combines PC telemetry, Cloudflare operational visibility, agent/session status and device diagnostics in a dedicated always-on dashboard.

## What is in this repository
- `firmware/panel/` — ESP-IDF + LVGL firmware for the CrowPanel Advance 7-inch display.
- `host/` — .NET Windows host, telemetry collectors, local UI and protocol implementation.
- `docs/` — public protocol and setup documentation.
- `tools/` — selected safe maintenance and validation tools.
- `OPSDECK_BASLAT.cmd` — stable Windows launcher.

## Current capabilities
- CPU, GPU, RAM/VRAM/shared memory, fan, disk and network telemetry.
- Cloudflare Workers, D1, R2, HTTPS health and cost snapshots.
- Multiple Cloudflare account views with freshness/coverage state.
- Codex/agent status and session-oriented panel UI.
- USB primary transport with same-LAN read-only Wi-Fi telemetry.
- Pinned TLS + application authentication for Wi-Fi telemetry.
- On-device Settings, Wi-Fi setup, diagnostics, history and alert surfaces.

## Hardware
The firmware currently targets the Elecrow CrowPanel Advance 7-inch ESP32-S3 HMI at 800×480 using LVGL 9.x.

## Security model
Secrets are deliberately excluded from Git. API tokens, private keys, `.env` files, local credential blobs, runtime logs and backups must remain outside the repository. See `SECURITY.md` and `.gitignore`.

## Development
### Windows host
The host targets modern .NET/WinForms. Build from `host/` with the .NET SDK selected by `host/global.json`.

### Panel firmware
The panel uses ESP-IDF 5.5.x. Configure the ESP-IDF environment, then build from `firmware/panel/` with `idf.py build`.

For physical flashing, use only `tools/safe_flash_panel.py`. Normal updates are app-only at `0x10000`; the tool verifies the serial device, SHA256, safe address range and performs a separate flash verification.

## Cloudflare credentials
`docs/CLOUDFLARE_TOKEN_KURULUMU.md` documents least-privilege read-only token setup. The document contains no live token. Never paste a real token into source files, issues, screenshots or logs.

## Repository policy
Generated toolchains, build outputs, runtime binaries, evidence captures, local backups and historical workstation-only material are intentionally ignored. The public repository should remain reproducible from source without containing machine secrets.

## License
MIT. See `LICENSE`.

Third-party components and generated assets remain under their respective licenses; see `THIRD_PARTY_NOTICES.md`.

