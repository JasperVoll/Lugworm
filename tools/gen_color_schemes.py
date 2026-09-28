#!/usr/bin/env python3
"""Generate the Legend palette registry: Lugworm.Core/Analysis/ColorSchemeData.cs and the
palette table docs/reference/legend-palette-table.md.

5.0.0 cut the registry from 147 palettes to the 35 below, chosen for engineering use:
perceptual and single-hue ramps for magnitudes, diverging maps for signed values, cyclic
maps for angles, qualitative sets for classes. ``REGISTRY`` is the single source: its
order IS the palette number a user may type into the Legend's ``scheme`` input.

**Numbers are append-only.** Never reorder or remove an entry: a new palette goes at the
end with the next number, and a palette taken out keeps its line (commented) so its
number is never reused. ``tests/tooling/test_color_schemes_sync.py`` pins every number.

Sources:

* ``mpl`` - matplotlib colormaps (needs matplotlib; the installed version is recorded in
  the generated header). Continuous maps are sampled at 11 stops, Brewer maps at their
  native stop count, qualitative maps are copied exactly. ``plasma`` and ``viridis`` are
  numbered here but NOT generated: Core keeps the hand-typed ``PlasmaColorScale`` /
  ``DeviationColorScale`` anchors so Interlayer Time and Live View colours stay
  byte-identical.
* ``r`` - R ``grDevices``: ``rainbow`` is the R formula ported below, ``Okabe-Ito`` R's
  fixed table.
* ``lugworm`` - ``traffic`` (the overhang classes), owned by Core like plasma / viridis.

Usage::

    python tools/gen_color_schemes.py            # rewrite both generated files
    python tools/gen_color_schemes.py --check    # exit 1 when either file is stale

Deterministic output: same inputs, same bytes (invariant formatting, LF).
"""
from __future__ import annotations

import argparse
import colorsys
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CS_OUT = ROOT / "Lugworm.Core" / "Analysis" / "ColorSchemeData.cs"
MD_OUT = ROOT / "docs" / "reference" / "legend-palette-table.md"

# (number, name, source, kind, stop count or None for "exact list"). Number = position.
REGISTRY = [
    (0, "viridis", "core", "Sequential", None),
    (1, "plasma", "core", "Sequential", None),
    (2, "inferno", "mpl", "Sequential", 11),
    (3, "magma", "mpl", "Sequential", 11),
    (4, "cividis", "mpl", "Sequential", 11),
    (5, "turbo", "mpl", "Sequential", 11),
    (6, "Greys", "mpl", "Sequential", 9),
    (7, "Blues", "mpl", "Sequential", 9),
    (8, "Greens", "mpl", "Sequential", 9),
    (9, "Oranges", "mpl", "Sequential", 9),
    (10, "Reds", "mpl", "Sequential", 9),
    (11, "Purples", "mpl", "Sequential", 9),
    (12, "YlOrRd", "mpl", "Sequential", 9),
    (13, "YlGnBu", "mpl", "Sequential", 9),
    (14, "coolwarm", "mpl", "Diverging", 11),
    (15, "RdBu", "mpl", "Diverging", 11),
    (16, "RdYlBu", "mpl", "Diverging", 11),
    (17, "Spectral", "mpl", "Diverging", 11),
    (18, "PuOr", "mpl", "Diverging", 11),
    (19, "BrBG", "mpl", "Diverging", 11),
    (20, "PiYG", "mpl", "Diverging", 11),
    (21, "bwr", "mpl", "Diverging", 11),
    (22, "seismic", "mpl", "Diverging", 11),
    (23, "RdYlGn", "mpl", "Diverging", 11),
    (24, "twilight", "mpl", "Cyclic", 13),
    (25, "hsv", "mpl", "Cyclic", 13),
    (26, "rainbow", "r", "Cyclic", 12),
    (27, "tab10", "mpl", "Qualitative", None),
    (28, "tab20", "mpl", "Qualitative", None),
    (29, "Set1", "mpl", "Qualitative", None),
    (30, "Set2", "mpl", "Qualitative", None),
    (31, "Dark2", "mpl", "Qualitative", None),
    (32, "Paired", "mpl", "Qualitative", None),
    (33, "Okabe-Ito", "r", "Qualitative", None),
    (34, "traffic", "core", "Sequential", None),
]

