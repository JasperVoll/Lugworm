#!/usr/bin/env python3
"""Generate docs/reference/example_doe_measurements.xlsx - the filled DoE table that
RSM Evaluate and Desirability are demonstrated on.

The sheet is a three-factor Box-Behnken design (15 runs, 3 centre points) whose three
response columns are simulated measurements: a ground-truth quadratic surface in coded
units plus Gaussian measurement noise, rounded to the precision the instrument would
actually report.

Why the responses are generated and not typed by hand
-----------------------------------------------------
The hand-typed version shipped before 2026-09-22 had ``print_time`` as whole minutes
over the range 3..9, and all three centre points read exactly 6. Two failures follow
from that:

* Identical centre replicates mean the pure-error sum of squares is 0, so the ANOVA's
  lack-of-fit F is a division by zero (reported as infinite, p = 0) on a model with
  R2 = 0.993.
* With no real noise, the residuals are pure rounding artefacts and RMSE collapses to
  the rounding step. Cook's distance is ``e^2*h / (p*MSE*(1-h)^2)``, so a +/-0.25
  rounding residual on a Box-Behnken face point (h = 0.75) reached D = 1.50 on runs 6
  and 7 - the component then warned that a single run was steering the fitted surface,
  which was an artefact of the example data, not of the fit.

The generator fixes both at the source: every response carries genuine noise that is
several times its rounding step, so the centre points differ, pure error is real, and
no run is influential.

``SEED`` is searched, and deliberately so. Cook's distance on this design is scale
invariant - a 4000-draw Monte Carlo puts P(max D > 1) at 27.5 % and P(max D > 4/n) at
90 % at every noise level tested (1x to 8x sigma), because a studentized residual does
not care about sigma. A Box-Behnken face point carries leverage 0.75, where
D = 0.3 * t_studentized^2, so one ordinary-looking run clears D = 1 whenever
|t| > 1.83. An uneventful reference table is therefore a chosen draw on ANY design this
size, not a property noise level can buy: ``SEED`` is the first seed (search over
1..60000) whose draw keeps every response at max Cook's D <= 0.45, lack-of-fit
p >= 0.15, R2 >= 0.95 and R2-pred >= 0.85. That is what makes step 3.12.7 of the final
test round meaningful - a corrupted cell stays the only thing that fires the influence
warning.

Sigma is set at roughly 3x instrument repeatability rather than the smallest value that
still looks tidy: R2 lands near 0.99 instead of 0.999, predicted-R2 near 0.97, and the
prediction intervals come out wide enough to read. The check below re-derives all of it
with the same OLS the plugin uses and fails the run if any response regresses.

Usage::

    python tools/gen_doe_example.py            # rewrite the workbook
    python tools/gen_doe_example.py --check    # exit 1 when the workbook is stale
    python tools/gen_doe_example.py --report   # print the fit the plugin will show

Deterministic output: same seed, same bytes.
"""
from __future__ import annotations

import argparse
import math
import random
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
XLSX_OUT = ROOT / "docs" / "reference" / "example_doe_measurements.xlsx"
POISSON_OUT = XLSX_OUT.with_name("example_doe_poisson.csv")
AUGMENTED_OUT = XLSX_OUT.with_name("example_doe_augmented.csv")

SEED = 2226

# ── the design ──────────────────────────────────────────────────────────────────────
# Box-Behnken for 3 factors: 12 edge points + 3 centre points, in the order
# DoeDesign.Bbd emits them.
FACTORS = [
    # name,           low,  high, number format
    ("layer_height",  2.5,  4.5,  "0.###"),
    ("print_speed",  30.0, 50.0,  "0.###"),
    ("pressure",      1.0,  1.3,  "0.###"),
]
CODED_HEADERS = ["x_lh", "x_ps", "x_pc"]

