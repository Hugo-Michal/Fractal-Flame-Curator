# Fractal Flame Curator experiment notes

This notebook primarily records the researcher's statements and decisions. Application parameters, folders, and generated files will be inspected only when requested.

## 2026-08-23 — Experiment concept and current setup

### Research idea

- Use X-ray images of seashells as the reference for a desired fractal-flame style.
- The seashell structures look strongly fractal and provide a natural visual target, following the broader idea of fractals as a language of nature.

### Current data and labeling plan

- The positive reference set contains exactly 32 grayscale seashell X-ray images.
- The images were normalized and placed in the five-star category as examples of the wanted style.
- A batch of 10,000 randomly generated fractal flames was rendered overnight.
- The negative set is finalized at exactly 54 generated flames labeled with one star as examples of styles that are not wanted.
- Only the one-star and five-star extremes will be used initially. The intent is to create a clean separation between wanted and unwanted styles rather than populate the intermediate categories.

### Planned AI and generation loop

1. Train the DINOv2 preference scorer using the 32 five-star reference images and 54 one-star fractal flames.
2. Score the remaining generated flames from the 10,000-image batch.
3. Review whether the highest-scoring fractals match the intended seashell-like style.
4. Improve the scoring approach and labels based on those results.
5. Later, modify and tune the random flame generator so it produces more fractals of the desired style.
6. Render another population, score it, review it, and continue the loop.

### Measurement objective

- Make improvement across generator iterations quantifiable.
- Track how the generator's parameter distributions, including variation transforms, converge toward settings that produce seashell-like fractals.
- Study this as a possible “collapse of styles”: a narrowing or concentration of random-generator parameters around the desired style.
- Continue using DINOv2 scores to measure whether successive generated populations improve.
- Develop the experiment and measurements with publication in mind.

### Open questions for discussion

- How should “style collapse” be defined mathematically?
- How should useful convergence toward the target be distinguished from undesirable loss of visual diversity?
- Which generator parameters and variation-transform statistics should be retained for each iteration?
- Which score, human-evaluation, and diversity metrics should be used to compare iterations?
- Which parts of the experiment are publishable will be refined after reviewing the forthcoming research memo.

### Next step

- Review the research memo when provided and continue the discussion from there.

## 2026-08-23 — Research documents received

Sources supplied by the researcher:

- `fresh_framework_novelty_reassessment.md`
- `literature_review.md`

### Main conclusion of the research review

- The general loop “generate → obtain human preference → learn a scorer → bias future generation → repeat” is not novel.
- Electric Sheep is a direct fractal-flame precedent, and interactive evolution, preference learning, estimation-of-distribution methods, Bayesian optimization, and quality-diversity research contain the same general mechanism.
- The defensible contribution is a reproducible, flame-specific experimental framework: paired Apophysis-compatible genomes and renders, fixed seeds, explicit human labels, controlled image/genome models, logged adaptive proposal distributions, continued broad exploration, diversity preservation, and equal-budget comparisons.
- The paper should distinguish aesthetic quality, resemblance to a target style, and semantic resemblance. The seashell experiment is principally a target-style/reference-resemblance experiment, not a universal aesthetic-quality experiment.

### Consequences for the current seashell pilot

- With labels only at one star and five stars, the first experiment is binary. For these two extremes, all four ordinal DINOv2 thresholds receive identical targets, so the pilot does not yet contain ordinal information.
- The 32 positive examples are X-ray images while the negative examples are rendered flames. This creates a major domain confound: the model may learn “X-ray versus synthetic render” instead of seashell-like morphology.
- DINOv2 score cannot be both the optimization target and the sole measure of success. Final evaluation must include independent, preferably blind, human judgments of seashell-style resemblance.
- The initial 32-positive/~50-negative dataset is appropriate as a pilot, but not by itself as strong evidence for a publishable model.
- Hard negatives and controls will be important: non-shell X-rays, visually matched non-shell structures, and grayscale flames that resemble the references in tone or composition without having the intended morphology.

### Working measurement model

- **Target alignment:** blind human hit rate among top-ranked candidates, compared with a random or stratified sample from the same 10,000-image population.
- **Scorer performance:** binary ranking and calibration metrics for the initial extreme-label pilot; ordinal metrics only after intermediate ratings exist.
- **Search efficiency:** target-style hits per fixed number of valid renders and per unit of human labeling effort.
- **Genome convergence:** change in transform-count, variation-choice, affine, weight, symmetry, camera, and other parameter distributions relative to the frozen broad prior.
- **Phenotype diversity:** coverage and spread in an image-feature space, measured independently enough to detect visual homogenization rather than merely echo the optimized score.
- **Interpretation:** call intended narrowing “parameter concentration” or “style convergence”; reserve “mode collapse” for harmful loss of distinct visual or genome modes.

