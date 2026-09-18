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

<svg id="iamlab-map" viewBox="0 0 1320 676" role="img"
     aria-label="Map of all IAMLab Slicer components and the wires between them"
     xmlns="http://www.w3.org/2000/svg">
<title>IAMLab Slicer component map</title>
<style>
#iamlab-map { width: 100%; height: auto; display: block; font-family: inherit; }
#iamlab-map .band { fill: #f8f9fa; stroke: #e9ecef; stroke-width: 1; }
#iamlab-map .bl { fill: #6c757d; font-size: 9.5px; font-weight: 700; letter-spacing: 0.08em; }
#iamlab-map .box { fill: #ffffff; stroke: #dee2e6; stroke-width: 1.2; }
#iamlab-map .box-d { fill: #ffffff; stroke: #ced4da; stroke-width: 1.2; stroke-dasharray: 4 3; }
#iamlab-map .nm { fill: #12334f; font-size: 12.5px; font-weight: 600; }
#iamlab-map .pn { fill: #adb5bd; font-size: 8.5px; font-weight: 700; text-anchor: end; }
#iamlab-map .con { fill: none; stroke: #52606d; stroke-width: 1.5; }
#iamlab-map .bus { fill: none; stroke: #52606d; stroke-width: 1.5; }
#iamlab-map .dcon { fill: none; stroke: #8b98a5; stroke-width: 1.4; stroke-dasharray: 5 4; }
#iamlab-map .wl { fill: #6c757d; font-size: 8.5px; font-style: italic; }
#iamlab-map .ext { fill: #ffffff; stroke: #adb5bd; stroke-width: 1.2; stroke-dasharray: 5 4; }
#iamlab-map .extt { fill: #6c757d; font-size: 10px; text-anchor: middle; }
#iamlab-map .note { fill: #6c757d; font-size: 9.5px; }
#iamlab-map a { cursor: pointer; }
#iamlab-map a:hover .box { stroke: #28699f; stroke-width: 2; }
#iamlab-map a:hover .nm { fill: #28699f; }
#iamlab-map a:focus-visible .box { stroke: #28699f; stroke-width: 2.4; }
</style>
<defs>
  <marker id="ah" viewBox="0 0 8 8" refX="7.2" refY="4" markerWidth="6.5" markerHeight="6.5" orient="auto-start-reverse">
    <path d="M0,0.9 L7.4,4 L0,7.1 Z" fill="#52606d"/>
  </marker>
  <marker id="ahm" viewBox="0 0 8 8" refX="7.2" refY="4" markerWidth="6.5" markerHeight="6.5" orient="auto-start-reverse">
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
  <g id="i-move" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
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
  <g id="i-ramp" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round">
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
    <path d="M-6,5 L-1,-2 L6,-4"/><circle cx="-6" cy="5" r="1.5"/><circle cx="-1" cy="-2" r="1.5"/>
    <path d="M6,-6.5 V-1.5 M3.5,-4 H8.5"/>
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
  <g id="i-grid" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round">
    <rect x="-6.5" y="-6.5" width="13" height="13" rx="1.2"/><path d="M-6.5,-2 H6.5 M-6.5,2 H6.5 M-2,-6.5 V6.5 M2,-6.5 V6.5"/>
  </g>
  <g id="i-fit" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,6 h14 M-7,6 V-6"/><path d="M-5,4 C-1,4 1,-3 6,-4"/>
    <circle cx="-3.5" cy="3.4" r="1.2" fill="currentColor"/><circle cx="0.5" cy="0.4" r="1.2" fill="currentColor"/>
    <circle cx="4" cy="-3.4" r="1.2" fill="currentColor"/>
  </g>
  <g id="i-target" fill="none" stroke="currentColor" stroke-width="1.6">
    <circle cx="0" cy="0" r="6.8"/><circle cx="0" cy="0" r="3.6"/><circle cx="0" cy="0" r="1" fill="currentColor"/>
  </g>
  <g id="i-pareto" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M-7,6 h14 M-7,6 V-6"/><path d="M-5,-4 C-1,-4 3,0 5,4"/>
    <circle cx="-4" cy="-3.6" r="1.2" fill="currentColor"/><circle cx="0.2" cy="-1.6" r="1.2" fill="currentColor"/>
    <circle cx="3.6" cy="2" r="1.2" fill="currentColor"/><circle cx="-1.5" cy="3.5" r="1.1"/>
  </g>
