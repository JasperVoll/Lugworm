# Legacy Machine Calibration — 30 mm Ram Extruder (RamGcodeBody)

Preserved 2026-07-14. Source: the legacy `RamGcodeBody.py` (maintainer archive)
lines ~247–262 — the field-calibrated defaults of the FIRST-generation single-component
G-code generator. They describe a **different physical machine** than the current
defaults (30 mm ram cartridge vs the 94 mm ram the C# plugin defaults to) and are the
only surviving record of that calibration.

| Parameter | Legacy 30 mm-ram value | Current C# default (94 mm ram) |
|---|---:|---:|
| layerHeight | **3.02 mm** | 2.0 mm |
| width | **7.0 mm** | 4.0 mm |
| ramDiameter | **30.0 mm** | 94.0 mm |
| pressureCoeff | **1.3** | 1.0 |
| printSpeed | **43.0 mm/s** | 20.0 mm/s |
| travelSpeed | **40.0 mm/s** | 60.0 mm/s |
| retractSpeed | **10.0 mm/s** | 2.0 mm/s |
| pressureOffset | **1.0 mm** | 0.1 mm |
| primeExtra | 0.0 | 0.0 |
| zHop | **2.0 mm** | 0.0 (→ one layer height) |
| coast / dwell / zOffset | 0.0 | 0.0 |

Notes:
- The C# retraction values were deliberately rescaled to the 94 mm ram screw
  (TESTING.md v1.5.0); do not "fix" them back.
- To print on a 30 mm-ram machine again, start from the legacy column.
- `RamGcodeBody.py` itself is no longer in this repository (maintainer's
  private archive); this document is the surviving record of its calibration.

## Legacy Python status after the 2026-07-14 cleanup

| Artifact | Status | Why |
|---|---|---|
| `Claude/RamGcodeBody.py` | removed from this repo | superseded; the calibration it carried is recorded above |
| `Claude/ClayToolkit/` (v1: ClaySlicer/ClayMover/ClayGcode + test) | removed from this repo | superseded even by v2; referenced by nothing |
| `Claude/ClayToolkit_v2/` incl. `clay_core_v2.py` | removed from this repo (kept in the maintainer's private archive) | historical parity oracle for `Lugworm.Core.Tests/fixtures/foundation.json` |
| `tests/parity/dump_foundation_fixtures.py` | kept as provenance record | documents how the parity fixture was generated; running it requires the private legacy tree |

The checked-in fixture `Lugworm.Core.Tests/fixtures/foundation.json` is the
authoritative parity baseline; it does not need regeneration to run the tests.

Known provenance gap (open question): `Lugworm.Core.Tests/fixtures/heat/*.json`
have **no in-repo generator** — they were produced ad hoc during the 2026-07-05 run
and remain frozen as-is. The newer heat-v2 tests generate their own geometry
in-code and do not depend on these files.