### Proposed first validation step

1. Use the finalized set of 54 one-star negatives and 32 five-star positives.
2. Train the first scorer and rank the 10,000 generated flames.
3. Blindly evaluate a fixed top-ranked sample against an equally sized random sample, with the evaluator judging seashell-style resemblance rather than general attractiveness.
4. Record false positives and false negatives as hard examples for the next training round.
5. Analyze generator parameters only after confirming that scorer rank predicts independent human judgments.
6. Before adapting the generator, freeze and record the baseline generator distribution, renderer settings, seeds, label budget, and primary evaluation metric.

### Publication direction retained from the documents

- The strongest near-term study is a controlled comparison showing whether preference- or style-conditioned sampling improves target hit rate over the same broad random prior without destroying diversity.
- A single-user experiment should be described as individual preference or a target-style case study; broader personalization claims require multiple users or target styles.
- Attractive examples are illustrative results, not sufficient experimental evidence.

## 2026-08-23 — Pilot training set finalized

- Positive examples: 32 normalized grayscale seashell X-ray images in the five-star category.
- Negative examples: 54 generated fractal flames in the one-star category.
- Total labeled training examples: 86.
- Decision: train DINOv2 on this clean extreme-label separation.
- Next action: use the trained model to score the full 10,000-fractal batch.
- Immediate purpose: prioritize the candidates most likely to match the wanted style so the highest-ranked results can be inspected first.

## 2026-08-23 — Parameter-concentration baseline, Pass 1

### Measurement decision

- Treat the current 10,000-flame random population as the frozen Pass 1 reference distribution.
- Measure style convergence across later passes as changes and narrowing in generator-parameter distributions relative to that reference.
- Store both a flame-by-parameter matrix and a transposed parameter-by-flame matrix, along with empirical distributions and a visual report.
- Keep folders and cohorts configurable so each future selected population or generator pass can be analyzed without changing the program.
- Represent continuous values with histograms and quantiles, rotations with circular statistics, discrete choices and counts with empirical probabilities, and checkboxes with occurrence rates.

### Cohort audit at the time of Pass 1

- `rendered` contains exactly 10,000 PNG/`.flame` pairs from run `62b27347`.
- Rating folders 1 through 4 are empty. The planned 54 one-star negatives had not yet been moved into the inspected one-star folder.
- Rating folder 5 contains the 32 JPG seashell references and no flame genomes. They can train the image scorer but cannot contribute generator parameters.
- The Pass 1 configuration reconstructs the fixed reference from the union of `rendered` and `ratings/1`, filtered to run `62b27347`. This preserves the same 10,000-source baseline after later rating moves.

### Tool and outputs

- Added the read-only tool under `work/flame-parameter-analysis`.
- The completed baseline parsed all 10,000 reference flames with no warnings, errors, or duplicate-source exclusions.
- It extracted 401 per-flame matrix parameter columns and analyzed 185 pooled or flame-level parameter marginals.
- Outputs include CSV matrices, categorical probabilities, normalized histograms, circular summaries, concentration comparisons, generator goodness-of-fit tests, a machine-readable distribution profile, and a self-contained HTML report.
- The reference group enforces exactly 10,000 parsed flames so a missing, malformed, duplicated, or incorrectly filtered source cannot silently change the baseline.

### Baseline finding

- The current random generator is empirically consistent with its configured broad distributions. None of 16 goodness-of-fit checks exceeded the practical effect-size threshold of `0.02`; the largest observed effect was approximately `0.01125`.
- No tested affine, post-transform, or weight value fell outside its configured range.
- Transform counts were 24.05% for 2, 25.00% for 3, 25.58% for 4, and 25.37% for 5 transforms.
- Flame symmetry was 59.99% identity, 20.49% rotational order 2, and 19.52% rotational order 3, consistent with the configured 40% rotational-symmetry chance.
- Post transforms occurred on 42.23% of base transforms, close to the configured 42% probability.
- Variation counts were 33.07% with 1, 33.53% with 2, and 33.40% with 3 variations per transform.
- Across 70,573 selected variation instances, all 38 enabled types were close to the expected probability of 2.63%; observed probabilities ranged from 2.44% to 2.74%.
- No parameter-concentration comparison exists yet because the inspected one-star folder was empty. This pass establishes and validates the broad reference prior; it does not yet demonstrate style convergence.

