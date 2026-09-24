#!/usr/bin/env python3
"""Generate the Food4Rhino logo set in Lugworm/Resources/icons-src/food4rhino/.

The scene: a clay-printed wall component (layer stack with lattice infill and an
overhang, after the lab print in docs/github/_food4rhino/media) stands on a sand slab.
The lugworm's head surfaces left of it and its body waves through the sand. In the main
logo the tail climbs out on the right, hooks over the component and a brass nozzle on
the tail tip feeds a fresh clay strand into the top wall - the worm printed it, where a
real lugworm would leave its coiled cast. Drawn in the ribbon-icon language
(light-to-dark gradients, dark same-hue outline, layer lines) in sand tones.

Two kinds of output, both plain SVG (Inkscape, Illustrator, Affinity, Figma):

* ``workbench.svg`` - every part once as a master (top rows, layer "Masters") and every
  instance built from clones of those masters (layer "Instances"). Edit a master - enter
  its group and change it - and every instance that uses it follows. In Inkscape the
  clones are live; other editors import them as copies.
* ``lugworm-<instance>.svg`` - one flat, self-contained file per instance, no clones,
  one Inkscape layer per part. Use these to edit a single variant or to export.

Parts are sized in a 256-unit scene box. Worm outlines are smooth Bezier paths with few
nodes, so they can be reshaped by hand; the segment rings are clipped to the body path
(a live reference), but keep their computed positions. Rasterize with
tools/make_food4rhino_logo.ps1.

Usage::

    python tools/gen_food4rhino_logo.py
"""
import math
from pathlib import Path

OUT_DIR = Path(__file__).resolve().parent.parent / "Lugworm" / "Resources" / "icons-src" / "food4rhino"

NS = ('xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" '
      'xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" '
      'xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd"')


def f2(v):
    return f"{v:.2f}".rstrip("0").rstrip(".")


def P(p):
    return f"{f2(p[0])} {f2(p[1])}"


def poly_d(pts):
    return "M " + " L ".join(P(p) for p in pts) + " Z"


def smooth_closed_d(pts):
    """Closed Catmull-Rom spline through pts as cubic Beziers (smooth nodes)."""
    n = len(pts)
    d = [f"M {P(pts[0])}"]
    for i in range(n):
        p0, p1, p2, p3 = pts[i-1], pts[i], pts[(i+1) % n], pts[(i+2) % n]
        c1 = (p1[0] + (p2[0]-p0[0])/6, p1[1] + (p2[1]-p0[1])/6)
        c2 = (p2[0] - (p3[0]-p1[0])/6, p2[1] - (p3[1]-p1[1])/6)
        d.append(f"C {P(c1)} {P(c2)} {P(p2)}")
    return " ".join(d) + " Z"


# ---------------------------------------------------------------- palette
LINE = "#6e4b24"
SAND = ("#f0d9ae", "#e2c08a", "#c9a166")
BURROW = "#c09762"
COMP = ("#fffaf0", "#f1dfbf", "#d9bd90")
HOLE = ("#a98356", "#7d5a33")
WORM = ("#f6dcc4", "#dba882", "#b27a52")
WORM_LINE = "#5a3a1f"
INK = "#2b160a"
BRASS = ("#fdecc0", "#dcae52", "#a47526")
BRASS_LINE = "#5a3a12"
TEXT_MAIN, TEXT_SUB = "#5a3a1f", "#8a6334"
FONT = "'Segoe UI', 'Helvetica Neue', Arial, sans-serif"

# ---------------------------------------------------------------- sand slab
SAND_Y = 150.0
x0, y0, x1, y1 = 14.0, SAND_Y, 242.0, 244.0
SLAB_D = (f"M {f2(x0)} {f2(y0+8)} Q {f2(x0)} {f2(y0)} {f2(x0+8)} {f2(y0)} L {f2(x1-8)} {f2(y0)} "
          f"Q {f2(x1)} {f2(y0)} {f2(x1)} {f2(y0+8)} L {f2(x1)} {f2(y1-16)} Q {f2(x1)} {f2(y1)} {f2(x1-16)} {f2(y1)} "
          f"L {f2(x0+16)} {f2(y1)} Q {f2(x0)} {f2(y1)} {f2(x0)} {f2(y1-16)} Z")