# Retired names that still resolve. The first three are the pre-4.0.0 in-house ramps;
# RdPu, YlOrBr and R's gray left the registry in 5.0.0, so their aliases moved to the
# nearest kept ramp (the colours shift slightly - see UPGRADE.md).
ALIASES = {
    "Purples": ["pinks"],
    "Oranges": ["ambers"],
    "Greys": ["gray", "grays"],
}

# The facade-owned palettes, for the docs table only (Core holds the real definitions).
CORE_STOPS = {
    "traffic": ["#5AA05A", "#EB9B00", "#D22828"],
}
CORE_SOURCE = {"viridis": "mpl", "plasma": "mpl", "traffic": "lugworm"}
CORE_NOTE = {
    "viridis": "Live View deviation scale (`DeviationColorScale`)",
    "plasma": "Interlayer Time scale (`PlasmaColorScale`)",
    "magma": "Overhang colours",
    "Blues": "default",
    "traffic": "Overhang classes",
}


def _byte(v: float) -> int:
    """R's rgb() rounding: (unsigned int)(255 * v + 0.5), clamped."""
    v = min(1.0, max(0.0, v))
    return int(255.0 * v + 0.5)


def _hex(rgb) -> str:
    r, g, b = rgb
    return "#%02X%02X%02X" % (r, g, b)


def _fixed(hexes):
    return [tuple(int(h[i:i + 2], 16) for i in (1, 3, 5)) for h in hexes]


# ---------------------------------------------------------------------------------------
# matplotlib
# ---------------------------------------------------------------------------------------

def mpl_stops(name: str, n):
    from matplotlib import colormaps

    cm = colormaps[name]
    if n is None:
        return [tuple(_byte(c) for c in col[:3]) for col in cm.colors]
    return [tuple(_byte(c) for c in cm(i / (n - 1))[:3]) for i in range(n)]


def mpl_sampled(name: str, n: int):
    """Docs-only samples of a Core-owned matplotlib map (viridis, plasma)."""
    return mpl_stops(name, n)


# ---------------------------------------------------------------------------------------
# R grDevices
# ---------------------------------------------------------------------------------------

def _seq(a: float, b: float, n: int):
    if n <= 0:
        return []
    if n == 1:
        return [a]
    return [a + (b - a) * i / (n - 1) for i in range(n)]


def _hsv(h, s=1.0, v=1.0):
    return tuple(_byte(c) for c in colorsys.hsv_to_rgb(h % 1.0, s, v))


def r_rainbow(n: int, start=0.0, end=None):
    if end is None:
        end = max(1, n - 1) / n
    if n == 1:
        return [_hsv(start)]
    hs = _seq(start, end + 1 if start > end else end, n)
    return [_hsv(h) for h in hs]


R_FIXED = {
    # palette.colors(palette = "Okabe-Ito")
    "Okabe-Ito": ["#000000", "#E69F00", "#56B4E9", "#009E73", "#F0E442", "#0072B2", "#D55E00", "#CC79A7", "#999999"],
}


def r_stops(name: str, n):
    if name == "rainbow":
        return r_rainbow(n)
    return _fixed(R_FIXED[name])


# ---------------------------------------------------------------------------------------
# build
# ---------------------------------------------------------------------------------------

def schemes():
    """(number, name, source, kind, stops, aliases, generated) in number order."""
    out = []
    for expected, (number, name, source, kind, n) in enumerate(REGISTRY):
        if number != expected:
            sys.exit(f"REGISTRY numbers must be 0..N in order: {name} has {number}, expected {expected}")
        if source == "mpl":
            stops, generated = mpl_stops(name, n), True
        elif source == "r":
            stops, generated = r_stops(name, n), True
        elif name in CORE_STOPS:
            stops, generated = _fixed(CORE_STOPS[name]), False
        else:
            stops, generated = mpl_sampled(name, 11), False
        out.append((number, name, CORE_SOURCE.get(name, source), kind, stops, ALIASES.get(name, []), generated))
    names = [s[1].lower() for s in out] + [a.lower() for s in out for a in s[5]]
    dupes = sorted({n for n in names if names.count(n) > 1})
    if dupes:
        sys.exit(f"duplicate scheme names: {dupes}")
    return out


