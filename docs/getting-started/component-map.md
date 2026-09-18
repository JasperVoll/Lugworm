---
title: "Component Map"
nav_order: 2
parent: "Getting started"
description: "How the IAMLab components connect: one map of the whole plugin."
permalink: /docs/getting-started/component-map/
---

# Component Map

Every box is a component. Click one to open its page. Arrows are the wires that
carry data between them; the label on an arrow is the name of the value.

<svg id="iamlab-map" viewBox="0 0 640 890" role="img"
     aria-label="Map of all IAMLab Slicer components and the wires between them"
     xmlns="http://www.w3.org/2000/svg">
<title>IAMLab Slicer component map</title>
<style>
#iamlab-map { width: 100%; height: auto; display: block; font-family: inherit; margin: 1.5rem 0; }
#iamlab-map .band { fill: #f8f9fa; stroke: #e9ecef; stroke-width: 1; }
#iamlab-map .bl { fill: #6c757d; font-size: 10px; font-weight: 700; letter-spacing: 0.07em; }
#iamlab-map .box { fill: #ffffff; stroke: #dee2e6; stroke-width: 1.2; }
#iamlab-map .box-d { fill: #ffffff; stroke: #ced4da; stroke-width: 1.2; stroke-dasharray: 4 3; }
#iamlab-map .nm { fill: #12334f; font-size: 14px; font-weight: 600; }
#iamlab-map .pn { fill: #adb5bd; font-size: 9px; font-weight: 700; text-anchor: end; }
#iamlab-map .con { fill: none; stroke: #52606d; stroke-width: 1.6; }
#iamlab-map .dcon { fill: none; stroke: #8b98a5; stroke-width: 1.5; stroke-dasharray: 5 4; }
#iamlab-map .wl { fill: #6c757d; font-size: 9.5px; font-style: italic; }
#iamlab-map .ext { fill: #ffffff; stroke: #adb5bd; stroke-width: 1.2; stroke-dasharray: 5 4; }
#iamlab-map .extt { fill: #6c757d; font-size: 11px; text-anchor: middle; }
#iamlab-map .note { fill: #868e96; font-size: 10.5px; }
#iamlab-map a { cursor: pointer; }
#iamlab-map a:hover .box, #iamlab-map a:hover .box-d { stroke: #28699f; stroke-width: 2; }
#iamlab-map a:hover .nm { fill: #28699f; }
#iamlab-map a:focus-visible .box, #iamlab-map a:focus-visible .box-d { stroke: #28699f; stroke-width: 2.4; }
</style>
<defs>
  <marker id="ah" viewBox="0 0 8 8" refX="7.2" refY="4" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
    <path d="M0,0.9 L7.4,4 L0,7.1 Z" fill="#52606d"/>
  </marker>
  <marker id="ahm" viewBox="0 0 8 8" refX="7.2" refY="4" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
    <path d="M0,0.9 L7.4,4 L0,7.1 Z" fill="#8b98a5"/>
  </marker>
  <g id="i-folder" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,-4 h5 l1.5,2 h7.5 v8 h-14 z"/>
  </g>
  <g id="i-curve" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,5 h14 M-7,5 V-6"/><path d="M-6,4 C-2,4 -1,-4 5,-4"/>
  </g>
  <g id="i-bed" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,1 l4,-4 h10 l-4,4 z"/><path d="M-7,1 v3 l4,4 h10 l-4,-4 v-3"/>
  </g>
  <g id="i-robot" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-6,6 h4 M-4,6 V1 L1,-4 l5,2"/><circle cx="-4" cy="1" r="1.6"/><circle cx="1" cy="-4" r="1.6"/>
  </g>
  <g id="i-tag" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-6,-6 h6 l6,6 -6,6 -6,-6 z"/><circle cx="-2.5" cy="-2.5" r="1.1"/>
  </g>
  <g id="i-layers" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round">
    <path d="M-7,-5 h14 M-7,-1 h14 M-7,3 h14 M-7,7 h14"/>
  </g>
  <g id="i-move" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
    <path d="M0,-7 V7 M-7,0 H7"/><path d="M-2.5,-4.5 L0,-7 2.5,-4.5 M-2.5,4.5 L0,7 2.5,4.5"/>
    <path d="M-4.5,-2.5 L-7,0 -4.5,2.5 M4.5,-2.5 L7,0 4.5,2.5"/>
  </g>
  <g id="i-code" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-6,-7 h8 l4,4 v10 h-12 z"/><path d="M-2.5,0 l-2,2 2,2 M2.5,0 l2,2 -2,2"/>
  </g>
  <g id="i-save" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M0,-7 V2 M-3.5,-1.5 L0,2 3.5,-1.5"/><path d="M-7,4 v3 h14 v-3"/>
  </g>
  <g id="i-restart" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M6,-1 A6.5,6.5 0 1 1 3,-5.6"/><path d="M6.6,-6.5 V-4.6 H4.2"/>
  </g>
  <g id="i-cylinder" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <ellipse cx="0" cy="-4" rx="5.5" ry="2.2"/><path d="M-5.5,-4 V4 M5.5,-4 V4"/>
    <path d="M-5.5,4 A5.5,2.2 0 0 0 5.5,4"/>
  </g>
  <g id="i-angle" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-6,6 H7 M-6,6 L4,-5"/><path d="M-0.5,6 A6,6 0 0 0 -2.6,1.5"/>
  </g>
  <g id="i-play" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <circle cx="0" cy="0" r="6.8"/><path d="M-2,-3.4 L4,0 -2,3.4 Z"/>
  </g>
  <g id="i-bars" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,6 H7"/><path d="M-4.5,6 V-1 M0,6 V-6 M4.5,6 V2"/>
  </g>
  <g id="i-clock" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <circle cx="0" cy="0" r="6.8"/><path d="M0,-3.6 V0.4 L2.8,2.4"/>
  </g>
  <g id="i-ramp" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round">
    <rect x="-7" y="-3" width="4.6" height="6" fill="currentColor" fill-opacity="0.25" stroke="currentColor"/>
    <rect x="-2.3" y="-3" width="4.6" height="6" fill="currentColor" fill-opacity="0.55" stroke="currentColor"/>
    <rect x="2.4" y="-3" width="4.6" height="6" fill="currentColor" fill-opacity="0.9" stroke="currentColor"/>
  </g>
  <g id="i-dots" fill="currentColor" stroke="none">
    <circle cx="-5" cy="-5" r="1.5"/><circle cx="0" cy="-5" r="1.5"/><circle cx="5" cy="-5" r="1.5"/>
    <circle cx="-5" cy="0" r="1.5"/><circle cx="0" cy="0" r="1.5"/><circle cx="5" cy="0" r="1.5"/>
    <circle cx="-5" cy="5" r="1.5"/><circle cx="0" cy="5" r="1.5"/><circle cx="5" cy="5" r="1.5"/>
  </g>
  <g id="i-arm" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,5 L-2,-2 L5,-4"/><circle cx="-7" cy="5" r="1.4"/><circle cx="-2" cy="-2" r="1.4"/>
    <path d="M5,-6.5 V-1.5 M2.5,-4 H7.5"/>
  </g>
  <g id="i-files" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-6.5,-6.5 h7 l3,3 v7 h-10 z"/><path d="M-3.5,3.5 v3 h10 v-7 l-1.5,-1.5"/>
  </g>
  <g id="i-eye" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7.5,0 C-4,-5 4,-5 7.5,0 C4,5 -4,5 -7.5,0 Z"/><circle cx="0" cy="0" r="2.1"/>
  </g>
  <g id="i-terminal" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <rect x="-7" y="-5.5" width="14" height="11" rx="1.6"/><path d="M-4,-2 L-1.2,0 -4,2 M0.5,2.6 H4.2"/>
  </g>
  <g id="i-grid" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round">
    <rect x="-6.5" y="-6.5" width="13" height="13" rx="1.2"/><path d="M-6.5,-2 H6.5 M-6.5,2 H6.5 M-2,-6.5 V6.5 M2,-6.5 V6.5"/>
  </g>
  <g id="i-fit" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,6 h14 M-7,6 V-6"/><path d="M-5,4 C-1,4 1,-3 6,-4"/>
    <circle cx="-3.5" cy="3.4" r="1.2" fill="currentColor"/><circle cx="0.5" cy="0.4" r="1.2" fill="currentColor"/>
    <circle cx="4" cy="-3.4" r="1.2" fill="currentColor"/>
  </g>
  <g id="i-target" fill="none" stroke="currentColor" stroke-width="1.6">
    <circle cx="0" cy="0" r="6.8"/><circle cx="0" cy="0" r="3.6"/><circle cx="0" cy="0" r="1" fill="currentColor"/>
  </g>
  <g id="i-pareto" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,6 h14 M-7,6 V-6"/><path d="M-5,-4 C-1,-4 3,0 5,4"/>
    <circle cx="-4" cy="-3.6" r="1.2" fill="currentColor"/><circle cx="0.2" cy="-1.6" r="1.2" fill="currentColor"/>
    <circle cx="3.6" cy="2" r="1.2" fill="currentColor"/><circle cx="-1.5" cy="3.5" r="1.1"/>
  </g>
