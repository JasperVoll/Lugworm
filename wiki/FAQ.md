# FAQ

## Why is the canvas not updating?

Slice, G-code Generator, Overhang, DoE Design, RSM Evaluate, Desirability,
G-code Restart, and Scara Fit are heavy components. Click the compute button
after changing their inputs, then read the `info` output.

## Why do connected wires produce no paths?

Check the output type and ensure the tree was not flattened or grafted
accidentally. The normal first pipeline is `Test Geometry.geometry` →
`Slice.geo` → `Slice.contours` → `Place.contours` →
`G-code Generator.crvs`. Slice accepts meshes, Breps, or surfaces; an empty or
unusable list produces a diagnostic instead of a path.

## Which robot profile should I choose?

Choose `none` for preview and file generation without kinematics. Choose a
generic Cartesian profile for a Cartesian machine, or `potterbot-scara-v4` for
the lab SCARA. Wire the same definition to G-code Generator and Robot
Simulation so the review matches the emitted program.

## Can I test machine components without hardware?

Yes, for offline portions: inspect diagnostics, use dry-run where offered, and
validate generated G-code in Robot Simulation. An offline solve does not prove
a real Duet connection. Record hardware steps as `NOT RUN` until
a controlled machine session is available; use [[Component Test Round 2026-09-08]].

**Why do some components have a compute button instead of running
automatically?**
Slice, G-code Generator, G-code Restart, Overhang, RSM Evaluate, and
Desirability are Heavy components. Slicing a real part or fitting a
model takes seconds, and Grasshopper would otherwise recompute on every
slider change. The button starts one deliberate run, and the result is
kept until the button is clicked again.

**Is a robot required?**
No. Slicing, placement, G-code generation, and preview run with no
robot selected, so the plugin works as an ordinary slicer for any
Marlin, Duet, or Klipper printer. A Robot Definition is only needed
when the program should be checked against the limits of an arm.

**Why is the G-code mirrored on my printer?**
When nothing is connected to the `robot` input, the G-code Generator
writes the axes for the SCARA arm of the lab, whose X and Y axes are
mirrored compared with an ordinary printer. Connect a
`generic-cartesian` (or `none`) Robot Definition, and the axes are
written the normal way. The `info` output always shows the convention
in use.

**Which firmware types are supported?**
Marlin, Duet RepRapFirmware, and Klipper for G-code. The `firmware`
input switches the dialect; left unwired, it follows the connected
Robot Definition's own default.

**Can the plugin drive a printer directly?**
Printers with a Duet board over WiFi, through the 7 Print components.
Every action that
moves the machine is checked first and blocked if a check fails.
Without a definition the components assume the SCARA arm of the lab; a
`generic-duet-cartesian` definition controls that printer instead.
There is no direct control for Marlin or Klipper - both are G-code
dialects only, written to file. See [[Robots and Machine Control]].

**Does the plugin make network connections on its own?**
No. Apart from the machine control components (Duet), the
only network code is the update check of the IAMLab Version component,
and that check is off by default. With `check = false` or no configured
release source, no request is made.

**Does the plugin work with documents in inches or metres?**
Yes. Geometry is converted at the boundary, numeric inputs documented
in mm stay in millimetres, and the G-code is always written in
millimetres.

**Windows uses a German locale. Are decimal commas a problem?**
No. G-code and reports always use decimal points, regardless
of the locale. CSV input files may use English or German numbers; the
format is detected per file, and a file that mixes both formats is
refused.

**Why does Overhang warn on my part at the default settings?**
The thresholds of 25° and 45° are placeholders that have not been
calibrated. Calibrate them for the material with a test print of a
stepped cone and enter the measured values.

**Is the `alternatives` output of Desirability a list of best
trade-offs?**
No. The alternatives are other good settings for the same combined
goal. Enable the `pareto` group for the list of best trade-offs between
the individual goals.

**Is the password of Machine Console stored?**
The `password` input can be stored inside the `.gh` file, where anyone
who has the file can read it. It is stored the same
way. Clear it before sharing a definition. Connection state is never
stored, and reopening a file never touches the machine.

**What should a bug report contain?**
Use the [bug template](../../issues/new/choose). Include the version
(from an IAMLab Version component), the `info` output of the failing
component, a screenshot of the error balloon, and a small `.gh` file
that reproduces the problem, with the password cleared.