CODED_RUNS = [
    (-1, -1, 0), (-1, +1, 0), (+1, -1, 0), (+1, +1, 0),
    (-1, 0, -1), (-1, 0, +1), (+1, 0, -1), (+1, 0, +1),
    (0, -1, -1), (0, -1, +1), (0, +1, -1), (0, +1, +1),
    (0, 0, 0), (0, 0, 0), (0, 0, 0),
]

# A 2^3 plan augmented to a face-centred CCD, exactly as DoeDesign.AugmentFaceCentred
# appends it with seed -1: the 8 corners in DoeDesign.FullFactorial2 order, then one
# star point per face and 3 centre runs. The plain 2^3 aliases every pure quadratic
# with the intercept; the star block is what makes the quadratic estimable.
AUGMENTED_RUNS = [tuple(1 if (s >> j) & 1 else -1 for j in range(3)) for s in range(8)] + [
    (-1, 0, 0), (+1, 0, 0), (0, -1, 0), (0, +1, 0), (0, 0, -1), (0, 0, +1),
    (0, 0, 0), (0, 0, 0), (0, 0, 0),
]
AUGMENTED_LABELS = [f"FUL{i:02d}" for i in range(1, 9)] + [f"aug{i:02d}" for i in range(1, 10)]
# Searched like SEED (first seed over 1..60000 that clears verify() on this plan): the
# corners of a face-centred CCD carry leverage near 0.7, so an ordinary draw puts one
# run above Cook's D = 1 more often than not.
AUGMENTED_SEED = 286

# ── ground truth ────────────────────────────────────────────────────────────────────
# Coefficients in CODED units (x in [-1, 1]), term labels as RsmFitter.BuildTerms emits
# them. Terms left out are exactly zero in the truth and are fitted against noise alone,
# which is what a real analysis looks like: pressure does not change print time.
#
#   layer_width      a pump-fed bead widens with layer height and with pressure and is
#                    stretched thinner by speed; the pressure effect is stronger at the
#                    thick end (AC), and the height effect curves (A^2).
#   surface_defects  tearing counts rise steeply with speed (B, B^2) and fall with
#                    pressure (C); more pressure also blunts the speed penalty (BC).
#   print_time       a 300 mm hollow cylinder, 150 mm across, one bead per layer:
#                    t = H*perimeter/(layer_height*speed) plus ~2.5 min of setup. The
#                    coefficients are that surface's quadratic approximation, rounded.
TRUTH = {
    "layer_width": {
        "coefs": {"1": 7.00, "A": 1.10, "B": -0.66, "C": 0.88, "AC": 0.24, "A^2": 0.35},
        "sigma": 0.15,     # mm, bead + caliper scatter over a 4 mm span
        "decimals": 2,
        "numfmt": "0.00",
    },
    "surface_defects": {
        "coefs": {"1": 195.0, "A": 8.0, "B": 55.0, "C": -122.0,
                  "BC": -12.0, "B^2": 40.0, "C^2": 10.0},
        "sigma": 18.0,     # counts, over a 70..450 span
        "decimals": 0,
        "numfmt": "0",
    },
    "print_time": {
        "coefs": {"1": 19.30, "A": -5.40, "B": -4.70, "AB": 1.40, "A^2": 1.55, "B^2": 1.15},
        "sigma": 1.05,     # min, machine pauses and priming over a 13..33 min span
        "decimals": 1,
        "numfmt": "0.0",
    },
}

# Thresholds the generated table has to clear; a regression here is a bug in the data.
MAX_COOK_D = 0.45          # the plugin warns above 1.0 and hints above 4/n
MIN_LOF_P = 0.15
MIN_R2 = 0.98
MIN_R2_PRED = 0.93


# ── model algebra (mirrors RsmFitter/RsmInference, Quadratic) ────────────────────────
def term_labels(m: int) -> list[str]:
    """Term order of RsmFitter.BuildTerms for a Quadratic model."""
    names = [chr(ord("A") + j) for j in range(m)]
    labels = ["1"] + names
    labels += [names[a] + names[b] for a in range(m - 1) for b in range(a + 1, m)]
    labels += [n + "^2" for n in names]
    return labels