## 2026-08-24 — Focused parameter-analysis scope

- Decision: simplify later rating-driven preset analyses to parameters linked to a user-visible random-generator control. Retain the full matrix for auditability, but do not rank fixed, serialized-redundant, or render-only fields as candidate generator controls.
- Predeclared primary article measures: transform count, pooled variation-type occurrence, and pooled affine scale.
- Most useful secondary checks: realised symmetry, variations per transform, and post-transform presence. Other exposed controls remain available for reproducible preset fitting but are not first-article headline measures.
- The decision table is `work/flame-parameter-analysis/parameter-selection.md`.
- Important implementation limitation: current controls largely express uniform ranges. They cannot yet encode learned non-uniform probabilities for transform counts or enabled variation types, nor learned mixture distributions for numeric ranges. Symmetry chance and orders remain hard-coded even though symmetry family is user-selectable.

## 2026-08-24 — Variation-weight histogram diagnostic

- Observation: the normalized variation-weight histograms peak instead of being flat. This raised the question of whether the random flame generator is sampling incorrectly.
- Investigation: the generator draws one raw weight per selected variation uniformly from `0.5` to `1.5` at the default blend-dominance value, then divides every raw weight by the transform total. The saved variation weights therefore always sum to one within a transform.
- Result: the observed peaks are an expected consequence of normalization, not evidence of a biased random-number generator. In the 10,000-flame reference there are 35,227 base transforms: 11,648 with one variation (33.07%; its sole saved weight is necessarily exactly `1.0`), 11,812 with two variations (each normalized weight lies between roughly `0.25` and `0.75` and centres at `0.5`), and 11,767 with three variations (each lies between roughly `1/7` and `3/5` and centres at `1/3`). Across all 70,573 selected variation instances, 16.50% are exactly `1.0`.
- Interpretation: the current report mixes one-, two-, and three-variation transforms in each variation-weight histogram, so it conflates three structurally different distributions. A future revision should stratify weight diagnostics by variation count or suppress the pooled weight histogram from the focused report.
- Design implication: a flat stored-weight histogram cannot be requested without defining a joint distribution that still keeps each transform's variation weights summing to one. The desired blend behaviour should be specified separately before changing the generator.

## 2026-08-24 — Proposed broad variation-weight sampling

- A local research branch, `research/variation-weight-sampling`, was created so the analysis and any later approved implementation remain together with the project research files.
- Proposed design: retain the existing sampler unchanged for reproducibility and add an opt-in `UniformSimplex` mode with a minimum per-variation share. The proposed first research value is `0.05`, allowing a two-variation blend to approach `0.05 / 0.95` as well as balanced cases.
- For three or more variations, the intended meaning of even sampling is uniform coverage of the valid sum-to-one simplex, not a flat histogram for one isolated weight column.
- The full proposal, alternatives, tests, and approval questions are in `work/flame-parameter-analysis/variation-weight-sampling-design.md`. No generator change has been made.

## 2026-08-24 — Revised coverage-first sampler proposal

- Decision: do not preserve the normalized-raw legacy sampler or its historical seed behavior. It is considered unsuitable because it excludes strongly asymmetric multi-variation ratios.
- Proposed replacement: a single `SpaceFillingSimplexSampler` for all transforms with two or more variations. It samples the valid relative-mixture simplex directly and uses a session-seeded low-discrepancy sequence to cover a finite render batch more evenly than independent random draws.
- Proposed first floor: `0.05`. This lets two-variation transforms range from near `0.05 / 0.95` to near `0.95 / 0.05`; for three variations it permits mixes near `0.90 / 0.05 / 0.05`.
- One-variation transforms stay exactly `1.0`, because that is the only valid one-part mixture.
- The proposal requires sequential genome creation in the bounded render producer, followed by the existing parallel render workers, so coverage counters remain deterministic independent of worker timing.

## 2026-08-26 — Updated v1.3 broad-prior baseline, Pass 2

### Branch and source audit

- The analysis was run from the `research` branch worktree at
  `C:\Users\Hugo\.codex\worktrees\e856\2026_07_14 Apophysis`. The branch
  contains the v1.3 deterministic `SpaceFillingSimplexSampler`, recorded
  generator profiles, and the prescribed `work/flame-parameter-analysis`
  tool.
