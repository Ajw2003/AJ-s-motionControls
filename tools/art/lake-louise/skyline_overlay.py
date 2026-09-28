"""Draw the photo's traced silhouettes, as the game camera sees them, over a preview render.

Usage:
    python3 tools/art/lake-louise/skyline_overlay.py <render.png> <out.png>

The lines are photo-silhouettes.svg run through the same azimuth/elevation conversion the build
uses, then projected through AimPreview's camera (60 degree vertical FOV, 16:9, pitched so the
horizon sits a third of the way down). Where a ridge in the render departs from its line, the
model departs from the photo. Red = left massif, yellow = glacier, magenta = right ridge.
"""

import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from PIL import Image, ImageDraw  # noqa: E402

import build_lake_louise  # noqa: E402
import preview  # noqa: E402

COLOURS = {"silhouette-left": "#ff3030", "silhouette-far": "#ffff00", "silhouette-right": "#ff00ff"}


def to_screen(azimuth_degrees, elevation_degrees, width, height):
    pitch = math.radians(preview.pitch_for_horizon_fraction())
    azimuth, elevation = math.radians(azimuth_degrees), math.radians(elevation_degrees)
    x = math.sin(azimuth) * math.cos(elevation)
    y = math.sin(elevation)
    z = math.cos(azimuth) * math.cos(elevation)
    camera_y = y * math.cos(pitch) + z * math.sin(pitch)
    camera_z = z * math.cos(pitch) - y * math.sin(pitch)
    tan_half = math.tan(math.radians(preview.VERTICAL_FOV_DEGREES / 2.0))
    screen_x = width / 2.0 * (1.0 + (x / camera_z) / (tan_half * width / height))
    screen_y = height / 2.0 * (1.0 - (camera_y / camera_z) / tan_half)
    return screen_x, screen_y


def main():
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    render = Image.open(sys.argv[1]).convert("RGB")
    draw = ImageDraw.Draw(render)
    for element_id, (azimuths, elevations) in build_lake_louise.load_photo_silhouettes().items():
        points = [to_screen(a, e, render.width, render.height) for a, e in zip(azimuths, elevations)]
        draw.line(points, fill=COLOURS.get(element_id, "white"), width=3)
    render.save(sys.argv[2])
    print(f"skyline_overlay: wrote {sys.argv[2]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
