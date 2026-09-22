---
title: "Getting started"
nav_order: 2
description: "Install Lugworm and create your first toolpath preview."
permalink: /docs/getting-started/
has_children: true
has_toc: false
---

# Getting started

Start with a simple shape and preview its toolpath in Rhino. This exercise takes you through the core components before you connect your Duet robot directly.

## Install and open the plugin

Follow [Installation]({{ '/docs/getting-started/installation/' | relative_url }}), then open Grasshopper and find the **Lugworm** tab. Its numbered groups follow the work from slicing to printing. The [Component Map]({{ '/docs/getting-started/component-map/' | relative_url }}) shows where each component fits.

## 1. Make your first toolpath

Use a Rhino document in millimetres for this example. Add the components below by searching for their full names, including **Lugworm**.

1. **Create a sample.** Add [Test Geometry]({{ '/docs/components/test-geometry/' | relative_url }}) and choose a cylinder. Connect its geometry output to `geo` on [Slice]({{ '/docs/components/slice/' | relative_url }}).
2. **Slice it.** Set `mode` to `0` for planar layers and choose a `layerHeight`. Click Slice's compute button, then connect a Panel to `info` and read the report.
3. **Place the layers.** Connect Slice's `contours` to `contours` on [Place]({{ '/docs/components/place/' | relative_url }}). Set `printCenter` to the point where you want the part's bottom centre.
4. **Choose a preview profile.** Add [Robot Definition]({{ '/docs/components/robot-definition/' | relative_url }}) and select `none`. Connect its profile to the `robot` inputs on G-code Generator and Robot Simulation. This gives you a toolpath preview without an arm model.
5. **Generate G-code.** Connect Place's `moved` to `crvs` on [G-code Generator]({{ '/docs/components/g-code-generator/' | relative_url }}). Match its `layerHeight` to Slice and set the layer `width`. Leave `savePath` empty for this preview. Click compute and read `info`.
6. **Review the result.** Connect Generator's `gcode` to [Robot Simulation]({{ '/docs/components/robot-simulation/' | relative_url }}). Move `sim` from `0` to `1` to inspect the extrusion and travel moves.

You should now see a placed, layered cylinder and be able to step through its toolpath. These settings are a preview exercise; choose your machine profile and calibrate material settings before printing.

## 2. Working with the components

- **Compute buttons:** Slice and G-code Generator run when you click compute. Recompute after changing their inputs, and read their `info` outputs.
- **Optional inputs:** right-click a component to enable the input groups you need. Each component reference lists those groups.
- **Layer trees:** keep the branches from Slice through Place to Generator. For multiple objects, branches use `{object; layer}`. Flattening removes that grouping.
- **Machine profiles:** connect Robot Definition explicitly. An unwired Generator uses the laboratory SCARA frame, including mirrored X/Y axes.

## Continue from here

Use [Save G-code]({{ '/docs/components/save-g-code/' | relative_url }}) to write a reviewed file. Then choose a [workflow]({{ '/docs/workflows/' | relative_url }}) for material calibration, non-planar slicing, or machine control. For individual inputs and outputs, open the [component reference]({{ '/docs/components/' | relative_url }}).
