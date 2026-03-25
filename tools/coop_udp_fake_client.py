#!/usr/bin/env python3
"""
Send valid GHPC.CoopFoundation UDP snapshot packets (v3 preferred; matches game 0.2.0+).

v3 = 84 bytes: GHP\\x03 + (same as v2 through phase+pad) + turret quat (world) + gun quat (world) + unitNetId (u32).

v2 (48 B) and v1 (40 B) are still parsed by the mod; this script sends v3 by default.

Use with Host in Playing on the SAME mission as --mission (default GT03_Native_Narrative).

  python tools/coop_udp_fake_client.py
  python tools/coop_udp_fake_client.py 192.168.1.10 27015
  python tools/coop_udp_fake_client.py --mission GT03_Native_Narrative --px -322 --py 126.8 --pz -2518

Requires: Python 3.6+
"""
from __future__ import annotations

import argparse
import math
import socket
import struct
import sys
import time
from typing import Tuple

# Wire: 0=none, 1=Planning, 2=Playing, 3=Finished (same as mod)
WIRE_PLAYING = 2

FNV_OFFSET = 2166136261
FNV_PRIME = 16777619


def mission_token(name: str) -> int:
    if not name:
        return 0
    h = FNV_OFFSET
    for c in name.lower():
        h ^= ord(c)
        h = (h * FNV_PRIME) & 0xFFFFFFFF
    return h if h != 0 else 1


def unity_euler_to_quaternion(ex_deg: float, ey_deg: float, ez_deg: float) -> Tuple[float, float, float, float]:
    """Match UnityEngine.Quaternion.Euler (degrees, ZXY apply order)."""
    x = math.radians(ex_deg) * 0.5
    y = math.radians(ey_deg) * 0.5
    z = math.radians(ez_deg) * 0.5
    cx, sx = math.cos(x), math.sin(x)
    cy, sy = math.cos(y), math.sin(y)
    cz, sz = math.cos(z), math.sin(z)
    qx = cz * sx * sy - sz * cx * cy
    qy = cz * cx * sy + sz * sx * cy
    qz = sz * cx * sy - cz * sx * cy
    qw = cz * cx * cy + sz * sx * sy
    return (qx, qy, qz, qw)


def build_packet_v3(
    sequence: int,
    instance_id: int,
    px: float,
    py: float,
    pz: float,
    hull_q: Tuple[float, float, float, float],
    token: int,
    phase: int,
    turret_q: Tuple[float, float, float, float],
    gun_q: Tuple[float, float, float, float],
    unit_net_id: int,
) -> bytes:
    header = b"GHP\x03"
    qx, qy, qz, qw = hull_q
    tqx, tqy, tqz, tqw = turret_q
    gqx, gqy, gqz, gqw = gun_q
    body = struct.pack(
        "<Ii7fIB3x8fI",
        sequence & 0xFFFFFFFF,
        instance_id,
        px,
        py,
        pz,
        qx,
        qy,
        qz,
        qw,
        token & 0xFFFFFFFF,
        phase & 0xFF,
        tqx,
        tqy,
        tqz,
        tqw,
        gqx,
        gqy,
        gqz,
        gqw,
        unit_net_id & 0xFFFFFFFF,
    )
    return header + body


def main() -> int:
    p = argparse.ArgumentParser(description="Fake CoopFoundation UDP client (v3 snapshot).")
    p.add_argument("host", nargs="?", default="127.0.0.1", help="Host IPv4 (default 127.0.0.1)")
    p.add_argument("port", nargs="?", type=int, default=27015, help="UDP port (default 27015)")
    p.add_argument(
        "-n",
        "--count",
        type=int,
        default=5,
        help="Packets to send (default 5); use 0 for infinite until Ctrl+C",
    )
    p.add_argument("-i", "--interval", type=float, default=0.2, help="Seconds between packets (default 0.2)")
    p.add_argument(
        "--mission",
        type=str,
        default="GT03_Native_Narrative",
        help="MissionSceneName for coherence token (must match Host mission)",
    )
    p.add_argument("--px", type=float, default=-322.0, help="World X")
    p.add_argument("--py", type=float, default=126.8, help="World Y")
    p.add_argument("--pz", type=float, default=-2518.0, help="World Z")
    p.add_argument("--ex", type=float, default=357.0, help="Hull Euler X deg (Unity)")
    p.add_argument("--ey", type=float, default=346.0, help="Hull Euler Y deg")
    p.add_argument("--ez", type=float, default=359.0, help="Hull Euler Z deg")
    p.add_argument("--tex", type=float, default=None, help="Turret world Euler X (default: same as hull)")
    p.add_argument("--tey", type=float, default=None, help="Turret world Euler Y")
    p.add_argument("--tez", type=float, default=None, help="Turret world Euler Z")
    p.add_argument("--gex", type=float, default=None, help="Gun world Euler X (default: same as turret)")
    p.add_argument("--gey", type=float, default=None, help="Gun world Euler Y")
    p.add_argument("--gez", type=float, default=None, help="Gun world Euler Z")
    p.add_argument(
        "--unit-net-id",
        type=int,
        default=0xDEADBEEF,
        help="32-bit unit net id (default 0xdeadbeef; real game uses FNV of UniqueName)",
    )
    args = p.parse_args()

    tex = args.ex if args.tex is None else args.tex
    tey = args.ey if args.tey is None else args.tey
    tez = args.ez if args.tez is None else args.tez
    gex = tex if args.gex is None else args.gex
    gey = tey if args.gey is None else args.gey
    gez = tez if args.gez is None else args.gez

    tok = mission_token(args.mission)
    hull_q = unity_euler_to_quaternion(args.ex, args.ey, args.ez)
    turret_q = unity_euler_to_quaternion(tex, tey, tez)
    gun_q = unity_euler_to_quaternion(gex, gey, gez)
    probe = build_packet_v3(
        0,
        -999001,
        args.px,
        args.py,
        args.pz,
        hull_q,
        tok,
        WIRE_PLAYING,
        turret_q,
        gun_q,
        args.unit_net_id,
    )
    if len(probe) != 84:
        print("internal error: packet length", len(probe), file=sys.stderr)
        return 1

    print(f"mission={args.mission!r} token={tok} (v3 {len(probe)} bytes)", file=sys.stderr)

    addr = (args.host, args.port)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        n = 0
        while True:
            seq = n + 1
            data = build_packet_v3(
                seq,
                -999001,
                args.px,
                args.py,
                args.pz,
                hull_q,
                tok,
                WIRE_PLAYING,
                turret_q,
                gun_q,
                args.unit_net_id,
            )
            sock.sendto(data, addr)
            n += 1
            print(f"sent seq={seq} {n} -> {addr[0]}:{addr[1]} ({len(data)} bytes)")
            if args.count != 0 and n >= args.count:
                break
            time.sleep(args.interval)
    except OSError as e:
        print(e, file=sys.stderr)
        return 1
    finally:
        sock.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
