"""Flat-shaded 2D reference sheets for the six shore rocks — Phase 1 of the rock rework.

Run:
    python tools/art/rocks/make_sheets.py

Reads   tools/art/rocks/rocks.json (via rock_shape.load_rocks/build)
Writes  tools/art/rocks/svg/Rock_0N_<Name>.svg   one per rock: top / side / front orthographic
                                                 views of the real mesh, plus a 3/4 silhouette.
        tools/art/rocks/svg/index.html           the owner-facing comparison page (the Phase 1
                                                 gate) — all six sheets, a TV-distance silhouette
                                                 strip, and a larger 3/4 shaded strip.

Deterministic: rock_shape.build() is seeded per rock, and nothing here reads wall-clock time or
filesystem ordering, so re-running with an unchanged rocks.json produces byte-identical output.

Rendering model, per view (top / side / front / 3/4):
  - Each view is a proper rotation from world space (x right, y up, z forward) into camera
    space (cam_x = screen right, cam_y = screen up, cam_z = depth toward the camera), built from
    an orthonormal, right-handed (det +1) basis so face normals transform the same way as
    vertices — no separate inverse-transpose needed.
  - Back-face culling: a face is visible if its camera-space normal has cam_z > 0.
  - Painter's algorithm: visible faces are drawn back to front, sorted by ascending average
    camera-space depth (cam_z) of their vertices.
  - Shading: flat Lambert term in WORLD space (one fixed light for all views, "upper-left-front":
    roughly -x, +y, +z), so the same face reads the same brightness in every view. A thin darker
    stroke outlines each face so facets remain legible at low poly counts (D2's flat-shaded
    low-poly style).
  - The 3/4 silhouette is solid black: every face (no back-face culling) is filled black, so the
    silhouette is the full outline of the mesh with no gaps from culled far-side faces.
"""

import math
from pathlib import Path

from rock_shape import build, load_rocks

HERE = Path(__file__).resolve().parent
SVG_DIR = HERE / "svg"

PX_PER_M = 420.0  # shared scale across all six main sheets, so sizes compare honestly.
MARGIN = 28
VIEW_GAP = 36
LABEL_H = 22
DIM_H = 16

LIGHT_DIR = (-0.4, 0.8, 0.6)  # world space, "upper-left-front"
GREY_MIN = 80
GREY_MAX = 215
AMBIENT = 0.35


def _normalize(v):
    m = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if m < 1e-12:
        return (0.0, 0.0, 0.0)
    return (v[0] / m, v[1] / m, v[2] / m)


LIGHT_DIR = _normalize(LIGHT_DIR)


def _dot(u, v):
    return u[0] * v[0] + u[1] * v[1] + u[2] * v[2]


def _cross(u, v):
    return (
        u[1] * v[2] - u[2] * v[1],
        u[2] * v[0] - u[0] * v[2],
        u[0] * v[1] - u[1] * v[0],
    )


def _face_normal(pts):
    nx = ny = nz = 0.0
    count = len(pts)
    for i in range(count):
        cur = pts[i]
        nxt = pts[(i + 1) % count]
        nx += (cur[1] - nxt[1]) * (cur[2] + nxt[2])
        ny += (cur[2] - nxt[2]) * (cur[0] + nxt[0])
        nz += (cur[0] - nxt[0]) * (cur[1] + nxt[1])
    return _normalize((nx, ny, nz))


def _apply_matrix(m, v):
    return (
        m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
        m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
        m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2],
    )


# Camera basis matrices: rows are (cam_x, cam_y, cam_z) expressed in world axes. Each is
# orthonormal and right-handed (right x up = forward-toward-camera), verified in the module
# docstring's rendering-model note above.
VIEW_MATRICES = {
    # top: camera above (+y) looking down. right=+x, up=-z, forward(to camera)=+y.
    "top": ((1, 0, 0), (0, 0, -1), (0, 1, 0)),
    # side: camera at +z looking toward -z. right=+x, up=+y, forward=+z.
    "side": ((1, 0, 0), (0, 1, 0), (0, 0, 1)),
    # front: camera at +x looking toward -x. right=-z, up=+y, forward=+x.
    "front": ((0, 0, -1), (0, 1, 0), (1, 0, 0)),
}


