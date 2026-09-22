"""Build the inline component map SVG from component-map.json and the icon folder.

Run from anywhere:

    python tools/component_map/build_component_map.py

Reads  tools/component_map/component-map.json
       assets/images/icons/svg/<icon>.svg
Writes _includes/component-map.svg

The output is a Jekyll include: node links use the relative_url filter, which
Liquid resolves when the page is built. Standard library only.
"""
import html
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = ROOT / "tools" / "component_map" / "component-map.json"
ICON_DIR = ROOT / "assets" / "images" / "icons" / "svg"
OUT = ROOT / "_includes" / "component-map.svg"
COMPONENT_DIR = ROOT / "docs" / "components"

SIDE_RE = re.compile(r"^([A-Za-z0-9_]+)\.([lrtb])([+-]\d+(?:\.\d+)?)?$")


class Node:
    def __init__(self, spec, columns, sizes):
        self.id = spec["id"]
        self.label = spec["label"]
        self.icon = spec["icon"]
        self.slug = spec.get("slug")
        self.kind = spec["kind"]
        w, h = sizes[self.kind]
        self.w = spec.get("width", w)
        self.h = spec.get("height", h)
        self.x = columns[spec["column"]] + spec.get("x_offset", 0)
        self.y = spec["y"]

    @property
    def big(self):
        return self.kind in ("spine", "hub")

    def side(self, side, offset=0.0):
        cx, cy = self.x + self.w / 2, self.y + self.h / 2
        return {
            "l": (self.x, cy + offset),
            "r": (self.x + self.w, cy + offset),
            "t": (cx + offset, self.y),
            "b": (cx + offset, self.y + self.h),
        }[side]