</defs>

<rect class="band" x="20" y="56" width="600" height="288" rx="10"/>
<rect class="band" x="20" y="352" width="600" height="170" rx="10"/>
<rect class="band" x="20" y="534" width="600" height="152" rx="10"/>
<rect class="band" x="20" y="698" width="600" height="152" rx="10"/>
<text class="bl" x="22" y="72">PIPELINE — 1 SLICE · 2 PLACE · 3 G-CODE</text>
<text class="bl" x="22" y="368">CHECK AND ANALYZE — 4 ROBOT · 5 ANALYZE</text>
<text class="bl" x="22" y="550">MACHINE — 7 PRINT</text>
<text class="bl" x="22" y="714">CALIBRATE A MATERIAL — 6 CALIBRATE</text>

<path class="con" marker-end="url(#ah)" d="M318,128 V146"/>
<path class="con" marker-end="url(#ah)" d="M318,196 V214"/>
<path class="con" marker-end="url(#ah)" d="M318,264 V282"/>
<path class="con" marker-end="url(#ah)" d="M208,173 H224"/>
<path class="con" marker-end="url(#ah)" d="M208,241 H224"/>
<path class="con" marker-end="url(#ah)" d="M208,309 H224"/>
<path class="con" marker-end="url(#ah)" d="M408,173 H424"/>
<path class="con" marker-end="url(#ah)" d="M408,309 H418 V241 H424"/>
<path class="con" marker-end="url(#ah)" d="M408,309 H424"/>
<path class="con" d="M318,332 V380"/>
<path class="con" d="M118,380 H518"/>
<circle cx="318" cy="380" r="2.6" fill="#52606d"/>
<circle cx="118" cy="380" r="2.6" fill="#52606d"/>
<circle cx="518" cy="380" r="2.6" fill="#52606d"/>
<path class="con" marker-end="url(#ah)" d="M118,380 V392"/>
<path class="con" marker-end="url(#ah)" d="M318,380 V392"/>
<path class="con" marker-end="url(#ah)" d="M518,380 V392"/>
<path class="con" marker-end="url(#ah)" d="M518,442 V460"/>
<path class="con" marker-end="url(#ah)" d="M608,173 H614 V487 H612"/>
<path class="con" marker-end="url(#ah)" d="M118,380 H15 V583 H22"/>
<path class="con" marker-end="url(#ah)" d="M208,583 H224"/>
<path class="con" marker-end="url(#ah)" d="M118,628 V610"/>
<path class="con" marker-end="url(#ah)" d="M208,651 H224"/>
<path class="con" marker-end="url(#ah)" d="M318,628 V616 H6 V309 H22"/>
<path class="con" marker-end="url(#ah)" d="M208,747 H224"/>
<path class="con" marker-end="url(#ah)" d="M408,747 H424"/>
<path class="con" marker-end="url(#ah)" d="M518,770 V781 H118 V788"/>
<path class="con" marker-end="url(#ah)" d="M208,815 H224"/>
<path class="dcon" marker-end="url(#ahm)" d="M408,815 H624 V136 H318 V146"/>

