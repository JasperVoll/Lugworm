;Home distal arm  (Y motor = distal joint, low-end switch)
;
; Changes from the 2023 version, with the evidence for each:
;   1. Approach distance -360 -> -310. The B range is 151.2-(-151.2) = 302.4 deg, so 310 covers
;      it with margin and 360 was 50 deg of wasted travel.
;   2. Feedrate 1000 -> 600. See homeproximal.g.
;   3. Back-off 3 -> 5 deg. The measured release angle is 0.1 deg (release_y.g, 2026-08-24).
;      This DISPROVES the leading theory for the August "pinned at the switch, needs M999"
;      symptom on this arm: 3 deg was already ample to release the switch, so the back-off was
;      not the cause. The remaining explanation is step loss at F1000. 5 deg is kept as margin.
;   4. NEW final step-off, as for the proximal arm.
;
; Ends stepped off but NOT parked - see the note in homeproximal.g.

G91                      ; relative movement
G1 H1 Y-310 F600         ; approach the low-end limit switch
G1 H2 Y5 F250            ; back off far enough to RELEASE the switch
G90                      ; absolute movement
