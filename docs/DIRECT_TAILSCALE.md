# Optional direct Tailscale transport

OpsDeck can build the CrowPanel firmware with an optional direct tailnet transport. The default firmware remains LAN/USB-only and does not require Tailscale.

## Security model

The remote path layers the existing OpsDeck application security inside the WireGuard tunnel:

1. MicroLink joins the user's tailnet.
2. The Deck connects only to a configured Tailscale IPv4 target in `100.64.0.0/10` on TCP port `47231`.
3. OpsDeck then performs its existing TLS 1.2 handshake and exact pinned-certificate validation.
4. The existing HMAC challenge, server proof, frame integrity and sequence/replay checks remain mandatory.
5. Remote telemetry is read-only in this phase. USB remains the control path.

The Windows host listener binds only to an active interface whose name or description identifies it as Tailscale and whose IPv4 address is inside `100.64.0.0/10`. It does not bind a public wildcard address.

## Build

Initialize the pinned submodule, then build the optional profile:

```text
git submodule update --init --recursive
python tools/run_idf.py -B build-ts -DOPSDECK_TAILSCALE=ON build
```

The Tailscale build uses `firmware/panel/sdkconfig.tailscale.defaults`. The normal firmware build remains:

```text
python tools/run_idf.py build
```

## Enrollment

A real Tailscale auth key is never compiled into firmware or stored in the repository. Initial enrollment is performed over the already trusted USB serial connection using a short-lived, preferably one-time auth key. OpsDeck stores only the enabled flag, the target Tailscale IPv4 address and port. The enrollment key exists only in RAM and is zeroed after successful tailnet registration. MicroLink persists its generated node identity in NVS so normal reboots do not require the auth key again.

Provisioning wire format:

```text
OPSDECK_TS_ENROLL_V1|100.x.y.z|47231|<one-time-auth-key>
```

Disable/reset the OpsDeck Tailscale configuration with:

```text
OPSDECK_TS_DISABLE_V1
```

Do not place real auth keys, node keys, personal tailnet DNS names, or account identifiers in source files, examples, screenshots, issue reports or logs.

## Transport priority

The intended priority is:

```text
USB -> LAN -> Tailscale
```

When the LAN host beacon is fresh or LAN telemetry is authenticated, the Deck closes the Tailscale telemetry socket. If the LAN path disappears while Wi-Fi remains available, the Deck falls back to the configured tailnet peer. Wi-Fi roaming rebinds MicroLink without replacing the stored node identity.
