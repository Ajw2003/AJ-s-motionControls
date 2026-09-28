"""Side-by-side check images for the Lake Louise art pipeline.

Usage:
    python tools/art/lake-louise/compare.py <left image or .svg> <right image or .svg> <out.png>
        [--labels "Left label" "Right label"]

An .svg input is rasterised with cairosvg first. Both images are scaled to the same height and
placed side by side with a label strip underneath, so a stage's output can be judged against the
reference photo or against the previous stage's output in one picture.
"""

import argparse
import io
import sys
from pathlib import Path

from PIL import Image, ImageDraw

HEIGHT = 540


def load(path: Path) -> Image.Image:
    if path.suffix.lower() == ".svg":
        import cairosvg

        png = cairosvg.svg2png(url=str(path))
        return Image.open(io.BytesIO(png)).convert("RGB")
    return Image.open(path).convert("RGB")


def fit(image: Image.Image) -> Image.Image:
    width = round(image.width * HEIGHT / image.height)
    return image.resize((width, HEIGHT), Image.LANCZOS)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("left", type=Path)
    parser.add_argument("right", type=Path)
    parser.add_argument("out", type=Path)
    parser.add_argument("--labels", nargs=2, default=None)
    args = parser.parse_args()

    for path in (args.left, args.right):
        if not path.exists():
            print(f"compare: input not found: {path}", file=sys.stderr)
            return 1

    left, right = fit(load(args.left)), fit(load(args.right))
    labels = args.labels or [args.left.name, args.right.name]
    gap, strip = 12, 36
    canvas = Image.new("RGB", (left.width + gap + right.width, HEIGHT + strip), "white")
    canvas.paste(left, (0, 0))
    canvas.paste(right, (left.width + gap, 0))

    draw = ImageDraw.Draw(canvas)
    draw.text((10, HEIGHT + 10), labels[0], fill="black")
    draw.text((left.width + gap + 10, HEIGHT + 10), labels[1], fill="black")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(args.out)
    print(f"compare: wrote {args.out} ({canvas.width}x{canvas.height})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
