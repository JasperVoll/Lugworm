# IAMLab Load Settings

IAMLab Load Settings points the document at a folder of saved settings
files, one per component. Every IAMLab component can save its current
inputs with "Save settings…" on its right-click menu, which writes a
small JSON file next to the ones this component reads. From then on,
the "⇤ inputs" button on any IAMLab component builds its sliders,
toggles, panels and value lists from the matching file instead of the
built-in defaults, and falls back to the defaults where the folder has
nothing to say. This makes it easy to keep one settings folder per
material or per machine and switch between them.

Panel: `1 Slice`. Nickname: `Settings`. Compute button: no (runs on every change).

<!-- IMAGE TODO: Load Settings pointed at a folder, with the report output listing matched and unmatched files. A short screen recording saved as GIF can replace the image. -->
![IAMLab Load Settings on the canvas](images/comp-load-settings.png)

## Inputs

| Name | Type | Description |
|---|---|---|
| `folder` | Text | Folder with *.iamset.json files, one per component type |
| `watch` | Boolean | Read the folder again whenever its file names, sizes or timestamps change. False scans once per folder path. Turning it off while editing files by hand is not needed, it only avoids the directory listing on every solve |

## Outputs

| Name | Type | Description |
|---|---|---|
| `files` | Text (list) | One line per match: the component key and the file it takes its inputs from |
| `unmatched` | Text (list) | Files that were not used: unreadable ones with their error, duplicate claims on one component, and files naming a component this build does not have |
| `info` | Text | Diagnostics |

## Notes

- Files are matched by the `component` name written inside the JSON,
  never by file name, so a file can be called `clay-fast.iamset.json`
  or anything else that is meaningful to you.
- The `apply` button applies every matching file to the canvas at once,
  as one undo step. An input with nothing connected gets a new object
  carrying the file's value; an input already connected to exactly one
  slider, toggle, panel or value list is updated in place; anything
  more complex upstream is left alone and listed in the report.
- `watch` re-checks the folder on the next solve when a file's name,
  size or timestamp changed. Turn it off only if the per-solve
  directory listing is a measured problem.
- `unmatched` lists files this build could not use: unreadable files,
  two files claiming the same component, and files naming a component
  that does not exist in this build.
- Only one Load Settings component is allowed per document; a second
  one is refused rather than silently taking over.
- Geometry, points, planes, colours and tree inputs are never saved or
  loaded; the save report says so.

Back to [[Components]].
