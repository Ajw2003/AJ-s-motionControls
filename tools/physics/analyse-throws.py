#!/usr/bin/env python3
"""Summarise recorded real throws, and check them against the published physics.

Why this exists
---------------
`SkipConstants.SpeedScale` converts the phone's raw swing speed into a stone's release
speed. It is the single point where reality enters the launch, so picking it by reasoning
would put an invented number right back at the front of a solver built to remove them.

`Verification/RockTelemetry` already records every completed throw to `rock-throws.csv` at
the repo root (it is created by `Bootstrap` and on by default). This reads that file and
reports what the phone ACTUALLY produces, then works out what SpeedScale would map it onto
the release speeds the literature uses.

It also checks the recorded spin against Bocquet's stability ceiling, which turns out to be
the binding constraint — see `docs/Findings-RealSkipPhysics.md` section 6.

Run it (Git Bash or PowerShell, from anywhere):
    python "C:/Users/aj/Desktop/GameDev/RockSkipping/tools/physics/analyse-throws.py"

Optionally pass a different CSV:
    python ".../analyse-throws.py" path/to/rock-throws.csv
"""

import csv
import os
import statistics
import sys

# Bocquet's standard stone — see ReferenceStone.cs and Findings section 3.
R = 0.05
G = 9.81

# Mirrors Assets/Scripts/Game/Physics/SpinTransfer.cs. Half a phone's long edge: what is
# conserved at release is the tangential speed at the grip point, not the angular rate,
# so stone spin = phone spin * (GRIP_RADIUS / R).
GRIP_RADIUS = 0.075
REAL_FLICK_TANGENTIAL_MPS = 4.4  # 88 rad/s * 0.05 m — a real finger flick

DEFAULT_CSV = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
    "rock-throws.csv",
)


def column(rows, key, absolute=False):
    out = []
    for r in rows:
        v = (r.get(key) or "").strip()
        if not v:
            continue
        try:
            f = float(v)
        except ValueError:
            continue
        out.append(abs(f) if absolute else f)
    return sorted(out)


