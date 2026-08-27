# Control-focused parameter selection

This table is the analysis scope for the future rating-driven generator
presets. It covers the parameters governed by a user-visible random-generator
control. `xform[*]` denotes the pooled value across all base transforms.

The table distinguishes a setting being user-configurable from the current
generator being able to represent a learned distribution. Most numeric controls
currently generate a uniform range, while the desired future preset may need a
non-uniform or mixture distribution.

| Analysis parameter(s) | Random-generator control | Default setting | Truly randomized for each flame? | Include in focused preset analysis? | Article recommendation | Current automatic-profile limitation |
|---|---|---|---|---|---|---|
| `genome.transform_count` | Transform count | 2–5 | Yes — discrete uniform integer | Yes — direct control | **Primary 1 of 3.** Report its selected-vs-reference probability distribution. | The UI represents only a uniform integer range; it cannot represent a learned non-uniform probability for 2, 3, 4, and 5 transforms. |
| `flame.symmetry` | Allowed symmetry types | Rotational enabled | Yes — a fixed 40% symmetry mixture; rotational order is 2 or 3 | Yes — direct type choice | Secondary descriptive check; include only if it materially separates the selected cohort. | The enabled types are editable, but the 40% chance and orders 2/3 are hard-coded. Do not fit or claim a learned symmetry probability until those controls exist. |
| `xform[*].derived.rotation_degrees` | Affine rotation range | -180° to 180° | Yes — uniform circular range | Yes — direct control | Secondary sensitivity check, not a headline measure. | The UI can only set range endpoints, not a directional or multimodal circular density. |
| `xform[*].derived.scale` | Affine scale range | 0.35–0.85 | Yes — uniform range | Yes — direct control | **Primary 3 of 3.** Report the selected-vs-reference scale distribution and concentration. | The UI can approximate a selected interval, but not a peaked or multimodal scale distribution. |
| `xform[*].derived.shear` | Affine shear range | -0.25 to 0.25 | Yes — uniform range | Yes — direct control | Secondary sensitivity check; omit from the first article unless its shift is larger than the primary measures. | Uniform endpoints only. |
| `xform[*].derived.translation_x`, `xform[*].derived.translation_y` | Translation extent | ±0.75 on each axis | Yes — independent uniform symmetric axes | Yes — direct control | Secondary sensitivity check; summarize the shared extent, not separate x/y article results. | The UI provides one symmetric extent, not an anisotropic or centred learned distribution. |
| `xform[*].weight` | Transform balance | 0.35 | Yes — uniform raw weight in approximately 0.65–1.35 | Yes — direct control | Exploratory only; do not make it a first-article result. | The setting changes a uniform spread around 1. It cannot encode a learned transform-specific weight distribution. |
| `xform[*].variation_count`; `variation_occurrence.csv`; `variation_weight_vectors.csv` | Variations per transform; enabled variations; minimum variation share | 1–3; all types; 0.05 floor | Yes — count is discrete, names are sampled without replacement, and weights occupy the valid simplex | Yes — use count as a marginal, one aggregate occurrence distribution over names, and vectors conditionally by count. | **Primary 2 of 3.** Report variation-count probabilities, occurrence rates by type, and separate one-, two-, and three-variation weight geometry. | The UI can enable/disable names and set a global floor, but cannot assign learned selection probabilities or per-name weight ranges. |
| `xform[*].post.present` | Post-transform chance | 42% | Yes — Bernoulli per base transform | Yes — canonical direct control. Do not analyze the redundant per-flame post-transform total. | Secondary binary check; suitable as a covariate or supplement. | The UI can set only one global chance, not a conditional probability by variation or transform count. |
| `xform[*].post.derived.rotation_degrees` | Post rotation range | -180° to 180° | Yes — uniform, conditional on post presence | Yes — direct control, but conditional sample size applies. | Omit from the first article; retain for preset fitting and diagnostics. | Uniform endpoints only; no circular density. |
| `xform[*].post.derived.scale` | Post scale range | 0.75–1.25 | Yes — uniform, conditional on post presence | Yes — direct control, but conditional sample size applies. | Omit from the first article; retain for preset fitting and diagnostics. | Uniform endpoints only. |
| `xform[*].post.derived.translation_x`, `xform[*].post.derived.translation_y` | Post translation extent | ±0.18 on each axis | Yes — uniform, conditional on post presence | Yes — direct control, but conditional sample size applies. | Omit from the first article; retain for preset fitting and diagnostics. | One symmetric extent only. |
| `genome.finalxform_present` | Allow final transforms + final-transform chance | Off; 15% chance if enabled | Conditional — no final transforms are generated at the default setting | Yes, but only after final transforms have been enabled in a pass. | Omit from the first article and from Pass 1 fitting because the default cohort has no events. | The final transform reuses the main affine and variation settings; only its enablement/chance is separately controlled. |

## Article scope

Use three primary, predeclared parameter families:

1. `genome.transform_count` — structural complexity.
2. `xform[*].variation_count` and conditional variation-weight geometry — nonlinear transformation mixture complexity.
3. `xform[*].derived.scale` — contraction and spatial branching.

The other user-controlled parameters remain in the reproducible preset report
and are eligible for generator fitting, but should be secondary checks rather
than additional headline claims. Symmetry, variation count, and post-transform
presence are the most useful secondary checks.

## Explicit exclusions from the focused preset

These values are not parsed into the focused matrix because no random-generator
GUI control determines them independently:

| Extracted parameter family | Reason for exclusion |
|---|---|
| `flame.center.*`, `flame.scale`, `flame.rotate` | Fixed camera values in generated flames; not random-generator controls. |
| `xform[*].affine.a` through `f`, and `xform[*].post.a` through `f` | Serialized representation; the derived rotation, scale, shear, and translation values are the generator-facing controls. |
| `xform[*].color` and `xform[*].symmetry` | Fixed generator constants: color is a 0–1 transform-index ramp and transform symmetry is 0.3. |
| `flame.size.*`, `flame.oversample`, `flame.filter`, `flame.quality` | Render settings, not random flame-generation settings. |
| Tone, background, palette, and palette-summary fields | Rendering/color controls or fixed defaults, not independently randomized genome style parameters; omitted from the parser. |
| Per-transform indexed duplicates such as `xform.01.*` | Retain only for the source-level record where needed; fit the pooled `xform[*]` parameter to avoid treating position in a variable-length transform list as a separate control. |
| Per-variation true/false fields and derived genome totals/unique counts | Thousands of Boolean cells obscure the comparison. Variation names are retained as one aggregate occurrence distribution instead. |

The current tool is intentionally marginal. A later automatic preset should
learn the listed distributions while retaining an explicit exploration reserve;
it must not infer causality from a single marginal or replace the reference
distribution with selected examples alone.
