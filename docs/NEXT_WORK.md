# Next Work

Updated: 2026-09-19

Completed:
- RDC telemetry: Deck uses current remote-run call/session counters instead of lifetime usageStats counters.
- Responsiveness phase 2: host/panel UART raised from 115200 to 460800; request-priority scheduler retained; M5.18-A panel flashed app-only and M6.18-A host deployed.
- Fan control phase 2: OMEN manual mode validated on physical hardware; Deck supports Auto / Max / Manual 50-100% in 5% steps. M5.19-A panel and M6.19-A host deployed; manual 75% -> Auto live round-trip passed.
- Cloud screen polish: account tabs show live cost snapshots; usage vs monthly fee is explicit; period-start is shown even when Cloudflare omits period-end; M5.20-A panel deployed app-only.

Deferred:
- Codex quota visibility intermittently disappears. Verify primary/secondary quota, next reset, restart/cache/stale behavior, and Deck/Windows UI parity before changing it.
