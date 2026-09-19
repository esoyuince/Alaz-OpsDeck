# Next Work

Updated: 2026-09-19

Completed:
- RDC telemetry: Deck uses current remote-run call/session counters instead of lifetime usageStats counters.
- Responsiveness phase 2: host/panel UART raised from 115200 to 460800; request-priority scheduler retained; M5.18-A panel flashed app-only and M6.18-A host deployed.
- Fan control phase 2: OMEN manual mode validated on physical hardware; Deck supports Auto / Max / Manual 50-100% in 5% steps. M5.19-A panel and M6.19-A host deployed; manual 75% -> Auto live round-trip passed.
- Cloud screen polish: account tabs show live cost snapshots; usage vs monthly fee is explicit; period-start is shown even when Cloudflare omits period-end; M5.20-A panel deployed app-only.
- Codex quota resilience: M6.20-A keeps the last-good quota visible as STALE across transient app-server failures, retries failures after 30 seconds, restores a bounded 30-minute local cache on host restart, and hides expired reset timestamps. Live restart acceptance restored codex quota=98 before the next live read.
- Direct Tailscale transport foundation: M5.21-A/M6.21-A add optional MicroLink-based ESP32 tailnet membership, a host listener bound only to the Tailscale interface, LAN-first fallback, TLS 1.2 certificate pinning and existing HMAC telemetry over the WireGuard tunnel. Software build acceptance is complete; physical enrollment/hotspot acceptance remains open.

Next candidates:
- Complete one-time tailnet enrollment and physical LAN -> hotspot -> Tailscale -> LAN acceptance on the CrowPanel.
- Longer M5.21/M6.21 stability soak and responsiveness measurements under normal use.