def _yaw_pitch_matrix(yaw_deg, pitch_deg):
    """Proper rotation (det +1): yaw around world Y, then pitch around the resulting X."""
    a = math.radians(yaw_deg)
    b = math.radians(pitch_deg)
    ca, sa = math.cos(a), math.sin(a)
    cb, sb = math.cos(b), math.sin(b)
    # yaw: x' = x*ca + z*sa ; z' = -x*sa + z*ca ; y'=y
    # pitch: y'' = y'*cb - z'*sb ; z'' = y'*sb + z'*cb ; x''=x'
    # Compose into one 3x3 matrix applied to (x,y,z):
    return (
        (ca, 0.0, sa),
        (sa * sb, cb, -ca * sb),
        (-sa * cb, sb, ca * cb),
    )


THREE_QUARTER_MATRIX = _yaw_pitch_matrix(35.0, 25.0)


def _project(vertices, faces, matrix, cull_backfaces):
    """Return list of (screen_polygon, fill_grey, depth) sorted back-to-front."""
    cam_verts = [_apply_matrix(matrix, v) for v in vertices]
    drawn = []
    for f in faces:
        pts_world = [vertices[i] for i in f]
        pts_cam = [cam_verts[i] for i in f]
        normal_world = _face_normal(pts_world)
        normal_cam = _apply_matrix(matrix, normal_world)
        if cull_backfaces and normal_cam[2] <= 0.0:
            continue
        intensity = max(0.0, _dot(normal_world, LIGHT_DIR))
        lit = AMBIENT + (1.0 - AMBIENT) * intensity
        grey = int(round(GREY_MIN + lit * (GREY_MAX - GREY_MIN)))
        grey = max(0, min(255, grey))
        depth = sum(p[2] for p in pts_cam) / len(pts_cam)
        screen = [(p[0], p[1]) for p in pts_cam]
        drawn.append((screen, grey, depth))
    drawn.sort(key=lambda t: t[2])  # ascending depth: far (small) first, near last -> back to front
    return drawn


def _polygon_svg(points_screen, origin_x, origin_y, scale, flip_y, fill, stroke):
    pts = []
    for x, y in points_screen:
        sx = origin_x + x * scale
        sy = origin_y + (-y if flip_y else y) * scale
        pts.append(f"{sx:.2f},{sy:.2f}")
    return f'<polygon points="{" ".join(pts)}" fill="{fill}" stroke="{stroke}" stroke-width="0.75" />'


def _grey_hex(grey):
    return f"#{grey:02x}{grey:02x}{grey:02x}"


def _stroke_hex(grey):
    dark = max(0, int(grey * 0.55))
    return f"#{dark:02x}{dark:02x}{dark:02x}"


def _bbox_screen(vertices, matrix):
    cam_verts = [_apply_matrix(matrix, v) for v in vertices]
    xs = [v[0] for v in cam_verts]
    ys = [v[1] for v in cam_verts]
    return min(xs), max(xs), min(ys), max(ys)


def _render_ortho_view(vertices, faces, view_name, origin_x, origin_y, scale):
    """Render one orthographic view; returns (svg_fragment, width_px, height_px, dim_w_cm, dim_h_cm)."""
    matrix = VIEW_MATRICES[view_name]
    min_x, max_x, min_y, max_y = _bbox_screen(vertices, matrix)
    dim_w_cm = (max_x - min_x) * 100.0
    dim_h_cm = (max_y - min_y) * 100.0

    width_px = (max_x - min_x) * scale
    height_px = (max_y - min_y) * scale

    drawn = _project(vertices, faces, matrix, cull_backfaces=True)

    # origin_x/origin_y is the top-left of this view's box; centre the projected shape in it.
    cx = origin_x + width_px / 2.0 - (min_x + max_x) / 2.0 * scale
    cy = origin_y + height_px / 2.0 + (min_y + max_y) / 2.0 * scale

    parts = []
    for screen, grey, _depth in drawn:
        parts.append(_polygon_svg(screen, cx, cy, scale, True, _grey_hex(grey), _stroke_hex(grey)))
    return "\n      ".join(parts), width_px, height_px, dim_w_cm, dim_h_cm, matrix


