# Native Fractal-Flame Curator: product specification

This document describes the current product, its user flows, persistent data,
and acceptance behavior. It is deliberately not a changelog. When a user asks
for a feature or behavioral change, revise the relevant current-state section
in the same work item so a future agent can recreate the application from
scratch without reading Git history.

## Purpose and boundaries

The application helps a human curate generated fractal flames:

1. It creates reproducible Apophysis-compatible source genomes.
2. It renders one candidate at a time while continuing generation in the
   background.
3. The human rates candidates from one to five stars.
4. Every human rating preserves both the displayed PNG and its matching source
   .flame file.
5. Optional AI learns a preference estimate from those human ratings and ranks
   future rendered candidates without replacing human judgment.

The product is a native Windows WPF application. The primary workflow must work
without a web server, remote rendering, a genetic algorithm, a preexisting
image dataset, Python, CUDA, or AI. The optional AI feature is additive and
never controls or edits human ratings.

## User interface

The window uses a left control panel and a large image viewport. The Rendering
and AI Scoring drawers begin expanded; Image, Dataset Statistics, and
Diagnostics begin collapsed. The preview fits the viewport by default, supports
pointer-anchored mouse-wheel zoom, and provides Previous, Next, Zoom to fit,
Actual size, and Undo controls.

### Rendering drawer

The user chooses:

- Output directory; default is Documents/ApophysisCurator.
- Base random seed.
- Session limit; default 100 candidates.
- Worker count; default is between one and four, bounded by logical CPUs.
- Bounded queue capacity; default 4.

Start begins a finite render session and changes to Stop while active. Pause
changes to Resume. Status reports the truthful renderer backend, queue depth,
ready count, completed/failed renders, elapsed session time, and active sample
progress. Keyboard P toggles pause/resume and Escape stops rendering.

### Image drawer

Image settings affect a source only when the user explicitly invokes a
re-render. They do not silently alter a saved .flame genome.

| Setting | Default | Meaning |
|---|---:|---|
| Output resolution | 2048 by 2048 | Square final image size. |
| Sample budget | 20,000,000 points | Histogram samples used by the CPU renderer. The UI permits up to 500,000,000. |
| Oversample | 1 | Real internal render multiplier before downsampling; valid values are 1 to 3. |
| Filter radius | 0.5 | Downsampling filter radius. |
| Gamma | 1 in the UI | Tone response for the viewport render. |
| Brightness | 1.0 | Exposure before tone mapping. |
| Vibrancy | 1.0 | Saturation effect for color palettes. |
| White point | 0.0 | Normalized density at or below which pixels are white. |
| Black point | 0.85 | Normalized density at or above which pixels are black. |
| Contrast curve | 1.0 | One is neutral; greater values increase midtone separation. |
| Low-density cutoff | 0.01 | Densities below this become white. |
| Palette | Monochrome | Explicit default; Fire, Ocean, and Violet are opt-in alternatives. |

Re-render current flame toggles to cancellation while active. It loads the
source .flame, applies the selected image settings, and replaces only the
current PNG after a successful render. Re-render rated flames processes all
complete rated pairs with the configured worker count; it may replace rated
PNGs in place but preserves star folders and every source .flame file.

### Rating and navigation

The central viewport shows one ready candidate at a time. The user can assign
1 through 5 stars, skip to the next candidate, or navigate previous/next.
Rating immediately moves the matched PNG and .flame pair together. Undo reverses
the latest rating or re-rating without losing either file. Rating folders are
the raw human labels and are the source of truth for AI training.

### Optional AI Scoring drawer

The application displays Python, PyTorch, CUDA, GPU, active-device, and model
diagnostics. Buttons start/stop scoring, train a model, and rescore the rated
dataset. A missing or unsuitable Python/CUDA setup disables only AI functions;
manual rendering and rating stay available.

## Manual generation and rendering flow

~~~text
User chooses seed and session settings
  -> producer creates a deterministic sequence of seeds
  -> bounded queue feeds one or more worker tasks
  -> worker generates and validates a flame genome
  -> CPU renderer samples points and tone-maps a PNG
  -> source archive publishes matching PNG and .flame files
  -> candidate catalog makes the complete candidate available in the viewport
  -> user rates, skips, re-renders, or navigates while workers continue
~~~

A session seed derives the next candidate seed deterministically from the base
seed and sequence index. A source ID also contains a unique session suffix so
separate sessions cannot overwrite one another. Given the same seed, generator
version, and render settings, the generated genome and pixel result are
reproducible.

The generator chooses two through five transforms from the supported variation
registry, varied affine coefficients, weights, colors, optional post
transforms, camera values, symmetry, and a 256-color palette. Validation allows
two through twelve transforms so imported valid genomes with a larger count
remain supported. Invalid, singular, non-finite, unknown-variation, or
non-renderable genomes are rejected before serialization or rendering.