<text class="wl" x="324" y="142">geometry</text>
<text class="wl" x="324" y="210">contours</text>
<text class="wl" x="324" y="278">moved</text>
<text class="wl" x="212" y="168">numbers</text>
<text class="wl" x="212" y="236">bed</text>
<text class="wl" x="212" y="304">robot</text>
<text class="wl" x="412" y="168">contours</text>
<text class="wl" x="412" y="304">gcode</text>
<text class="wl" x="324" y="374">gcode</text>
<text class="wl" x="124" y="606">gcode</text>
<text class="wl" x="124" y="646">points</text>
<text class="wl" x="12" y="470" transform="rotate(-90 12 470)" text-anchor="middle">fitted L1 / L2</text>
<text class="wl" x="630" y="330" transform="rotate(-90 630 330)" text-anchor="middle">chosen settings</text>

<a href="{{ '/docs/components/iamlab-load-settings/' | relative_url }}">
<rect class="box-d" x="28" y="82" width="180" height="46" rx="8"/>
<rect x="28" y="82" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="51" cy="105" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-folder" x="51" y="105" style="color:#28699f"/>
<text class="nm" x="71" y="110">Load Settings</text><text class="pn" x="200" y="97">1</text></a>

<a href="{{ '/docs/components/iamlab-test-geometry/' | relative_url }}">
<rect class="box" x="228" y="82" width="180" height="46" rx="8"/>
<rect x="228" y="82" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="251" cy="105" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-cylinder" x="251" y="105" style="color:#a92764"/>
<text class="nm" x="271" y="110">Test Geometry</text><text class="pn" x="400" y="97">6</text></a>