# ---------------------------------------------------------------- component
# plan: rounded rectangle W x D; oblique projection (depth goes up and right);
# every layer shifts left by LEAN/H per unit height - the printed overhang
W, D, RC = 114.0, 48.0, 16.0
N_LAYERS, DZ = 14, 6.0
KX, KY = 0.3, 0.55
LEAN = 18.0
BEAD = 5.4
TEETH = 6
X0 = 144.0
Y0 = SAND_Y - KY*D/2
H = (N_LAYERS - 1)*DZ


def rrect(w, d, r, n=12):
    hw, hd = w/2, d/2
    pts = []
    for cx, cy, a0 in ((hw-r, -hd+r, -90), (hw-r, hd-r, 0), (-hw+r, hd-r, 90), (-hw+r, -hd+r, 180)):
        for i in range(n+1):
            a = math.radians(a0 + 90*i/n)
            pts.append((cx + r*math.cos(a), cy + r*math.sin(a)))
    return pts


def proj(x, y, z):
    return (X0 + x + KX*y - LEAN*z/H, Y0 - z - KY*y)


layers = [[proj(x, y, k*DZ) for x, y in rrect(W, D, RC)] for k in range(N_LAYERS)]
xs = [p[0] for layer in layers for p in layer]
ys = [p[1] for layer in layers for p in layer]
CBOX = (min(xs), min(ys), max(xs), max(ys))
ZTOP = H
wall = [proj(x, y, ZTOP) for x, y in rrect(W - BEAD, D - BEAD, RC - BEAD/2)]
zx0, zx1 = -W/2 + RC - 2, W/2 - RC + 2
yf, yb = -D/2 + BEAD/2, D/2 - BEAD/2
zig = [proj(zx0 + (zx1-zx0)*i/TEETH, yf if i % 2 == 0 else yb, ZTOP) for i in range(TEETH + 1)]
BEADS = [poly_d(wall), "M " + " L ".join(P(p) for p in zig)]

# painted bottom-up, each layer hides all of the one below except its front lip,
# so the stroke left visible is the layer seam
COMPONENT = (
    f'<path inkscape:label="outline" d="{" ".join(poly_d(layer) for layer in layers)}" '
    f'fill="{LINE}" stroke="{LINE}" stroke-width="6" stroke-linejoin="round"/>'
    f'<g inkscape:label="layers">'
    + "".join(f'<path inkscape:label="layer {k+1}" d="{poly_d(layer)}" fill="url(#comp)" stroke="{LINE}" '
              f'stroke-width="1" stroke-opacity="0.42"/>' for k, layer in enumerate(layers[:-1]))
    + f'</g><g inkscape:label="top lattice">'
    f'<path inkscape:label="cells" d="{poly_d(layers[-1])}" fill="url(#hole)"/>'
    f'<g fill="none" stroke-linecap="round" stroke-linejoin="round">'
    f'<g inkscape:label="bead outline" stroke="{LINE}" stroke-width="{f2(BEAD + 2.2)}">'
    + "".join(f'<path d="{b}"/>' for b in BEADS)
    + f'</g><g inkscape:label="beads" stroke="url(#comp)" stroke-width="{f2(BEAD)}">'
    + "".join(f'<path d="{b}"/>' for b in BEADS)
    + '</g></g></g>')

# ---------------------------------------------------------------- nozzle + strand
# the print tail ends NOZ_BODY + NOZ_TIP + STRAND above the right end of the top
# wall bead; the nozzle part is drawn with its top centre at the origin
WALL_PT = proj(W/2 - BEAD/2, 0.0, ZTOP)
NOZ_BODY, NOZ_TIP, STRAND = 9.5, 3.0, 9.5
TAIL_END = (WALL_PT[0], WALL_PT[1] - NOZ_BODY - NOZ_TIP - STRAND)
ex, ey = TAIL_END
NOZZLE = (
    f'<path inkscape:label="orifice" d="{poly_d([(-2.2, NOZ_BODY - 0.5), (2.2, NOZ_BODY - 0.5), (2.2, NOZ_BODY + NOZ_TIP), (-2.2, NOZ_BODY + NOZ_TIP)])}" '
    f'fill="{BRASS_LINE}" stroke="{BRASS_LINE}" stroke-width="1.2" stroke-linejoin="round"/>'
    f'<path inkscape:label="body" d="{poly_d([(-7.5, -3), (7.5, -3), (7.5, 3.2), (3.8, NOZ_BODY), (-3.8, NOZ_BODY), (-7.5, 3.2)])}" '
    f'fill="url(#brass)" stroke="{BRASS_LINE}" stroke-width="2.2" stroke-linejoin="round"/>')
