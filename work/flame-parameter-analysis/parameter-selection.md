# Control-focused parameter selection

This table is the analysis scope for the future rating-driven generator
presets. It intentionally reduces the current 185 extracted marginal
parameters to the parameters governed by a user-visible random-generator
control. `xform[*]` denotes the pooled value across all base transforms, and
`<name>` denotes each supported variation type.

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
| `xform[*].variation_count`; `genome.total_variation_count`; `genome.unique_variation_count` | Variations per transform | 1–3 | Yes — discrete uniform count | Yes — direct control; use `xform[*].variation_count` as canonical and keep the genome totals as diagnostics. | Secondary descriptive check; report only if it explains the variation-type result. | The UI represents only a uniform integer range. |
| `xform[*].variation.<name>.present`; `genome.variation.<name>.present` | Enabled variation types | All 38 supported types | Yes — types are sampled without replacement from the enabled list | Yes — direct control; use pooled per-transform occurrence as canonical. | **Primary 2 of 3.** Report the ranked occurrence probabilities of variation types; aggregate rare types or use a predeclared top-k to control multiplicity. | The UI can enable or disable a type but cannot assign a learned probability weight to enabled types. |
| `xform[*].variation.<name>.weight` | Variation blend dominance | 0.50 | Yes — raw weights are random, then normalized within a transform | Yes — direct control, conditional on the type being selected. | Exploratory; do not report one result per variation weight in the first article. | One dominance scalar cannot reproduce a learned conditional weight distribution. Treat an observed shift as evidence for a future profile extension. |
| `xform[*].post.present`; `genome.post_transform_count` | Post-transform chance | 42% | Yes — Bernoulli per base transform | Yes — direct control; use `xform[*].post.present` as canonical. | Secondary binary check; suitable as a covariate or supplement. | The UI can set only one global chance, not a conditional probability by variation or transform count. |
| `xform[*].post.derived.rotation_degrees` | Post rotation range | -180° to 180° | Yes — uniform, conditional on post presence | Yes — direct control, but conditional sample size applies. | Omit from the first article; retain for preset fitting and diagnostics. | Uniform endpoints only; no circular density. |
| `xform[*].post.derived.scale` | Post scale range | 0.75–1.25 | Yes — uniform, conditional on post presence | Yes — direct control, but conditional sample size applies. | Omit from the first article; retain for preset fitting and diagnostics. | Uniform endpoints only. |
| `xform[*].post.derived.translation_x`, `xform[*].post.derived.translation_y` | Post translation extent | ±0.18 on each axis | Yes — uniform, conditional on post presence | Yes — direct control, but conditional sample size applies. | Omit from the first article; retain for preset fitting and diagnostics. | One symmetric extent only. |
| `genome.finalxform_present` | Allow final transforms + final-transform chance | Off; 15% chance if enabled | Conditional — no final transforms are generated at the default setting | Yes, but only after final transforms have been enabled in a pass. | Omit from the first article and from Pass 1 fitting because the default cohort has no events. | The final transform reuses the main affine and variation settings; only its enablement/chance is separately controlled. |

## Article scope

Use three primary, predeclared parameter families:

1. `genome.transform_count` — structural complexity.
2. `xform[*].variation.<name>.present` — nonlinear transformation choice.
3. `xform[*].derived.scale` — contraction and spatial branching.

The other user-controlled parameters remain in the reproducible preset report
and are eligible for generator fitting, but should be secondary checks rather
than additional headline claims. Symmetry, variation count, and post-transform
presence are the most useful secondary checks.

## Explicit exclusions from the focused preset

These values may remain in the raw matrix for auditability, but the preset
analysis should not rank or fit them because no random-generator GUI control
determines them independently:

| Extracted parameter family | Reason for exclusion |
|---|---|
| `flame.center.*`, `flame.scale`, `flame.rotate` | Fixed camera values in generated flames; not random-generator controls. |
| `xform[*].affine.a` through `f`, and `xform[*].post.a` through `f` | Serialized representation; the derived rotation, scale, shear, and translation values are the generator-facing controls. |
| `xform[*].color` and `xform[*].symmetry` | Fixed generator constants: color is a 0–1 transform-index ramp and transform symmetry is 0.3. |
| `flame.size.*`, `flame.oversample`, `flame.filter`, `flame.quality` | Render settings, not random flame-generation settings. |
| Tone, background, palette, and palette-summary fields | Rendering/color controls or fixed defaults, not independently randomized genome style parameters. |
| Per-transform indexed duplicates such as `xform.01.*` | Retain in the matrix for audit, but fit the pooled `xform[*]` parameter to avoid treating position in a variable-length transform list as a separate control. |
| Derived genome totals and unique counts | Useful diagnostics, but consequences of transform count, variation count, and enabled variation types rather than independently configurable controls. |

The current tool is intentionally marginal. A later automatic preset should
learn the listed distributions while retaining an explicit exploration reserve;
it must not infer causality from a single marginal or replace the reference
distribution with selected examples alone.