def pct(values, p):
    if not values:
        return float("nan")
    i = (len(values) - 1) * p
    lo = int(i)
    hi = min(lo + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (i - lo)


def spin_ceiling(rev_per_sec):
    """Bocquet Eq. (25): N_c = R*Omega^2/g. Bounces before the stone tumbles out."""
    omega = rev_per_sec * 2.0 * 3.141592653589793
    return R * omega * omega / G


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CSV
    if not os.path.exists(path):
        print(f"No telemetry at {path}")
        print("Play the game and take some throws first — Bootstrap records them automatically.")
        return 1

    with open(path, newline="", encoding="utf-8") as fh:
        rows = list(csv.DictReader(fh))
    if not rows:
        print(f"{path} has a header but no throws yet.")
        return 1

    speed = column(rows, "swingSpeed")
    spin = column(rows, "spinRate", absolute=True)

    print("=" * 72)
    print(f"RECORDED THROWS — {len(rows)} rows from {os.path.basename(path)}")
    print("=" * 72)

    print(f"\nSWING SPEED (n={len(speed)}) — raw m/s off the phone, unclamped")
    for label, p in [("min", 0.0), ("p10", .10), ("median", .50),
                     ("p90", .90), ("p95", .95), ("p99", .99), ("MAX", 1.0)]:
        print(f"   {label:>7}: {pct(speed, p):8.2f}")
    if len(speed) > 1:
        print(f"   {'mean':>7}: {statistics.mean(speed):8.2f}   "
              f"stdev {statistics.pstdev(speed):.2f}")

    print(f"\nSPIN (n={len(spin)}) — |deg/s| about the phone's screen normal")
    for label, p in [("min", 0.0), ("median", .50), ("p90", .90), ("MAX", 1.0)]:
        v = pct(spin, p)
        print(f"   {label:>7}: {v:8.1f} deg/s = {v / 360:5.2f} rev/s")

    print("\n" + "-" * 72)
    print("SPEED CALIBRATION — what SpeedScale maps the phone onto reality")
    print("-" * 72)
    p95 = pct(speed, .95)
    print(f"   Using p95 = {p95:.1f} m/s as 'a hard throw' (max is noisier):")
    for target, note in [(12.0, "world record, 38 skips"),
                         (10.0, "very strong throw"),
                         (8.0, "good adult throw, 17 skips")]:
        print(f"      -> {target:5.1f} m/s : SpeedScale = {target / p95:.3f}   [{note}]")

    print("\n" + "-" * 72)
    print("SPIN REALITY CHECK — Bocquet Eq. (25), N_c = R*Omega^2/g")
    print("-" * 72)
    print("   Bounces the stone survives before tumbling out, by spin rate.")
    print("   This ceiling does NOT depend on throw speed, so it binds on its own.\n")
    print(f"   {'source':<34}{'rev/s':>8}{'max bounces':>14}")
    print("   " + "-" * 56)
    for label, rev in [("published: world record", 14.0),
                       ("published: decent spin", 5.0),
                       ("published: minimum for stability", 2.0)]:
        print(f"   {label:<34}{rev:>8.2f}{spin_ceiling(rev):>14.1f}")
    for label, p in [("MEASURED: your best throw", 1.0),
                     ("MEASURED: your p90", .90),
                     ("MEASURED: your median", .50)]:
        rev = pct(spin, p) / 360.0
        print(f"   {label:<34}{rev:>8.2f}{spin_ceiling(rev):>14.1f}")

    print()
    print("-" * 72)
    print("SPIN TRANSFER — stone spin = phone spin * (grip radius / stone radius)")
    print("-" * 72)
    print(f"   SpinTransfer.cs, grip radius {GRIP_RADIUS} m, stone radius {R} m")
    print(f"   -> ratio {GRIP_RADIUS / R:.2f}x")
    print()
    print(f"   {'throw':<28}{'raw rev/s':>11}{'stone rev/s':>13}{'bounces':>10}")
    print("   " + "-" * 62)
    for label, p in [("best of the set", 1.0), ("p90", .90), ("median", .50)]:
        raw = pct(spin, p) / 360.0
        moved = raw * (GRIP_RADIUS / R)
        print(f"   {label:<28}{raw:>11.2f}{moved:>13.2f}{spin_ceiling(moved):>10.1f}")

    tangential = pct(spin, 1.0) * 3.141592653589793 / 180.0 * GRIP_RADIUS
    print()
    print(f"   Grip tangential speed, best throw : {tangential:.2f} m/s")
    print(f"   A real finger flick               : {REAL_FLICK_TANGENTIAL_MPS:.2f} m/s")
    print(f"   -> the gesture captures {tangential / REAL_FLICK_TANGENTIAL_MPS * 100:.0f}% of a real flick.")
    print("      The rest is a CONTROLLER problem; no Unity constant can recover it.")

    best_rev = pct(spin, 1.0) / 360.0
    print()
    if spin_ceiling(best_rev * (GRIP_RADIUS / R)) < 12:
        print("   *** The phone's measured wrist spin is far below what real skipping needs.")
        print(f"   *** Best throw: {spin_ceiling(best_rev):.0f} bounces raw, "
              f"{spin_ceiling(best_rev * (GRIP_RADIUS / R)):.0f} after the transfer model.")
        print("   *** This is a units/semantics gap, not a tuning gap: a wrist does not")
        print("   *** rotate at 14 rev/s, but the stone it releases does. See Findings section 6.")

    skips = column(rows, "skips")
    dist = column(rows, "distance")
    if skips and dist:
        print("\n" + "-" * 72)
        print("CURRENT GAME OUTCOMES (the old solver, for comparison)")
        print("-" * 72)
        print(f"   skips    median {pct(skips, .5):.0f}   max {max(skips):.0f}")
        print(f"   distance median {pct(dist, .5):.1f} m   max {max(dist):.1f} m")
    return 0


if __name__ == "__main__":
    sys.exit(main())