def design_row(xc: tuple[float, ...]) -> list[float]:
    m = len(xc)
    row = [1.0] + list(xc)
    row += [xc[a] * xc[b] for a in range(m - 1) for b in range(a + 1, m)]
    row += [v * v for v in xc]
    return row


def inverse(a: list[list[float]]) -> list[list[float]]:
    """Gauss-Jordan with partial pivoting."""
    n = len(a)
    aug = [row[:] + [1.0 if i == j else 0.0 for j in range(n)] for i, row in enumerate(a)]
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(aug[r][col]))
        if abs(aug[piv][col]) < 1e-12:
            raise ValueError("singular normal-equation matrix")
        aug[col], aug[piv] = aug[piv], aug[col]
        d = aug[col][col]
        aug[col] = [v / d for v in aug[col]]
        for r in range(n):
            if r == col:
                continue
            f = aug[r][col]
            if f:
                aug[r] = [v - f * w for v, w in zip(aug[r], aug[col])]
    return [row[n:] for row in aug]


def fit(design: list[list[float]], y: list[float]) -> dict:
    """OLS on a full-rank design; returns the statistics the plugin reports."""
    n, p = len(design), len(design[0])
    xtx = [[sum(design[i][a] * design[i][b] for i in range(n)) for b in range(p)] for a in range(p)]
    xty = [sum(design[i][a] * y[i] for i in range(n)) for a in range(p)]
    inv = inverse(xtx)
    beta = [sum(inv[a][b] * xty[b] for b in range(p)) for a in range(p)]
    resid = [y[i] - sum(beta[a] * design[i][a] for a in range(p)) for i in range(n)]
    ss_res = sum(e * e for e in resid)
    ybar = sum(y) / n
    ss_tot = sum((v - ybar) ** 2 for v in y)
    df_res = n - p
    mse = ss_res / df_res
    hat = [sum(design[i][a] * inv[a][b] * design[i][b] for a in range(p) for b in range(p))
           for i in range(n)]
    cook = [resid[i] ** 2 * hat[i] / (p * mse * (1 - hat[i]) ** 2) for i in range(n)]
    press = sum((resid[i] / (1 - hat[i])) ** 2 for i in range(n))
    return {
        "beta": beta,
        "resid": resid,
        "hat": hat,
        "cook": cook,
        "rmse": math.sqrt(mse),
        "r2": 1 - ss_res / ss_tot,
        "r2adj": 1 - (ss_res / df_res) / (ss_tot / (n - 1)),
        "r2pred": 1 - press / ss_tot,
        "ss_res": ss_res,
        "df_res": df_res,
    }


def lack_of_fit(f: dict, keys: list[tuple], y: list[float]) -> tuple[float, float]:
    """Lack-of-fit F and its p-value from the replicated design points."""
    ss_pe, df_pe = 0.0, 0
    for key in set(keys):
        vals = [y[i] for i, k in enumerate(keys) if k == key]
        if len(vals) < 2:
            continue
        mean = sum(vals) / len(vals)
        ss_pe += sum((v - mean) ** 2 for v in vals)
        df_pe += len(vals) - 1
    df_lof = f["df_res"] - df_pe
    if df_pe <= 0 or df_lof <= 0 or ss_pe <= 0:
        return float("inf"), 0.0
    f_stat = (max(0.0, f["ss_res"] - ss_pe) / df_lof) / (ss_pe / df_pe)
    return f_stat, f_dist_sf(f_stat, df_lof, df_pe)


def f_dist_sf(x: float, d1: int, d2: int) -> float:
    """Upper tail of the F distribution via the regularised incomplete beta."""
    if x <= 0:
        return 1.0
    return betainc(d2 / 2.0, d1 / 2.0, d2 / (d2 + d1 * x))


