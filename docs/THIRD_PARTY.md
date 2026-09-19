# Third-party software

## MicroLink

OpsDeck can optionally build the CrowPanel firmware with MicroLink for direct Tailscale-compatible tailnet membership.

- Upstream: `CamM2325/microlink`
- Pinned commit: `216da3300f0493b0860247d43f7af5ce29df63a5`
- License: MIT
- Integration: Git submodule at `third_party/microlink`

MicroLink includes `wireguard_lwip` under a BSD 3-clause style license and X25519 code with its own public-domain license notice. The authoritative license texts remain in the pinned submodule.

Tailscale is a trademark of Tailscale Inc. MicroLink and OpsDeck are not affiliated with or endorsed by Tailscale Inc.