</defs>

<rect class="band" x="8" y="58" width="1304" height="186" rx="10"/>
<rect class="band" x="8" y="254" width="1304" height="164" rx="10"/>
<rect class="band" x="8" y="426" width="1304" height="98" rx="10"/>
<rect class="band" x="8" y="534" width="1304" height="92" rx="10"/>
<text class="bl" x="20" y="74">PIPELINE — 1 SLICE · 2 PLACE · 3 G-CODE</text>
<text class="bl" x="20" y="270">CHECK AND ANALYZE — 5 ANALYZE · 4 ROBOT</text>
<text class="bl" x="20" y="440">MACHINE — 7 PRINT</text>
<text class="bl" x="20" y="548">CALIBRATE A MATERIAL — 6 CALIBRATE</text>

<path class="con" marker-end="url(#ah)" d="M317,138 V156"/>
<path class="con" marker-end="url(#ah)" d="M525,138 V156"/>
<path class="con" marker-end="url(#ah)" d="M733,138 V156"/>
<path class="con" marker-end="url(#ah)" d="M188,187 H234"/>
<path class="con" marker-end="url(#ah)" d="M396,187 H442"/>
<path class="con" marker-end="url(#ah)" d="M604,187 H650"/>
<path class="bus" d="M733,232 H1270"/>
<circle cx="733" cy="232" r="2.6" fill="#52606d"/>
<path class="con" marker-end="url(#ah)" d="M733,214 V264"/>
<path class="con" marker-end="url(#ah)" d="M941,232 V218"/>
<path class="con" marker-end="url(#ah)" d="M1149,232 V218"/>
<path class="con" marker-end="url(#ah)" d="M941,232 V268"/>
<path class="con" marker-end="url(#ah)" d="M1149,232 V268"/>
<path class="con" marker-end="url(#ah)" d="M1270,232 V469 H1232"/>
<path class="con" marker-end="url(#ah)" d="M317,214 V268"/>
<path class="con" marker-end="url(#ah)" d="M317,326 V371 H442"/>
<path class="con" marker-end="url(#ah)" d="M1149,326 V406 H525 V402"/>
<path class="con" marker-end="url(#ah)" d="M812,111 H837 V299 H816"/>
<path class="con" marker-end="url(#ah)" d="M604,469 H629 V150 H733 V142"/>
<path class="con" marker-end="url(#ah)" d="M188,469 H234"/>
<path class="con" marker-end="url(#ah)" d="M109,496 V510 H1149 V500"/>
<path class="con" marker-end="url(#ah)" d="M1070,469 H1024"/>
<path class="con" marker-end="url(#ah)" d="M396,583 H442"/>
<path class="con" marker-end="url(#ah)" d="M604,583 H650"/>
<path class="con" marker-end="url(#ah)" d="M812,583 H858"/>
<path class="con" marker-end="url(#ah)" d="M1020,583 H1066"/>
<path class="dcon" marker-end="url(#ahm)" d="M1149,610 V652 H14 V150 H317 V156"/>

<text class="wl" x="213" y="181" text-anchor="middle">geometry</text>
<text class="wl" x="419" y="181" text-anchor="middle">contours</text>
<text class="wl" x="627" y="181" text-anchor="middle">moved</text>
<text class="wl" x="325" y="152">numbers</text>
<text class="wl" x="533" y="152">bed</text>
<text class="wl" x="741" y="152">robot</text>
<text class="wl" x="745" y="227">gcode</text>
<text class="wl" x="324" y="246">contours</text>
<text class="wl" x="636" y="252">fitted L1 / L2</text>
<text class="wl" x="1258" y="360" text-anchor="end">gcode</text>
<text class="wl" x="700" y="506" text-anchor="middle">points</text>
<text class="wl" x="600" y="648" text-anchor="middle">chosen settings feed Slice and G-code Generator</text>