def betacf(a: float, b: float, x: float) -> float:
    """Continued fraction for the incomplete beta, evaluated by Lentz's method
    (Numerical Recipes, betacf)."""
    tiny, eps = 1e-300, 3e-16
    c, d = 1.0, 1.0 - (a + b) * x / (a + 1)
    if abs(d) < tiny:
        d = tiny
    d = 1.0 / d
    h = d
    for m in range(1, 300):
        m2 = 2 * m
        for num in (m * (b - m) * x / ((a - 1 + m2) * (a + m2)),
                    -(a + m) * (a + b + m) * x / ((a + m2) * (a + 1 + m2))):
            d = 1.0 + num * d
            if abs(d) < tiny:
                d = tiny
            c = 1.0 + num / c
            if abs(c) < tiny:
                c = tiny
            d = 1.0 / d
            h *= d * c
        if abs(d * c - 1.0) < eps:
            break
    return h


def betainc(a: float, b: float, x: float) -> float:
    """Regularised incomplete beta I_x(a, b)."""
    if x <= 0:
        return 0.0
    if x >= 1:
        return 1.0
    if x > (a + 1) / (a + b + 2):
        return 1 - betainc(b, a, 1 - x)
    lbeta = math.lgamma(a) + math.lgamma(b) - math.lgamma(a + b)
    front = math.exp(math.log(x) * a + math.log(1 - x) * b - lbeta)
    return front * betacf(a, b, x) / a


# ── data ────────────────────────────────────────────────────────────────────────────
def actual_levels(runs=CODED_RUNS) -> list[tuple[float, ...]]:
    out = []
    for xc in runs:
        row = []
        for j, (_, low, high, _) in enumerate(FACTORS):
            centre = 0.5 * (low + high)
            row.append(centre + xc[j] * (high - centre if xc[j] > 0 else centre - low))
        out.append(tuple(row))
    return out


def measurements(runs=CODED_RUNS, seed=SEED) -> dict[str, list[float]]:
    """Ground truth plus noise, rounded to the reported precision."""
    rnd = random.Random(seed)
    labels = term_labels(len(FACTORS))
    out = {}
    for name, spec in TRUTH.items():
        values = []
        for xc in runs:
            row = design_row(tuple(float(v) for v in xc))
            truth = sum(spec["coefs"].get(lab, 0.0) * row[k] for k, lab in enumerate(labels))
            v = round(truth + rnd.gauss(0.0, spec["sigma"]), spec["decimals"])
            values.append(int(v) if spec["decimals"] == 0 else v)
        out[name] = values
    return out


def diagnostics(values: dict[str, list[float]], runs=CODED_RUNS) -> dict[str, dict]:
    design = [design_row(tuple(float(v) for v in xc)) for xc in runs]
    keys = [tuple(xc) for xc in runs]
    out = {}
    for name, y in values.items():
        y = [float(v) for v in y]
        f = fit(design, y)
        f["lof_f"], f["lof_p"] = lack_of_fit(f, keys, y)
        out[name] = f
    return out


def poisson_csv() -> str:
    """Independent count fixture; the original Gaussian workbook stays unchanged."""
    rnd = random.Random(SEED)
    values = measurements()
    lines = ["Run," + ",".join(f[0] for f in FACTORS) + "," + ",".join(TRUTH)]
    for i, (xc, actual) in enumerate(zip(CODED_RUNS, actual_levels())):
        mean = math.exp(2 + 0.5 * xc[1] - 0.8 * xc[2])
        # Product-of-uniforms sampler, exact for these small Poisson means.
        count, product = -1, 1.0
        while product > math.exp(-mean):
            count += 1
            product *= rnd.random()
        row = [f"BBD{i+1:02d}", *actual, values["layer_width"][i], count, values["print_time"][i]]
        lines.append(",".join(str(v) for v in row))
    return "\n".join(lines) + "\n"


def augmented_csv() -> str:
    """The same surface measured on the augmented 2^3 plan (17 runs, block in order)."""
    values = measurements(AUGMENTED_RUNS, AUGMENTED_SEED)
    lines = ["Run," + ",".join(f[0] for f in FACTORS) + "," + ",".join(TRUTH)]
    for i, (label, actual) in enumerate(zip(AUGMENTED_LABELS, actual_levels(AUGMENTED_RUNS))):
        row = [label, *actual, *(values[n][i] for n in TRUTH)]
        lines.append(",".join(str(v) for v in row))
    return "\n".join(lines) + "\n"


