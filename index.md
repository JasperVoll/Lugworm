---
title: "Home"
nav_order: 1
description: "Slicing, material experiments, and machine control in Grasshopper."
permalink: /
---

# Lugworm

<p class="iamlab-intro">Slicing, material experiments, and machine control in Grasshopper.</p>

Named after the lugworm, which extrudes its casts one coil at a time.

Lugworm (formerly IAMLab Slicer) is a Rhino 8 plugin for clay and cementitious-material 3D printing. It was
developed at the Institute of Digital and Autonomous Construction (IDAC), Hamburg University
of Technology, for liquid deposition modelling (LDM) in a laboratory setting.

{: .warning }
> **The public package is being prepared.** For early access, contact [jasper.vollmert@tuhh.de](mailto:jasper.vollmert@tuhh.de). The [installation guide]({{ '/docs/getting-started/installation/' | relative_url }}) explains how to install the package once you have it.

<div class="iamlab-actions">
<a class="btn btn-primary" href="{{ '/docs/getting-started/' | relative_url }}">Get started</a>
<a class="btn" href="{{ '/docs/getting-started/component-map/' | relative_url }}">Explore the component map</a>
</div>

{% include figure.html src="home/overview_home" alt="Lugworm in Rhino 8: the SCARA simulation in the viewport and the slicing-to-print pipeline on the Grasshopper canvas" %}

## Find the right guide

| Task | Start here |
|---|---|
| Install the Rhino 8 Windows package | [Installation]({{ '/docs/getting-started/installation/' | relative_url }}) |
| Create and preview a first toolpath | [Getting started]({{ '/docs/getting-started/' | relative_url }}) |
| Understand how the components connect | [Component Map]({{ '/docs/getting-started/component-map/' | relative_url }}) |
| Look up inputs, outputs, or optional groups | [Component reference]({{ '/docs/components/' | relative_url }}) |
| Follow a task from start to finish | [Workflows]({{ '/docs/workflows/' | relative_url }}) |
| Report a problem or get in touch | [About]({{ '/docs/about/' | relative_url }}) |

## Safety

Lugworm generates toolpaths and can command real machinery. Review every G-code file and
robot program in [Robot Simulation]({{ '/docs/components/robot-simulation/' | relative_url }})
before running it. Keep the emergency stop within reach and stay with a running print.

The drawn robot models are approximate. Read the reach, joint-limit, and speed checks as well as
inspecting the preview. Machine-state and board-identity checks block control actions when they
fail.

## No warranty

Lugworm is provided without warranty of any kind; see the
[MIT License]({{ '/docs/license/' | relative_url }}) for the full disclaimer.

Machine control has been tested only on our own machine. Treat any other machine as untested. To
collaborate on making a machine compatible, see [About]({{ '/docs/about/' | relative_url }}).