def _render_silhouette(vertices, faces, box_w, box_h, pad=6):
    """Solid-black 3/4 silhouette fit inside box_w x box_h (its own scale, not the shared one)."""
    matrix = THREE_QUARTER_MATRIX
    min_x, max_x, min_y, max_y = _bbox_screen(vertices, matrix)
    span_x = max(max_x - min_x, 1e-6)
    span_y = max(max_y - min_y, 1e-6)
    scale = min((box_w - 2 * pad) / span_x, (box_h - 2 * pad) / span_y)

    drawn = _project(vertices, faces, matrix, cull_backfaces=False)
    cx = box_w / 2.0 - (min_x + max_x) / 2.0 * scale
    cy = box_h / 2.0 + (min_y + max_y) / 2.0 * scale

    parts = []
    for screen, _grey, _depth in drawn:
        parts.append(_polygon_svg(screen, cx, cy, scale, True, "#000000", "#000000"))
    return "\n      ".join(parts)


def _render_three_quarter_shaded(vertices, faces, box_w, box_h, pad=8):
    """Shaded (not silhouette) 3/4 view fit inside box_w x box_h, for the index.html strip."""
    matrix = THREE_QUARTER_MATRIX
    min_x, max_x, min_y, max_y = _bbox_screen(vertices, matrix)
    span_x = max(max_x - min_x, 1e-6)
    span_y = max(max_y - min_y, 1e-6)
    scale = min((box_w - 2 * pad) / span_x, (box_h - 2 * pad) / span_y)

    drawn = _project(vertices, faces, matrix, cull_backfaces=True)
    cx = box_w / 2.0 - (min_x + max_x) / 2.0 * scale
    cy = box_h / 2.0 + (min_y + max_y) / 2.0 * scale

    parts = []
    for screen, grey, _depth in drawn:
        parts.append(_polygon_svg(screen, cx, cy, scale, True, _grey_hex(grey), _stroke_hex(grey)))
    return "\n      ".join(parts), scale, (min_x, max_x, min_y, max_y)


def build_sheet_svg(params):
    vertices, faces = build(params)
    name = params["name"]
    rock_id = params["id"]
    ordinal = rock_id.split("_")[1]

    views = []
    x_cursor = MARGIN
    top_y = MARGIN + LABEL_H
    max_height = 0.0
    view_boxes = []  # (label, fragment, x, y, w, h, dim_w_cm, dim_h_cm)

    for view_name, label in (("top", "Top (x-z)"), ("side", "Side (x-y)"), ("front", "Front (z-y)")):
        # First pass to know width/height at shared scale.
        matrix = VIEW_MATRICES[view_name]
        min_x, max_x, min_y, max_y = _bbox_screen(vertices, matrix)
        w = (max_x - min_x) * PX_PER_M
        h = (max_y - min_y) * PX_PER_M
        max_height = max(max_height, h)
        view_boxes.append((view_name, label, x_cursor, w, h))
        x_cursor += w + VIEW_GAP

    total_width = x_cursor - VIEW_GAP + MARGIN
    total_height = top_y + max_height + DIM_H + MARGIN

    silhouette_box = 90
    total_width_with_icon = total_width + silhouette_box + MARGIN

    fragments = []
    for view_name, label, vx, w, h in view_boxes:
        frag, width_px, height_px, dim_w_cm, dim_h_cm, _matrix = _render_ortho_view(
            vertices, faces, view_name, vx, top_y, PX_PER_M
        )
        fragments.append(
            f'    <g>\n'
            f'      <text x="{vx + w/2:.1f}" y="{top_y - 6}" text-anchor="middle" '
            f'class="viewlabel">{label}</text>\n'
            f'      {frag}\n'
            f'      <rect x="{vx:.1f}" y="{top_y:.1f}" width="{w:.1f}" height="{h:.1f}" '
            f'fill="none" stroke="#cccccc" stroke-width="0.5" />\n'
            f'      <text x="{vx + w/2:.1f}" y="{top_y + max_height + DIM_H:.1f}" '
            f'text-anchor="middle" class="dimlabel">{dim_w_cm:.1f} x {dim_h_cm:.1f} cm</text>\n'
            f'    </g>'
        )

    silhouette_x = total_width
    silhouette_frag = _render_silhouette(vertices, faces, silhouette_box, silhouette_box)
    fragments.append(
        f'    <g transform="translate({silhouette_x:.1f},{top_y:.1f})">\n'
        f'      <rect x="0" y="0" width="{silhouette_box}" height="{silhouette_box}" '
        f'fill="#f4f4f0" stroke="#cccccc" stroke-width="0.5" />\n'
        f'      {silhouette_frag}\n'
        f'      <text x="{silhouette_box/2:.1f}" y="{silhouette_box - 4}" text-anchor="middle" '
        f'class="dimlabel">3/4 view</text>\n'
        f'    </g>'
    )

    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{total_width_with_icon:.1f}" '
        f'height="{total_height:.1f}" viewBox="0 0 {total_width_with_icon:.1f} {total_height:.1f}">\n'
        f'  <rect x="0" y="0" width="{total_width_with_icon:.1f}" height="{total_height:.1f}" fill="#ffffff" />\n'
        f'  <style>\n'
        f'    .title {{ font: bold 16px sans-serif; fill: #222; }}\n'
        f'    .viewlabel {{ font: 11px sans-serif; fill: #555; }}\n'
        f'    .dimlabel {{ font: 10px sans-serif; fill: #777; }}\n'
        f'  </style>\n'
        f'  <text x="{MARGIN}" y="{MARGIN - 8}" class="title">Rock {ordinal} - {name}</text>\n'
        + "\n".join(fragments)
        + "\n</svg>\n"
    )
    return svg


