# Flame parameter analysis

This folder contains a read-only research tool for measuring how a selected set
of Apophysis-compatible fractal flames differs from a reference population. It
extracts serialized XML values, reconstructs generator-facing affine settings,
tests the reference distribution against an explicitly configured generator,
and produces CSV data plus a self-contained HTML report.

The tool does not modify, rename, move, rate, or score source files.

When a configuration supplies `generator_profile_path`, the analyzer reads the
settings snapshot written by that render session and uses it as the expected
reference profile. Duplicate values in `expected_generator` must match the
recorded profile; that block is reserved for additional known sampling rules
such as the fixed symmetry chance and orders.

## Control-focused scope

`parameter-selection.md` maps the extracted generator-facing parameters to the
user-visible random-generator controls. It identifies the canonical pooled
parameter for each control, distinguishes a user-editable setting from a
hard-coded sampling rule, excludes static/render-only fields from the focused
preset, and predeclares three primary article measures: transform count,
variation-type occurrence, and affine scale.

This document is the decision table to update before a new automatic-preset
policy is implemented. Fixed fields excluded from the focused scope are not
parsed; the source files remain untouched for any future independent audit.

`variation-weight-sampling-design.md` documents the implemented replacement for
the normalized raw-weight sampler: a seed-scrambled, low-discrepancy simplex
coverage sampler for broad relative variation-weight coverage. The analyzer
validates this sampler conditionally by variation count rather than treating
pooled component histograms as independent uniform variables.

## Run Pass 2 with rating folders separated

From the repository root:

```powershell
$py = 'C:\\Users\\Hugo\\.cache\\codex-runtimes\\codex-primary-runtime\\dependencies\\python\\python.exe'
& $py .\work\flame-parameter-analysis\analyze_flames.py `
  .\work\flame-parameter-analysis\pass-02-human-ratings.json
```

The Pass 2 configuration keeps the cohorts separate:

- `reference_pass_01_v13`: all 24,000 sources from run `001e81a2`, reconstructed
  from the current `rendered` folder plus the 500 rated flame pairs so later
  rating moves do not remove selected flames from the random reference
  population;
- `rating_1_pass_02` through `rating_4_pass_02`: the complete flame pairs in
  each human-rating folder; and
- rating folder 5 is inventoried without attempting to parse its image-only
  seashell references as flame genomes.

The reference and rating groups have enforced expected counts of 24,000, 364,
87, 44, and 5. A missing,
malformed, duplicated, or incorrectly filtered source therefore fails the run
instead of silently changing the baseline. All groups and filters are
configurable in JSON. `report_groups` controls which cohorts are initially
shown; every analytical cohort is still embedded in the report. Checkboxes plus
reference-only, ratings-only, and select-all shortcuts update graphs and cohort
table rows without rerunning the analysis. Copy `config.example.json`
for subsequent passes, give each output directory a stable pass name, and keep
the reference group unchanged when comparing convergence across passes.

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
| `variation_occurrence.csv` | Per-cohort occurrence rate for every supported variation, measured over base transforms, with reference differences and ratios. |
| `variation_weight_vectors.csv` | Per-transform normalized weight vectors with matching variation names and variation-count labels for reproducible simplex analysis. |
| `variation_weight_by_name.csv` | Conditional weight quantiles for each variation name and cohort. |
| `generator_control_findings.csv` | Plain-language, sample-guarded screening guidance mapped to controls in the random-generator window. |
| `simplex_coverage.csv` | Conditional weight-floor, sum-invariant, two-part KS, and three-part triangular-occupancy diagnostics. |
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
transform count, transform weights, variation count, aggregate variation-name
occurrence, conditional variation-weight vectors with their names,
final-transform presence, flame symmetry, and
post-transform settings. Fixed camera, render, tone, background, and palette
fields are intentionally not parsed because they do not vary in the generator
experiment.

The matrix retains transform-indexed values. Statistical summaries additionally
pool equivalent transform positions as `xform[*]...`, which is usually the
correct level for configuring generator distributions.

## Distribution and concentration rules

- Continuous parameters use normalized histograms with deterministic
  Freedman–Diaconis bin selection, bounded by the configured minimum and maximum
  bin counts.
- Transform and variation counts and generator checkboxes use exact category
  probabilities. Variation names are summarized as one occurrence distribution;
  per-variation Boolean parameters are not created.
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
- Variation-weight distributions are conditional on selected variation count
  and are displayed in separate one-, two-, and three-variation report tabs:
  one-part vectors are checked for exact weight one, two-part vectors use a
  pooled component histogram and a uniform-share KS test, and three-part
  vectors use triangular-cell occupancy plus the ternary plot. No isolated
  three-part component histogram is interpreted as uniform.
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