# the strand starts under the tail tip; its outline stops short of the wall so the
# light core merges into the bead
STRAND_PART = (
    f'<g fill="none" stroke-linecap="round">'
    f'<path inkscape:label="strand outline" d="M {P(TAIL_END)} L {P((WALL_PT[0], WALL_PT[1] - 2.8))}" '
    f'stroke="{LINE}" stroke-width="{f2(BEAD + 2.2)}"/>'
    f'<path inkscape:label="strand" d="M {P(TAIL_END)} L {P(WALL_PT)}" stroke="url(#comp)" stroke-width="{f2(BEAD)}"/>'
    f'</g>')

# ---------------------------------------------------------------- face
# drawn for a 34-unit-wide head centred on the origin; worms scale it to fit
FACE = (
    '<ellipse inkscape:label="cheek" cx="-11" cy="4.5" rx="3.4" ry="2.1" fill="#f08c6e" opacity="0.5"/>'
    '<ellipse inkscape:label="cheek" cx="11" cy="4.5" rx="3.4" ry="2.1" fill="#f08c6e" opacity="0.5"/>'
    f'<ellipse inkscape:label="eye" cx="-6.2" cy="-2.4" rx="2.9" ry="3.8" fill="{INK}"/>'
    f'<ellipse inkscape:label="eye" cx="6.2" cy="-2.4" rx="2.9" ry="3.8" fill="{INK}"/>'
    f'<path inkscape:label="smile" d="M -5.6 4.4 Q 0 10.2 5.6 4.4" fill="none" stroke="{INK}" '
    'stroke-width="2.3" stroke-linecap="round"/>')

# ---------------------------------------------------------------- worms
def catmull_chain(ctrl, per=40):
    out = []
    ext = [ctrl[0]] + ctrl + [ctrl[-1]]
    for i in range(1, len(ext) - 2):
        p0, p1, p2, p3 = ext[i-1], ext[i], ext[i+1], ext[i+2]
        for k in range(per):
            t = k/per
            t2, t3 = t*t, t*t*t
            out.append(tuple(0.5*((2*p1[j]) + (-p0[j]+p2[j])*t + (2*p0[j]-5*p1[j]+4*p2[j]-p3[j])*t2
                                  + (-p0[j]+3*p1[j]-3*p2[j]+p3[j])*t3) for j in (0, 1)))
    out.append(ctrl[-1])
    return out


def resample(raw, step=1.0):
    seg = [math.dist(raw[i], raw[i+1]) for i in range(len(raw)-1)]
    total = sum(seg)
    pts, acc, j, tgt = [raw[0]], 0.0, 0, step
    while tgt < total:
        while acc + seg[j] < tgt:
            acc += seg[j]
            j += 1
        f = (tgt - acc)/seg[j]
        pts.append((raw[j][0] + (raw[j+1][0]-raw[j][0])*f, raw[j][1] + (raw[j+1][1]-raw[j][1])*f))
        tgt += step
    pts.append(raw[-1])
    return pts


def smoothstep(e0, e1, x):
    t = min(max((x-e0)/(e1-e0), 0), 1)
    return t*t*(3-2*t)


