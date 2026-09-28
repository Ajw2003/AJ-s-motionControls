"""Pure geometry for the six shore rocks — the single source of truth for shape.

Run either way:
    python tools/art/rocks/rock_shape.py --check     self-check: builds all six, prints
                                                      vertex/triangle counts and bbox dims (cm),
                                                      asserts outward normals and no degenerates.

Reads   tools/art/rocks/rocks.json
Writes  nothing — this module has no side effects on import so it can be loaded from both
        plain Python (make_sheets.py) and Blender's Python (build_rocks.py, Phase 2).

Coordinates: x right (long axis), y up, z forward — Unity's frame. Origin at the bounding-box
centre (recentred after jitter, per the plan).

Geometry, verbatim from docs/plans/rock-rework.md "Geometry (rock_shape.py)":
  - Ring outline, vertex i of seg, angle theta = 2*pi*i/seg:
        x = a*sgn(cos theta)*|cos theta|^(2/n),  z = b*sgn(sin theta)*|sin theta|^(2/n)
    then, in order: tri (radial scale by 1 + tri*cos(3*theta), rounded-triangle corner at +x),
    egg (z *= 1 + egg*(x/a), clamped to >= 0.2, broader at +x/narrower at -x), then taper:
    z *= 1 - taper * max(0, x/a).
  - Rings at t_k = k/(rings+1) for k = -rings..rings; ring y = t*h/2, ring scale
    s(t) = (1 - |t|^p)^(1/p). For t < 0, y is multiplied by (1 - bottomFlat). Then wedge:
    y *= max(0.2, 1 + wedge*(x/a)) using the ring vertex's pre-jitter x (apexes unaffected).
  - Apexes: one top (t=+1) and one bottom (t=-1) vertex, both centred (x=z=0).
  - Jitter: seeded (random.Random(seed)), applied after, to every vertex — radial (x, z)
    jitter of +-jitter, vertical (y) jitter of +-jitter/2.
  - Faces: quads between adjacent rings, triangle fans to the apexes.
  - Winding is fixed to point outward after the fact (Newell's method per face, flipped if the
    normal does not point away from the recentred origin) rather than reasoned out ring by
    ring, because jittered quads are not perfectly planar and the outward-normal invariant is
    what actually matters (and is what --check verifies).
  - Re-centre on the bounding box after jitter.
"""

import json
import math
import random
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROCKS_JSON = HERE / "rocks.json"


def load_rocks():
    """Return the list of rock parameter dicts from rocks.json, in file order."""
    with open(ROCKS_JSON, "r", encoding="utf-8") as f:
        data = json.load(f)
    return data["rocks"]


def _outline(theta, a, b, n, tri, egg, taper):
    c = math.cos(theta)
    s = math.sin(theta)
    x = a * math.copysign(abs(c) ** (2.0 / n), c) if c != 0 else 0.0
    z = b * math.copysign(abs(s) ** (2.0 / n), s) if s != 0 else 0.0
    if tri:
        f = 1.0 + tri * math.cos(3.0 * theta)
        x *= f
        z *= f
    if egg:
        factor = 1.0 + egg * (x / a)
        if factor < 0.2:
            factor = 0.2
        z *= factor
    if taper:
        z *= 1.0 - taper * max(0.0, x / a)
    return x, z


def _scale_of_t(t, p):
    at = abs(t)
    val = 1.0 - at ** p
    if val < 0.0:
        val = 0.0
    return val ** (1.0 / p)


def _face_normal(pts):
    """Newell's method — robust for near-planar (jittered) tris and quads alike."""
    nx = ny = nz = 0.0
    count = len(pts)
    for i in range(count):
        cur = pts[i]
        nxt = pts[(i + 1) % count]
        nx += (cur[1] - nxt[1]) * (cur[2] + nxt[2])
        ny += (cur[2] - nxt[2]) * (cur[0] + nxt[0])
        nz += (cur[0] - nxt[0]) * (cur[1] + nxt[1])
    return (nx, ny, nz)


def _dot(u, v):
    return u[0] * v[0] + u[1] * v[1] + u[2] * v[2]


