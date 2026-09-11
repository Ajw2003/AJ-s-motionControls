#!/usr/bin/env python3
"""Independent verification of the skip-physics contact model, outside Unity.

Why this exists
---------------
Unity holds an exclusive lock on the project, so the C# solver can only be compiled and run
from the open Editor. That makes it painfully slow to answer questions like "does this force
law even produce a magic angle?" — and that question has to be answered BEFORE the C# is
trusted, not after.

So the closed-form parts of `Assets/Scripts/Game/Physics/DiscContact.cs` are re-implemented
here in ~100 lines of Python and checked two ways:

  1. `verify_geometry()`  — the wetted area and centroid closed forms against brute-force
     numerical integration over the disc. Pure maths, no physics assumptions.
  2. `sweep_magic_angle()` — integrates a fixed-attitude 2-D contact and finds the tilt that
     minimises the speed required to bounce. This is check R1, the single result the whole
     rewrite rests on.

This is a MODEL check, not a port. It is 2-D, holds attitude fixed through the collision,
ignores spin and thickness, and uses a coarse sink criterion. It cannot tell you the C# is
correct — only whether the force law it implements is capable of the published behaviour.
Treat a disagreement as "investigate", not "the C# is broken".

See `docs/Findings-RealSkipPhysics.md` for the sources and every published number.

Run it (Git Bash or PowerShell, from anywhere):
    python "C:/Users/aj/Desktop/GameDev/RockSkipping/tools/physics/verify-disc-contact.py"
"""

import sys
from math import acos, cos, pi, radians, sin, sqrt

# Bocquet's standard stone (Am. J. Phys. 71, 150 (2003)) — mirrors ReferenceStone.cs.
R = 0.05      # m, radius (10 cm diameter)
M = 0.1       # kg
RHO = 1000.0  # kg/m^3
G = 9.81      # m/s^2


def wetted_area(radius, d):
    """Bocquet Eq. (9). `d` is the in-plane distance from disc centre to the waterline."""
    if radius <= 0.0 or d >= radius:
        return 0.0
    if d <= -radius:
        return pi * radius * radius
    ratio = max(-1.0, min(1.0, d / radius))
    return radius * radius * acos(ratio) - d * sqrt(max(0.0, radius * radius - d * d))


def wetted_centroid(radius, d):
    """Circular-segment centroid: (2/3)(R^2 - d^2)^1.5 / A."""
    a = wetted_area(radius, d)
    if a <= 0.0:
        return 0.0
    c = max(-radius, min(radius, d))
    return (2.0 / 3.0) * max(0.0, radius * radius - c * c) ** 1.5 / a


def _numeric(radius, d, n=4000):
    """Brute-force area and centroid of the region x >= d inside the disc."""
    area = moment = 0.0
    h = 2.0 * radius / n
    for i in range(n):
        x = -radius + (i + 0.5) * h
        if x < d:
            continue
        da = 2.0 * sqrt(max(0.0, radius * radius - x * x)) * h
        area += da
        moment += x * da
    return area, (moment / area if area > 0 else 0.0)


def verify_geometry():
    print("=" * 70)
    print("1. GEOMETRY — closed form vs numerical integration")
    print("=" * 70)
    worst_a = worst_c = 0.0
    for k in range(-10, 11):
        d = R * k / 10.0
        an, cn = _numeric(R, d)
        worst_a = max(worst_a, abs(wetted_area(R, d) - an))
        worst_c = max(worst_c, abs(wetted_centroid(R, d) - cn))
    print(f"  worst area error     : {worst_a:.2e}")
    print(f"  worst centroid error : {worst_c:.2e}")

    checks = [
        ("area at d=0 is a half disc", wetted_area(R, 0.0), pi * R * R / 2.0),
        ("centroid at d=0 is 4R/3pi", wetted_centroid(R, 0.0), 4.0 * R / (3.0 * pi)),
        ("area at d=-R is a full disc", wetted_area(R, -R), pi * R * R),
        ("centroid at d=-R is the centre", wetted_centroid(R, -R), 0.0),
        ("area at d=+R is zero", wetted_area(R, R), 0.0),
    ]
    ok = worst_a < 1e-6 and worst_c < 1e-5
    for label, got, want in checks:
        good = abs(got - want) < 1e-9
        ok = ok and good
        print(f"  {'PASS' if good else 'FAIL'}  {label}: {got:.8f} vs {want:.8f}")
    return ok