def verify(diag: dict[str, dict]) -> list[str]:
    problems = []
    for name, f in diag.items():
        worst = max(f["cook"])
        if worst > MAX_COOK_D:
            runs = [i + 1 for i, c in enumerate(f["cook"]) if c > MAX_COOK_D]
            problems.append(f"{name}: Cook's D {worst:.3f} on run(s) {runs} "
                            f"exceeds {MAX_COOK_D}")
        if f["lof_p"] < MIN_LOF_P:
            problems.append(f"{name}: lack-of-fit p {f['lof_p']:.3f} below {MIN_LOF_P}")
        if f["r2"] < MIN_R2:
            problems.append(f"{name}: R2 {f['r2']:.4f} below {MIN_R2}")
        if f["r2pred"] < MIN_R2_PRED:
            problems.append(f"{name}: R2-pred {f['r2pred']:.4f} below {MIN_R2_PRED}")
    return problems


# ── minimal xlsx writer (same part layout as Core/Doe/XlsxWriter.cs) ─────────────────
HEADER_FILL, SUB_FILL, ALT_FILL, CENTRE_FILL = "1F3864", "D6E4F7", "EBF3FB", "E2EFDA"


class Fmt:
    __slots__ = ("bold", "font_color", "fill", "numfmt")

    def __init__(self, bold=False, font_color=None, fill=None, numfmt=None):
        self.bold, self.font_color, self.fill, self.numfmt = bold, font_color, fill, numfmt

    def key(self):
        return (self.bold, self.font_color, self.fill, self.numfmt)


def esc(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def col_name(c: int) -> str:
    s = ""
    while c > 0:
        c -= 1
        s = chr(ord("A") + c % 26) + s
        c //= 26
    return s


class Styles:
    def __init__(self):
        self.fonts = ["<font><sz val=\"10\"/><name val=\"Calibri\"/></font>"]
        self.fills = ["<fill><patternFill patternType=\"none\"/></fill>",
                      "<fill><patternFill patternType=\"gray125\"/></fill>"]
        self.numfmts: list[tuple[int, str]] = []
        self.xfs = ["<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>"]
        self.index: dict[tuple, int] = {}

    def style_id(self, f: Fmt) -> int:
        font = "<font>" + ("<b/>" if f.bold else "") + "<sz val=\"10\"/>"
        if f.font_color:
            font += f'<color rgb="FF{f.font_color}"/>'
        font += "<name val=\"Calibri\"/></font>"
        if font not in self.fonts:
            self.fonts.append(font)
        font_id = self.fonts.index(font)

        fill_id = 0
        if f.fill:
            fill = ('<fill><patternFill patternType="solid">'
                    f'<fgColor rgb="FF{f.fill}"/></patternFill></fill>')
            if fill not in self.fills:
                self.fills.append(fill)
            fill_id = self.fills.index(fill)

        num_id = 0
        if f.numfmt:
            hit = [i for i, fmt in self.numfmts if fmt == f.numfmt]
            if hit:
                num_id = hit[0]
            else:
                num_id = 164 + len(self.numfmts)
                self.numfmts.append((num_id, f.numfmt))

        key = (font_id, fill_id, num_id)
        if key in self.index:
            return self.index[key]
        self.xfs.append(
            f'<xf numFmtId="{num_id}" fontId="{font_id}" fillId="{fill_id}" borderId="1" '
            f'applyNumberFormat="{1 if num_id else 0}" applyFont="1" '
            f'applyFill="{1 if fill_id else 0}" applyBorder="1">'
            '<alignment vertical="center"/></xf>')
        self.index[key] = len(self.xfs) - 1
        return self.index[key]

    def xml(self) -> str:
        out = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
               '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">']
        if self.numfmts:
            out.append(f'<numFmts count="{len(self.numfmts)}">')
            out += [f'<numFmt numFmtId="{i}" formatCode="{esc(fmt)}"/>' for i, fmt in self.numfmts]
            out.append("</numFmts>")
        out.append(f'<fonts count="{len(self.fonts)}">' + "".join(self.fonts) + "</fonts>")
        out.append(f'<fills count="{len(self.fills)}">' + "".join(self.fills) + "</fills>")
        out.append('<borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border>'
                   '<border><left style="thin"/><right style="thin"/><top style="thin"/>'
                   '<bottom style="thin"/><diagonal/></border></borders>')
        out.append('<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>'
                   "</cellStyleXfs>")
        out.append(f'<cellXfs count="{len(self.xfs)}">' + "".join(self.xfs) + "</cellXfs>")
        out.append("</styleSheet>")
        return "".join(out)


