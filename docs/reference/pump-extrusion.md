# Pump Extrusion Mode

Implementation: `Lugworm.Core/Extrusion/PumpModel.cs` (pure, 12 unit tests) +
the **pump** toggle group on the "Lugworm G-code" component.
Calibration placeholder: `Lugworm/Resources/pump_calibration_placeholder.csv` (and the
bucket-test workbook `pump_calibration_placeholder.xlsx` beside it). The model is
pump-agnostic: any speed-controlled screw pump with a measured setting → flow table.

## 1. What pump mode does (and does not do)

Pump mode computes, per print, the **required volumetric flow rate Q** and — once a
measured calibration table exists — the **recommended pump setting** that delivers it.
It does **not** change the emitted G-code: a screw pump is speed-controlled through its
own interface (typically an analog 0–10 V speed input), not through E-words. E-values stay in the file (harmless for a
pump-driven setup, still correct for the ram extruder).

## 2. Flow-rate model (units on every symbol)

| Symbol | Meaning | Unit |
|---|---|---|
| `h` | layer height = layer height (`layerHeight` input) | mm |
| `w` | layer width (`width` input) | mm |
| `v` | print speed while extruding (`printSpeed` input) | mm/s |
| `f_fl` | first-layer speed factor (`firstLayerSpeedFactor`) | – |
| `η` | slip factor / volumetric efficiency (`slipFactor`, default **1.0**, calibrate per material) | – |
| `Q` | required volumetric flow of fresh mortar | l/min |

Rectangular layer cross-section (same convention as the ram E-factor in `GcodeBuilder`):

```
Q [mm³/s] = h - w - v - η
Q [l/min] = Q [mm³/s] - 60 / 1e6          (1 l = 1e6 mm³, 1 min = 60 s)
Q_firstLayer = Q - f_fl
```

Hand-check (unit-tested): h=10 mm, w=30 mm, v=50 mm/s → 15 000 mm³/s = **0.9 l/min**.

The nozzle diameter input is used for sanity checks only (warning when the layer is
narrower than the nozzle bore); with a pump the flow is set by the layer geometry and
speed, not by the nozzle.

Known limitation (inherited): `h×w` is the *nominal* cross-section.
Adaptive/conical/heat slicing vary the real layer thickness; Q is then an average.

## 3. Pump setting from the calibration table

A screw pump is speed-controlled (typically analog 0–10 V). The mapping
**setting → delivered l/min depends on the material and the hose setup**, so it must be
measured, not assumed:

- The table is a CSV: `setting,flow_lpm`, `#` comments for metadata.
- `PumpCalibration.SettingForFlow(Q)` interpolates **piecewise-linearly** between
  measured points and **never extrapolates**: outside the measured flow range you get a
  warning and *no* recommendation.
- The shipped file is a placeholder full of `NEEDS_INPUT` markers: the component reports
  Q but refuses to recommend a setting until you replace it with measurements
  (unit-tested behaviour).
- Validation rejects tables with < 2 points or non-monotone flow.

No pump's rated delivery range is built in (5.0.0 removed the lab pump's 0.5–5.0 l/min
plausibility check): the measured table's own flow range is the only range, and a
required Q outside it is reported as "OUTSIDE the calibrated range" with no
recommendation. Measure across the range you print with, within your pump's datasheet.

## 4. Calibration protocol (bucket test, ~15 min)

1. Mix the material exactly as for printing. Prime pump + hose until the flow is steady.
2. Pick 4–6 settings spanning the range you will actually print with (e.g. 2/4/6/8/10 V).
3. Per setting: pump into a tared bucket for a timed 60 s. Weigh: `m` [kg].
4. Fresh density `ρ` [kg/l]: fill a 1-litre cup, strike off, weigh.
5. `flow_lpm = m / ρ / t[min]`. Enter one `setting,flow_lpm` row per point
   (ascending settings) in the CSV; keep material/date in the `#` header.
6. Wire the CSV path into the `pumpCalibCsv` input (pump toggle group). Repeat per
   material/mix — and per hose length if you change it.

`slipFactor`: after a calibrated print, compare planned vs actually deposited volume
(weigh the part): `η = V_planned / V_actual` — typically ≥ 1.0 slightly. Default 1.0.

## 5. Outputs

The `pump` output (text lines) per compute:

```
# pump mode - calibration: <file or 'embedded placeholder'>
Q required: 0.900 l/min (layer 10x30mm at 50mm/s, slip 1.00)
Q first layer: 0.450 l/min (speed factor 0.50)
pump setting: 4.30 (calibrated 2..10 → 0.50..4.80 l/min)
pump setting first layer: 2.70
WARNING: ...            (out-of-range / NEEDS_INPUT / sanity findings, if any)
```

The same warnings appear as component runtime messages, and Q/setting are appended to
the `info` output.