The built-in renderer is a bounded managed CPU renderer. It reports CPU
honestly and never claims GPU use. Rendering work is cancellable. Pause blocks
both queue consumption and active renderer progress, so a paused session does
not continue CPU sampling. The UI updates status at most four times per second
and does not repeatedly enumerate the workspace while idle.

## Flame-file compatibility

Each generated candidate has a .flame source file that remains available after
rating. New files are declaration-free UTF-8 Apophysis 7X-style XML containing:

- root flames/flame structure, seed, square size, camera, symmetry, oversample,
  filter, tone values, and a 256-color palette;
- native coefs affine attributes and direct variation attributes;
- post transforms and finalxform when present;
- hue_rotation and sample-density quality mapped from the curator sample
  budget.

The reader remains compatible with older files that used a-f affine fields,
var_* variation attributes, hue, or an earlier total-sample quality value.
Imported final transforms are validated and rendered.

## Workspace and file rules

The selected workspace has this contract:

~~~text
workspace/
  rendered/
    [six-digit-score__]stable-source.png
    [six-digit-score__]stable-source.flame
  ratings/
    1/ through 5/
      stable-source.png
      stable-source.flame
  controls/
    <control-name>/*.png
~~~

Unrated generated pairs live in rendered/. A candidate is visible only when its
PNG and .flame both exist and are non-empty. A six-digit score prefix ranges
from 000000 to 100000 and is metadata for ordering; it is removed when finding
the stable source ID and when placing a candidate in a rating folder.

For a new rating, each ratings/N directory contains only matched PNG/.flame
pairs. The storage layer moves both files through temporary names and rolls the
pair back if publication fails. Re-rating moves the same pair to the new star
folder and removes stale duplicate copies. Undo restores it to rendered/ or
the former star folder. The application can read legacy PNG-only rated images
for AI dataset compatibility, but it must not create new unpaired ratings.

## Candidate catalog and ordering

CandidateCatalog excludes source IDs already present in a rating folder. It
groups duplicate legacy IDs and picks the most recently written complete pair
deterministically. Without AI, candidates are ordered by source ID. With AI
enabled, scored candidates appear first in descending score order, followed by
unscored candidates.

## AI preference scoring

AI is optional and CUDA-only. The C# service starts a bundled Python 3.12
JSON-lines worker. The worker uses a frozen pretrained DINOv2 ViT-B/14 backbone
and trains only a small ordinal head with four thresholds:

~~~text
rating >= 2, rating >= 3, rating >= 4, rating >= 5
~~~

The four cumulative probabilities produce an expected rating, and the displayed
continuous score is:

~~~text
(expected rating - 1) / 4
~~~

Scores range from zero to one. The service watches rendered complete pairs and
also performs a low-frequency fallback scan. It prefixes the rendered PNG and
matching .flame with the fixed-width score, then stores the score in the
catalog. Low scores are retained; AI does not discard candidates.

Training snapshots only ratings/1 through ratings/5. It groups images by stable
source ID before assigning deterministic train, validation, and test splits to
avoid near-duplicate leakage. The UI reports rating counts, readiness, ordinal
accuracy, mean absolute rating error, Spearman/rank correlation, calibration,
and control-image results. Small or imbalanced datasets are trainable but their
metrics are explicitly unreliable.

Controls are PNG files under controls/<name>/. They are evaluated only and
never added to human labels. Trained heads are stored under
LocalAppData/FractalFlameCurator/models. Starting a new AI session rescans
existing rendered candidates; retraining replaces the active model and
rescoring uses the replacement.

Rated-dataset rescoring is deliberately separate: it scores every rated PNG,
including legacy PNG-only entries, and updates score prefixes without moving,
deleting, or changing the star folders.

## Reliability and acceptance behavior

The product is correct when these statements hold:

- Fixed inputs reproduce a valid .flame and identical rendered pixels.
- Default output is a 2048 by 2048 monochrome image.
- Saved .flame files open as valid Apophysis-compatible XML and older supported
  files still load.
- Render sessions obey queue and worker bounds, reach their finite limits, and
  do not freeze the WPF UI.
- Pause, stop, current re-render, rated re-render, AI scoring, and training
  have cancellation paths that preserve complete persisted pairs.
- A rating, re-rating, and undo keep PNG/.flame files together.
- The UI reports CPU/GPU and AI availability truthfully.
- Manual work remains usable when AI is unavailable.
- Idle status updates do not cause persistent workspace scans or meaningful CPU
  use.

Automated coverage is split between PhaseOneTests for the manual product and
PhaseTwoTests for AI/data behavior. Changes to any behavior above require a
corresponding focused test and an update to this specification.
