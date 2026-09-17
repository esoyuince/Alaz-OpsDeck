from pathlib import Path
root=Path(__file__).resolve().parents[1]
host=(root/'host/OpsDeck.Core/WifiTelemetry.cs').read_text(encoding='utf-8')
engine=(root/'host/OpsDeck.Core/AppEngine.cs').read_text(encoding='utf-8')
wifi=(root/'firmware/panel/main/opsdeck_wifi.c').read_text(encoding='utf-8')
auth=(root/'firmware/panel/main/opsdeck_link_auth.c').read_text(encoding='utf-8')
fw=(root/'tools/configure_opsdeck_firewall.ps1').read_text(encoding='utf-8')
checks={
 'chat-not-on-wifi':'PanelCodexChatFrame()' not in engine[engine.index('private string[] BuildWifiTelemetryFrames'):engine.index('private async Task ProcessLoop')],
 'signed-standby-host':'OPSDECK_STANDBY_V2' in host and 'StandbyMac' in host,
 'signed-standby-firmware':'OPSDECK_STANDBY_V2' in wifi and 'verify_standby' in auth,
 'standby-prefix-exact':'strncmp(start,\"OPSDECK_STANDBY_V2|\",19)' in wifi and 'start+19' in wifi,
 'legacy-standby-rejected':'OPSDECK_STANDBY_V1' not in wifi,
 'bounded-auth':'AuthTimeout=TimeSpan.FromSeconds(2)' in host and 'MaxConcurrentSessions=8' in host,
 'nonblocking-discovery':'O_NONBLOCK' in wifi,
 'larger-drain':'char chunk[768]' in wifi and 'reads<8' in wifi,
 'fast-poll':'pdMS_TO_TICKS(50)' in wifi,
 'firewall-tcp-only':"-Protocol TCP -LocalPort 47231" in fw,
 'firewall-deck-only':'-RemoteAddress $DeckIp' in fw,
 'firewall-no-udp':'inbound_udp_rules_added=0' in fw,
 'firewall-program-guard':'ProgramPath must be an OpsDeck artifacts or runtime host.' in fw,
 'interface-bound-listener':'new TcpListener(bindAddress,WifiPairing.Port)' in host and 'IPAddress.Any,WifiPairing.Port' not in host,
 'interface-bind-fail-closed':'wifi_bind_unavailable' in engine and 'ResolveIPv4(config.NetworkInterface)' in engine,
 'stable-runtime-launcher':'runtime\\OpsDeck.Host.exe' in (root/'OPSDECK_BASLAT.cmd').read_text(encoding='utf-8') and 'artifacts\\host-' not in (root/'OPSDECK_BASLAT.cmd').read_text(encoding='utf-8'),
 'discovery-outbound-unbound':'new UdpClient(AddressFamily.InterNetwork)' in (root/'host/OpsDeck.Core/WifiDiscovery.cs').read_text(encoding='utf-8'),
}
for name,ok in checks.items(): print(('PASS ' if ok else 'FAIL ')+name)
print(f"RESULT passed={sum(checks.values())} failed={sum(not x for x in checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)