def num(v) -> str:
    """Round-trippable literal, as XlsxWriter's \"R\" format writes it."""
    if isinstance(v, int):
        return str(v)
    return repr(float(v))


def sheet_xml(styles: Styles) -> str:
    levels = actual_levels()
    values = measurements()
    responses = list(TRUTH)

    header_fmt = Fmt(bold=True, font_color="FFFFFF", fill=HEADER_FILL)
    sub_fmt = Fmt(bold=True, fill=SUB_FILL)

    out = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
           '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">']
    widths = [8] + [14] * len(FACTORS) + [10] * len(CODED_HEADERS) + [16] * len(responses)
    out.append("<cols>")
    out += [f'<col min="{i+1}" max="{i+1}" width="{w}" customWidth="1"/>'
            for i, w in enumerate(widths)]
    out.append("</cols><sheetData>")

    headers = (["Run"] + [f[0] for f in FACTORS] + CODED_HEADERS + responses)
    cells = []
    for c, text in enumerate(headers, start=1):
        fmt = sub_fmt if c > 1 + len(FACTORS) + len(CODED_HEADERS) else header_fmt
        s = styles.style_id(fmt)
        cells.append(f'<c r="{col_name(c)}1" s="{s}" t="inlineStr">'
                     f'<is><t xml:space="preserve">{esc(text)}</t></is></c>')
    out.append('<row r="1">' + "".join(cells) + "</row>")

    for i, xc in enumerate(CODED_RUNS):
        r = i + 2
        centre = all(v == 0 for v in xc)
        fill = CENTRE_FILL if centre else (ALT_FILL if i % 2 == 1 else None)
        cells = [f'<c r="A{r}" s="{styles.style_id(Fmt(bold=centre, fill=fill))}" t="inlineStr">'
                 f'<is><t xml:space="preserve">BBD{i + 1:02d}</t></is></c>']
        col = 2
        for j, (_, _, _, numfmt) in enumerate(FACTORS):
            s = styles.style_id(Fmt(fill=fill, numfmt=numfmt))
            cells.append(f'<c r="{col_name(col)}{r}" s="{s}"><v>{num(levels[i][j])}</v></c>')
            col += 1
        for j in range(len(CODED_HEADERS)):
            s = styles.style_id(Fmt(fill=fill, numfmt="0"))
            cells.append(f'<c r="{col_name(col)}{r}" s="{s}"><v>{num(int(xc[j]))}</v></c>')
            col += 1
        for name in responses:
            s = styles.style_id(Fmt(fill=fill, numfmt=TRUTH[name]["numfmt"]))
            cells.append(f'<c r="{col_name(col)}{r}" s="{s}"><v>{num(values[name][i])}</v></c>')
            col += 1
        out.append(f'<row r="{r}">' + "".join(cells) + "</row>")

    out.append("</sheetData></worksheet>")
    return "".join(out)


