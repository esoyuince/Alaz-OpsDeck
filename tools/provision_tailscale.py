"""Provision optional direct-Tailscale transport over the already trusted USB link.

The auth key is never accepted as a command-line argument and is never printed or logged.
Use interactive hidden input by default, or --key-stdin for an automation pipe.
"""
from __future__ import annotations
import argparse
import getpass
import ipaddress
import sys
import time
import serial
from serial.tools import list_ports

BAUD = 460800
EXPECTED_VID = 0x1A86
EXPECTED_PID = 0x7522
EXPECTED_PORT = 47231
PREFIX = b"tskey-auth-"
MAX_KEY = 192


def expected_port(name: str):
    for p in list_ports.comports():
        if p.device.upper() == name.upper():
            if p.vid != EXPECTED_VID or p.pid != EXPECTED_PID:
                raise SystemExit(f"Refusing {name}: expected CH340K VID:PID 1A86:7522")
            return p
    raise SystemExit(f"{name} is not currently enumerated")


def validate_host(value: str) -> str:
    ip = ipaddress.ip_address(value)
    net = ipaddress.ip_network("100.64.0.0/10")
    if ip.version != 4 or ip not in net:
        raise argparse.ArgumentTypeError("host must be a Tailscale IPv4 address in 100.64.0.0/10")
    return str(ip)


def read_key(from_stdin: bool) -> bytearray:
    raw = (sys.stdin.buffer.readline() if from_stdin else getpass.getpass("One-time Tailscale auth key: ").encode("ascii", "strict"))
    key = bytearray(raw.strip())
    if not (20 <= len(key) <= MAX_KEY and key.startswith(PREFIX)):
        for i in range(len(key)): key[i] = 0
        raise SystemExit("Invalid Tailscale auth key shape")
    if any(c < 33 or c > 126 or c == ord('|') for c in key):
        for i in range(len(key)): key[i] = 0
        raise SystemExit("Invalid Tailscale auth key characters")
    return key


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", default="COM9")
    ap.add_argument("--host", required=True, type=validate_host)
    ap.add_argument("--host-port", type=int, default=EXPECTED_PORT, choices=[EXPECTED_PORT])
    ap.add_argument("--key-stdin", action="store_true", help="read one auth-key line from stdin without logging it")
    ap.add_argument("--timeout", type=float, default=8.0)
    a = ap.parse_args()
    expected_port(a.port)
    key = read_key(a.key_stdin)
    ser = serial.Serial(port=None, baudrate=BAUD, timeout=0.1, write_timeout=2)
    ser.dtr = False; ser.rts = False; ser.port = a.port
    try:
        ser.open()
        prefix = f"OPSDECK_TS_ENROLL_V1|{a.host}|{a.host_port}|".encode("ascii")
        ser.write(prefix); ser.write(key); ser.write(b"\n"); ser.flush()
        for i in range(len(key)): key[i] = 0
        del key
        deadline = time.monotonic() + a.timeout
        pending = bytearray()
        while time.monotonic() < deadline:
            raw = ser.read(ser.in_waiting or 1)
            if not raw: continue
            pending.extend(raw)
            while b"\n" in pending:
                line, _, pending = pending.partition(b"\n")
                line = line.rstrip(b"\r")
                if line.startswith(b"OPSDECK_TS_ACK_V1|"):
                    safe = line.decode("ascii", "strict")
                    if safe == f"OPSDECK_TS_ACK_V1|ENROLL|{a.host}|{a.host_port}":
                        print("TAILSCALE_PROVISION_ACK_OK")
                        return 0
                    print("TAILSCALE_PROVISION_REJECTED", safe)
                    return 2
            if len(pending) > 4096: pending.clear()
        print("TAILSCALE_PROVISION_ACK_TIMEOUT")
        return 3
    finally:
        try:
            for i in range(len(key)): key[i] = 0
        except UnboundLocalError:
            pass
        if ser.is_open: ser.close()

if __name__ == "__main__":
    raise SystemExit(main())