def load_icon(name):
    path = ICON_DIR / f"{name}.svg"
    if not path.is_file():
        sys.exit(f"Missing icon: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    head = re.search(r"<svg\b[^>]*>", text)
    view_box = re.search(r'viewBox="([^"]+)"', head.group(0))
    if not view_box:
        sys.exit(f"Icon has no viewBox: {path.name}")
    body = text[head.end():text.rfind("</svg>")].strip()
    prefix = "ic-" + name.lower()
    for ident in sorted(set(re.findall(r'\bid="([^"]+)"', body)), key=len, reverse=True):
        body = re.sub(rf'\bid="{re.escape(ident)}"', f'id="{prefix}-{ident}"', body)
        body = body.replace(f"url(#{ident})", f"url(#{prefix}-{ident})")
        body = body.replace(f'href="#{ident}"', f'href="#{prefix}-{ident}"')
    return view_box.group(1), body


def fmt(v):
    return f"{v:.1f}".rstrip("0").rstrip(".")


def polyline(points, colour, width, dash=None, marker=None):
    d = " ".join(f"{fmt(x)},{fmt(y)}" for x, y in points)
    attrs = f'points="{d}" fill="none" stroke="{colour}" stroke-width="{width}"'
    if dash:
        attrs += f' stroke-dasharray="{dash}"'
    if marker:
        attrs += f' marker-end="url(#{marker})"'
    return f"<polyline {attrs}/>"


class Builder:
    def __init__(self, spec):
        self.spec = spec
        self.style = spec["style"]
        self.nodes = {}
        for n in spec["nodes"]:
            if n["id"] in self.nodes:
                sys.exit(f"Duplicate node id: {n['id']}")
            self.nodes[n["id"]] = Node(n, spec["columns"], spec["sizes"])
        self.icons = {}

    # ----- anchors -------------------------------------------------------
    def anchor(self, ref):
        """'id.side[+off]' -> (x, y) on that node's side."""
        m = SIDE_RE.match(ref)
        if not m:
            sys.exit(f"Bad anchor: {ref!r} (expected id.side, e.g. slice.t-30)")
        node = self.nodes.get(m.group(1))
        if node is None:
            sys.exit(f"Unknown node in anchor: {ref!r}")
        return node.side(m.group(2), float(m.group(3) or 0))

    def resolve_points(self, items):
        """Absolute [x, y], steps [x|null, y|null] (null keeps the previous value),
        coordinates from anchors ('id.side' as a string inside a step), or a full
        anchor string."""
        pts = []
        for item in items:
            if isinstance(item, str):
                pts.append(self.anchor(item))
                continue
            x, y = item
            px, py = pts[-1] if pts else (None, None)
            if isinstance(x, str):
                x = self.anchor(x)[0]
            if isinstance(y, str):
                y = self.anchor(y)[1]
            x = px if x is None else x
            y = py if y is None else y
            if x is None or y is None:
                sys.exit(f"Point {item!r} has no previous value to keep")
            pts.append((x, y))
        return pts

    @staticmethod
    def route(a, sa, b, sb):
        """Orthogonal route between two side points."""
        (x1, y1), (x2, y2) = a, b
        if sa in "lr" and sb in "lr":
            if abs(y1 - y2) < 0.5:
                return [a, b]
            xm = (x1 + x2) / 2
            return [a, (xm, y1), (xm, y2), b]
        if sa in "tb" and sb in "tb":
            if abs(x1 - x2) < 0.5:
                return [a, b]
            ym = (y1 + y2) / 2
            return [a, (x1, ym), (x2, ym), b]
        if sa in "lr":
            return [a, (x2, y1), b]
        return [a, (x1, y2), b]

    # ----- edges ---------------------------------------------------------
    def edge_svg(self, e):
        st = self.style
        style = e["style"]
        width = st["edge_widths"][style]
        colour = st["spine_arrow"] if style in ("spine", "direct") else st["arrow"]
        marker = "cm-arrow-spine" if style in ("spine", "direct") else "cm-arrow"
        dash = st["dash"] if style == "dashed" else None
        arrow = e.get("arrow", True)
        out = []
        if "points" in e:
            pts = self.resolve_points(e["points"])
        else:
            fa, ta = e["from"], e["to"]
            pts = self.route(self.anchor(fa), SIDE_RE.match(fa).group(2), self.anchor(ta), SIDE_RE.match(ta).group(2))
        out.append(polyline(pts, colour, width, dash, marker if arrow else None))
        fan = e.get("fan_out")
        if fan:
            trunk_x = pts[-1][0]
            for target in fan["targets"]:
                node = self.nodes.get(target)
                if node is None:
                    sys.exit(f"Unknown fan_out target: {target}")
                tx, ty = node.side(fan["side"])
                out.append(polyline([(trunk_x, ty), (tx, ty)], colour, width, dash, marker))
        return "\n".join(out)

    # ----- nodes ---------------------------------------------------------
    def node_svg(self, n):
        st = self.style
        big = n.big
        stroke = st["spine_stroke"] if big else st["satellite_stroke"]
        sw = st["spine_stroke_width"] if big else st["satellite_stroke_width"]
        fs = st["font_spine"] if big else st["font_satellite"]
        fw = 700 if big else 500
        isz = st["icon_spine"] if big else st["icon_satellite"]
        ix = n.x + (12 if big else 10)
        iy = n.y + (n.h - isz) / 2
        tx = ix + isz + (10 if big else 8)
        ty = n.y + n.h / 2 + fs * 0.36
        if n.icon not in self.icons:
            self.icons[n.icon] = load_icon(n.icon)
        view_box, body = self.icons[n.icon]
        label = html.escape(n.label)
        parts = [
            f'<rect class="box" x="{fmt(n.x)}" y="{fmt(n.y)}" width="{fmt(n.w)}" height="{fmt(n.h)}" rx="{st["radius"]}" '
            f'fill="{st["fill"]}" stroke="{stroke}" stroke-width="{sw}"/>',
            f'<svg x="{fmt(ix)}" y="{fmt(iy)}" width="{isz}" height="{isz}" viewBox="{view_box}" aria-hidden="true">{body}</svg>',
            f'<text class="nm" x="{fmt(tx)}" y="{fmt(ty)}" font-size="{fs}" font-weight="{fw}" fill="{st["text"]}">{label}</text>',
        ]
        inner = "\n  ".join(parts)
        if n.slug:
            page = COMPONENT_DIR / f"{n.slug}.md"
            if not page.is_file():
                sys.exit(f"Node {n.id}: no component page {page.relative_to(ROOT)}")
            href = "{{ '/docs/components/" + n.slug + "/' | relative_url }}"
            return f'<a href="{href}">\n  <title>{label}</title>\n  {inner}\n</a>'
        return f'<g>\n  <title>{label}</title>\n  {inner}\n</g>'

    # ----- document ------------------------------------------------------
    def build(self):
        st = self.style
        w, h = self.spec["canvas"]["width"], self.spec["canvas"]["height"]
        title = html.escape(self.spec["title"])
        edges = [self.edge_svg(e) for e in self.spec["edges"]]
        nodes = [self.node_svg(n) for n in self.nodes.values()]
        lines = [
            "{% comment %}Generated by tools/component_map/build_component_map.py from component-map.json. Do not edit by hand.{% endcomment %}",
            f'<svg id="component-map" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" role="img" aria-labelledby="component-map-title">',
            f'<title id="component-map-title">{title}</title>',
            "<style>",
            "#component-map { font-family: inherit; }",
            f"#component-map a:hover .box, #component-map a:focus-visible .box {{ stroke: {st['hover']}; stroke-width: 2.4; }}",
            f"#component-map a:hover .nm, #component-map a:focus-visible .nm {{ fill: {st['hover']}; }}",
            "</style>",
            "<defs>",
            f'<marker id="cm-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="{st["arrow"]}"/></marker>',
            f'<marker id="cm-arrow-spine" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="{st["spine_arrow"]}"/></marker>',
            "</defs>",
            '<g class="edges">',
            *edges,
            "</g>",
            '<g class="nodes">',
            *nodes,
            "</g>",
            "</svg>",
        ]
        text = "\n".join(lines) + "\n"
        ids = re.findall(r'\bid="([^"]+)"', text)
        dupes = sorted({i for i in ids if ids.count(i) > 1})
        if dupes:
            sys.exit(f"Duplicate ids in output: {dupes}")
        return text


def main():
    spec = json.loads(SPEC.read_text(encoding="utf-8"))
    text = Builder(spec).build()
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote {OUT.relative_to(ROOT)} ({len(text)} bytes, {len(spec['nodes'])} nodes, {len(spec['edges'])} edges)")


if __name__ == "__main__":
    main()