<a href="{{ '/docs/components/iamlab-version/' | relative_url }}">
<rect class="box-d" x="428" y="82" width="180" height="46" rx="8"/>
<rect x="428" y="82" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="451" cy="105" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-tag" x="451" y="105" style="color:#28699f"/>
<text class="nm" x="471" y="110">Version</text><text class="pn" x="600" y="97">1</text></a>

<a href="{{ '/docs/components/iamlab-graph-mapper/' | relative_url }}">
<rect class="box" x="28" y="150" width="180" height="46" rx="8"/>
<rect x="28" y="150" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="51" cy="173" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-curve" x="51" y="173" style="color:#28699f"/>
<text class="nm" x="71" y="178">Graph Mapper</text><text class="pn" x="200" y="165">1</text></a>

<a href="{{ '/docs/components/iamlab-slice/' | relative_url }}">
<rect class="box" x="228" y="150" width="180" height="46" rx="8"/>
<rect x="228" y="150" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="251" cy="173" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-layers" x="251" y="173" style="color:#28699f"/>
<text class="nm" x="271" y="178">Slice</text><text class="pn" x="400" y="165">1</text></a>

<a href="{{ '/docs/components/iamlab-overhang/' | relative_url }}">
<rect class="box" x="428" y="150" width="180" height="46" rx="8"/>
<rect x="428" y="150" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="451" cy="173" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-angle" x="451" y="173" style="color:#a92764"/>
<text class="nm" x="471" y="178">Overhang</text><text class="pn" x="600" y="165">5</text></a>

<a href="{{ '/docs/components/iamlab-print-bed/' | relative_url }}">
<rect class="box" x="28" y="218" width="180" height="46" rx="8"/>
<rect x="28" y="218" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="51" cy="241" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-bed" x="51" y="241" style="color:#28699f"/>
<text class="nm" x="71" y="246">Print Bed</text><text class="pn" x="200" y="233">2</text></a>

<a href="{{ '/docs/components/iamlab-place/' | relative_url }}">
<rect class="box" x="228" y="218" width="180" height="46" rx="8"/>
<rect x="228" y="218" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="251" cy="241" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-move" x="251" y="241" style="color:#28699f"/>
<text class="nm" x="271" y="246">Place</text><text class="pn" x="400" y="233">2</text></a>

<a href="{{ '/docs/components/iamlab-save-g-code/' | relative_url }}">
<rect class="box" x="428" y="218" width="180" height="46" rx="8"/>
<rect x="428" y="218" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="451" cy="241" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-save" x="451" y="241" style="color:#28699f"/>
<text class="nm" x="471" y="246">Save G-code</text><text class="pn" x="600" y="233">3</text></a>

<a href="{{ '/docs/components/iamlab-robot-definition/' | relative_url }}">
<rect class="box" x="28" y="286" width="180" height="46" rx="8"/>
<rect x="28" y="286" width="4" height="46" rx="2" fill="#14773c"/>
<circle cx="51" cy="309" r="12" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-robot" x="51" y="309" style="color:#14773c"/>
<text class="nm" x="71" y="314">Robot Definition</text><text class="pn" x="200" y="301">4</text></a>

<a href="{{ '/docs/components/iamlab-g-code-generator/' | relative_url }}">
<rect class="box" x="228" y="286" width="180" height="46" rx="8"/>
<rect x="228" y="286" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="251" cy="309" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-code" x="251" y="309" style="color:#28699f"/>
<text class="nm" x="271" y="314">G-code Generator</text><text class="pn" x="400" y="301">3</text></a>

