# Native Fractal-Flame Curator: agent guide

This is the technical handoff for agents working on Native Fractal-Flame
Curator. Read this file and features.md before changing the application.
Together they are the complete project brief for rebuilding the product from
scratch: this file explains the implementation structure and engineering
constraints; features.md specifies the user-visible product and its flows.

## Documentation rule

Every user-requested behavior change, addition, removal, or altered user flow
must update features.md in the same work item. Revise the relevant current-state
section so it remains an accurate specification of how the product works. Do
not turn features.md into a dated changelog or append historical implementation
notes. Update this file only when architecture, toolchain, module ownership,
invariants, or agent workflow changes.

## Product identity

Native Fractal-Flame Curator is a Windows desktop application for generating
reproducible, Apophysis-compatible fractal flames, rendering them locally, and
collecting human one-to-five-star preferences. Optional AI scoring ranks
candidates using those human ratings, but manual ratings remain authoritative.

The current product is version 1.2.0. It is a WPF/.NET 8 application with a
managed CPU renderer and an optional CUDA-only Python DINOv2 worker. Do not
reintroduce the historical web server, genetic algorithm, heuristic evaluator,
or research corpus into this application.

## Toolchain and local commands

Required for development:

- Windows and the .NET SDK pinned by global.json (currently 8.0.424).
- The Windows desktop workload supplied by that SDK.
- Python 3.12, CUDA, PyTorch, and Pillow only when exercising optional AI
  scoring. Manual generation, rendering, rating, and tests do not require
  working CUDA.

Run these commands from the repository root:

~~~powershell
dotnet build .\FractalFlameCurator.sln --configuration Release
dotnet test .\FractalFlameCurator.sln --configuration Release
dotnet run --project .\src\FractalFlameCurator\FractalFlameCurator.csproj
~~~

The application project publishes the bundled Ai/dinov2_service.py worker.
For a distributable Windows x64 build, use a self-contained publish and keep
that Python file beside the executable under Ai/.

## Repository map

| Location | Responsibility |
|---|---|
| FractalFlameCurator.sln | Builds the WPF application and its xUnit tests. |
| global.json | Pins the .NET SDK used by local builds and CI. |
| src/FractalFlameCurator/App.xaml and App.xaml.cs | WPF application entry point and shared dark-theme control resources. |
| src/FractalFlameCurator/MainWindow.xaml | All visual layout, drawers, controls, and event bindings. |
| src/FractalFlameCurator/MainWindow.xaml.cs | UI orchestration, status updates, navigation, cancellation, workspace selection, and service lifecycle. Keep UI-only coordination here; move domain behavior to the appropriate module. |
| src/FractalFlameCurator/Models | In-memory flame, transform, palette, render, scoring, and dataset contracts. VariationRegistry is the supported-variation source of truth. |
| src/FractalFlameCurator/Generation | Seeded random source, FlameGenerator, and FlameValidator. This owns reproducible valid genomes. |
| src/FractalFlameCurator/Serialization | FlameXmlSerializer reads and writes the Apophysis-compatible .flame dialect and legacy attributes. |
| src/FractalFlameCurator/Rendering | CpuFlameRenderer produces deterministic pixels, ToneMapper maps density to color, and ArtifactRerenderer safely replaces an existing PNG. |
| src/FractalFlameCurator/Storage | SourceArchive writes complete source pairs; RatingStore moves, re-rates, validates, and undoes matched PNG/.flame pairs. |
| src/FractalFlameCurator/Pipeline | BoundedRenderQueue, ContinuousRenderService, CandidateCatalog, and ContinuousAiScoringService coordinate background work and candidate ordering. |
| src/FractalFlameCurator/Ai | Dataset snapshot/splitting, C# JSON-lines process client, and the Python DINOv2 service. Keep this boundary independent of manual rendering/rating. |
| tests/FractalFlameCurator.Tests | PhaseOneTests covers manual-workflow acceptance behavior; PhaseTwoTests covers dataset, scoring, and AI-pipeline behavior without requiring CUDA. |
| native-fractal-flame-curator-user-manual.pdf | Optional end-user document included in releases; not a source-of-truth engineering specification. |

## Runtime architecture

~~~text
MainWindow
  |-- ContinuousRenderService
  |     |-- FlameGenerator -> FlameValidator
  |     |-- CpuFlameRenderer -> ToneMapper
  |     '-- SourceArchive -> rendered PNG/.flame pair
  |
  |-- CandidateCatalog <-> SourceArchive + RatingStore
  |-- RatingStore -> ratings/1 through ratings/5
  |
  '-- ContinuousAiScoringService
        |-- PreferenceDatasetBuilder <- ratings/1 through ratings/5
        '-- DinoV2PreferenceBackend <-> Ai/dinov2_service.py
              '-- local model files under LocalAppData/FractalFlameCurator/models
~~~

MainWindow must remain responsive. Rendering, re-rendering, AI startup,
scoring, training, and rated-dataset rescoring run asynchronously. The
DispatcherTimer only updates displayed status every 250 ms; it must not become a
periodic full-workspace scan. Workspace refresh occurs at explicit lifecycle
events such as workspace selection, completed renders, ratings, and undo.

