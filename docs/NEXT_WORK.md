# Next Work

Updated: 2026-09-19

Completed:
- RDC telemetry: Deck uses current remote-run call/session counters instead of lifetime usageStats counters.
- Responsiveness phase 2: host/panel UART raised from 115200 to 460800; request-priority scheduler retained; M5.18-A panel flashed app-only and M6.18-A host deployed.
- Fan control phase 2: OMEN manual mode validated on physical hardware; Deck supports Auto / Max / Manual 50-100% in 5% steps. M5.19-A panel and M6.19-A host deployed; manual 75% -> Auto live round-trip passed.
- Cloud screen polish: account tabs show live cost snapshots; usage vs monthly fee is explicit; period-start is shown even when Cloudflare omits period-end; M5.20-A panel deployed app-only.
- Codex quota resilience: M6.20-A keeps the last-good quota visible as STALE across transient app-server failures, retries failures after 30 seconds, restores a bounded 30-minute local cache on host restart, and hides expired reset timestamps. Live restart acceptance restored codex quota=98 before the next live read.

Next candidates:
- Secure remote Deck connectivity when panel and host are on different networks (Tailscale/VPN/tunnel design was previously deferred).
- Longer M5.20/M6.20 stability soak and responsiveness measurements under normal use.
