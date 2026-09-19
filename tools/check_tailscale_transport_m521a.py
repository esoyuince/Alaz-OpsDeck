from pathlib import Path
import re
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[1]

def text(rel):
    return (ROOT/rel).read_text(encoding="utf-8")

def check(name, ok):
    print(f"{'PASS' if ok else 'FAIL'} {name}")
    return bool(ok)

cmake=text('firmware/panel/CMakeLists.txt')
main=text('firmware/panel/main/opsdeck_tailscale.c')
hdr=text('firmware/panel/main/opsdeck_tailscale.h')
host=text('host/OpsDeck.Core/AppEngine.cs')
binding=text('host/OpsDeck.Core/WifiDiscovery.cs')
server=text('host/OpsDeck.Core/WifiTelemetry.cs')
defaults=text('firmware/panel/sdkconfig.tailscale.defaults')
gitmodules=text('.gitmodules')
doc=text('docs/DIRECT_TAILSCALE.md')
provision=text('tools/provision_tailscale.py')
all_local='\n'.join([cmake,main,hdr,host,binding,server,defaults,doc])

checks={
 'default-off':'option(OPSDECK_TAILSCALE "Build optional direct Tailscale transport via MicroLink" OFF)' in cmake,
 'pinned-submodule':'third_party/microlink' in gitmodules and 'CamM2325/microlink' in gitmodules,
 'separate-sdkconfig':'sdkconfig.tailscale' in cmake and 'sdkconfig.tailscale.defaults' in cmake,
 'small-peer-profile':'CONFIG_ML_MAX_PEERS=4' in defaults and 'CONFIG_ML_NVS_MAX_PEERS=16' in defaults,
 'bounded-control-buffers':'CONFIG_ML_H2_BUFFER_SIZE_KB=64' in defaults and 'CONFIG_ML_JSON_BUFFER_SIZE_KB=64' in defaults,
 'optional-httpd-off':'# CONFIG_ML_ENABLE_CONFIG_HTTPD is not set' in defaults,
 'cellular-off':'# CONFIG_ML_ENABLE_CELLULAR is not set' in defaults,
 'crypto-enabled':all(x in defaults for x in ['CONFIG_MBEDTLS_CHACHA20_C=y','CONFIG_MBEDTLS_POLY1305_C=y','CONFIG_MBEDTLS_CHACHAPOLY_C=y','CONFIG_MBEDTLS_HKDF_C=y','CONFIG_MBEDTLS_ECP_DP_CURVE25519_ENABLED=y']),
 'target-cgnat-validated':'a!=100||b<64||b>127' in main,
 'fixed-port':'TS_HOST_PORT 47231u' in main and 'port!=TS_HOST_PORT' in main,
 'auth-key-ram-only':'enrollment_key' in main and 'clear_enrollment_key' in main and 'status.enrollment_key_in_ram=false' in main,
 'auth-key-not-nvs':not re.search(r'nvs_set_(?:str|blob).*enrollment_key',main,re.I),
 'microlink-tcp':'microlink_tcp_connect' in main and 'microlink_tcp_send' in main and 'microlink_tcp_recv' in main,
 'tls12-inner':'MBEDTLS_SSL_VERSION_TLS1_2' in main,
 'exact-cert-pin':'peer->raw.len!=cert_n||memcmp(peer->raw.p,cert,cert_n)' in main,
 'hmac-server-proof':'opsdeck_link_auth_verify_server' in main,
 'hmac-frame':'opsdeck_link_auth_verify_frame' in main and 'opsdeck_link_auth_verify_standby' in main,
 'replay-order':'seq<=rx->last_seq' in main,
 'lan-first':'lan_preferred' in main and 'wifi->telemetry_authenticated' in main and 'wifi->host_seen_us' in main,
 'wifi-rebind':'microlink_rebind' in main,
 'read-only-host-server':'mode="read_only"' in host and 'chat_over_wifi=false' in server,
 'tailscale-interface-name-check':'InterfaceLooksLikeTailscale' in binding,
 'tailscale-range-check':'b[0]==100&&b[1]>=64&&b[1]<=127' in binding,
 'host-specific-bind':'new WifiTelemetryServer(tailscaleAddress' in host and 'IPAddress.Any' not in host[host.find('tailscaleAddress'):host.find('tailscaleAddress')+900],
 'no-real-auth-key':not re.search(r'tskey-auth-[A-Za-z0-9_-]{20,}',all_local),
 'no-personal-magicdns':'.ts.net' not in doc,
 'version-panel':'M5.21-A' in text('firmware/panel/main/main.c') and 'M5.21-A / DIRECT TAILSCALE' in text('firmware/panel/main/opsdeck_ui.c'),
 'version-host':'M6.21-A' in text('host/OpsDeck.Core/AppEngine.cs') and 'M6.21-A / Direct Tailscale' in text('host/OpsDeck.Host/MainForm.cs'),
 'third-party-notice':(ROOT/'docs/THIRD_PARTY.md').exists(),
 'provision-no-key-cli':'add_argument("--key"' not in provision and '--key-stdin' in provision,
 'provision-hidden-default':'getpass.getpass' in provision,
 'provision-uart-460800':'BAUD = 460800' in provision,
 'provision-ch340k':'EXPECTED_VID = 0x1A86' in provision and 'EXPECTED_PID = 0x7522' in provision,
 'provision-host-required':'add_argument("--host", required=True' in provision and 'default=' not in provision.split('add_argument("--host"',1)[1].split(')',1)[0],
}
try:
    pin=subprocess.check_output(['git','-C',str(ROOT/'third_party/microlink'),'rev-parse','HEAD'],text=True).strip()
    checks['expected-microlink-pin']=pin=='216da3300f0493b0860247d43f7af5ce29df63a5'
except Exception:
    checks['expected-microlink-pin']=False

passed=sum(check(k,v) for k,v in checks.items())
print(f'RESULT passed={passed} failed={len(checks)-passed}')
sys.exit(0 if passed==len(checks) else 1)
