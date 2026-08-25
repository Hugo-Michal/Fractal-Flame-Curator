# Flame parameter analysis

This folder contains a read-only research tool for measuring how a selected set
of Apophysis-compatible fractal flames differs from a reference population. It
extracts serialized XML values, reconstructs generator-facing affine settings,
tests the reference distribution against an explicitly configured generator,
and produces CSV data plus a self-contained HTML report.

The tool does not modify, rename, move, rate, or score source files.

## Control-focused scope

`parameter-selection.md` maps the 185 extracted marginal parameters to the
user-visible random-generator controls. It identifies the canonical pooled
parameter for each control, distinguishes a user-editable setting from a
hard-coded sampling rule, excludes static/render-only fields from the focused
preset, and predeclares three primary article measures: transform count,
variation-type occurrence, and affine scale.

This document is the decision table to update before a new automatic-preset
policy is implemented. It does not change the raw matrix: excluded fields stay
available for auditability and later joint-distribution work.

`variation-weight-sampling-design.md` contains the proposed replacement for
the normalized raw-weight sampler: a seed-scrambled, low-discrepancy simplex
coverage sampler for broad relative variation-weight coverage. It is a design
document only; the current generator remains unchanged until the proposal is
approved and implemented.

## Run Pass 1

From the repository root:

```powershell
py -3.12 .\work\flame-parameter-analysis\analyze_flames.py `
  .\work\flame-parameter-analysis\pass-01.json
```

The current Pass 1 configuration keeps cohorts separate:

- `reference_pass_01`: all 10,000 sources from run `62b27347`, reconstructed
  from the union of `rendered` and `ratings/1` so later rating moves do not
  remove selected flames from the original random reference population;
- `negative_pass_01`: any one-star sources from that same run; and
- rating folders 2 through 5 are inventoried without attempting to parse
  image-only files as flame genomes.

The reference group has an enforced expected count of 10,000. A missing,
malformed, duplicated, or incorrectly filtered source therefore fails the run
instead of silently changing the baseline. All groups and filters are
configurable in JSON. Copy `config.example.json` for subsequent passes, give
each output directory a stable pass name, and keep the reference group
unchanged when comparing convergence across passes.

## Outputs

Each run creates these files under its configured output directory:

| File | Purpose |
|---|---|
| `flame_parameter_matrix.csv` | Conventional research matrix: one flame per row and one serialized or derived parameter per column. |
| `flame_parameter_matrix_transposed.csv` | Requested transposed view: one parameter per row and one group/source ID per column. Disable it with `write_transposed_matrix: false` if a later corpus exceeds spreadsheet column limits. |
| `flame_manifest.csv` | Source path, stable ID, run ID, seed token, sequence, filter status, and parse errors. |
| `folder_inventory.csv` | File counts by configured group, folder, and extension, including image-only folders. |
| `parameter_catalog.csv` | Parameter type, category, unit, scope, description, and corresponding generator control where known. |
| `parameter_summary.csv` | Counts, missingness, quantiles, spread, entropy, and marginal concentration for every analyzed parameter and group. |
| `numeric_histograms.csv` | Normalized histogram-bin probabilities for continuous and count parameters. |
| `categorical_probabilities.csv` | Exact empirical probabilities for categorical, checkbox, and presence parameters. |
| `distribution_profiles.json` | Machine-readable per-group histogram/category profiles intended as input evidence for later generator-profile design. |
| `uniformity_tests.csv` | Reference goodness-of-fit tests against the expected generator configuration. |
| `concentration_comparisons.csv` | Target-versus-reference distribution shifts and narrowing measures. |
| `report.html` | Self-contained visual report with cohort inventory, uniformity checks, concentration ranking, and graphs for every analyzed marginal. |
| `run_summary.json` | Machine-readable audit of counts, exclusions, duplicate IDs, warnings, and tool version. |

CSV files use UTF-8 with a byte-order mark so identifiers and paths open
correctly in Excel while remaining readable by normal CSV tools.

## Parameter reconstruction

The source `.flame` file stores affine coefficients rather than the controls
shown in the random-generator window. Under the current generator convention,
the tool reconstructs:

```text
scale       = sqrt(a² + c²)
rotation    = atan2(c, a)
shear       = b + c
translation = (e, f)
```

It performs the equivalent reconstruction for post transforms. It also records
transform count, transform weights, variation count, variation type presence,
conditional variation weights, final-transform presence, flame symmetry,
camera/render/tone values, and palette summaries.

The matrix retains transform-indexed values. Statistical summaries additionally
pool equivalent transform positions as `xform[*]...`, which is usually the
correct level for configuring generator distributions.

## Distribution and concentration rules

- Continuous parameters use normalized histograms with deterministic
  Freedman–Diaconis bin selection, bounded by the configured minimum and maximum
  bin counts.
- Transform and variation counts, checkboxes, and presence flags use exact category
  probabilities.
- Rotation parameters retain histograms but use circular means and circular
  variance for concentration, avoiding a false wide spread when values cluster
  across the -180/180-degree boundary.
- Reference uniformity uses one-sample Kolmogorov–Smirnov tests for configured
  continuous ranges and chi-square goodness-of-fit tests for configured
  categorical probabilities.
- P-values are reported but are not treated as practical importance. With tens
  of thousands of transform observations, tiny effects can be statistically
  significant. The report separately flags effect sizes above `0.02`.
- Numeric concentration is based on the target/reference IQR ratio. Categorical
  concentration is based on normalized-entropy loss. Jensen–Shannon divergence
  describes broader distribution change.
- The HTML headline ranking requires at least 100 reference and 30 target
  observations by default. Configure these thresholds under `comparison`.
  Smaller conditional samples remain in the CSV, but they are excluded from
  the ranking to prevent rare variation types from dominating through noise.
- A positive concentration score means a marginal became narrower. It does not
  mean the selected flames are better or that the generator should immediately
  adopt the narrowed range.

## Scientific limitation

This first tool is deliberately a univariate analysis. Flame parameters
interact strongly, and multiple good joint modes can share broad or misleading
marginals. Keep the raw matrix for later multivariate clustering, dependence,
and mixture modelling. Generator changes should be based on concentration,
joint structure, preserved exploration, and an independently validated human
target-style hit rate—not on DINOv2 score alone.

The expected generator configuration is supplied explicitly because current
`.flame` files do not persist the complete random-generator settings snapshot.
Future application work should log a generator-profile/version identifier and
the full applied settings with each run so the reference distribution is not
reconstructed from memory.

## Tests

```powershell
py -3.12 -m unittest discover `
  -s .\work\flame-parameter-analysis\tests `
  -p 'test_*.py'
```
