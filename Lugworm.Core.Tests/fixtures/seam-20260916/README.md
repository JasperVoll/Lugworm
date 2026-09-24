# Seam telemetry regression

Source: `20260916-120054-49bee0`, captured 2026-09-16 in the operator's Logs folder.
Original `job.gcode` SHA-256: `382ca39b23accf165aacaa345702c33701974c185880ca5dce40ac035fcf787a`.

- `first-two-layers.gcode`: original lines up to (excluding) `; layer 3/25`, including the travel leading into that layer. Line endings/encoding may differ from the archive.
- `seam.csv`: acquisitions 778–803 from `diagnostics.jsonl`, deduplicated by acquisition ID. Coordinates are the recorded machine values, not projected or substituted user positions.

At acquisitions 790–791, reported Y leaves the outside contour and reaches -480.291 mm; acquisition 792 returns. The operator confirmed the robot did not make the displayed excursions. These readings passed the former acquisition-gap check. The display persistence regression suppresses the brief off-path samples while retaining ordinary motion before and after them. The raw observations remain in the original logs.

`latest-polls.csv` contains all 204 deduplicated Processing observations dated at or after the manifest start of `20260916-122541-dc4ced` (same G-code SHA-256). Four samples, in two roughly 0.25-second episodes, are more than 5 mm off path. `latest-reference.csv` contains the union of the nearest original G0/G1 segments for those 204 samples, extracted from that archived job. Segments retain their original endpoints and are independent: never concatenate them into a polyline. This subset preserves each sample's nearest distance without storing another full copy of the job. The regression verifies four pending samples, 200 normal samples, and zero confirmed deviations.

`layer2-20260916.csv` contains acquisitions 450-544 of `20260916-124703-99cbd9` (same G-code SHA-256), deduplicated by acquisition ID, machine coordinates as recorded: the end of layer 1, the layer-2 start reported 48 mm early for one poll (460), the seam readings 8-15 mm off the contour (464, 465), a Z that reads a layer high (462), a one-axis glitch 45 mm off that lands near a later pass (511), and the Z hop into layer 3 reported at its top (540) before the machine had finished the layer (541-544). `first-two-layers.gcode` covers all of it. The follower regression (`LivePathFollowerTests`) requires: no deviation shown, no relocation, 460 deferred and corroborated by 461, 464/465/511 held, progress never running backwards, and every drawn frame on the intended path.
