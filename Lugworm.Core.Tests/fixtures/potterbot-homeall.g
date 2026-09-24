;Home all
;
; Rewritten 2026-08-25 to CALL the per-axis macros instead of duplicating them inline.
; The 2023 version carried its own copy of the arm-homing block, so homeall.g and
; homeproximal.g/homedistal.g could drift apart - and had already done so.
; Now each axis has exactly one homing definition, and G28 and G28 X/Y/Z agree by construction.
;
; Z is homed FIRST and deliberately: the arm then sweeps at the top of the rail rather than
; through a tall part.
;
; The arms are homed SEQUENTIALLY, not with a combined G1 H1 X-360 Y-360. Whether this driver
; set can sustain two axes at once is untested; sequential homing costs time and removes the
; question. Revisit only with a measurement.

M98 P"0:/sys/homez.g"           ; Z first, unchanged
G91
G1 H1 X-365 Y-310 F600          ; both arms, one touch each
G1 H2 X5 Y5 F250                ; step off both switches
G90
G0 X200 Y-500 F4000             ; park