<a href="{{ '/docs/components/iamlab-load-settings/' | relative_url }}">
<rect class="box-d" x="30" y="84" width="158" height="54" rx="8"/>
<rect x="30" y="84" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="57" cy="111" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-folder" x="57" y="111" style="color:#28699f"/>
<text class="nm" x="78" y="115">Load Settings</text><text class="pn" x="180" y="99">1</text></a>

<a href="{{ '/docs/components/iamlab-graph-mapper/' | relative_url }}">
<rect class="box" x="238" y="84" width="158" height="54" rx="8"/>
<rect x="238" y="84" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="265" cy="111" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-curve" x="265" y="111" style="color:#28699f"/>
<text class="nm" x="286" y="115">Graph Mapper</text><text class="pn" x="388" y="99">1</text></a>

<a href="{{ '/docs/components/iamlab-print-bed/' | relative_url }}">
<rect class="box" x="446" y="84" width="158" height="54" rx="8"/>
<rect x="446" y="84" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="473" cy="111" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-bed" x="473" y="111" style="color:#28699f"/>
<text class="nm" x="494" y="115">Print Bed</text><text class="pn" x="596" y="99">2</text></a>

<a href="{{ '/docs/components/iamlab-robot-definition/' | relative_url }}">
<rect class="box" x="654" y="84" width="158" height="54" rx="8"/>
<rect x="654" y="84" width="4" height="54" rx="2" fill="#14773c"/>
<circle cx="681" cy="111" r="13" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-robot" x="681" y="111" style="color:#14773c"/>
<text class="nm" x="702" y="115">Robot Definition</text><text class="pn" x="804" y="99">4</text></a>

<a href="{{ '/docs/components/iamlab-version/' | relative_url }}">
<rect class="box-d" x="1070" y="84" width="158" height="54" rx="8"/>
<rect x="1070" y="84" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="1097" cy="111" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-tag" x="1097" y="111" style="color:#28699f"/>
<text class="nm" x="1118" y="115">Version</text><text class="pn" x="1220" y="99">1</text></a>

<a href="{{ '/docs/components/iamlab-test-geometry/' | relative_url }}">
<rect class="box" x="30" y="160" width="158" height="54" rx="8"/>
<rect x="30" y="160" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="57" cy="187" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-cylinder" x="57" y="187" style="color:#a92764"/>
<text class="nm" x="78" y="191">Test Geometry</text><text class="pn" x="180" y="175">6</text></a>

<a href="{{ '/docs/components/iamlab-slice/' | relative_url }}">
<rect class="box" x="238" y="160" width="158" height="54" rx="8"/>
<rect x="238" y="160" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="265" cy="187" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-layers" x="265" y="187" style="color:#28699f"/>
<text class="nm" x="286" y="191">Slice</text><text class="pn" x="388" y="175">1</text></a>

<a href="{{ '/docs/components/iamlab-place/' | relative_url }}">
<rect class="box" x="446" y="160" width="158" height="54" rx="8"/>
<rect x="446" y="160" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="473" cy="187" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-move" x="473" y="187" style="color:#28699f"/>
<text class="nm" x="494" y="191">Place</text><text class="pn" x="596" y="175">2</text></a>

<a href="{{ '/docs/components/iamlab-g-code-generator/' | relative_url }}">
<rect class="box" x="654" y="160" width="158" height="54" rx="8"/>
<rect x="654" y="160" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="681" cy="187" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-code" x="681" y="187" style="color:#28699f"/>
<text class="nm" x="702" y="191">G-code Generator</text><text class="pn" x="804" y="175">3</text></a>

<a href="{{ '/docs/components/iamlab-save-g-code/' | relative_url }}">
<rect class="box" x="862" y="160" width="158" height="54" rx="8"/>
<rect x="862" y="160" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="889" cy="187" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-save" x="889" y="187" style="color:#28699f"/>
<text class="nm" x="910" y="191">Save G-code</text><text class="pn" x="1012" y="175">3</text></a>

<a href="{{ '/docs/components/iamlab-g-code-restart/' | relative_url }}">
<rect class="box" x="1070" y="160" width="158" height="54" rx="8"/>
<rect x="1070" y="160" width="4" height="54" rx="2" fill="#28699f"/>
<circle cx="1097" cy="187" r="13" fill="#28699f" fill-opacity="0.1"/>
<use href="#i-restart" x="1097" y="187" style="color:#28699f"/>
<text class="nm" x="1118" y="191">G-code Restart</text><text class="pn" x="1220" y="175">3</text></a>