def _silhouette_fragment_for_strip(vertices, faces, box_w, box_h):
    return _render_silhouette(vertices, faces, box_w, box_h, pad=2)


def build_index_html(rocks_params):
    sheet_rows = []
    for params in rocks_params:
        rock_id = params["id"]
        ordinal = rock_id.split("_")[1]
        name = params["name"]
        fname = f"Rock_{ordinal}_{name}.svg"
        sheet_rows.append(
            f'      <div class="sheet-card">\n'
            f'        <img src="{fname}" alt="Rock {ordinal} {name}" />\n'
            f'      </div>'
        )

    # TV-distance strip: six 3/4 silhouettes, largest rock ~40px wide, others proportional.
    built = [(p, build(p)) for p in rocks_params]
    widest_spans = []
    for p, (vertices, faces) in built:
        matrix = THREE_QUARTER_MATRIX
        min_x, max_x, _min_y, _max_y = _bbox_screen(vertices, matrix)
        widest_spans.append(max_x - min_x)
    max_span = max(widest_spans)
    tv_px_per_m = 40.0 / max_span

    tv_cells = []
    for (p, (vertices, faces)), span in zip(built, widest_spans):
        matrix = THREE_QUARTER_MATRIX
        min_x, max_x, min_y, max_y = _bbox_screen(vertices, matrix)
        w_px = (max_x - min_x) * tv_px_per_m
        h_px = (max_y - min_y) * tv_px_per_m
        drawn = _project(vertices, faces, matrix, cull_backfaces=False)
        cx = w_px / 2.0 - (min_x + max_x) / 2.0 * tv_px_per_m
        cy = h_px / 2.0 + (min_y + max_y) / 2.0 * tv_px_per_m
        parts = []
        for screen, _grey, _depth in drawn:
            parts.append(_polygon_svg(screen, cx, cy, tv_px_per_m, True, "#000000", "#000000"))
        svg_frag = "\n        ".join(parts)
        tv_cells.append(
            f'      <div class="tv-cell">\n'
            f'        <svg width="{w_px:.1f}" height="60" viewBox="0 0 {w_px:.1f} 60">\n'
            f'          <g transform="translate(0,{30 - h_px/2:.1f})">\n'
            f'        {svg_frag}\n'
            f'          </g>\n'
            f'        </svg>\n'
            f'        <div class="tv-label">{p["name"]}</div>\n'
            f'      </div>'
        )

    # Larger 3/4 shaded strip, shared scale (largest rock spans a fixed pixel width).
    shaded_px_per_m = 160.0 / max_span
    shaded_cells = []
    for (p, (vertices, faces)), span in zip(built, widest_spans):
        matrix = THREE_QUARTER_MATRIX
        min_x, max_x, min_y, max_y = _bbox_screen(vertices, matrix)
        w_px = (max_x - min_x) * shaded_px_per_m
        h_px = (max_y - min_y) * shaded_px_per_m
        drawn = _project(vertices, faces, matrix, cull_backfaces=True)
        cx = w_px / 2.0 - (min_x + max_x) / 2.0 * shaded_px_per_m
        cy = h_px / 2.0 + (min_y + max_y) / 2.0 * shaded_px_per_m
        parts = []
        for screen, grey, _depth in drawn:
            parts.append(_polygon_svg(screen, cx, cy, shaded_px_per_m, True, _grey_hex(grey), _stroke_hex(grey)))
        svg_frag = "\n        ".join(parts)
        shaded_cells.append(
            f'      <div class="shaded-cell">\n'
            f'        <svg width="{w_px:.1f}" height="180" viewBox="0 0 {w_px:.1f} 180">\n'
            f'          <g transform="translate(0,{90 - h_px/2:.1f})">\n'
            f'        {svg_frag}\n'
            f'          </g>\n'
            f'        </svg>\n'
            f'        <div class="shaded-label">{p["name"]}</div>\n'
            f'      </div>'
        )

    html = f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>Rock rework - Phase 1 reference sheets</title>