<a href="{{ '/docs/components/iamlab-g-code-restart/' | relative_url }}">
<rect class="box" x="428" y="286" width="180" height="46" rx="8"/>
<rect x="428" y="286" width="4" height="46" rx="2" fill="#28699f"/>
<circle cx="451" cy="309" r="12" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-restart" x="451" y="309" style="color:#28699f"/>
<text class="nm" x="471" y="314">G-code Restart</text><text class="pn" x="600" y="301">3</text></a>

<a href="{{ '/docs/components/iamlab-robot-simulation/' | relative_url }}">
<rect class="box" x="28" y="396" width="180" height="46" rx="8"/>
<rect x="28" y="396" width="4" height="46" rx="2" fill="#14773c"/>
<circle cx="51" cy="419" r="12" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-play" x="51" y="419" style="color:#14773c"/>
<text class="nm" x="71" y="424">Robot Simulation</text><text class="pn" x="200" y="411">4</text></a>

<a href="{{ '/docs/components/iamlab-print-stats/' | relative_url }}">
<rect class="box" x="228" y="396" width="180" height="46" rx="8"/>
<rect x="228" y="396" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="251" cy="419" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-bars" x="251" y="419" style="color:#a92764"/>
<text class="nm" x="271" y="424">Print Stats</text><text class="pn" x="400" y="411">5</text></a>

<a href="{{ '/docs/components/iamlab-interlayer-time/' | relative_url }}">
<rect class="box" x="428" y="396" width="180" height="46" rx="8"/>
<rect x="428" y="396" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="451" cy="419" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-clock" x="451" y="419" style="color:#a92764"/>
<text class="nm" x="471" y="424">Interlayer Time</text><text class="pn" x="600" y="411">5</text></a>

<a href="{{ '/docs/components/iamlab-legend/' | relative_url }}">
<rect class="box" x="428" y="464" width="180" height="46" rx="8"/>
<rect x="428" y="464" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="451" cy="487" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-ramp" x="451" y="487" style="color:#a92764"/>
<text class="nm" x="471" y="492">Legend</text><text class="pn" x="600" y="479">5</text></a>

<text class="note" x="30" y="482">Legend draws the colour key for</text>
<text class="note" x="30" y="498">Overhang and Interlayer Time.</text>

<a href="{{ '/docs/components/iamlab-machine-console/' | relative_url }}">
<rect class="box" x="28" y="560" width="180" height="46" rx="8"/>
<rect x="28" y="560" width="4" height="46" rx="2" fill="#14773c"/>
<circle cx="51" cy="583" r="12" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-terminal" x="51" y="583" style="color:#14773c"/>
<text class="nm" x="71" y="588">Machine Console</text><text class="pn" x="200" y="575">7</text></a>

<a href="{{ '/docs/components/iamlab-live-view/' | relative_url }}">
<rect class="box" x="228" y="560" width="180" height="46" rx="8"/>
<rect x="228" y="560" width="4" height="46" rx="2" fill="#14773c"/>
<circle cx="251" cy="583" r="12" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-eye" x="251" y="583" style="color:#14773c"/>
<text class="nm" x="271" y="588">Live View</text><text class="pn" x="400" y="575">7</text></a>

<a href="{{ '/docs/components/iamlab-machine-files/' | relative_url }}">
<rect class="box" x="428" y="560" width="180" height="46" rx="8"/>
<rect x="428" y="560" width="4" height="46" rx="2" fill="#14773c"/>
<circle cx="451" cy="583" r="12" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-files" x="451" y="583" style="color:#14773c"/>
<text class="nm" x="471" y="588">Machine Files</text><text class="pn" x="600" y="575">7</text></a>

<a href="{{ '/docs/components/iamlab-calib-points/' | relative_url }}">
<rect class="box" x="28" y="628" width="180" height="46" rx="8"/>
<rect x="28" y="628" width="4" height="46" rx="2" fill="#14773c"/>
<circle cx="51" cy="651" r="12" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-dots" x="51" y="651" style="color:#14773c"/>
<text class="nm" x="71" y="656">Calib Points</text><text class="pn" x="200" y="643">7</text></a>

