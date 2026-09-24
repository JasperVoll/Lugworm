# Settings files

A settings file is one small JSON file holding the inputs of **one** Lugworm
component: layer height, speeds, firmware choice, file paths, which optional
input groups are switched on. Save one per component and you have a material or
machine preset you can mail to a colleague, put in a repository, or diff after a
calibration round.

Files live wherever you like. Point **Load Settings** at the folder and
every `⇤ inputs` button on the canvas builds its sliders, toggles, panels and
value lists from those files instead of the built-in defaults.

## What a file looks like

```json
{
  "iamlab-settings": 1,
  "component": "gcode",
  "displayName": "G-code Generator",
  "plugin": "4.0.0",
  "groups": ["advance"],
  "inputs": {
    "firmware": 2,
    "layerHeight": 1.0,
    "printSpeed": 20,
    "relativeE": true,
    "savePath": "C:\\out\\a.gcode"
  }
}
```

- `iamlab-settings` is the schema version. A file from a newer schema is refused
  with a message, never guessed at.
- `component` is the **stable key of the component class**. It, not the file
  name, decides which component a file belongs to — call the file
  `clay-fast.iamset.json` if that is what it is.
- `displayName` and `plugin` are informative: what the component was called and
  which plugin version wrote the file.
- `groups` are the optional input groups that were switched on. Loading the file
  switches them on again first, so the inputs it talks about exist.
- `inputs` are the values, one per input name. Numbers keep a decimal point
  (`1.0`), integers do not (`2`), booleans are `true`/`false`, text is quoted,
  and a list input is a JSON array of one kind. Everything is written and read
  under the invariant culture, so a file written on a German machine loads
  identically anywhere.

Everything is optional except the version and `component`. Keys the build does
not know are ignored, so an older Lugworm still reads the part of a newer file it
understands.

The recommended extension is `.iamset.json`; the folder component also reads
plain `.json`.

## Saving and loading one component

Right-click any Lugworm component:

- **Save settings…** writes the values the component **last solved with** from
  the sliders, toggles, panels and value lists wired to it, plus its active
  groups, as UTF-8 JSON without a byte-order mark. An input fed by anything else
  (a component output such as the generator's G-code lines or a Slice contour
  list, a Range, several sources at once) is the definition's data, not a
  setting: it is named in the save report and left out of the file. The same
  goes for a list longer than 64 items or a text longer than 4096 characters.
- **Load settings…** reads a file back. A file whose `component` key is not this
  component's is refused with a warning rather than applied to the wrong sockets.

Loading is one undoable step (`Ctrl+Z` puts everything back) and reports what it
did as a remark: how many inputs were **created**, **updated** and **skipped**.

- **created** — the input had nothing wired to it, so a slider / toggle / panel /
  value list carrying the file value was made and connected. A slider whose hint
  range does not reach the file value is widened to include it rather than
  clamping the value.
- **updated** — the input was wired to exactly one slider, toggle, panel or value
  list, and that object was set to the file value in place.
- **skipped** — the input is fed by something else (a component, an expression, a
  cluster, several sources at once). Those are never overwritten; they are
  listed so you can see what the file did not reach.

A value list whose items do not contain the file's value is left alone and
reported; when such an input has nothing wired yet, a panel carries the raw
value instead.

## A whole folder: Load Settings

Find **Load Settings** in **Lugworm → 1 Slice**.

1. Give `folder` the path of a folder of settings files.
2. `watch` (default true) re-reads the folder whenever a file's name, size or
   timestamp changes, so editing a file by hand takes effect on the next solve.
   Set it false to scan a folder only once. There is no background watcher
   thread either way.
3. `files` lists every match as `gcode ← clay-fast.iamset.json`.
4. `unmatched` lists everything that was **not** used: files that could not be
   read (with the reason), several files claiming one component, and files naming
   a component this build does not have.
5. `info` summarises the scan.

From then on, pressing `⇤ inputs` on any Lugworm component in **that document**
first looks for a file for its key. If there is one, the created objects carry
its values and its groups are switched on; if there is not, the built-in
`ParamSpec` defaults are used exactly as before. Nothing else changes.

The capsule **Apply to canvas** does the same for every matching component in the
document at once, under a single undo event.

### One folder per document

Only one Load Settings component may own a document. A second one reports an
error and registers nothing, rather than leaving it to chance which folder wins.

If two files in the folder claim the same `component` key, the alphabetically
first path wins and the rest are reported under `unmatched` — the choice never
depends on the order the operating system listed the directory in.

The registration is **not** saved in the `.gh` file. A settings folder is a
machine-local convenience; a definition you send to someone else carries the
values already on its sliders, not a path into your file system.

## What is not captured

A settings file describes process parameters, never the part being printed.
Inputs of these types are named in the save report and left to the definition:

- geometry, curves, meshes and breps
- points, vectors and planes
- colours
- anything arriving as a data tree rather than an item or list
- anything wired from a component output rather than a slider, toggle, panel or
  value list (G-code lines, contours, values from a Range), and any list over 64
  items or text over 4096 characters

Inputs that have never solved (nothing wired, no data yet) are simply absent from
the file; loading it leaves them alone. A folder file larger than 1 MB is refused
by Load Settings with a note saying so; such a file was saved by a 4.0.0
build that still captured wired data - re-save it from the component.

## Stable component keys

The `component` key is the component's class name with a trailing `Component`
removed, lower-cased. Some of the ones you will meet:

| Component | Key |
|---|---|
| Slice | `slicer` |
| G-code Generator | `gcode` |
| Place | `mover` |
| Print Bed | `printbed` |
| Robot Simulation | `robotsimulation` |
| Overhang | `overhang` |
| Interlayer Time | `interlayertime` |
| Legend | `legend` |
| Duet Console (Machine Console in 4.x) | `duetprintconsole` |
| Load Settings | `loadsettings` |

The quickest way to get a key right is to press **Save settings…** on the
component once and read the `component` field of the file it writes.

Keys are part of the file format: they stay put even when a component's display
name changes, so a preset written today keeps working.