<a href="{{ '/docs/components/iamlab-overhang/' | relative_url }}">
<rect class="box" x="238" y="272" width="158" height="54" rx="8"/>
<rect x="238" y="272" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="265" cy="299" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-angle" x="265" y="299" style="color:#a92764"/>
<text class="nm" x="286" y="303">Overhang</text><text class="pn" x="388" y="287">5</text></a>

<a href="{{ '/docs/components/iamlab-robot-simulation/' | relative_url }}">
<rect class="box" x="654" y="272" width="158" height="54" rx="8"/>
<rect x="654" y="272" width="4" height="54" rx="2" fill="#14773c"/>
<circle cx="681" cy="299" r="13" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-play" x="681" y="299" style="color:#14773c"/>
<text class="nm" x="702" y="303">Robot Simulation</text><text class="pn" x="804" y="287">4</text></a>

<a href="{{ '/docs/components/iamlab-print-stats/' | relative_url }}">
<rect class="box" x="862" y="272" width="158" height="54" rx="8"/>
<rect x="862" y="272" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="889" cy="299" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-bars" x="889" y="299" style="color:#a92764"/>
<text class="nm" x="910" y="303">Print Stats</text><text class="pn" x="1012" y="287">5</text></a>

<a href="{{ '/docs/components/iamlab-interlayer-time/' | relative_url }}">
<rect class="box" x="1070" y="272" width="158" height="54" rx="8"/>
<rect x="1070" y="272" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="1097" cy="299" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-clock" x="1097" y="299" style="color:#a92764"/>
<text class="nm" x="1118" y="303">Interlayer Time</text><text class="pn" x="1220" y="287">5</text></a>

<a href="{{ '/docs/components/iamlab-legend/' | relative_url }}">
<rect class="box" x="446" y="344" width="158" height="54" rx="8"/>
<rect x="446" y="344" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="473" cy="371" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-ramp" x="473" y="371" style="color:#a92764"/>
<text class="nm" x="494" y="375">Legend</text><text class="pn" x="596" y="359">5</text></a>

<a href="{{ '/docs/components/iamlab-calib-points/' | relative_url }}">
<rect class="box" x="30" y="442" width="158" height="54" rx="8"/>
<rect x="30" y="442" width="4" height="54" rx="2" fill="#14773c"/>
<circle cx="57" cy="469" r="13" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-dots" x="57" y="469" style="color:#14773c"/>
<text class="nm" x="78" y="473">Calib Points</text><text class="pn" x="180" y="457">7</text></a>

<a href="{{ '/docs/components/iamlab-scara-fit/' | relative_url }}">
<rect class="box" x="238" y="442" width="158" height="54" rx="8"/>
<rect x="238" y="442" width="4" height="54" rx="2" fill="#14773c"/>
<circle cx="265" cy="469" r="13" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-arm" x="265" y="469" style="color:#14773c"/>
<text class="nm" x="286" y="473">Scara Fit</text><text class="pn" x="388" y="457">7</text></a>

<a href="{{ '/docs/components/iamlab-machine-files/' | relative_url }}">
<rect class="box" x="654" y="442" width="158" height="54" rx="8"/>
<rect x="654" y="442" width="4" height="54" rx="2" fill="#14773c"/>
<circle cx="681" cy="469" r="13" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-files" x="681" y="469" style="color:#14773c"/>
<text class="nm" x="702" y="473">Machine Files</text><text class="pn" x="804" y="457">7</text></a>

<a href="{{ '/docs/components/iamlab-live-view/' | relative_url }}">
<rect class="box" x="862" y="442" width="158" height="54" rx="8"/>
<rect x="862" y="442" width="4" height="54" rx="2" fill="#14773c"/>
<circle cx="889" cy="469" r="13" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-eye" x="889" y="469" style="color:#14773c"/>
<text class="nm" x="910" y="473">Live View</text><text class="pn" x="1012" y="457">7</text></a>