class Worm:
    """Tapered body along a centerline (head first), with rings and a face."""

    def __init__(self, raw, w_head, w_tail, face_rot):
        self.pts = pts = resample(raw)
        n = len(pts)
        self.S = S = [0.0]
        for i in range(1, n):
            S.append(S[-1] + math.dist(pts[i-1], pts[i]))
        L = S[-1]

        def tangent(i):
            a, b = pts[max(i-1, 0)], pts[min(i+1, n-1)]
            d = math.dist(a, b)
            return ((b[0]-a[0])/d, (b[1]-a[1])/d)

        self.T = [tangent(i) for i in range(n)]
        self.NRM = [(-t[1], t[0]) for t in self.T]
        self.WID = [w_head - (w_head-w_tail)*smoothstep(0.15*L, 0.95*L, s) for s in S]
        self.w_head, self.face_rot = w_head, face_rot
        self.L = L

    def outline(self, extra=0.0, step=8):
        pts, NRM, T = self.pts, self.NRM, self.T
        n = len(pts)
        idx = list(range(0, n-1, step))
        if n-1 - idx[-1] < step/2:
            idx.pop()
        idx.append(n-1)
        w = [self.WID[i] + extra for i in range(n)]
        left = [(pts[i][0] + NRM[i][0]*w[i]/2, pts[i][1] + NRM[i][1]*w[i]/2) for i in idx]
        right = [(pts[i][0] - NRM[i][0]*w[i]/2, pts[i][1] - NRM[i][1]*w[i]/2) for i in idx]

        def cap(i, sg):
            r = w[i]/2
            segs = max(2, round(math.pi*r/7))
            return [(pts[i][0] + sg*r*(math.cos(math.pi*k/segs)*NRM[i][0] + math.sin(math.pi*k/segs)*T[i][0]),
                     pts[i][1] + sg*r*(math.cos(math.pi*k/segs)*NRM[i][1] + math.sin(math.pi*k/segs)*T[i][1]))
                    for k in range(1, segs)]
        return smooth_closed_d(left + cap(n-1, 1) + right[::-1] + cap(0, -1))

    def rings(self):
        out, s = [], 17.0
        while s < self.L - 4:
            i = min(range(len(self.pts)), key=lambda k: abs(self.S[k]-s))
            p, nr, t, w = self.pts[i], self.NRM[i], self.T[i], self.WID[i]
            a = (p[0]+nr[0]*w/2, p[1]+nr[1]*w/2)
            b = (p[0]-nr[0]*w/2, p[1]-nr[1]*w/2)
            c = (p[0]-t[0]*3.0, p[1]-t[1]*3.0)
            out.append(f'<path d="M {P(a)} Q {P(c)} {P(b)}"/>')
            s += 6.0
        return "".join(out)

    def face_transform(self):
        hc = (self.pts[0][0] - self.T[0][0]*1.5, self.pts[0][1] - self.T[0][1]*1.5)
        return f"translate({f2(hc[0])} {f2(hc[1])}) rotate({f2(self.face_rot)}) scale({f2(self.w_head/34)})"


WORMS = {
    # head up out of the sand, trough under the component, out on the right, up and
    # hooked over the top wall down to the nozzle
    "print": Worm(catmull_chain([(44, 128), (44, 152), (54, 180), (80, 197), (114, 202), (150, 199),
                                 (186, 194), (211, 182), (222, 160), (223, 130), (222, 96), (218, 66),
                                 (ex + 28, ey + 2), (ex + 14, ey - 10), (ex + 3, ey - 8), (ex, ey)]),
                  23.0, 9.5, 6.0),
    # tail rises until its tip tucks under the component's front edge
    "touch": Worm(catmull_chain([(44, 128), (44, 152), (52, 184), (76, 206), (108, 211), (138, 202),
                                 (157, 188), (167, 171), (170, 156)]),
                  23.0, 12.0, 6.0),
    # entirely in the sand: one sine period, crest to crest
    "wave": Worm([(44 + 170*i/400, 202 - 15*math.cos(2*math.pi*i/400)) for i in range(401)],
                 23.0, 12.0, -8.0),
}


# ---------------------------------------------------------------- parts
def ref(name, transform=""):
    t = f' transform="{transform}"' if transform else ""
    return f'<use href="#m-{name}" xlink:href="#m-{name}"{t}/>'


def part(name, mode):
    """SVG body of a part. mode 'clone' draws nested parts as clones, 'inline' flat."""
    if name == "sand-fill":
        return f'<path id="sand-shape" d="{SLAB_D}" fill="url(#sand)"/>'
    if name == "sand-line":
        return f'<path d="{SLAB_D}" fill="none" stroke="{LINE}" stroke-width="3.4" stroke-linejoin="round"/>'
    if name == "component":
        return COMPONENT
    if name == "nozzle":
        return NOZZLE
    if name == "face":
        return FACE
    if name == "strand":
        return STRAND_PART
    kind, key = name.split("-", 1)
    worm = WORMS[key]
    if kind == "burrow":
        return f'<path d="{worm.outline(7.5)}" fill="{BURROW}" clip-path="url(#clip-slab)"/>'
    face = (ref("face", worm.face_transform()) if mode == "clone"
            else f'<g inkscape:label="face" transform="{worm.face_transform()}">{FACE}</g>')
    return (f'<path id="worm-{key}-body" inkscape:label="body" d="{worm.outline()}" fill="url(#worm)" '
            f'stroke="{WORM_LINE}" stroke-width="3" stroke-linejoin="round"/>'
            f'<g inkscape:label="rings" clip-path="url(#clip-worm-{key})" fill="none" stroke="{WORM_LINE}" '
            f'stroke-width="1.3" opacity="0.3">{worm.rings()}</g>{face}')