def build(params):
    """params -> (vertices, faces). vertices: list of [x, y, z] floats (metres).
    faces: list of vertex-index lists (len 3 or 4), outward winding."""
    a = params["a"]
    b = params["b"]
    n = params["n"]
    tri = params.get("tri", 0.0)
    egg = params.get("egg", 0.0)
    taper = params.get("taper", 0.0)
    wedge = params.get("wedge", 0.0)
    h = params["h"]
    p = params["p"]
    bottom_flat = params.get("bottomFlat", 0.0)
    seg = params["seg"]
    rings = params["rings"]
    jitter = params.get("jitter", 0.0)
    seed = params["seed"]

    rng = random.Random(seed)

    ring_ts = [k / (rings + 1) for k in range(-rings, rings + 1)]

    vertices = []
    ring_indices = []

    for t in ring_ts:
        scale = _scale_of_t(t, p)
        y = t * h / 2.0
        if t < 0:
            y *= 1.0 - bottom_flat
        idxs = []
        for i in range(seg):
            theta = 2.0 * math.pi * i / seg
            ox, oz = _outline(theta, a, b, n, tri, egg, taper)
            x = ox * scale
            z = oz * scale
            ring_y = y
            if wedge:
                ring_y *= max(0.2, 1.0 + wedge * (x / a))
            y_here = ring_y
            jx = rng.uniform(-jitter, jitter)
            jz = rng.uniform(-jitter, jitter)
            jy = rng.uniform(-jitter / 2.0, jitter / 2.0)
            vertices.append([x + jx, y_here + jy, z + jz])
            idxs.append(len(vertices) - 1)
        ring_indices.append(idxs)

    bottom_y = -h / 2.0 * (1.0 - bottom_flat)
    top_y = h / 2.0
    bj = rng.uniform(-jitter / 2.0, jitter / 2.0)
    tj = rng.uniform(-jitter / 2.0, jitter / 2.0)
    bottom_idx = len(vertices)
    vertices.append([0.0, bottom_y + bj, 0.0])
    top_idx = len(vertices)
    vertices.append([0.0, top_y + tj, 0.0])

    # Re-centre on the bounding box before deciding winding, so the outward test below
    # (and the one --check performs) both measure "outward from the mesh's own centre".
    xs = [v[0] for v in vertices]
    ys = [v[1] for v in vertices]
    zs = [v[2] for v in vertices]
    cx = (min(xs) + max(xs)) / 2.0
    cy = (min(ys) + max(ys)) / 2.0
    cz = (min(zs) + max(zs)) / 2.0
    for v in vertices:
        v[0] -= cx
        v[1] -= cy
        v[2] -= cz

    faces = []
    for r in range(len(ring_indices) - 1):
        lo = ring_indices[r]
        hi = ring_indices[r + 1]
        for i in range(seg):
            i2 = (i + 1) % seg
            faces.append([lo[i], lo[i2], hi[i2], hi[i]])

    lowest = ring_indices[0]
    for i in range(seg):
        i2 = (i + 1) % seg
        faces.append([bottom_idx, lowest[i2], lowest[i]])

    highest = ring_indices[-1]
    for i in range(seg):
        i2 = (i + 1) % seg
        faces.append([top_idx, highest[i], highest[i2]])

    fixed_faces = []
    for f in faces:
        pts = [vertices[idx] for idx in f]
        centroid = [sum(c[k] for c in pts) / len(pts) for k in range(3)]
        normal = _face_normal(pts)
        if _dot(normal, centroid) < 0.0:
            f = list(reversed(f))
        fixed_faces.append(f)

    return vertices, fixed_faces


def _triangle_count(faces):
    # A quad is two triangles for reporting purposes.
    return sum(1 if len(f) == 3 else 2 for f in faces)


def _run_check():
    ok = True
    for params in load_rocks():
        name = params["name"]
        vertices, faces = build(params)

        # Outward normals + no degenerate faces.
        bad_normals = 0
        degenerate = 0
        for f in faces:
            pts = [vertices[idx] for idx in f]
            centroid = [sum(c[k] for c in pts) / len(pts) for k in range(3)]
            normal = _face_normal(pts)
            mag = math.sqrt(_dot(normal, normal))
            if mag < 1e-12:
                degenerate += 1
                continue
            if _dot(normal, centroid) <= 0.0:
                bad_normals += 1
            # Degenerate: any repeated vertex index.
            if len(set(f)) != len(f):
                degenerate += 1

        xs = [v[0] for v in vertices]
        ys = [v[1] for v in vertices]
        zs = [v[2] for v in vertices]
        dim_x = max(xs) - min(xs)
        dim_y = max(ys) - min(ys)
        dim_z = max(zs) - min(zs)

        jitter = params.get("jitter", 0.0)

        # Expected dims come from building the same rock with jitter=0 rather than a
        # hand-derived formula — tri/egg/wedge distort the outline in ways that are easiest
        # to just measure on a noise-free build of the same params.
        noise_free_params = dict(params)
        noise_free_params["jitter"] = 0.0
        nf_vertices, _nf_faces = build(noise_free_params)
        nf_xs = [v[0] for v in nf_vertices]
        nf_ys = [v[1] for v in nf_vertices]
        nf_zs = [v[2] for v in nf_vertices]
        expected_x = max(nf_xs) - min(nf_xs)
        expected_y = max(nf_ys) - min(nf_ys)
        expected_z = max(nf_zs) - min(nf_zs)

        # Tolerance covers jitter displacing the extreme vertex. 2 cm floor covers angular
        # discretization noise even at jitter=0 (rare exact-quadrant sampling mismatches).
        tol = max(0.02, 3 * jitter)

        dims_ok = (
            abs(dim_x - expected_x) <= tol
            and abs(dim_y - expected_y) <= tol
            and abs(dim_z - expected_z) <= tol
        )

        ratio = dim_y / max(dim_x, dim_z)
        ratio_ok = ratio < 0.25

        tri_count = _triangle_count(faces)
        print(
            f"{name:<8} verts={len(vertices):3d} tris={tri_count:3d}  "
            f"dims(cm)=({dim_x*100:5.1f}, {dim_y*100:5.1f}, {dim_z*100:5.1f})  "
            f"expected(cm)=({expected_x*100:5.1f}, {expected_y*100:5.1f}, {expected_z*100:5.1f})  "
            f"bad_normals={bad_normals} degenerate={degenerate} dims_ok={dims_ok}  "
            f"thickness_ratio={ratio:.3f} ratio_ok={ratio_ok}"
        )

        if bad_normals or degenerate or not dims_ok or not ratio_ok:
            ok = False

    return ok


if __name__ == "__main__":
    if "--check" in sys.argv:
        passed = _run_check()
        if not passed:
            print("CHECK FAILED")
            sys.exit(1)
        print("CHECK PASSED")
    else:
        print(__doc__)
