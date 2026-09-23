---
title: "Robot Definition"
nav_order: 18
parent: "Components"
description: "Robot Definition: component inputs and outputs."
permalink: /docs/components/robot-definition/
redirect_from:
  - /docs/components/iamlab-robot-definition/
---

# Robot Definition

Robot Definition selects the machine. The `robot` output is
a short text that other components read from their `robot` input, so
that the machine is chosen once for the whole definition. Available
profiles: `none` (no machine, toolpath only), `generic-cartesian` (an
ordinary printer), `generic-duet-cartesian` (an ordinary printer with a
Duet board), `potterbot-scara-v4` (the SCARA arm of the lab),
`generic-klipper` and `generic-marlin` (Cartesian machines whose default
G-code dialect is Klipper or Marlin; neither is controlled over the
network - generate the file and transfer it yourself).

Panel: `4 Robot`. Nickname: `Robot Def`. Compute button: no (runs on every change).

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `preset` | Text | none | Robot or machine preset. None keeps the toolpath and G-code workflows free of a model |

## Outputs

| Name | Type | Description |
|---|---|---|
| `robot` | Text | Robot definition. Wire it into the robot input of G-code Generator, Robot Simulation, Print Stats, Duet Console and Live View |
| `mechanism` | Text | Machine mechanism name, such as SCARA, Cartesian or None |
| `program` | Text | Program format supported by the selected profile |
| `control` | Text | Direct printer connection type supported by the profile (None = no direct control) |
| `info` | Text | Selected profile summary |
| `process` | Text | Material process used by the profile: paste extrusion (LDM) |
| `flavor` | Text | G-code dialect the generator uses while its firmware input is unwired |

Back to [Components]({{ '/docs/components/' | relative_url }}).
