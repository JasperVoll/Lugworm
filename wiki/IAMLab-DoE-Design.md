# IAMLab DoE Design

IAMLab DoE Design plans a series of test prints. You name the settings
that should be tuned, give the lowest and highest value for each, and
choose a plan type. The component produces a run table with one row
per test print and one column per setting, plus empty columns for the
measurements. The table can be exported to Excel or CSV and later read
by [[IAMLab RSM Evaluate]].

Panel: `6 Calibrate`. Nickname: `DoE`. Compute button: no (runs on every change).

<!-- IMAGE TODO: DoE Design with a panel on the table output showing a BBD plan for three settings. A short screen recording saved as GIF can replace the image. -->
![IAMLab DoE Design on the canvas](images/comp-doe-design.png)

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `names` | Text (list) | layer_height; print_speed; pressure | Factor names, one per item, for example layer_height, print_speed, pressure |
| `lows` | Number (list) | 2.5; 30; 1.0 | Factor low levels (coded -1), same order and length as names |
| `highs` | Number (list) | 4.5; 50; 1.3 | Factor high levels (coded +1) |
| `design` | Text | BBD | Design type: DSD (screening, 4 to 12 factors), BBD (RSM, 3 to 7), Taguchi (2 to 13), FullFact2 (1 to 10), FracFact2 (fractional two level, 4 to 9) |
| `responses` | Text (list) | layer_width; surface_defects; print_time | Response names. They become empty columns in the exported plan for lab measurements |

### Optional group: Advanced options (centres, center points, levels, seed)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `centres` | Number (list) |  | Optional center level per factor away from the midpoint, for example pressure 1.15 in [1.0, 1.3] |
| `centerPts` | Integer | -1 | Extra or total center points (-1 = design default, BBD 3 to 6, DSD 0) |
| `levels` | Integer | 3 | Taguchi factor levels, 2 or 3. DSD and BBD always use 3 |
| `seed` | Integer | -1 | Randomize run order with this seed (-1 = standard order) |
| `fracRuns` | Integer | -1 | FracFact2: target runs 8 or 16 (-1 = auto: smallest with resolution ≥ IV) |

### Optional group: Power analysis (can this plan detect the effect?)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Default | Description |
|---|---|---|---|
| `powerSigma` | Number | 0 | Residual standard deviation of the response, the RMSE of a previous campaign or the measurement repeatability. Required for the power estimate |
| `powerEffect` | Number (list) |  | Smallest low to high response change worth detecting, per factor. One value applies to all |
| `powerModel` | Text | Linear | Model that will be fitted to the data: Linear, 2FI or Quadratic. Power depends on its residual degrees of freedom |
| `powerSims` | Integer | 400 | Monte Carlo simulations. More gives a smoother estimate and takes longer |

### Optional group: Excel / CSV export inputs

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `xlsxPath` | Text | Write the plan as a styled .xlsx workbook. Fill in the responses and feed it to the RSM component |
| `csvPath` | Text | Write the plan as .csv |

## Outputs

| Name | Type | Description |
|---|---|---|
| `runs` | Number (tree) | Run plan in actual units, one branch per run, factor values in input order |
| `coded` | Number (tree) | Run plan in coded units, -1 to +1 |
| `factorNames` | Text (list) | Factor names. Wire into the RSM component |
| `runLabels` | Text (list) | Run labels (BBD01, …) |
| `table` | Text | Readable run table. Connect a panel |
| `info` | Text | Design summary and warnings |
| `power` | Text | Monte Carlo power per factor at α = 0.05. Enable the power group and connect a panel |

## Notes

- `design`: DSD screens many settings with few prints; BBD is the usual
  choice when the effect of each setting should be modelled with
  curvature; Taguchi, FullFact2, and FracFact2 are further standard
  plans.
- The Power group estimates whether the plan has enough runs to detect
  an effect of a given size, using the scatter of a previous campaign
  (`powerSigma`). A warning is shown when the plan is too small.
- The Advanced group sets centre points, the number of levels for
  Taguchi plans, and a random run order.

Back to [[Components]].