def defs():
    clips = "".join(f'<clipPath id="clip-worm-{k}"><use href="#worm-{k}-body" xlink:href="#worm-{k}-body"/></clipPath>'
                    for k in WORMS)
    return f'''<defs>
    <linearGradient id="sand" x1="0" y1="{f2(SAND_Y)}" x2="0" y2="{f2(y1)}" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{SAND[0]}"/><stop offset="0.5" stop-color="{SAND[1]}"/><stop offset="1" stop-color="{SAND[2]}"/>
    </linearGradient>
    <linearGradient id="comp" x1="{f2(CBOX[0])}" y1="{f2(CBOX[1])}" x2="{f2(CBOX[2])}" y2="{f2(CBOX[3])}" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{COMP[0]}"/><stop offset="0.5" stop-color="{COMP[1]}"/><stop offset="1" stop-color="{COMP[2]}"/>
    </linearGradient>
    <linearGradient id="hole" x1="0" y1="{f2(CBOX[1])}" x2="0" y2="{f2(CBOX[1] + KY*D)}" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{HOLE[1]}"/><stop offset="1" stop-color="{HOLE[0]}"/>
    </linearGradient>
    <linearGradient id="worm" x1="0" y1="40" x2="0" y2="236" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{WORM[0]}"/><stop offset="0.55" stop-color="{WORM[1]}"/><stop offset="1" stop-color="{WORM[2]}"/>
    </linearGradient>
    <linearGradient id="brass" x1="-7.5" y1="0" x2="7.5" y2="0" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{BRASS[1]}"/><stop offset="0.3" stop-color="{BRASS[0]}"/><stop offset="1" stop-color="{BRASS[2]}"/>
    </linearGradient>
    <clipPath id="clip-slab"><use href="#sand-shape" xlink:href="#sand-shape"/></clipPath>
    {clips}
  </defs>'''


# ---------------------------------------------------------------- instances
PRINT_SCENE = [("sand-fill", ""), ("burrow-print", ""), ("sand-line", ""), ("component", ""),
               ("strand", ""), ("worm-print", ""), ("nozzle", f"translate({f2(ex)} {f2(ey)})")]
BANNER_TEXT = (
    f'<g inkscape:label="wordmark" font-family="{FONT}">'
    f'<text x="274" y="140" font-size="52" font-weight="700" fill="{TEXT_MAIN}">Lugworm</text>'
    f'<text x="277" y="172" font-size="17" fill="{TEXT_SUB}">Paste-extrusion 3D printing</text>'
    f'<text x="277" y="194" font-size="17" fill="{TEXT_SUB}">for Grasshopper</text></g>')
cw, ch = CBOX[2] - CBOX[0], CBOX[3] - CBOX[1]
COMP_SCALE = 200/max(cw, ch)
COMP_FIT = (f"translate({f2(128 - COMP_SCALE*(CBOX[0] + cw/2))} {f2(128 - COMP_SCALE*(CBOX[1] + ch/2))}) "
            f"scale({f2(COMP_SCALE)})")

INSTANCES = [
    ("print", "main logo, nozzle prints", 256, 256, PRINT_SCENE, ""),
    ("print-bare", "no nozzle", 256, 256,
     [p for p in PRINT_SCENE if p[0] != "nozzle"], ""),
    ("touch", "tail under the component", 256, 256,
     [("sand-fill", ""), ("burrow-touch", ""), ("sand-line", ""), ("worm-touch", ""), ("component", "")], ""),
    ("wave", "worm in the sand", 256, 256,
     [("sand-fill", ""), ("burrow-wave", ""), ("sand-line", ""), ("worm-wave", ""), ("component", "")], ""),
    ("component", "icon candidate", 256, 256, [("component", COMP_FIT)], ""),
    ("banner", "16:9 with wordmark", 512, 288,
     [(n, f"translate(0 16) {t}".strip()) for n, t in PRINT_SCENE], BANNER_TEXT),
]


def instance_body(parts, mode):
    out = []
    for name, t in parts:
        if mode == "clone":
            out.append(ref(name, t))
        else:
            ta = f' transform="{t}"' if t else ""
            out.append(f'<g inkscape:groupmode="layer" inkscape:label="{name}"{ta}>{part(name, "inline")}</g>')
    return "\n  ".join(out)


