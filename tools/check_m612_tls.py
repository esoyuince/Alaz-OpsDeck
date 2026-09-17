from pathlib import Path
root=Path(__file__).resolve().parents[1]
host=(root/'host/OpsDeck.Core/WifiTelemetry.cs').read_text(encoding='utf-8')
identity=(root/'host/OpsDeck.Core/WifiTlsIdentity.cs').read_text(encoding='utf-8')
engine=(root/'host/OpsDeck.Core/AppEngine.cs').read_text(encoding='utf-8')
wifi=(root/'firmware/panel/main/opsdeck_wifi.c').read_text(encoding='utf-8')
auth=(root/'firmware/panel/main/opsdeck_link_auth.c').read_text(encoding='utf-8')
cmake=(root/'firmware/panel/main/CMakeLists.txt').read_text(encoding='utf-8')
checks={
 'host-sslstream':'new SslStream(network,false)' in host and 'AuthenticateAsServerAsync' in host,
 'host-tls12':'EnabledSslProtocols=SslProtocols.Tls12' in host,
 'host-cert-dpapi':'ProtectedData.Protect(pfx,null,DataProtectionScope.CurrentUser)' in identity,
 'host-ecdsa-p256':'ECCurve.NamedCurves.nistP256' in identity,
 'host-wide-validity':'new DateTimeOffset(1970,1,1' in identity and 'new DateTimeOffset(2099,12,31' in identity,
 'host-non-ephemeral-key':'EphemeralKeySet' not in identity and 'UserKeySet|X509KeyStorageFlags.Exportable' in identity,
 'pairing-v2-host':'OPSDECK_PAIR_V2' in host and 'OPSDECK_PAIR_ACK_V2' in host and 'OPSDECK_PAIR_V1' not in host,
 'pairing-v2-firmware':'OPSDECK_PAIR_V2' in auth and 'OPSDECK_PAIR_ACK_V2' in auth and 'OPSDECK_PAIR_V1' not in auth,
 'pin-stored-nvs':'LINK_CERT "tls_cert"' in auth and 'nvs_set_blob(h,LINK_CERT' in auth,
 'pin-psram':'MALLOC_CAP_SPIRAM' in auth,
 'tls-required-before-connect':'opsdeck_link_auth_tls_ready()' in wifi,
 'esp-tls-client':'esp_tls_conn_new_sync' in wifi and 'esp_tls_conn_read' in wifi and 'esp_tls_conn_write' in wifi,
 'pinned-ca-cert':'cfg.cacert_buf=cert' in wifi and 'cfg.cacert_bytes=' in wifi,
 'no-plain-fallback':'is_plain_tcp=true' not in wifi and 'open_telemetry_socket' not in wifi,
 'tls-component':'esp-tls' in cmake,
 'hmac-inside-tls':'authenticate_telemetry(esp_tls_t *tls' in wifi and 'opsdeck_link_auth_build_response' in wifi,
}
checks.update({
 'chat-still-usb-only':'PanelCodexChatFrame()' not in engine[engine.index('private string[] BuildWifiTelemetryFrames'):engine.index('private async Task ProcessLoop')],
 'cert-fingerprint-ack':'wifiTlsFingerprint' in engine and 'tls_pin' in engine,
 'tls-log':'wifi_tls_established' in host and 'wifi_tls_failed' in host,
 'firmware-version':'BOOT version=M5.13-' in (root/'firmware/panel/main/main.c').read_text(encoding='utf-8'),
 'host-version':'M6.12-B / Pinned TLS + Perf' in engine,
})
for name,ok in checks.items(): print(('PASS ' if ok else 'FAIL ')+name)
print(f"RESULT passed={sum(checks.values())} failed={sum(not x for x in checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)

