"""Tintable 3/4-view stone art for the phone's choose screen (#51, part 1).

Run:
    python tools/art/rocks/make_phone_art.py

Reads   tools/art/rocks/rocks.json (via rock_shape.load_rocks/build)
Writes  controller/js/rock-art.js   an ES module: { <rock id and rock name>: "<svg ...>" }

Why a JS module and not image files: the relay serves only /, index.html and js/<name>.js
(relay/server.js), deliberately, so art has to ride along as a script.

Why the layering: the silhouette is filled with currentColor, so the phone sets CSS `color`
to the player's colour and the whole stone recolours. Facets are then black/white overlays
whose opacity comes from the same Lambert term the reference sheets use, so they stay
readable over ANY tint and on the phone's dark (#0d1b2a) background. Projection, culling,
painter's sort and lighting are imported from make_sheets, not copied.

Deterministic: build() is seeded and nothing here reads the clock, so reruns are
byte-identical.
"""

from pathlib import Path

from make_sheets import AMBIENT, THREE_QUARTER_MATRIX, _bbox_screen, _project
from rock_shape import build, load_rocks

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[2] / "controller" / "js" / "rock-art.js"

LONG_SIDE = 100.0  # viewBox units for the longer axis; the phone scales the SVG by CSS width.
PAD = 1.5
# Lambert-lit value that shows the bare tint; brighter faces get a white wash, darker black.
MID_LIT = 0.62


def _num(v):
    s = f"{v:.1f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def stone_svg(params):
    vertices, faces = build(params)
    min_x, max_x, min_y, max_y = _bbox_screen(vertices, THREE_QUARTER_MATRIX)
    scale = LONG_SIDE / max(max_x - min_x, max_y - min_y, 1e-6)
    w = (max_x - min_x) * scale + 2 * PAD
    h = (max_y - min_y) * scale + 2 * PAD

    def pts(screen):
        return " ".join(
            f"{_num(PAD + (x - min_x) * scale)},{_num(PAD + (max_y - y) * scale)}" for x, y in screen
        )

    # Silhouette: every face, no culling, so far-side faces fill any gap. The thin
    # currentColor stroke closes hairline seams between neighbouring polygons.
    sil = "".join(f'<polygon points="{pts(s)}"/>' for s, _g, _d in _project(vertices, faces, THREE_QUARTER_MATRIX, False))

    shade = []
    for screen, grey, _d in _project(vertices, faces, THREE_QUARTER_MATRIX, True):
        # make_sheets maps lit -> grey linearly; invert it rather than recompute the light.
        lit = (grey - 80) / (215 - 80)
        t = (lit - MID_LIT) / (1.0 - MID_LIT if lit >= MID_LIT else MID_LIT - AMBIENT)
        colour, alpha = ("#fff", 0.5 * t) if t > 0 else ("#000", -0.6 * t)
        if alpha < 0.02:
            continue
        shade.append(f'<polygon points="{pts(screen)}" fill="{colour}" fill-opacity="{alpha:.2f}"/>')

    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {_num(w)} {_num(h)}" '
        f'aria-hidden="true" focusable="false">'
        f'<g fill="currentColor" stroke="currentColor" stroke-width=".6" stroke-linejoin="round">{sil}</g>'
        f'<g stroke="none">{"".join(shade)}</g></svg>'
    )


def main():
    lines = [
        "// GENERATED FILE - do not hand-edit.",
        "// Written by tools/art/rocks/make_phone_art.py from tools/art/rocks/rocks.json (#51).",
        "// Each SVG is tintable: set CSS `color` on its container. Keyed by rock id and name.",
        "export const ROCK_ART = {",
    ]
    for p in load_rocks():
        svg = stone_svg(p).replace("\\", "\\\\").replace("'", "\'")
        lines.append(f"  '{p['id']}': '{svg}',")
    lines.append("};")
    # Names alias the id entries rather than repeating the markup, which would double the file.
    for p in load_rocks():
        lines.append(f"ROCK_ART['{p['name']}'] = ROCK_ART['{p['id']}'];")
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {OUT.relative_to(HERE.parents[2])}")


if __name__ == "__main__":
    main()