def emit_cs(mpl_version: str, table) -> str:
    lines = [
        "// <auto-generated>",
        "//   Generated by tools/gen_color_schemes.py - do not edit by hand; re-run the script.",
        f"//   Sources: matplotlib {mpl_version} colormaps; R grDevices palettes (rainbow ported,",
        "//   Okabe-Ito copied). plasma, viridis and traffic are numbered here but defined in",
        "//   Core: PlasmaColorScale and DeviationColorScale stay the byte-exact source for the",
        "//   first two.",
        "// </auto-generated>",
        "",
        "namespace Lugworm.Core.Analysis",
        "{",
        "    static partial class ColorSchemeData",
        "    {",
        f"        internal const string MatplotlibVersion = \"{mpl_version}\";",
        "",
        "        /// <summary>Every palette in number order: a palette's number is its index here.",
        "        /// Append-only - a number is never reordered, removed or reused.</summary>",
        "        internal static readonly string[] Numbered =",
        "        {",
    ]
    for number, name, *_ in table:
        lines.append(f"            \"{name}\",   // {number}")
    lines += [
        "        };",
        "",
        "        internal static readonly ColorScheme[] All =",
        "        {",
    ]
    for number, name, source, kind, stops, aliases, generated in table:
        if not generated:
            continue
        hexes = ", ".join(f"\"{_hex(s)}\"" for s in stops)
        alias = "".join(f", \"{a}\"" for a in aliases)
        lines.append(f"            new ColorScheme(\"{name}\", \"{source}\", ColorSchemeKind.{kind}, Hex({hexes}){alias}),")
    lines += [
        "        };",
        "    }",
        "}",
        "",
    ]
    return "\n".join(lines)


def emit_md(mpl_version: str, table) -> str:
    source_label = {"mpl": "matplotlib", "r": "R grDevices", "lugworm": "Lugworm"}
    lines = [
        "<!-- Generated by tools/gen_color_schemes.py - do not edit by hand; re-run the script. -->",
        "",
        "# Legend palette table",
        "",
        "Every palette the Legend's `scheme` input accepts, by number. Type the number or the",
        "name into the input (a Panel works), or pick it from the value list the `⇤ inputs`",
        "button creates. Names are case-insensitive. Numbers never change: a palette added",
        "later gets the next free number. `custom` (no number) draws the `colors` or",
        "`gradient` input instead. See [Legend palettes](legend-and-palettes.md) for when to",
        "use which kind.",
        "",
        f"Colours sampled from matplotlib {mpl_version} and R `grDevices`.",
        "",
        "| # | Name | Kind | Source | Colours | Note |",
        "|---:|---|---|---|---|---|",
    ]
    for number, name, source, kind, stops, aliases, generated in table:
        swatch = "".join(
            f"<span style=\"display:inline-block;width:12px;height:12px;background:{_hex(s)}\" title=\"{_hex(s)}\"></span>"
            for s in stops)
        note = CORE_NOTE.get(name, "")
        if aliases:
            note = (note + "; " if note else "") + "also answers to " + ", ".join(f"`{a}`" for a in aliases)
        lines.append(f"| {number} | `{name}` | {kind.lower()} | {source_label[source]} | {swatch} | {note} |")
    lines.append("")
    return "\n".join(lines)


def build():
    import matplotlib

    table = schemes()
    return emit_cs(matplotlib.__version__, table), emit_md(matplotlib.__version__, table)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true", help="compare with the committed files; exit 1 when stale")
    args = ap.parse_args(argv)

    cs, md = build()
    # docs/ stays in the private repository: the public copy carries the C# registry only.
    outputs = [(CS_OUT, cs)] + ([(MD_OUT, md)] if MD_OUT.parent.is_dir() else [])
    if args.check:
        stale = [p for p, text in outputs
                 if (p.read_text(encoding="utf-8") if p.exists() else "").replace("\r\n", "\n") != text]
        if stale:
            for p in stale:
                print(f"{p} is stale - run tools/gen_color_schemes.py", file=sys.stderr)
            return 1
        print(" and ".join(p.name for p, _ in outputs) + " up to date")
        return 0
    for p, text in outputs:
        p.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {CS_OUT} ({cs.count('new ColorScheme(')} generated schemes)"
          + (f" and {MD_OUT}" if len(outputs) > 1 else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
