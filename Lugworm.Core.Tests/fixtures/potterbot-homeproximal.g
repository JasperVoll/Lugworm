;Home proximal arm  (X motor = proximal joint, low-end switch)
;
; Changes from the 2023 version, with the evidence for each:
;   1. Approach distance -360 -> -365. The A range is 319.1-(-41.1) = 360.2 deg, so -360 fell
;      0.2 deg short when starting from the extreme.
;   2. Feedrate 1000 -> 600. F1000 = 44 218 steps/s stalled the external driver on 2026-08-20.
;      F700 = 30 953 steps/s was validated clean on 2026-08-24 (10 stressed strokes, probe returned
;      35.1 deg against 35.0 commanded). 600 sits below the validated point for margin, and homes
;      365 deg in 36.5 s. Raising to 700 is supported by the data if the extra 5 s matters.
;   3. Back-off 3 -> 5 deg. The measured release angle is 0.1 deg (release_x.g, 2026-08-24), so 3
;      was already sufficient and the old back-off was NOT the cause of the August failure. 5 is
;      kept as generous margin; it costs 1.2 s.
;   4. NEW final step-off. The old macro ended on the trigger, so the switch stayed pressed:
;      M119 read "X: at min stop" at the homed position, and the next G28 X was a zero-distance
;      move. This line is the fix.
;
; The macro deliberately ends stepped off but NOT parked. ScaraKinematics::MustBeHomedAxes
; requires BOTH X and Y homed before any move touching either, so no absolute move is legal
; here. homeall.g parks once all three axes are homed.

G91                      ; relative movement
G1 H1 X-365 F600         ; approach the low-end limit switch
G1 H2 X5 F250            ; back off far enough to RELEASE the switch
G90                      ; absolute movement