## Persistent workspace contract

The user selects a workspace, defaulting to Documents/ApophysisCurator. The
application may create the following directories:

~~~text
workspace/
  rendered/
    [scoreprefix__]flame_<sequence>_seed_<seed>_run_<session>.png
    [scoreprefix__]flame_<sequence>_seed_<seed>_run_<session>.flame
  ratings/
    1/ through 5/
      flame_<stable-source-id>.png
      flame_<stable-source-id>.flame
  controls/
    <control-name>/*.png
~~~

The PNG and .flame sharing a stable source ID are a single candidate pair.
Never create a rated candidate without both files. SourceArchive publishes a
pair atomically enough that readers see only complete, non-empty candidates.
RatingStore rolls both files back if a move cannot be completed.

Rendered candidates may carry a six-digit score prefix such as
087342__name.png. CandidateFileNaming removes that prefix to find the stable
source ID. Rating strips it so rating folders retain score-free stable
basenames. The five rating folders must contain only matching PNG/.flame pairs;
legacy PNG-only images may be read for dataset compatibility, but new manual
ratings must be complete pairs.

## Core invariants

- A fixed seed and unchanged generation/render settings produce the same genome
  and pixels. Do not introduce time, unordered iteration, or non-seeded
  randomness into generation or rendering.
- A generated flame is square, defaults to 2048 by 2048, contains a
  256-color palette, has two to twelve valid transforms, and passes
  FlameValidator before serialization or rendering.
- New .flame files use declaration-free UTF-8 Apophysis 7X-style XML with
  coefs, direct variation attributes, hue_rotation, optional post transforms,
  and optional finalxform. The reader retains compatibility with legacy a-f
  and var_* attributes and legacy quality values.
- The default palette is Monochrome. Other palettes are explicit user choices.
- Renderer status must truthfully report the backend. The bundled renderer is
  CPU-only; never describe it as GPU accelerated.
- Render queue capacity and worker count are bounded. Pause must gate both
  queued work and active CPU sampling. Stop and every re-render action must be
  cancellable without freezing the UI.
- A current-flame re-render replaces only the PNG after success and preserves
  its source .flame. Cancellation or failure keeps the old PNG.
- AI scoring may reorder and rename rendered candidates, but it must not
  delete, move, rate, or otherwise alter human rating folders. Rated rescoring
  changes score prefixes only.
- The AI worker refuses CPU inference/training. Manual functionality remains
  available when Python, PyTorch, CUDA, DINOv2, or a trained model is absent.

## Phase boundaries

For a clean regeneration, implement Phase 1 first and accept it before
implementing Phase 2.

Phase 1 is the native manual workflow: deterministic flame generation,
Apophysis XML, managed CPU rendering, a responsive bounded render session,
archive persistence, five-star paired-file rating, undo/re-rating, tone
controls, and automated tests for those guarantees.

Phase 2 is optional preference scoring. Use a frozen DINOv2 ViT-B/14 backbone
with a small four-threshold ordinal head for rating >= 2, >= 3, >= 4, and >= 5.
Map cumulative probabilities to expected rating, then to a 0-1 score using
(expectedRating - 1) / 4. Split by stable source ID or generation family to
prevent near-duplicate leakage. Measure ordinal accuracy, mean absolute error,
Spearman/rank correlation, calibration, and controls such as black, sparse,
clipped, low-detail, and non-fractal images.

## How to make a change

1. Read the relevant flow in features.md and locate its owning module in the
   repository map above.
2. Make the smallest behavior-preserving change that meets the request. Do not
   perform unrelated formatting, architecture rewrites, or cleanup.
3. Add or update a focused test in PhaseOneTests or PhaseTwoTests whenever a
   behavior, invariant, persistence format, cancellation path, or ordering rule
   changes.
4. Run dotnet test in Release configuration. For rendering or UI changes, also
   run the application and confirm the affected flow stays responsive.
5. Update features.md in the same work item whenever the user requested any
   behavior change or addition. Rewrite the affected current-state section;
   do not add a dated change-log entry.

Use the module boundaries rather than adding behavior to MainWindow when it
belongs in generation, storage, rendering, pipeline, or AI. Preserve public
contracts and legacy flame compatibility unless a migration is explicitly
approved.

## Verification checklist

Before handing off a code change, run the smallest applicable set plus the full
test suite when practical:

- XML validity, deterministic generation, default dimensions/palette, variation
  coverage, render cancellation, sample budget, tone mapping, queue bounds,
  finite-session completion, and truthful CPU status.
- Pair publication, rating moves, re-rating, undo, pair-folder validation, and
  current/rated re-render preservation.
- Score prefixes, source-grouped dataset split, ordinal math, AI rescoring,
  candidate ordering, duplicate legacy IDs, model replacement, and unavailable
  CUDA diagnostics.
- Build, test, and (for release work) a self-contained publish plus a GUI smoke
  launch.

## External references

- DINOv2 model card: https://github.com/facebookresearch/dinov2/blob/main/MODEL_CARD.md
- DINOv2 repository: https://github.com/facebookresearch/dinov2
- DINOv2 paper: https://arxiv.org/abs/2304.07193