<style>
  body {{ background: #f7f7f4; color: #222; font-family: sans-serif; margin: 24px; }}
  h1 {{ font-size: 20px; }}
  p.note {{ color: #777; font-size: 12px; }}
  .sheets {{ display: flex; flex-wrap: wrap; gap: 16px; }}
  .sheet-card {{ background: #fff; border: 1px solid #ddd; padding: 8px; }}
  .sheet-card img {{ display: block; }}
  .tv-strip, .shaded-strip {{ display: flex; align-items: flex-end; gap: 24px; background: #222; padding: 16px; margin-top: 8px; }}
  .shaded-strip {{ background: #eeeeee; align-items: center; }}
  .tv-cell, .shaded-cell {{ text-align: center; }}
  .tv-label {{ color: #ccc; font-size: 10px; margin-top: 4px; }}
  .shaded-label {{ color: #444; font-size: 11px; margin-top: 4px; }}
  h2 {{ font-size: 15px; margin-top: 32px; }}
</style>
</head>
<body>
<h1>Rock rework - Phase 1 reference sheets</h1>
<p class="note">Generated by tools/art/rocks/make_sheets.py from tools/art/rocks/rocks.json. Do not hand-edit; re-run the script after changing rocks.json.</p>

<h2>TV-distance strip (silhouette, largest rock ~40px wide)</h2>
<div class="tv-strip">
{chr(10).join(tv_cells)}
</div>

<h2>3/4 shaded strip (shared scale)</h2>
<div class="shaded-strip">
{chr(10).join(shaded_cells)}
</div>

<h2>Reference sheets (shared scale)</h2>
<div class="sheets">
{chr(10).join(sheet_rows)}
</div>

</body>
</html>
"""
    return html


def main():
    SVG_DIR.mkdir(parents=True, exist_ok=True)
    rocks_params = load_rocks()

    for params in rocks_params:
        rock_id = params["id"]
        ordinal = rock_id.split("_")[1]
        name = params["name"]
        svg = build_sheet_svg(params)
        out_path = SVG_DIR / f"Rock_{ordinal}_{name}.svg"
        out_path.write_text(svg, encoding="utf-8", newline="\n")
        print(f"wrote {out_path.relative_to(HERE.parents[2])}")

    html = build_index_html(rocks_params)
    index_path = SVG_DIR / "index.html"
    index_path.write_text(html, encoding="utf-8", newline="\n")
    print(f"wrote {index_path.relative_to(HERE.parents[2])}")


if __name__ == "__main__":
    main()
