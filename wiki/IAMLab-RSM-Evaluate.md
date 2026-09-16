# IAMLab RSM Evaluate

IAMLab RSM Evaluate fits a formula to the measurements of a test
series. For each measured result, the formula describes how the result
depends on the settings. The component reports how strongly each
setting matters, how well the formula fits the data, and diagnostics
that show whether single runs or overlapping settings distort the fit.
The fitted models are passed on to [[IAMLab Desirability]]. The
component has a compute button.

Panel: `6 Calibrate`. Nickname: `RSM`. Compute button: yes (runs only when the button is clicked).

<!-- IMAGE TODO: RSM Evaluate with the on-canvas effect bars and a panel on the report output. A short screen recording saved as GIF can replace the image. -->
![IAMLab RSM Evaluate on the canvas](images/comp-rsm-evaluate.png)

## Inputs

| Name | Type | Default | Description |
|---|---|---|---|
| `tablePath` | Text |  | Filled measurement table, .xlsx or .csv. The header row names the factor and response columns. Extra columns such as Run and x_* are ignored |
| `factorNames` | Text (list) | layer_height; print_speed; pressure | Factor column names. Wire from the factorNames output of the DoE component |
| `responseNames` | Text (list) |  | Response column names to fit (empty = all other numeric columns) |
| `model` | Text | Quadratic | Model type: Linear, 2FI (main effects and interactions) or Quadratic. Aliased terms are dropped automatically, for example quadratics on a DSD |

### Optional group: Coding-range overrides (lows/highs/centres)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `lows` | Number (list) | Coding low per factor (default: data minimum) |
| `highs` | Number (list) | Coding high per factor (default: data maximum) |
| `centres` | Number (list) | Center per factor away from the midpoint (default: midpoint) |

### Optional group: Direct data inputs (X/Y trees instead of a file)

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `X` | Number (tree) | Direct factor data, one branch per run. An alternative to tablePath |
| `Y` | Number (tree) | Direct response data, one branch per run, values in responseNames order |

### Optional group: Excel report export input

Right-click the component to switch this group on. The inputs appear only while the group is enabled.

| Name | Type | Description |
|---|---|---|
| `xlsxReport` | Text | Write the full analysis report (data, coefficients, ANOVA) as .xlsx |

## Outputs

| Name | Type | Description |
|---|---|---|
| `models` | Generic (list) | Fitted RSM models. Wire into the Desirability component |
| `report` | Text | Readable analysis report: coefficients, ANOVA and fit statistics. Connect a panel |
| `equations` | Text (list) | Model equations in coded and actual units, one per response |
| `predicted` | Number (tree) | Model predictions for the input runs, one branch per response |
| `json` | Text (list) | Model coefficients as JSON for archiving or external tools |
| `info` | Text | Diagnostics |
| `diag` | Text (list) | Regression diagnostics per response: leverage, studentized residuals, Cook's distance and VIF. Connect a panel |

## Notes

- `tablePath` accepts the Excel or CSV file exported by DoE Design with
  the measurement columns filled in. CSV files may use English (`1.5`)
  or German (`1,5`) numbers; a file that mixes both is refused.
- `model`: Linear fits straight effects, 2FI adds interactions between
  settings, Quadratic adds curvature.
- A weak fit (adjusted R² below 0.5) is flagged. A recommendation from
  a weak formula is only as reliable as that formula.
- The Direct group accepts the data as Grasshopper trees instead of a
  file.

Back to [[Components]].