<a href="{{ '/docs/components/iamlab-scara-fit/' | relative_url }}">
<rect class="box" x="228" y="628" width="180" height="46" rx="8"/>
<rect x="228" y="628" width="4" height="46" rx="2" fill="#14773c"/>
<circle cx="251" cy="651" r="12" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-arm" x="251" y="651" style="color:#14773c"/>
<text class="nm" x="271" y="656">Scara Fit</text><text class="pn" x="400" y="643">7</text></a>

<text class="note" x="430" y="646">Machine Files reaches the</text>
<text class="note" x="430" y="662">machine through host alone.</text>

<a href="{{ '/docs/components/iamlab-doe-design/' | relative_url }}">
<rect class="box" x="28" y="724" width="180" height="46" rx="8"/>
<rect x="28" y="724" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="51" cy="747" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-grid" x="51" y="747" style="color:#a92764"/>
<text class="nm" x="71" y="752">DoE Design</text><text class="pn" x="200" y="739">6</text></a>

<rect class="ext" x="228" y="724" width="180" height="46" rx="23"/>
<text class="extt" x="318" y="742">print the runs,</text>
<text class="extt" x="318" y="757">then measure</text>

<a href="{{ '/docs/components/iamlab-rsm-evaluate/' | relative_url }}">
<rect class="box" x="428" y="724" width="180" height="46" rx="8"/>
<rect x="428" y="724" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="451" cy="747" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-fit" x="451" y="747" style="color:#a92764"/>
<text class="nm" x="471" y="752">RSM Evaluate</text><text class="pn" x="600" y="739">6</text></a>

<a href="{{ '/docs/components/iamlab-desirability/' | relative_url }}">
<rect class="box" x="28" y="792" width="180" height="46" rx="8"/>
<rect x="28" y="792" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="51" cy="815" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-target" x="51" y="815" style="color:#a92764"/>
<text class="nm" x="71" y="820">Desirability</text><text class="pn" x="200" y="807">6</text></a>

<a href="{{ '/docs/components/iamlab-pareto-select/' | relative_url }}">
<rect class="box" x="228" y="792" width="180" height="46" rx="8"/>
<rect x="228" y="792" width="4" height="46" rx="2" fill="#a92764"/>
<circle cx="251" cy="815" r="12" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-pareto" x="251" y="815" style="color:#a92764"/>
<text class="nm" x="271" y="820">Pareto Select</text><text class="pn" x="400" y="807">6</text></a>

<text class="note" x="430" y="810">The chosen recipe feeds back</text>
<text class="note" x="430" y="826">into Slice and G-code Generator.</text>
</svg>

## How to read the map

- **Colour** is the panel on the IAMLab ribbon. Blue is the slicing pipeline
  (`1 Slice`, `2 Place`, `3 G-code`), green is the machine side (`4 Robot`,
  `7 Print`), magenta is measurement and experiment (`5 Analyze`,
  `6 Calibrate`). The small number in each corner is the panel itself.
- **The column down the middle** is the path every print takes: geometry into
  Slice, contours into Place, placed layers into G-code Generator. The
  components on the left supply it — numbers from Graph Mapper, the plate from
  Print Bed, the machine from Robot Definition.
- **The horizontal line under G-code Generator** is one output, `gcode`, going
  to several places at once. Save G-code writes it to disk, G-code Restart
  rebuilds it after a failure, Robot Simulation and Print Stats and Interlayer
  Time read it, and Machine Console sends it to the machine.
- **Dashed boxes** are document helpers. Load Settings points the document at a
  folder of saved settings and Version reports the installed build; neither sits
  in the wire path of a print.
- **The dashed arrow up the right edge** closes the calibration loop. Plan runs
  with DoE Design, print and measure them, fit the results with RSM Evaluate,
  choose a recipe with Desirability and Pareto Select, and feed that recipe back
  into Slice and G-code Generator.

Robot Definition supplies the coordinate frame, kinematics, firmware dialect and
extrusion process. Its `robot` output goes to G-code Generator as drawn, and
also to Robot Simulation, Print Stats, Machine Console and Live View. Print Bed
likewise defines the plate once for Place, Robot Simulation, Live View and
Calib Points. Select `none` on Robot Definition for a model-free preview.

Components with a compute button run only when the button is clicked. Read every
`info` output after computing, and do not flatten Slice's contour tree: the
branches identify the layers.
