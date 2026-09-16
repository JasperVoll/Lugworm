# Components

Every component on the IAMLab tab has a page of its own with a short explanation, a table of inputs and outputs, and notes on the inputs that need more than one line. The pages are grouped by panel below. Components with a compute button run only when the button is clicked; all others run on every change.

<!-- IMAGE TODO: ribbon screenshot with all panels expanded, or a collage of the seven panels. -->
![The IAMLab ribbon](images/components-ribbon.png)

## 1 Slice

| Component | Purpose |
|---|---|
| [[IAMLab Graph Mapper]] | IAMLab Graph Mapper changes a list of numbers by passing them through a curve that you draw on the component. |
| [[IAMLab Load Settings]] | IAMLab Load Settings points the document at a folder of saved settings files, one per component. |
| [[IAMLab Slice]] | IAMLab Slice cuts a shape into layers. Has a compute button. |
| [[IAMLab Version]] | IAMLab Version shows which version of the plugin is installed, when the build was made, and which .NET runtime Rhino loaded. |

## 2 Place

| Component | Purpose |
|---|---|
| [[IAMLab Place]] | IAMLab Place moves the sliced layers to the print position on the bed and decides where each layer starts and ends (the seam). |
| [[IAMLab Print Bed]] | IAMLab Print Bed describes the print bed once: how large the plate is, where the plate is in the Rhino document, and where a part is placed by default. |

## 3 G-code

| Component | Purpose |
|---|---|
| [[IAMLab G-code Generator]] | IAMLab G-code Generator turns the placed layers into the G-code file that the printer runs. Has a compute button. |
| [[IAMLab G-code Restart]] | IAMLab G-code Restart rebuilds a G-code file so that a print can continue after a failure. Has a compute button. |
| [[IAMLab Save G-code]] | IAMLab Save G-code writes the G-code to a file whose name contains the date and time, so that no file is overwritten by accident. |

## 4 Robot

| Component | Purpose |
|---|---|
| [[IAMLab Robot Definition]] | IAMLab Robot Definition selects the machine. |
| [[IAMLab Robot Simulation]] | IAMLab Robot Simulation shows a print before it runs. |

## 5 Analyze

| Component | Purpose |
|---|---|
| [[IAMLab Interlayer Time]] | IAMLab Interlayer Time shows, for every point of the print, how long ago the material directly underneath it was laid down. |
| [[IAMLab Legend]] | IAMLab Legend draws a legend for any data range: a continuous ramp, thresholded classes, named groups, or a size scale. |
| [[IAMLab Overhang]] | IAMLab Overhang checks how far each part of a layer hangs out over the layer below. Has a compute button. |
| [[IAMLab Print Stats]] | IAMLab Print Stats reads a G-code file, or a toolpath, and reports the printed length, the travel length, the amount of material in cubic centimetres and grams, an estimated print time, and a table with one row per layer. |

## 6 Calibrate

| Component | Purpose |
|---|---|
| [[IAMLab Desirability]] | IAMLab Desirability takes the fitted models and a goal for each measured result, and finds the combination of settings that meets all goals best. Has a compute button. |
| [[IAMLab DoE Design]] | IAMLab DoE Design plans a series of test prints. |
| [[IAMLab Pareto Select]] | IAMLab Pareto Select shows the trade-off front from Desirability and lets you pick one recipe from it by clicking; the choice stays with the document and is flagged when the front changes. |
| [[IAMLab RSM Evaluate]] | IAMLab RSM Evaluate fits a formula to the measurements of a test series. Has a compute button. |
| [[IAMLab Test Geometry]] | IAMLab Test Geometry creates simple sample shapes for calibration prints: cylinder, vase, twisted vase, frustum, wall, and box. |

## 7 Print

| Component | Purpose |
|---|---|
| [[IAMLab Calib Points]] | IAMLab Calib Points generates a grid of marking positions for the SCARA arm of the lab and drives the machine to each of them, one by one, so that a mark can be made on the bed. |
| [[IAMLab Live View]] | IAMLab Live View shows the machine while it prints: the bed plate, the arm at the real nozzle position, and the printed bead growing as the job progresses. |
| [[IAMLab Machine Console]] | IAMLab Machine Console is the one window for controlling a printer with a Duet board from Grasshopper. |
| [[IAMLab Machine Files]] | IAMLab Machine Files browses the file storage of the connected machine - the Duet's SD card. |
| [[IAMLab Scara Fit]] | IAMLab Scara Fit calculates the real arm lengths of the SCARA from the difference between the commanded and the measured mark positions of a calibration run. Has a compute button. |

## Older components

The source currently contains 31 concrete components: 25 on the seven IAMLab
ribbon panels and six hidden compatibility components. Hidden components still
load in saved Grasshopper documents, but are not the starting point for new
workflows.

IAMLab Robot from versions before 2.0 is hidden from the ribbon but still opens and works in saved files. New files should use [[IAMLab Robot Definition]] and [[IAMLab Robot Simulation]].

Since 3.0.0 the same applies to [[IAMLab Duet Connect]], [[IAMLab Duet Status]], [[IAMLab Duet Jog]], [[IAMLab Duet Terminal]] and [[IAMLab Duet Print]]: they still open and work in saved files, with the same inputs, outputs and safety checks, but they are no longer on the ribbon. New files should use [[IAMLab Machine Console]], which does everything they did in one window. Since 4.1.0 it connects to Duet machines only - Klipper survives only as a G-code dialect written to file, not a live connection. Their pages stay as a reference for old definitions.
