# ALAZ OPSDECK — Contributor Notes

## Repository boundary
This public repository contains source code, reproducible configuration and selected maintenance tools. Local runtime state, logs, backups, credentials, firmware binaries and machine-specific evidence are intentionally excluded.

## Firmware / serial safety
1. Use `tools/safe_flash_panel.py` for panel app flashing.
2. Never leave an esptool process waiting for a serial port.
3. Never use full-chip erase for a normal update.
4. Preserve NVS, OTA data, storage, Wi-Fi credentials and pairing state.
5. Default panel updates are app-only at `0x10000`, with an explicit SHA256 and separate `verify_flash`.
6. Stop `OpsDeck.Host.exe` before flashing.
7. Do not unplug USB until the safe flasher reports `SAFE_FLASH_OK`.
8. Automatic host recovery may reopen the COM port only; it must not hard-reset the panel or invoke esptool.

## Network boundary
USB is the primary telemetry/control path. Same-LAN Wi-Fi telemetry is read-only and protected with pinned TLS plus application authentication. Do not expose the telemetry listener directly to the public Internet.

## Secrets
Never commit API tokens, keys, passwords, private certificates, `.env` files, DPAPI blobs, local logs or credential exports. Use placeholders in documentation and test fixtures.