- The user-facing Desktop folder is named `Fractal flame curator - SeaShells`
  rather than the shorter name used in the request.
- The rendered folder contains exactly 24,000 non-empty `.flame` files and
  exactly 24,000 matching non-empty PNGs. All 24,000 belong to session
  `001e81a2`; the recorded profile is
  `rendered/generator_profile_run_001e81a2.json`.
- Rating folders 1 through 4 are empty. Rating folder 5 contains 32 JPG
  seashell X-ray references. These images are scorer references, not flame
  genomes, so they cannot enter parameter-space analysis.
- The v1.3 profile records transform count 2–5, rotational symmetry, affine
  rotation ±180°, scale 0.35–0.85, shear ±0.25, translation ±0.75, 1–3
  variations, `minimum_variation_share = 0.05`, 42% post-transform chance,
  post scale 0.75–1.25, post translation ±0.18, and final transforms disabled.

### Tool correction before accepting the pass

- The branch specification promised conditional simplex diagnostics and
  complete per-transform weight vectors, but the checked-out analyzer did not
  emit them. This was a real research-tool mismatch, not a data failure.
- The analyzer was updated to version 1.1.0 before the final run. It now emits
  `variation_weight_vectors.csv` and `simplex_coverage.csv`, tests two-part
  mixtures against the configured interval, and plots three-part mixtures on a
  ternary simplex. The five analyzer tests pass after the change.

### Pass 2 results

- The final run parsed exactly 24,000 flames, produced 401 matrix parameter
  columns and 185 analyzed marginals, and reported zero parse warnings,
  duplicate IDs, or filtered reference records.
- All 16 configured broad-prior checks were inside their expected ranges and
  none had a p-value below 0.01 or a practical effect above 0.02. The largest
  KS deviation was affine rotation, D = 0.0055, p = 0.0122; this is small and
  not an out-of-range or practical distribution failure.
- Transform count, symmetry, affine scale/shear/translation, transform
  weights, variation count/weight geometry, post presence/ranges, and the
  disabled final-transform state are therefore consistent with the recorded
  v1.3 profile at this baseline size.
- The sampler-specific check captured 84,078 base-transform vectors: 28,109
  one-part, 27,906 two-part, and 28,063 three-part mixtures. One-part weights
  are exactly 1.0 with no violations. Two-part weights span 0.050002–0.949998,
  occupy all 18 diagnostic bins, and pass the uniform-share KS check (D =
  0.0041, p = 0.744). Three-part vectors have zero floor violations, maximum
  sum error 2.22e-16, and occupy all 55 cells of the 10-bin triangular grid.
- This is a validated broad prior, not yet a “normal distribution”: the
  generator intentionally uses discrete uniform choices, continuous uniform
  ranges, Bernoulli post transforms, and constrained simplex mixtures.

### Planned experimental rounds

1. **Pass 3 — human selection:** manually score approximately 500–1,000
   rendered flames. Keep the selected source IDs and their matching `.flame`
   files together. Re-run the same analyzer against that cohort versus this
   frozen 24,000-flame reference.
2. **Pass 4 — DINO-assisted selection:** score the complete 24,000-image
   population with DINOv2, inspect and independently human-check the highest
   ranked candidates, then analyze the confirmed subset. DINOv2 rank is a
   prioritization signal, not the sole success measure.
3. **Pass 5 and later — controlled iteration:** compare user-only and
   DINO-assisted cohorts with the same primary parameter families (transform
   count, variation count and conditional weight geometry, affine scale),
   secondary controls, joint simplex structure, target-style hit rate, and
   phenotype diversity. Preserve
   the original broad prior and an exploration reserve; do not infer a new
   generator mode from a single marginal or an unverified scorer ranking.

The current Pass 2 artifacts are under
`work/flame-parameter-analysis/outputs/pass-02-v13-baseline`. Future passes
should use a new output directory and retain this profile as the unchanged
reference unless the research question explicitly changes.

## 2026-08-26 — Report decluttering and conditional weight views

- Removed per-variation name probabilities and per-variation true/false
  presence observations from the analyzer, report, focused CSV summaries, and
  generator goodness-of-fit checks. The raw variation parser still recognizes
  supported XML attributes so it can form anonymous normalized weight vectors.
- Removed fixed camera, render, tone, background, filter, brightness, and
  palette fields from the parameter-analysis matrix and report. These values
  are not randomized by the generator and do not belong in the style-space
  comparison.
