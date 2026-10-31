#!/usr/bin/env python3
"""Builds a .pol polar from ORC certificate VPP data (jieter/orc-data ALLyyyy.json).

Takes the median over every certificate whose type matches, so one boat's sails
or measurement quirks don't dominate.

usage: orc_to_pol.py OUT.pol TYPE_REGEX ALL2023.json [ALL2024.json ...]
"""
import ast
import math
import re
import statistics
import sys

OUT_TWA = [0, 30, 35, 40, 45, 52, 60, 75, 90, 110, 120, 135, 150, 165, 180]


def interp(points, x):
    points = sorted(points)
    if x <= points[0][0]:
        return points[0][1]
    for (x0, y0), (x1, y1) in zip(points, points[1:]):
        if x <= x1:
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0)
    return points[-1][1]


def curve(vpp, i):
    """(twa, speed) points for the i-th wind speed of one certificate."""
    beat_a, beat_v = vpp["beat_angle"][i], vpp["beat_vmg"][i]
    run_a, run_v = vpp["run_angle"][i], vpp["run_vmg"][i]
    pts = [(beat_a - 8, 0.0), (beat_a, beat_v / math.cos(math.radians(beat_a)))]
    pts += [(a, vpp[a][i]) for a in vpp["angles"] if a > beat_a]
    pts.append((run_a, run_v / math.cos(math.radians(180 - run_a))))
    if run_a < 179:
        pts.append((180, run_v))  # dead downwind: speed equals downwind VMG at best
    return pts


def main():
    out, pattern, files = sys.argv[1], re.compile(sys.argv[2], re.I), sys.argv[3:]
    certs = []
    for f in files:
        for r in ast.literal_eval(open(f).read()):
            if pattern.fullmatch((r["boat"].get("type") or "").strip()):
                certs.append(r)
    if not certs:
        sys.exit("no certificates match")
    tws = list(certs[0]["vpp"]["speeds"])
    table = []
    for twa in OUT_TWA:
        row = []
        for i in range(len(tws)):
            vals = [interp(curve(c["vpp"], i), twa) if twa > 0 else 0.0 for c in certs]
            row.append(max(0.0, statistics.median(vals)))
        table.append(row)
    with open(out, "w") as f:
        f.write("TWA\\TWS\t0.0\t" + "\t".join(f"{t:.1f}" for t in tws) + "\n")
        for twa, row in zip(OUT_TWA, table):
            f.write(f"{twa:.1f}\t0.0\t" + "\t".join(f"{v:.2f}" for v in row) + "\n")
    names = sorted({f"{c['name']} ({c['sailnumber']})" for c in certs})
    print(f"{out}: median of {len(names)} certificates: " + ", ".join(names))


if __name__ == "__main__":
    main()