<a href="{{ '/docs/components/iamlab-machine-console/' | relative_url }}">
<rect class="box" x="1070" y="442" width="158" height="54" rx="8"/>
<rect x="1070" y="442" width="4" height="54" rx="2" fill="#14773c"/>
<circle cx="1097" cy="469" r="13" fill="#14773c" fill-opacity="0.1"/>
<use href="#i-terminal" x="1097" y="469" style="color:#14773c"/>
<text class="nm" x="1118" y="473">Machine Console</text><text class="pn" x="1220" y="457">7</text></a>

<a href="{{ '/docs/components/iamlab-doe-design/' | relative_url }}">
<rect class="box" x="238" y="556" width="158" height="54" rx="8"/>
<rect x="238" y="556" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="265" cy="583" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-grid" x="265" y="583" style="color:#a92764"/>
<text class="nm" x="286" y="587">DoE Design</text><text class="pn" x="388" y="571">6</text></a>

<rect class="ext" x="446" y="556" width="158" height="54" rx="27"/>
<text class="extt" x="525" y="579">print the runs,</text>
<text class="extt" x="525" y="594">then measure</text>

<a href="{{ '/docs/components/iamlab-rsm-evaluate/' | relative_url }}">
<rect class="box" x="654" y="556" width="158" height="54" rx="8"/>
<rect x="654" y="556" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="681" cy="583" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-fit" x="681" y="583" style="color:#a92764"/>
<text class="nm" x="702" y="587">RSM Evaluate</text><text class="pn" x="804" y="571">6</text></a>

<a href="{{ '/docs/components/iamlab-desirability/' | relative_url }}">
<rect class="box" x="862" y="556" width="158" height="54" rx="8"/>
<rect x="862" y="556" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="889" cy="583" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-target" x="889" y="583" style="color:#a92764"/>
<text class="nm" x="910" y="587">Desirability</text><text class="pn" x="1012" y="571">6</text></a>

<a href="{{ '/docs/components/iamlab-pareto-select/' | relative_url }}">
<rect class="box" x="1070" y="556" width="158" height="54" rx="8"/>
<rect x="1070" y="556" width="4" height="54" rx="2" fill="#a92764"/>
<circle cx="1097" cy="583" r="13" fill="#a92764" fill-opacity="0.1"/>
<use href="#i-pareto" x="1097" y="583" style="color:#a92764"/>
<text class="nm" x="1118" y="587">Pareto Select</text><text class="pn" x="1220" y="571">6</text></a>

<text class="note" x="446" y="466">Machine Files reaches the machine</text>
<text class="note" x="446" y="480">on its own, through `host`.</text>
</svg>

## How to read the map

- **Colour** is the panel on the IAMLab ribbon. Blue is the slicing pipeline
  (`1 Slice`, `2 Place`, `3 G-code`), green is the machine side (`4 Robot`,
  `7 Print`), magenta is measurement and experiment (`5 Analyze`,
  `6 Calibrate`). The small number in each corner is the panel itself.
- **The straight row through the middle** is the path every print takes:
  geometry into Slice, contours into Place, placed layers into
  G-code Generator.
- **The horizontal line under G-code Generator** is one output, `gcode`, going
  to several places at once. Save G-code writes it to disk, G-code Restart
  rebuilds it after a failure, Robot Simulation and Print Stats and Interlayer
  Time read it, and Machine Console sends it to the machine.
- **Dashed boxes** are document helpers. Load Settings points the document at a
  folder of saved settings and Version reports the installed build; neither sits
  in the wire path of a print.
- **The dashed loop along the bottom** is calibration. Plan runs with DoE
  Design, print and measure them, fit the results with RSM Evaluate, choose a
  recipe with Desirability and Pareto Select, and feed that recipe back into
  Slice and G-code Generator.

Robot Definition supplies the coordinate frame, kinematics, firmware dialect and
extrusion process. Its `robot` output goes to G-code Generator and Robot
Simulation as drawn, and also to Print Stats, Machine Console and Live View.
Print Bed likewise defines the plate once for Place, Robot Simulation, Live View
and Calib Points. Select `none` on Robot Definition for a model-free preview.

Components with a compute button run only when the button is clicked. Read every
`info` output after computing, and do not flatten Slice's contour tree: the
branches identify the layers.
