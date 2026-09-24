"""Rasterize IamInterlayer.svg with Pillow, without a browser or Cairo.

Run: python rasterize_interlayer.py [--verify-only]
Requires Pillow. The SVG remains the source of geometry, colours and gradients.
This focused renderer supports only the primitives used by this icon; additional
SVG element types fail explicitly. Output is a supersampled 24 px RGBA PNG with
the Lugworm family's 1 px offset, 2 px blur, 30 percent black shadow.
"""

import argparse
from io import BytesIO
from pathlib import Path
import xml.etree.ElementTree as ET

from PIL import Image, ImageColor, ImageDraw, ImageFilter


HERE = Path(__file__).resolve().parent
SOURCE = HERE / "IamInterlayer.svg"
OUTPUT = HERE.parent.parent / "IamInterlayer_24.png"
SIZE = 24
SCALE = 8
NS = "{http://www.w3.org/2000/svg}"


def render():
    root = ET.parse(SOURCE).getroot()
    origin_x, origin_y, width, height = map(float, root.attrib["viewBox"].split())
    if root.attrib["width"] != "24" or root.attrib["height"] != "24" or width != height:
        raise ValueError("Expected a square 24 px SVG")
    side = SIZE * SCALE
    factor = side / width

    def point(x, y):
        return ((float(x) - origin_x) * factor, (float(y) - origin_y) * factor)

    gradients = {}
    for gradient in root.findall(f"{NS}defs/{NS}linearGradient"):
        if gradient.attrib["gradientUnits"] != "userSpaceOnUse":
            raise ValueError("Only userSpaceOnUse gradients are supported")
        start = point(gradient.attrib["x1"], gradient.attrib["y1"])
        end = point(gradient.attrib["x2"], gradient.attrib["y2"])
        stops = [(float(stop.attrib["offset"]), ImageColor.getrgb(stop.attrib["stop-color"]))
                 for stop in gradient]
        dx, dy = end[0] - start[0], end[1] - start[1]
        pixels = []
        for y in range(side):
            for x in range(side):
                t = max(0, min(1, ((x + 0.5 - start[0]) * dx +
                                  (y + 0.5 - start[1]) * dy) / (dx * dx + dy * dy)))
                for (a, colour_a), (b, colour_b) in zip(stops, stops[1:]):
                    if t <= b:
                        blend = (t - a) / (b - a)
                        pixels.append(tuple(round(v + blend * (w - v))
                                            for v, w in zip(colour_a, colour_b)) + (255,))
                        break
        paint = Image.new("RGBA", (side, side))
        paint.putdata(pixels)
        gradients[gradient.attrib["id"]] = paint

    artwork = Image.new("RGBA", (side, side))
    for element in root:
        tag = element.tag.removeprefix(NS)
        if tag in ("defs", "title"):
            continue
        attrs = element.attrib
        mask = Image.new("L", artwork.size)
        draw = ImageDraw.Draw(mask)
        if tag == "polygon":
            points = [point(*pair.split(",")) for pair in attrs["points"].split()]
            draw.polygon(points, fill=255)
        elif tag == "circle":
            x, y = point(attrs["cx"], attrs["cy"])
            radius = float(attrs["r"]) * factor
            bounds = (x - radius, y - radius, x + radius, y + radius)
            draw.ellipse(bounds, fill=255)
        elif tag == "line":
            points = [point(attrs["x1"], attrs["y1"]), point(attrs["x2"], attrs["y2"])]
        else:
            raise ValueError(f"Unsupported SVG element: {tag}")

        fill = attrs.get("fill", "none")
        if fill != "none":
            paint = (gradients[fill[5:-1]] if fill.startswith("url(#") else
                     Image.new("RGBA", artwork.size, fill))
            artwork.paste(paint, (0, 0), mask)
        if "stroke" in attrs:
            draw = ImageDraw.Draw(artwork)
            stroke = attrs["stroke"]
            thickness = float(attrs.get("stroke-width", "1")) * factor
            half = thickness / 2
            if tag == "circle":
                draw.ellipse((bounds[0] - half, bounds[1] - half,
                              bounds[2] + half, bounds[3] + half),
                             outline=stroke, width=round(thickness))
            else:
                draw.line(points + points[:1] if tag == "polygon" else points,
                          fill=stroke, width=round(thickness), joint="curve")
                if attrs.get("stroke-linecap") == "round" or attrs.get("stroke-linejoin") == "round":
                    for x, y in points:
                        draw.ellipse((x - half, y - half, x + half, y + half), fill=stroke)

    artwork = artwork.resize((SIZE, SIZE), Image.Resampling.LANCZOS)
    # Pad before blur so the shadow is cropped at the canvas edge, never wrapped.
    shadow_alpha = Image.new("L", (SIZE + 16, SIZE + 16))
    shadow_alpha.paste(artwork.getchannel("A"), (9, 9))
    shadow_alpha = shadow_alpha.filter(ImageFilter.GaussianBlur(2)).crop((8, 8, 32, 32))
    shadow = Image.new("RGBA", artwork.size)
    shadow.putalpha(shadow_alpha.point(lambda alpha: round(alpha * 77 / 255)))
    return Image.alpha_composite(shadow, artwork)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify-only", action="store_true", help="Check committed PNG against fresh rendering")
    args = parser.parse_args()
    rendered = render()
    if rendered.size != (24, 24) or rendered.mode != "RGBA" or rendered.getchannel("A").getextrema() != (0, 255):
        raise ValueError("Icon must be 24x24 RGBA with transparent and opaque pixels")
    encoded = BytesIO()
    rendered.save(encoded, format="PNG", optimize=False, compress_level=9)
    if args.verify_only:
        if OUTPUT.read_bytes() != encoded.getvalue():
            raise ValueError("Committed PNG differs from the SVG rendering; rerun without --verify-only")
    else:
        OUTPUT.write_bytes(encoded.getvalue())
    print(f"Interlayer icon {'verified' if args.verify_only else 'rendered'}: 24x24 RGBA, alpha 0..255")


if __name__ == "__main__":
    main()