def bounces(v0, tilt_deg, incidence_deg, c_lift=1.0, c_friction=0.0,
            dt=5e-6, t_max=0.6):
    """Integrate one fixed-attitude contact. True if the disc clears the water rising.

    c_friction is Bocquet's tangential term (proportional to the FULL speed squared).
    It defaults to 0 — see `docs/Findings-RealSkipPhysics.md` section 1.3 for why anything
    near 1.0 sinks the stone at every tilt and every speed.
    """
    th = radians(tilt_deg)
    be = radians(incidence_deg)
    s = sin(th)
    if s < 1e-6:
        return False
    nx, ny = -s, cos(th)                 # face normal, tilted back by the attack angle
    vx, vy = v0 * cos(be), -v0 * sin(be)
    h = R * s                            # centre height at first rim contact
    t = 0.0
    while t < t_max:
        d = h / s
        if d <= -R:
            return False                 # fully immersed — it sank
        area = wetted_area(R, d)
        ax = ay = 0.0
        if area > 0.0:
            vn = vx * nx + vy * ny
            lift = 0.5 * RHO * c_lift * vn * vn * area
            fx, fy = lift * nx, lift * ny
            if c_friction > 0.0:
                ipx, ipy = vx - vn * nx, vy - vn * ny
                ipm = sqrt(ipx * ipx + ipy * ipy)
                if ipm > 1e-9:
                    fr = 0.5 * RHO * c_friction * (vx * vx + vy * vy) * area
                    fx -= ipx / ipm * fr
                    fy -= ipy / ipm * fr
            ax, ay = fx / M, fy / M
        ay -= G
        vx += ax * dt
        vy += ay * dt
        h += vy * dt
        t += dt
        if vx <= 0.0:
            return False
        if h >= R * s and vy > 0.0:
            return True
    return False


def min_speed(tilt_deg, incidence_deg, c_friction=0.0):
    """Bisect for the slowest speed that still bounces at this tilt. None if never."""
    hi = 30.0
    if not bounces(hi, tilt_deg, incidence_deg, c_friction=c_friction):
        return None
    lo = 0.02
    for _ in range(22):
        mid = 0.5 * (lo + hi)
        if bounces(mid, tilt_deg, incidence_deg, c_friction=c_friction):
            hi = mid
        else:
            lo = mid
    return hi


def sweep_magic_angle():
    print()
    print("=" * 70)
    print("2. MAGIC ANGLE (R1) — tilt that minimises the speed needed to bounce")
    print("=" * 70)
    print("   Published: ~20 deg, falling slightly as incidence rises (Clanet 2004;")
    print("   Nagahiro & Hayakawa 2004 Fig. 4).")
    ok = True
    for incidence in (10, 15, 20):
        rows = [(t, min_speed(t, incidence)) for t in range(5, 61, 5)]
        rows = [(t, v) for t, v in rows if v is not None]
        if not rows:
            print(f"\n  incidence {incidence:>2} deg -> NEVER BOUNCES")
            ok = False
            continue
        best = min(rows, key=lambda r: r[1])
        print(f"\n  incidence {incidence:>2} deg")
        print("     tilt : " + " ".join(f"{t:>6d}" for t, _ in rows))
        print("     vmin : " + " ".join(f"{v:>6.2f}" for _, v in rows))
        print(f"     -> interior optimum at {best[0]} deg ({best[1]:.2f} m/s)")
        if not 15 <= best[0] <= 40:
            ok = False
    return ok


def sweep_friction():
    print()
    print("=" * 70)
    print("3. FRICTION SENSITIVITY — why C_f must be 0 with a (v.n)^2 lift")
    print("=" * 70)
    for cf in (0.0, 0.05, 0.1, 0.25, 0.5, 1.0):
        rows = [(t, min_speed(t, 15, c_friction=cf)) for t in range(5, 61, 5)]
        rows = [(t, v) for t, v in rows if v is not None]
        if not rows:
            print(f"  C_f = {cf:<5} -> NEVER BOUNCES at any tilt or speed")
            continue
        best = min(rows, key=lambda r: r[1])
        print(f"  C_f = {cf:<5} -> optimum {best[0]:>3} deg, v_min {best[1]:.2f} m/s")


if __name__ == "__main__":
    geometry_ok = verify_geometry()
    angle_ok = sweep_magic_angle()
    sweep_friction()
    print()
    print("=" * 70)
    print(f"GEOMETRY   : {'PASS' if geometry_ok else 'FAIL'}")
    print(f"MAGIC ANGLE: {'PASS (interior optimum found)' if angle_ok else 'FAIL'}")
    print("=" * 70)
    sys.exit(0 if (geometry_ok and angle_ok) else 1)