- Removed serialized affine coefficient marginals and fixed per-transform
  color/symmetry fields from the focused matrix/report; generator-facing
  rotation, scale, shear, and translation are retained as the interpretable
  derived values.
- Replaced the single pooled variation-weight view with three report tabs:
  one-variation exact weight, two-variation pooled component histogram plus
  uniform-share KS test, and three-variation ternary simplex coverage.
- Bumped the analyzer to v1.2.0 and updated the focused-scope documentation and
  tests. The next 24,000-flame baseline rerun must confirm the removed fields
  are absent and all three tabs are present before it is used as the reference
  artifact.

## 2026-08-26 — Renamed Pass 1: 500 human-rated flames

- The 500 newly sorted flame pairs are distributed across ratings 1–4:
  364, 87, 44, and 5 respectively. Rating 5 still contains 32 JPG seashell
  references rather than `.flame` genomes and remains inventory-only.
- The renamed Pass 1 configuration is
  `work/flame-parameter-analysis/pass-01-human-500.json`. It compares the
  500-flame human cohort with the complete 24,000-flame v1.3 reference,
  reconstructed from the current rendered folder plus the rated pairs.
- The run parsed 24,500 records in total with no warnings, producing 64
  matrix parameter columns, 16 focused marginals, and 85,853 weight vectors.
  Intentional overlap between the reference population and its 500-flame
  target is recorded as 500 duplicate source IDs across groups; it is not a
  parse failure.
- The strongest concentration signal is base-transform scale
  (concentration score ≈ 0.0382; target IQR/reference IQR ≈ 0.9618). The
  report retains all eligible comparisons and the conditional one-, two-, and
  three-variation weight tabs for the next pass decision.

## 2026-08-27 — Pass 2: rating folders separated

- Naming is now explicit: Pass 1 is the 24,000-flame v1.3 reference; Pass 2 is
  the first 500 human-sorted images.
- Pass 2 analyzes the rating folders independently: rating 1 = 364 flames,
  rating 2 = 87, rating 3 = 44, and rating 4 = 5. The reference still uses
  the complete 24,000-flame population reconstructed from rendered plus rated
  pairs. Rating 5 remains image-only seashell references.
- The new configuration is
  `work/flame-parameter-analysis/pass-02-human-ratings.json`, with output in
  `work/flame-parameter-analysis/outputs/pass-02-human-ratings`.
- The report accepts `report_groups` for the initial graph selection and adds
  live checkboxes that show or hide cohort series without changing analysis
  tables or CSV output. The initial view includes the reference and ratings
  1–4.

## 2026-08-27 — Generator-control report redesign

- The researcher clarified that variation presence means the occurrence rate
  of each variation type within each cohort, not a separate true/false
  parameter for every name and flame.
- The analyzer was audited against the controls actually exposed in the random
  generator settings window. The redundant `genome.post_transform_count`
  marginal and unrecognized XML-attribute statistics were removed from the
  focused analysis; `xform[*].post.present` remains the direct measure for the
  post-transform chance control.
- The previously configured session profile path was not being read. The
  analyzer now treats `generator_profile_run_001e81a2.json` as the authoritative
  generator settings snapshot and rejects conflicting duplicated expectations;
  manual expectations add only rules not stored in the profile.
- Variation names are now retained as one cohort-level occurrence distribution
  over base transforms. The report and `variation_occurrence.csv` compare exact
  occurrence rates with the frozen reference, while
  `variation_weight_by_name.csv` preserves conditional per-type weight evidence
  for later profile work. No matrix of per-variation Boolean parameters was
  reintroduced.
- The cohort configurator failure had two causes: checkbox inputs inherited the
  full-width search-field style, and cohorts omitted from the initial
  `report_groups` list were not rendered into charts at all. Every analytical
  cohort is now embedded; checkboxes plus Select all, Reference only, and
  Ratings only controls update graph series and cohort table rows using CSS
  display state.
- The main report is organized by generator control rather than XML parameter
  name. It leads with sample-guarded configuration guidance, places occurrence
  evidence beside the Enabled variations control, and moves p-values, effect
  size, JS divergence, spread ratio, entropy, simplex diagnostics, and folder
  inventory into explained advanced sections.
- Automatic guidance is intentionally conservative: fewer than 30 target
  observations cannot produce a configuration recommendation, proposed numeric
  intervals use target q05–q95 only as candidate uniform ranges, and every
  narrowing recommendation retains a broad exploration reserve for the next
  validation pass.
