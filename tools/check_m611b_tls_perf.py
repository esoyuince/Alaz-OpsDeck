from pathlib import Path
root=Path(__file__).resolve().parents[1]
wifi=(root/'firmware/panel/main/opsdeck_wifi.c').read_text(encoding='utf-8')
main=(root/'firmware/panel/main/main.c').read_text(encoding='utf-8')
host=(root/'host/OpsDeck.Core/WifiTelemetry.cs').read_text(encoding='utf-8')
engine=(root/'host/OpsDeck.Core/AppEngine.cs').read_text(encoding='utf-8')
sdk=(root/'firmware/panel/sdkconfig').read_text(encoding='utf-8')
sdkb=(root/'firmware/panel/sdkconfig.m511b').read_text(encoding='utf-8')
checks={
 'tls-in-4k':'CONFIG_MBEDTLS_SSL_IN_CONTENT_LEN=4096' in sdk and 'CONFIG_MBEDTLS_SSL_IN_CONTENT_LEN=4096' in sdkb,
 'tls-out-4k':'CONFIG_MBEDTLS_SSL_OUT_CONTENT_LEN=4096' in sdk and 'CONFIG_MBEDTLS_SSL_OUT_CONTENT_LEN=4096' in sdkb,
 'tls-dynamic-buffer':'CONFIG_MBEDTLS_DYNAMIC_BUFFER=y' in sdk and 'CONFIG_MBEDTLS_DYNAMIC_BUFFER=y' in sdkb,
 'frame-budget-3k':'GetByteCount(frame)>3000' in host,
 'tls-perf-line':'TLS_PERF ms=' in wifi,
 'tls-largest-block':'heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL)' in wifi,
 'tls-free-before-after':'internal_before=' in wifi and 'internal_after=' in wifi and 'internal_min=' in wifi,
 'host-tls-elapsed':'elapsed_ms=tlsElapsedMs' in host,
 'host-auth-elapsed':'auth_elapsed_ms=authElapsedMs' in host,
 'host-captures-tls-perf':'|TLS_PERF|' in engine and '|\\.wifi' in engine,
 'tls-timeout-bounded':'cfg.timeout_ms=6000' in wifi,
 'fast-poll-preserved':'pdMS_TO_TICKS(50)' in wifi,
 'firmware-version':'BOOT version=M5.13-' in main,
 'host-version':'M6.12-B / Pinned TLS + Perf' in engine,
}
for name,ok in checks.items(): print(('PASS ' if ok else 'FAIL ')+name)
print(f"RESULT passed={sum(checks.values())} failed={sum(not x for x in checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)