def standalone(key, desc, w, h, parts, extra):
    text = f'\n  <g inkscape:groupmode="layer" inkscape:label="text">{extra}</g>' if extra else ""
    return (f'<svg {NS} width="{w}" height="{h}" viewBox="0 0 {w} {h}">\n'
            f'  <title>Lugworm - {key}</title>\n  <desc>{desc}. Generated by tools/gen_food4rhino_logo.py.</desc>\n'
            f'  {defs()}\n  {instance_body(parts, "inline")}{text}\n</svg>\n')


# workbench: masters on top, instances below, each in a labelled frame
CELL, GAP, LABEL = 256, 64, 26


def frame(x, y, w, h, label, sub=""):
    s = (f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="none" stroke="#c9c2b6" '
         f'stroke-width="1" stroke-dasharray="4 3"/>'
         f'<text x="{x}" y="{y - 9}" font-family="{FONT}" font-size="13" font-weight="600" fill="#6b6257">{label}'
         + (f'<tspan font-weight="400" fill="#9a9187"> - {sub}</tspan>' if sub else "") + '</text>')
    return s


def workbench():
    frames, masters, insts = [], [], []
    # row 0 / row 1: masters. (name, description, display transform inside the cell)
    rows = [
        [("sand-fill", "sand slab fill", ""), ("sand-line", "sand slab outline", ""),
         ("component", "printed component", ""), ("nozzle", "brass nozzle (4x)", "translate(128 112) scale(4)"),
         ("face", "face (4x)", "translate(128 128) scale(4)")],
        [("burrow-print", "burrow, print worm", ""), ("worm-print", "print worm", ""), ("strand", "fresh strand", ""),
         ("burrow-touch", "burrow, touch worm", ""), ("worm-touch", "touch worm", ""),
         ("burrow-wave", "burrow, wave worm", ""), ("worm-wave", "wave worm", "")],
    ]
    y = LABEL + 30
    for row in rows:
        for c, (name, desc, disp) in enumerate(row):
            x = GAP/2 + c*(CELL + GAP)
            frames.append(frame(x, y, CELL, CELL, f"m-{name}", desc))
            t = f"translate({x} {y}) {disp}".strip()
            masters.append(f'<g transform="{t}"><g id="m-{name}" inkscape:label="m-{name}">{part(name, "clone")}</g></g>')
        y += CELL + GAP + LABEL
    y += 30
    x = GAP/2
    row_h = 0
    for key, desc, w, h, parts, extra in INSTANCES:
        if x + w > 7*(CELL + GAP):
            x, y = GAP/2, y + row_h + GAP + LABEL
            row_h = 0
        frames.append(frame(x, y, w, h, key, desc))
        body = instance_body(parts, "clone")
        insts.append(f'<g inkscape:label="{key}" transform="translate({x} {y})">{body}{extra}</g>')
        x += w + GAP
        row_h = max(row_h, h)
    width = int(7*(CELL + GAP))
    height = int(y + row_h + GAP)
    heading = (f'<text x="{GAP/2}" y="30" font-family="{FONT}" font-size="18" font-weight="700" fill="#4a3f33">'
               'Lugworm logo workbench<tspan font-weight="400" font-size="13" fill="#8a8177">'
               '   masters on top - enter a master group and edit it; every instance below is a clone and follows</tspan></text>')
    return (f'<svg {NS} width="{width}" height="{height}" viewBox="0 0 {width} {height}">\n'
            f'  <title>Lugworm logo workbench</title>\n  {defs()}\n'
            f'  <rect width="{width}" height="{height}" fill="#faf8f4" sodipodi:insensitive="true" inkscape:label="paper"/>\n'
            f'  <g inkscape:groupmode="layer" inkscape:label="Frames and labels" sodipodi:insensitive="true">{heading}{"".join(frames)}</g>\n'
            f'  <g inkscape:groupmode="layer" inkscape:label="Masters">\n  {chr(10).join(masters)}\n  </g>\n'
            f'  <g inkscape:groupmode="layer" inkscape:label="Instances">\n  {chr(10).join(insts)}\n  </g>\n</svg>\n')


if __name__ == "__main__":
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    (OUT_DIR / "workbench.svg").write_text(workbench(), encoding="utf-8", newline="\n")
    for inst in INSTANCES:
        (OUT_DIR / f"lugworm-{inst[0]}.svg").write_text(standalone(*inst), encoding="utf-8", newline="\n")
    print(f"wrote workbench.svg and {len(INSTANCES)} instances to {OUT_DIR}")