def workbook_parts() -> dict[str, str]:
    styles = Styles()
    sheet = sheet_xml(styles)     # interns the styles on the way
    return {
        "[Content_Types].xml":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            '<Default Extension="xml" ContentType="application/xml"/>'
            '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
            '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
            '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
            "</Types>",
        "_rels/.rels":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>'
            "</Relationships>",
        "xl/workbook.xml":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
            'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
            '<sheets><sheet name="Measurements" sheetId="1" r:id="rId1"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>'
            '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>'
            "</Relationships>",
        "xl/styles.xml": styles.xml(),
        "xl/worksheets/sheet1.xml": sheet,
    }


def write_xlsx(path: Path, parts: dict[str, str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, content in parts.items():
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o600 << 16
            z.writestr(info, content.encode("utf-8"))


def read_parts(path: Path) -> dict[str, str]:
    with zipfile.ZipFile(path) as z:
        return {n: z.read(n).decode("utf-8") for n in z.namelist()}


# ── reporting ───────────────────────────────────────────────────────────────────────
def report(values: dict[str, list[float]], diag: dict[str, dict]) -> str:
    levels = actual_levels()
    lines = [f"{'Run':<7}" + "".join(f"{f[0]:>14}" for f in FACTORS)
             + "".join(f"{n:>17}" for n in values)]
    for i in range(len(CODED_RUNS)):
        lines.append(f"BBD{i + 1:02d}  " + "".join(f"{levels[i][j]:>14g}"
                                                   for j in range(len(FACTORS)))
                     + "".join(f"{values[n][i]:>17g}" for n in values))
    lines.append("")
    for name, f in diag.items():
        lines.append(f"{name}: R2={f['r2']:.4f} R2adj={f['r2adj']:.4f} "
                     f"R2pred={f['r2pred']:.4f} RMSE={f['rmse']:.4f}  "
                     f"LoF F={f['lof_f']:.2f} p={f['lof_p']:.3f}  "
                     f"max Cook's D={max(f['cook']):.3f} (warns above 1.0)")
    return "\n".join(lines)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--check", action="store_true",
                    help="exit 1 when the workbook on disk is not what this script builds")
    ap.add_argument("--report", action="store_true", help="print the table and its fit")
    args = ap.parse_args()

    values = measurements()
    diag = diagnostics(values)
    problems = verify(diag) + ["augmented " + p for p in verify(
        diagnostics(measurements(AUGMENTED_RUNS, AUGMENTED_SEED), AUGMENTED_RUNS))]
    if problems:
        print("generated data fails its own diagnostics:", file=sys.stderr)
        for p in problems:
            print("  " + p, file=sys.stderr)
        return 1

    if args.report:
        print(report(values, diag))
        return 0

    parts = workbook_parts()
    if args.check:
        if not POISSON_OUT.exists() or POISSON_OUT.read_text(encoding="utf-8") != poisson_csv():
            print("Poisson fixture is stale - run tools/gen_doe_example.py", file=sys.stderr)
            return 1
        if not AUGMENTED_OUT.exists() or AUGMENTED_OUT.read_text(encoding="utf-8") != augmented_csv():
            print("augmented fixture is stale - run tools/gen_doe_example.py", file=sys.stderr)
            return 1
        if not XLSX_OUT.exists():
            print(f"{XLSX_OUT} is missing - run tools/gen_doe_example.py", file=sys.stderr)
            return 1
        current = read_parts(XLSX_OUT)
        stale = [n for n, c in parts.items() if current.get(n) != c]
        if stale or set(current) != set(parts):
            print(f"{XLSX_OUT} is stale ({', '.join(sorted(stale)) or 'part list differs'}) "
                  "- run tools/gen_doe_example.py", file=sys.stderr)
            return 1
        print("example_doe_measurements.xlsx is up to date")
        return 0

    write_xlsx(XLSX_OUT, parts)
    POISSON_OUT.write_text(poisson_csv(), encoding="utf-8")
    AUGMENTED_OUT.write_text(augmented_csv(), encoding="utf-8")
    print(f"wrote {XLSX_OUT}")
    print(report(values, diag))
    return 0


if __name__ == "__main__":
    sys.exit(main())
